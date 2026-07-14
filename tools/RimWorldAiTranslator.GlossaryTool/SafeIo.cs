using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;

namespace RimWorldAiTranslator.GlossaryTool;

internal static class SafeLimits
{
    public const long XmlFileBytes = 128L * 1024 * 1024;
    public const long TarFileBytes = 512L * 1024 * 1024;
    public const long TarEntryBytes = 128L * 1024 * 1024;
    public const long TarExpandedBytes = 512L * 1024 * 1024;
    public const long ModListBytes = 64L * 1024 * 1024;
    public const long RuleFileBytes = 1024L * 1024;
    public const int FileSystemEntries = 100_000;
    public const int TarEntries = 100_000;
    public const int TarNameCharacters = 1024;
    public const int TranslationEntries = 2_000_000;
    public const int OutputBytes = 512 * 1024 * 1024;
}

internal static class SafePaths
{
    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new GlossaryToolException("A supplied path is invalid.");
        }
    }

    public static string RequireInputDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new GlossaryToolException($"{label} is required.");
        var full = Normalize(path);
        if (!Directory.Exists(full)) throw new GlossaryToolException($"{label} does not exist.");
        RejectReparsePoint(full, label);
        return full;
    }

    public static string RequireInputFile(string path, string label, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new GlossaryToolException($"{label} is required.");
        var full = Normalize(path);
        if (!File.Exists(full)) throw new GlossaryToolException($"{label} does not exist.");
        RejectReparsePoint(full, label);
        var length = new FileInfo(full).Length;
        if (length < 0 || length > maxBytes) throw new GlossaryToolException($"{label} exceeds its size limit.");
        return full;
    }

    public static void PrepareOutputPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new GlossaryToolException($"{label} is required.");
        var full = Normalize(path);
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(parent)) throw new GlossaryToolException($"{label} has no parent directory.");
        RejectReparseAncestors(parent, label + " directory");
        CreateOutputDirectory(parent, label);
        RejectReparseAncestors(parent, label + " directory");
        if (Directory.Exists(full)) throw new GlossaryToolException($"{label} points to a directory.");
        if (File.Exists(full)) RejectReparsePoint(full, label);
    }

    public static bool IsInsideOrEqual(string candidate, string root)
    {
        var fullCandidate = Normalize(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Normalize(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static void RejectReparsePoint(string path, string label)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new GlossaryToolException($"{label} cannot be a reparse point.");
        }
        catch (GlossaryToolException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GlossaryToolException($"{label} could not be inspected safely.");
        }
    }

    public static void RejectReparseBetween(string root, string target, string label)
    {
        var fullRoot = Normalize(root);
        var fullTarget = Normalize(target);
        if (!IsInsideOrEqual(fullTarget, fullRoot)) throw new GlossaryToolException($"{label} escaped its permitted root.");
        RejectReparsePoint(fullRoot, label + " root");
        var relative = Path.GetRelativePath(fullRoot, fullTarget);
        if (relative == ".") return;
        var cursor = fullRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            if (File.Exists(cursor) || Directory.Exists(cursor)) RejectReparsePoint(cursor, label);
        }
    }

    public static void RejectReparseAncestors(string path, string label)
    {
        DirectoryInfo? cursor = new(Directory.Exists(path) ? Normalize(path) : Path.GetDirectoryName(Normalize(path))!);
        while (cursor is not null)
        {
            if (cursor.Exists) RejectReparsePoint(cursor.FullName, label);
            cursor = cursor.Parent;
        }
    }

    private static void CreateOutputDirectory(string directory, string label)
    {
        if (Directory.Exists(directory)) return;
        try { Directory.CreateDirectory(directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GlossaryToolException($"{label} directory could not be created.");
        }
    }
}

internal static class SafeFileTree
{
    public static IReadOnlyList<string> Files(string root, Func<string, bool> predicate, CancellationToken cancellationToken)
    {
        var fullRoot = SafePaths.RequireInputDirectory(root, "Input directory");
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        var inspected = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            SafePaths.RejectReparseBetween(fullRoot, directory, "Input directory");
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos()
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InputDataException("An input directory could not be enumerated safely.");
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspected > SafeLimits.FileSystemEntries)
                    throw new InputDataException("An input tree exceeds the file-system entry limit.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InputDataException("An input tree contains a reparse point.");
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child.FullName);
                }
                else if (entry is FileInfo file && predicate(file.FullName))
                {
                    files.Add(file.FullName);
                }
            }
        }

        return files.OrderBy(path => Path.GetRelativePath(fullRoot, path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => Path.GetRelativePath(fullRoot, path), StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string> ImmediateDirectories(string root, CancellationToken cancellationToken)
    {
        var fullRoot = SafePaths.RequireInputDirectory(root, "Input directory");
        try
        {
            return new DirectoryInfo(fullRoot).EnumerateDirectories()
                .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(directory => directory.Name, StringComparer.Ordinal)
                .Select(directory =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InputDataException("An input root contains a reparse-point directory.");
                    return directory.FullName;
                })
                .ToArray();
        }
        catch (GlossaryToolException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InputDataException("An input root could not be enumerated safely.");
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    private const string Prefix = "RimWorldAiTranslator-glossary-";
    private bool disposed;

    private TemporaryDirectory(string path) => Path = path;

    public string Path { get; }

    public static TemporaryDirectory Create()
    {
        var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        var path = System.IO.Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!SafePaths.IsInsideOrEqual(path, temp)
            || !IsOwnedName(System.IO.Path.GetFileName(path) ?? string.Empty))
            throw new GlossaryToolException("A safe temporary directory could not be established.");
        SafePaths.RejectReparsePoint(path, "Temporary directory");
        return new TemporaryDirectory(path);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!Directory.Exists(Path)) return;
        var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (!SafePaths.IsInsideOrEqual(Path, temp)
            || !IsOwnedName(System.IO.Path.GetFileName(Path) ?? string.Empty))
            throw new GlossaryToolException("Refusing to remove an unverified temporary directory.");
        _ = SafeFileTree.Files(Path, _ => false, CancellationToken.None);
        Directory.Delete(Path, recursive: true);
    }

    private static bool IsOwnedName(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal)
        && name.Length == Prefix.Length + 32
        && Guid.TryParseExact(name[Prefix.Length..], "N", out _);
}

internal static class SafeTarExtractor
{
    public static void Extract(string tarPath, string destinationRoot, CancellationToken cancellationToken)
    {
        var archive = SafePaths.RequireInputFile(tarPath, "Korean language archive", SafeLimits.TarFileBytes);
        var root = SafePaths.Normalize(destinationRoot);
        Directory.CreateDirectory(root);
        SafePaths.RejectReparsePoint(root, "Archive destination");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        var entryCount = 0;
        using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var reader = new TarReader(input, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > SafeLimits.TarEntries) throw new InputDataException("Language archive has too many entries.");
            if (entry.EntryType is TarEntryType.ExtendedAttributes
                or TarEntryType.GlobalExtendedAttributes
                or TarEntryType.LongPath
                or TarEntryType.LongLink)
                continue;
            var rawName = entry.Name.Replace('\\', '/').TrimEnd('/');
            var isDirectory = entry.EntryType is TarEntryType.Directory or TarEntryType.DirectoryList;
            if (isDirectory && rawName is "." or "./") continue;
            var relative = NormalizeEntryName(entry.Name);
            var destination = SafeDestination(root, relative);
            if (!seen.Add(destination)) throw new InputDataException("Language archive contains a duplicate destination.");

            if (isDirectory)
            {
                Directory.CreateDirectory(destination);
                SafePaths.RejectReparseBetween(root, destination, "Archive entry");
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
                throw new InputDataException("Language archive contains a link or unsupported entry type.");
            if (entry.Length < 0 || entry.Length > SafeLimits.TarEntryBytes)
                throw new InputDataException("Language archive entry exceeds its size limit.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > SafeLimits.TarExpandedBytes)
                throw new InputDataException("Language archive exceeds its expanded-size limit.");
            if (entry.DataStream is null) throw new InputDataException("Language archive entry has no data stream.");

            var parent = System.IO.Path.GetDirectoryName(destination)
                ?? throw new InputDataException("Language archive entry has no safe parent.");
            Directory.CreateDirectory(parent);
            SafePaths.RejectReparseBetween(root, parent, "Archive entry");
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.SequentialScan | FileOptions.WriteThrough);
            CopyExactly(entry.DataStream, output, entry.Length, cancellationToken);
            output.Flush(flushToDisk: true);
            SafePaths.RejectReparseBetween(root, destination, "Archive entry");
        }
    }

    private static string NormalizeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > SafeLimits.TarNameCharacters || name.Contains('\0'))
            throw new InputDataException("Language archive contains an invalid entry name.");
        var normalized = name.Replace('\\', '/').TrimEnd('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        if (normalized.Length == 0 || normalized.StartsWith('/') || Path.IsPathRooted(normalized))
            throw new InputDataException("Language archive contains an absolute entry path.");
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(IsUnsafeWindowsSegment))
            throw new InputDataException("Language archive contains a traversal entry path.");
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static bool IsUnsafeWindowsSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || !segment.Equals(segment.TrimEnd(' ', '.'), StringComparison.Ordinal))
            return true;
        if (segment.Any(character => character < ' ' || "<>:\"/\\|?*".Contains(character)))
            return true;
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                                     || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                               && stem[3] is >= '1' and <= '9');
    }

    private static string SafeDestination(string root, string relative)
    {
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        if (!SafePaths.IsInsideOrEqual(destination, root) || destination.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InputDataException("Language archive entry escaped its extraction root.");
        return destination;
    }

    private static void CopyExactly(Stream input, Stream output, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (copied < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = (int)Math.Min(buffer.Length, length - copied);
            var read = input.Read(buffer, 0, wanted);
            if (read == 0) throw new InputDataException("Language archive entry ended unexpectedly.");
            output.Write(buffer, 0, read);
            copied += read;
        }
    }
}

internal static class AtomicOutput
{
    public static void CommitPair(
        string jsonPath,
        byte[] json,
        string csvPath,
        byte[] csv,
        CancellationToken cancellationToken,
        Action? afterJsonCommitForTest = null)
    {
        SafePaths.PrepareOutputPath(jsonPath, "JSON output");
        SafePaths.PrepareOutputPath(csvPath, "CSV output");
        var destinations = new[]
        {
            Path.GetFullPath(jsonPath),
            Path.GetFullPath(csvPath),
            Path.GetFullPath(jsonPath) + ".bak",
            Path.GetFullPath(csvPath) + ".bak"
        };
        if (destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != destinations.Length)
            throw new GlossaryToolException("JSON, CSV, and their backup paths must all be distinct.");
        if (json.Length > SafeLimits.OutputBytes || csv.Length > SafeLimits.OutputBytes)
            throw new GlossaryToolException("Generated output exceeds its size limit.");
        ValidateJson(json);
        ValidateCsv(csv);

        string? jsonTemp = null;
        string? csvTemp = null;
        CommitState? jsonState = null;
        CommitState? csvState = null;
        try
        {
            jsonTemp = WriteTemp(jsonPath, json, cancellationToken);
            csvTemp = WriteTemp(csvPath, csv, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            jsonState = CommitOne(jsonTemp, jsonPath);
            afterJsonCommitForTest?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            csvState = CommitOne(csvTemp, csvPath);
        }
        catch
        {
            if (csvState is not null) RollBack(csvState);
            if (jsonState is not null) RollBack(jsonState);
            throw;
        }
        finally
        {
            if (jsonTemp is not null) DeleteIfExists(jsonTemp);
            if (csvTemp is not null) DeleteIfExists(csvTemp);
        }
        FinalizeCommit(jsonState!);
        FinalizeCommit(csvState!);
    }

    private static string WriteTemp(string destination, byte[] content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var temp = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
        return temp;
    }

    private static CommitState CommitOne(string temp, string destination)
    {
        var full = Path.GetFullPath(destination);
        var backup = full + ".bak";
        var hadOriginal = File.Exists(full);
        if (Directory.Exists(backup)) throw new GlossaryToolException("Output backup path is a directory.");
        if (!hadOriginal && File.Exists(backup))
            throw new GlossaryToolException("An orphaned output backup must be resolved before generating new output.");
        var previousBackup = backup + ".previous-" + Guid.NewGuid().ToString("N");
        var state = new CommitState(full, backup, previousBackup, hadOriginal);
        if (File.Exists(backup))
        {
            SafePaths.RejectReparsePoint(backup, "Output backup");
            File.Move(backup, previousBackup);
            state.HadPreviousBackup = true;
        }

        try
        {
            if (hadOriginal)
            {
                SafePaths.RejectReparsePoint(full, "Output file");
                File.Replace(temp, full, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, full);
            }
            state.Installed = true;
            FlushFile(full);
            return state;
        }
        catch
        {
            if (state.Installed) RollBack(state);
            else RestorePreviousBackup(state);
            throw;
        }
    }

    private static void RollBack(CommitState state)
    {
        if (state.HadOriginal && File.Exists(state.Backup))
        {
            var discard = state.Destination + ".discard-" + Guid.NewGuid().ToString("N");
            File.Replace(state.Backup, state.Destination, discard, ignoreMetadataErrors: true);
            DeleteIfExists(discard);
            FlushFile(state.Destination);
        }
        else if (!state.HadOriginal)
        {
            DeleteIfExists(state.Destination);
        }
        state.Installed = false;
        RestorePreviousBackup(state);
    }

    private static void FinalizeCommit(CommitState state)
    {
        if (!state.HadPreviousBackup) return;
        try
        {
            if (state.HadOriginal) DeleteIfExists(state.PreviousBackup);
            else File.Move(state.PreviousBackup, state.Backup);
            state.HadPreviousBackup = false;
        }
        catch (IOException exception)
        {
            Debug.WriteLine($"Older glossary backup retained ({exception.GetType().Name}).");
        }
        catch (UnauthorizedAccessException exception)
        {
            Debug.WriteLine($"Older glossary backup retained ({exception.GetType().Name}).");
        }
    }

    private static void RestorePreviousBackup(CommitState state)
    {
        if (!state.HadPreviousBackup || !File.Exists(state.PreviousBackup)) return;
        if (File.Exists(state.Backup)) DeleteIfExists(state.Backup);
        File.Move(state.PreviousBackup, state.Backup);
        state.HadPreviousBackup = false;
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateJson(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            if (!document.RootElement.TryGetProperty("terms", out var terms) || terms.ValueKind != JsonValueKind.Array)
                throw new GlossaryToolException("Generated JSON has no terms array.");
            if (!document.RootElement.TryGetProperty("stats", out var stats)
                || !stats.TryGetProperty("terms", out var termCount)
                || termCount.GetInt32() != terms.GetArrayLength())
                throw new GlossaryToolException("Generated JSON term statistics are inconsistent.");
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var term in terms.EnumerateArray())
            {
                if (!term.TryGetProperty("source", out var source)
                    || !term.TryGetProperty("ko", out var korean)
                    || string.IsNullOrWhiteSpace(source.GetString())
                    || string.IsNullOrWhiteSpace(korean.GetString())
                    || !sources.Add(source.GetString()!))
                    throw new GlossaryToolException("Generated JSON contains an invalid or duplicate term.");
            }
        }
        catch (JsonException exception)
        {
            throw new GlossaryToolException("Generated JSON failed validation.", exception);
        }
    }

    private static void ValidateCsv(byte[] content)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException exception) { throw new GlossaryToolException("Generated CSV is not valid UTF-8.", exception); }
        if (!text.StartsWith("\"source\",\"chosenKo\",\"chosenPriority\",\"chosenCount\",\"alternatives\"\n", StringComparison.Ordinal))
            throw new GlossaryToolException("Generated CSV failed header validation.");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class CommitState(string destination, string backup, string previousBackup, bool hadOriginal)
    {
        public string Destination { get; } = destination;
        public string Backup { get; } = backup;
        public string PreviousBackup { get; } = previousBackup;
        public bool HadOriginal { get; } = hadOriginal;
        public bool HadPreviousBackup { get; set; }
        public bool Installed { get; set; }
    }
}
