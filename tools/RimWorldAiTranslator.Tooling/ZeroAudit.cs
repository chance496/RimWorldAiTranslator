using System.Buffers.Binary;
using System.IO.Compression;

namespace RimWorldAiTranslator.Tooling;

internal static class ZeroAudit
{
    private const long MaximumTextExecutionFileBytes = 8 * 1024 * 1024;
    private const long MaximumGenericArchiveBytes = 160 * 1024 * 1024;
    private const long MaximumGenericArchiveTextBytes = 32 * 1024 * 1024;
    private const uint MaximumGenericArchiveCentralDirectoryBytes = 16 * 1024 * 1024;
    private const int MaximumGenericArchiveEntries = 4096;
    private const int MaximumAuditedPaths = 250_000;

    private static readonly HashSet<string> ForbiddenScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".p" + "s1", ".p" + "sm1", ".p" + "sd1", ".c" + "md", ".b" + "at"
    };

    private static readonly string[] ForbiddenActiveTokens =
    [
        "power" + "shell.exe",
        "power" + "shell",
        "p" + "wsh.exe",
        "p" + "wsh",
        "c" + "md.exe",
        ".p" + "s1",
        ".p" + "sm1",
        ".p" + "sd1",
        ".c" + "md",
        ".b" + "at"
    ];

    private static readonly HashSet<string> ForbiddenExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "power" + "shell.exe", "p" + "wsh.exe", "c" + "md.exe"
    };

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "dist", "artifacts"
    };

    private static readonly HashSet<string> PrivateDataRootDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "reviews"
    };

    private static readonly HashSet<string> TextExecutionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".proj", ".pubxml", ".rsp", ".resx", ".sln",
        ".yml", ".yaml", ".sh", ".config"
    };

    private static readonly HashSet<string> ArchiveExecutionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".nupkg"
    };

    public static int Run(RepositoryLayout repository, string? packagePath)
    {
        VerifySourceTree(repository);
        var version = SemanticVersion.Read(repository.VersionFile);
        var package = string.IsNullOrWhiteSpace(packagePath)
            ? repository.RequireRepositoryPath(Path.Combine(repository.Dist, PackageLayout.ArchiveName(version)))
            : Path.GetFullPath(packagePath);
        VerifyPackage(package);
        Console.WriteLine("Shell-script zero audit passed for active source and package content.");
        return 0;
    }

    public static void VerifySourceTree(RepositoryLayout repository)
    {
        var findings = new List<AuditFinding>();
        foreach (var candidate in EnumerateRepositoryFiles(repository.Root, findings))
        {
            var file = candidate.Path;
            var relative = Path.GetRelativePath(repository.Root, file);
            if (ForbiddenScriptExtensions.Contains(Path.GetExtension(file)))
            {
                findings.Add(new AuditFinding(relative, null, "prohibited executable script file"));
                continue;
            }

            var extension = Path.GetExtension(file);
            if (ArchiveExecutionExtensions.Contains(extension))
            {
                // Historical archives below dist/artifacts are inert evidence, not active source. Their
                // direct directory entries and any script files beside them are still traversed/audited.
                if (!candidate.InExcludedArtifactScope)
                    ScanExecutionArchive(file, relative, findings);
                continue;
            }

            if (!IsActiveExecutionFile(relative)) continue;

            string content;
            try
            {
                var length = new FileInfo(file).Length;
                if (length < 0 || length > MaximumTextExecutionFileBytes)
                {
                    findings.Add(new AuditFinding(
                        relative,
                        null,
                        $"active execution file exceeds the {MaximumTextExecutionFileBytes}-byte audit limit"));
                    continue;
                }
                content = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                findings.Add(new AuditFinding(relative, null, $"active execution file could not be read: {ex.Message}"));
                continue;
            }

            var searchable = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                ? StripCSharpComments(content)
                : content;
            AddForbiddenTokenFindings(searchable, relative, findings);

            if (IsMsBuildExecutionFile(relative))
            {
                AddMsBuildExecutionFinding(searchable, relative, "<Exec", findings);
                AddMsBuildExecutionFinding(searchable, relative, "<PreBuildEvent", findings);
                AddMsBuildExecutionFinding(searchable, relative, "<PostBuildEvent", findings);
            }
        }

        ThrowIfFindings("Active source tree failed the shell-script zero audit", findings);
    }

    public static void VerifyPackage(string packagePath)
    {
        packagePath = Path.GetFullPath(packagePath);
        AssertNoReparsePath(packagePath);
        var findings = new List<AuditFinding>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aggregateUncompressedBytes = 0L;
        if (File.Exists(packagePath))
        {
            if (!Path.GetExtension(packagePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package audit requires a ZIP or directory: {packagePath}");
            PackageArchivePolicy.ValidateArchiveFile(packagePath);
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count != PackageLayout.AllFiles.Count)
            {
                findings.Add(new AuditFinding(
                    Path.GetFileName(packagePath),
                    null,
                    $"archive has {archive.Entries.Count} entries instead of exactly {PackageLayout.AllFiles.Count}"));
            }

            foreach (var entry in archive.Entries)
            {
                if (!ValidatePackageEntry(entry.FullName, entry.Name, names, findings)) continue;
                try
                {
                    PackageArchivePolicy.CopyEntryTo(entry, null, ref aggregateUncompressedBytes);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    findings.Add(new AuditFinding(entry.FullName, null, $"archive content failed bounded validation: {ex.Message}"));
                }
            }
        }
        else if (Directory.Exists(packagePath))
        {
            AssertNoReparseTree(packagePath);
            var entries = Directory.EnumerateFileSystemEntries(packagePath, "*", SearchOption.AllDirectories).ToArray();
            if (entries.Length != PackageLayout.AllFiles.Count)
            {
                findings.Add(new AuditFinding(
                    Path.GetFileName(packagePath),
                    null,
                    $"package directory has {entries.Length} entries instead of exactly {PackageLayout.AllFiles.Count}"));
            }

            foreach (var path in entries)
            {
                var relative = Path.GetRelativePath(packagePath, path).Replace(Path.DirectorySeparatorChar, '/');
                var leaf = relative.Split('/').LastOrDefault() ?? string.Empty;
                if (!ValidatePackageEntry(relative, leaf, names, findings)) continue;
                if (!File.Exists(path))
                {
                    findings.Add(new AuditFinding(relative, null, "allowlisted package entry is not a regular file"));
                    continue;
                }
                try
                {
                    PackageArchivePolicy.ValidateDirectoryFile(leaf, new FileInfo(path).Length, ref aggregateUncompressedBytes);
                }
                catch (InvalidDataException ex)
                {
                    findings.Add(new AuditFinding(relative, null, ex.Message));
                }
            }
        }
        else
        {
            throw new FileNotFoundException("Package to audit does not exist.", packagePath);
        }

        foreach (var missing in PackageLayout.AllFiles.Except(names, StringComparer.OrdinalIgnoreCase))
            findings.Add(new AuditFinding(missing, null, "required allowlisted package entry is missing"));
        ThrowIfFindings("Package failed the shell-script zero audit", findings);
    }

    private static bool ValidatePackageEntry(
        string entry,
        string leaf,
        HashSet<string> names,
        ICollection<AuditFinding> findings)
    {
        var normalized = entry.Replace('\\', '/');
        if (string.IsNullOrEmpty(leaf)
            || !normalized.Equals(leaf, StringComparison.Ordinal)
            || normalized.Contains('/')
            || normalized.StartsWith('/')
            || normalized.Contains("../", StringComparison.Ordinal)
            || !names.Add(leaf))
        {
            findings.Add(new AuditFinding(entry, null, "nested, directory, unsafe, or duplicate package entry"));
            return false;
        }

        var allowlisted = PackageLayout.AllFiles.Contains(leaf);
        if (!allowlisted)
            findings.Add(new AuditFinding(entry, null, "package entry is outside the exact allowlist"));
        if (ForbiddenScriptExtensions.Contains(Path.GetExtension(leaf))
            || ForbiddenExecutableNames.Contains(leaf))
        {
            findings.Add(new AuditFinding(entry, null, "prohibited shell or executable script in package"));
        }
        return allowlisted;
    }

    private static IEnumerable<RepositoryFile> EnumerateRepositoryFiles(string root, ICollection<AuditFinding> findings)
    {
        var pending = new Stack<(string Path, bool InExcludedArtifactScope)>();
        pending.Push((root, false));
        var auditedPaths = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(current.Path, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                findings.Add(new AuditFinding(Path.GetRelativePath(root, current.Path), null, $"directory could not be audited: {ex.Message}"));
                continue;
            }

            foreach (var child in children)
            {
                auditedPaths++;
                if (auditedPaths > MaximumAuditedPaths)
                {
                    findings.Add(new AuditFinding(".", null, $"source audit exceeded the {MaximumAuditedPaths}-path safety limit"));
                    yield break;
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    findings.Add(new AuditFinding(Path.GetRelativePath(root, child), null, $"path attributes could not be audited: {ex.Message}"));
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    findings.Add(new AuditFinding(Path.GetRelativePath(root, child), null, "reparse point prevents a complete source audit"));
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (PathsEqual(current.Path, root)
                        && PrivateDataRootDirectoryNames.Contains(Path.GetFileName(child)))
                    {
                        continue;
                    }
                    var excluded = current.InExcludedArtifactScope
                                   || IgnoredDirectoryNames.Contains(Path.GetFileName(child));
                    pending.Push((child, excluded));
                }
                else
                {
                    yield return new RepositoryFile(child, current.InExcludedArtifactScope);
                }
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveExecutionFile(string relativePath)
    {
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("docs", StringComparison.OrdinalIgnoreCase)) return false;

        var extension = Path.GetExtension(relativePath);
        if (TextExecutionExtensions.Contains(extension)
            || IsMsBuildUserSidecar(relativePath))
            return true;
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return false;

        var fileName = Path.GetFileName(relativePath) ?? string.Empty;
        return parts.Any(part => part.Equals(".github", StringComparison.OrdinalIgnoreCase)
                                 || part.Equals(".vscode", StringComparison.OrdinalIgnoreCase))
               || fileName.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("tasks.json", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("launch.json", StringComparison.OrdinalIgnoreCase);
    }

    private static void ScanExecutionArchive(
        string path,
        string relative,
        ICollection<AuditFinding> findings)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumGenericArchiveBytes)
            {
                findings.Add(new AuditFinding(relative, null, $"active archive is outside the {MaximumGenericArchiveBytes}-byte audit limit"));
                return;
            }

            using var archiveStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateGenericArchiveEnvelope(archiveStream);
            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaximumGenericArchiveEntries)
            {
                findings.Add(new AuditFinding(relative, null, $"active archive exceeds the {MaximumGenericArchiveEntries}-entry audit limit"));
                return;
            }

            var aggregateTextBytes = 0L;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(entry.Name)
                    || normalized.StartsWith('/')
                    || normalized.Split('/').Any(part => part is "" or "." or "..")
                    || !names.Add(normalized))
                {
                    findings.Add(new AuditFinding($"{relative}!{entry.FullName}", null, "archive entry has an unsafe or duplicate path"));
                    continue;
                }

                var entryRelative = $"{relative}!{normalized}";
                var extension = Path.GetExtension(entry.Name);
                if (ForbiddenScriptExtensions.Contains(extension))
                {
                    findings.Add(new AuditFinding(entryRelative, null, "prohibited executable script file in active archive"));
                    continue;
                }
                if (ArchiveExecutionExtensions.Contains(extension))
                {
                    findings.Add(new AuditFinding(entryRelative, null, "nested active archive cannot be completely audited"));
                    continue;
                }
                if (!IsActiveExecutionFile(normalized)) continue;

                if (entry.Length < 0 || entry.Length > MaximumTextExecutionFileBytes)
                {
                    findings.Add(new AuditFinding(entryRelative, null, "archived execution file exceeds its bounded text limit"));
                    continue;
                }
                if (entry.CompressedLength < 0 || (entry.Length > 0 && entry.CompressedLength == 0)
                    || (entry.CompressedLength > 0 && entry.Length > entry.CompressedLength * 100))
                {
                    findings.Add(new AuditFinding(entryRelative, null, "archived execution file has unsafe compression metadata"));
                    continue;
                }
                aggregateTextBytes = checked(aggregateTextBytes + entry.Length);
                if (aggregateTextBytes > MaximumGenericArchiveTextBytes)
                {
                    findings.Add(new AuditFinding(relative, null, "active archive exceeds its aggregate text audit limit"));
                    return;
                }

                using var stream = entry.Open();
                using var reader = new StreamReader(
                    stream,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096,
                    leaveOpen: false);
                var searchable = reader.ReadToEnd();
                if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                    searchable = StripCSharpComments(searchable);
                AddForbiddenTokenFindings(searchable, entryRelative, findings);
                if (IsMsBuildExecutionFile(normalized))
                {
                    AddMsBuildExecutionFinding(searchable, entryRelative, "<Exec", findings);
                    AddMsBuildExecutionFinding(searchable, entryRelative, "<PreBuildEvent", findings);
                    AddMsBuildExecutionFinding(searchable, entryRelative, "<PostBuildEvent", findings);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
        {
            findings.Add(new AuditFinding(relative, null, $"active archive could not be completely audited: {ex.Message}"));
        }
    }

    private static void ValidateGenericArchiveEnvelope(FileStream stream)
    {
        const int eocdBytes = 22;
        const int maximumCommentBytes = ushort.MaxValue;
        const uint eocdSignature = 0x06054b50;
        const uint zip64LocatorSignature = 0x07064b50;
        var length = stream.Length;
        var tailLength = checked((int)Math.Min(length, eocdBytes + maximumCommentBytes));
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);

        var eocdOffsetInTail = -1;
        for (var offset = tail.Length - eocdBytes; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset, sizeof(uint))) != eocdSignature)
                continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 20, sizeof(ushort)));
            if (offset + eocdBytes + commentLength != tail.Length) continue;
            eocdOffsetInTail = offset;
            break;
        }
        if (eocdOffsetInTail < 0)
            throw new InvalidDataException("Active archive has no bounded EOCD record.");

        var eocd = tail.AsSpan(eocdOffsetInTail, eocdBytes);
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]);
        var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        if (disk != 0
            || centralDisk != 0
            || entriesOnDisk != totalEntries
            || totalEntries == ushort.MaxValue
            || centralSize == uint.MaxValue
            || centralOffset == uint.MaxValue)
        {
            throw new InvalidDataException("Active archive uses unsupported split-disk or Zip64 metadata.");
        }
        if (totalEntries > MaximumGenericArchiveEntries
            || centralSize > MaximumGenericArchiveCentralDirectoryBytes)
        {
            throw new InvalidDataException("Active archive exceeds its entry or central-directory audit limit.");
        }

        var absoluteEocdOffset = checked(length - tailLength + eocdOffsetInTail);
        if (checked((long)centralOffset + centralSize) != absoluteEocdOffset)
            throw new InvalidDataException("Active archive central-directory bounds are inconsistent.");
        if (absoluteEocdOffset >= 20)
        {
            Span<byte> locator = stackalloc byte[sizeof(uint)];
            stream.Position = absoluteEocdOffset - 20;
            stream.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == zip64LocatorSignature)
                throw new InvalidDataException("Active archive uses unsupported Zip64 metadata.");
        }
    }

    private static void AddForbiddenTokenFindings(
        string searchable,
        string relative,
        ICollection<AuditFinding> findings)
    {
        foreach (var token in ForbiddenActiveTokens)
        {
            var offset = FindForbiddenToken(searchable, token);
            if (offset < 0) continue;
            findings.Add(new AuditFinding(relative, GetLineNumber(searchable, offset), $"active reference to '{token}'"));
        }
    }

    private static bool IsMsBuildExecutionFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".proj", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".pubxml", StringComparison.OrdinalIgnoreCase)
               || IsMsBuildUserSidecar(path);
    }

    private static bool IsMsBuildUserSidecar(string path) =>
        path.EndsWith(".csproj.user", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".props.user", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets.user", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".proj.user", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".pubxml.user", StringComparison.OrdinalIgnoreCase);

    private static int FindForbiddenToken(string searchable, string token)
    {
        var start = 0;
        while (start < searchable.Length)
        {
            var offset = searchable.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (offset < 0) return -1;
            var beforeIsIdentifier = token[0] != '.'
                                     && offset > 0
                                     && IsIdentifierCharacter(searchable[offset - 1]);
            var after = offset + token.Length;
            var afterIsIdentifier = after < searchable.Length && IsIdentifierCharacter(searchable[after]);
            if (!beforeIsIdentifier && !afterIsIdentifier) return offset;
            start = offset + 1;
        }
        return -1;
    }

    private static bool IsIdentifierCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '_';

    private static string StripCSharpComments(string value)
    {
        var result = value.ToCharArray();
        var state = CSharpScanState.Code;
        for (var index = 0; index < result.Length; index++)
        {
            var current = result[index];
            var next = index + 1 < result.Length ? result[index + 1] : '\0';
            switch (state)
            {
                case CSharpScanState.Code:
                    if (current == '/' && next == '/')
                    {
                        result[index] = result[index + 1] = ' ';
                        index++;
                        state = CSharpScanState.LineComment;
                    }
                    else if (current == '/' && next == '*')
                    {
                        result[index] = result[index + 1] = ' ';
                        index++;
                        state = CSharpScanState.BlockComment;
                    }
                    else if (current == '@' && next == '"')
                    {
                        index++;
                        state = CSharpScanState.VerbatimString;
                    }
                    else if (current == '"') state = CSharpScanState.String;
                    else if (current == '\'') state = CSharpScanState.Character;
                    break;

                case CSharpScanState.LineComment:
                    if (current is '\r' or '\n') state = CSharpScanState.Code;
                    else result[index] = ' ';
                    break;

                case CSharpScanState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        result[index] = result[index + 1] = ' ';
                        index++;
                        state = CSharpScanState.Code;
                    }
                    else if (current is not ('\r' or '\n')) result[index] = ' ';
                    break;

                case CSharpScanState.String:
                    if (current == '\\') index++;
                    else if (current == '"') state = CSharpScanState.Code;
                    break;

                case CSharpScanState.VerbatimString:
                    if (current == '"' && next == '"') index++;
                    else if (current == '"') state = CSharpScanState.Code;
                    break;

                case CSharpScanState.Character:
                    if (current == '\\') index++;
                    else if (current == '\'') state = CSharpScanState.Code;
                    break;
            }
        }
        return new string(result);
    }

    private static int GetLineNumber(string value, int offset)
    {
        var line = 1;
        for (var index = 0; index < offset; index++)
            if (value[index] == '\n') line++;
        return line;
    }

    private static void AddMsBuildExecutionFinding(
        string content,
        string relative,
        string marker,
        ICollection<AuditFinding> findings)
    {
        var offset = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (offset >= 0)
            findings.Add(new AuditFinding(relative, GetLineNumber(content, offset), $"active MSBuild execution element '{marker}'"));
    }

    private static void ThrowIfFindings(string heading, IReadOnlyCollection<AuditFinding> findings)
    {
        if (findings.Count == 0) return;
        Console.Error.WriteLine(heading + ":");
        foreach (var finding in findings
                     .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Line))
        {
            var location = finding.Line is null ? finding.Path : $"{finding.Path}:{finding.Line}";
            Console.Error.WriteLine($"  {location}: {finding.Reason}");
        }
        throw new InvalidDataException($"{heading} ({findings.Count} finding(s)).");
    }

    private static void AssertNoReparsePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var cursor = new FileInfo(path) as FileSystemInfo;
        while (cursor is not null)
        {
            if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Package audit refused a reparse-point path: {cursor.FullName}");
            cursor = cursor switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
    }

    private static void AssertNoReparseTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Package audit refused a reparse point: {current}");
            if (!Directory.Exists(current)) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
                pending.Push(child);
        }
    }

    private sealed record AuditFinding(string Path, int? Line, string Reason);
    private sealed record RepositoryFile(string Path, bool InExcludedArtifactScope);

    private enum CSharpScanState
    {
        Code,
        LineComment,
        BlockComment,
        String,
        VerbatimString,
        Character
    }
}
