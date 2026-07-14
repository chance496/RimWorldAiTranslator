using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Core.Safety;

public static class PathSafety
{
    internal const long MaximumTrustedLeafBytes = 256L * 1024 * 1024;
    internal const long MaximumTrustedBoundaryBytes = 2L * 1024 * 1024 * 1024;
    internal static Action<string>? AfterAtomicLeafLockReleasedTestHook { get; set; }
    internal static Action<string>? AfterAtomicFinalEvidenceReleasedTestHook { get; set; }
    internal static Action<string>? BeforeAtomicRollbackHandleMoveTestHook { get; set; }
    internal static Action<string>? AfterAtomicCommitBeforeEvidencePinnedTestHook { get; set; }
    internal static Action<string>? AfterAtomicEvidencePinnedTestHook { get; set; }
    internal static Action<string>? AfterAtomicTargetDisplacedTestHook { get; set; }
    internal static Action<string>? AfterAtomicBackupDisplacedTestHook { get; set; }
    internal static Action<string>? BeforeTrustedDirectoryCreationTestHook { get; set; }
    internal static Action<string>? BeforeTrustedBoundaryRootLockTestHook { get; set; }

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsNetworkPath(string? path) =>
        IsNetworkPath(path, static root => new DriveInfo(root).DriveType);

    internal static bool IsNetworkPath(
        string? path,
        Func<string, DriveType> getDriveType)
    {
        ArgumentNullException.ThrowIfNull(getDriveType);
        var value = (path ?? string.Empty).Trim();
        if (value.Length >= 2
            && value[0] is '\\' or '/'
            && value[1] is '\\' or '/')
        {
            return true;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsUnc)
            return true;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(value));
            return !string.IsNullOrWhiteSpace(root)
                   && getDriveType(root) == DriveType.Network;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or NotSupportedException
                                          or UnauthorizedAccessException)
        {
            return Path.IsPathFullyQualified(value);
        }
    }

    public static bool IsSafeFileNameSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || !value.Equals(value.Trim(), StringComparison.Ordinal)
            || value.EndsWith(' ')
            || value.EndsWith('.')
            || value.Contains('/')
            || value.Contains('\\')
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }
        var deviceBase = value.Split('.', 2)[0];
        return !WindowsReservedNames.Contains(deviceBase);
    }

    public static bool IsStrictlyInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Normalize(path);
            var fullRoot = Normalize(root);
            if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
            var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                             || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Workshop path classification failed closed ({exception.GetType().Name}).");
            return false;
        }
    }

    public static string ResolveInside(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Expected a relative path.");
        }

        var fullRoot = Normalize(root);
        var fullPath = Normalize(Path.Combine(fullRoot, relativePath));
        if (!IsStrictlyInside(fullPath, fullRoot))
        {
            throw new InvalidDataException($"Path escapes the allowed root: {relativePath}");
        }

        return fullPath;
    }

    public static bool ContainsReparsePoint(string path, string stopRoot)
    {
        try
        {
            var stop = Normalize(stopRoot);
            var current = new DirectoryInfo(Normalize(path));
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if (Normalize(current.FullName).Equals(stop, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = current.Parent;
            }

            return false;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Reparse-point check failed closed ({exception.GetType().Name}).");
            return true;
        }
    }

    public static void EnsureNoReparsePoints(string path, string stopRoot)
    {
        var fullPath = Normalize(path);
        var fullStop = Normalize(stopRoot);
        if (!fullPath.Equals(fullStop, StringComparison.OrdinalIgnoreCase)
            && !IsStrictlyInside(fullPath, fullStop))
        {
            throw new InvalidDataException($"Path is outside the trusted root: {fullPath}");
        }

        var current = fullPath;
        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidDataException($"Could not verify the writable path: {current}", ex);
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Writable paths must not contain a reparse point: {current}");
                }
            }

            if (current.Equals(fullStop, StringComparison.OrdinalIgnoreCase)) return;
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new InvalidDataException($"Could not reach the trusted root while validating: {fullPath}");
            }
            current = Normalize(parent);
        }
    }

    public static void EnsureNoReparsePointsToVolumeRoot(string path)
    {
        var fullPath = Normalize(path);
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new InvalidDataException($"Could not identify the path root: {fullPath}");
        }

        var current = volumeRoot;
        VerifyExistingRegularPath(current);
        var relative = Path.GetRelativePath(volumeRoot, fullPath);
        if (relative.Equals(".", StringComparison.Ordinal)) return;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            VerifyExistingRegularPath(current);
        }
    }

    public static string GetCanonicalExistingDirectory(string path)
    {
        var fullPath = Normalize(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");
        if (!OperatingSystem.IsWindows()) return fullPath;
        using var handle = WindowsPathHandle.OpenDirectory(fullPath);
        return Normalize(WindowsPathHandle.GetFinalPath(handle));
    }

    public static string GetCanonicalProspectiveDirectory(string path)
    {
        var fullPath = Normalize(path);
        var existingAncestor = fullPath;
        var missingSegments = new Stack<string>();
        while (!Directory.Exists(existingAncestor))
        {
            if (File.Exists(existingAncestor))
                throw new InvalidDataException("A prospective output directory resolves to a file.");
            var segment = Path.GetFileName(existingAncestor);
            var parent = Directory.GetParent(existingAncestor)?.FullName;
            if (string.IsNullOrWhiteSpace(segment) || string.IsNullOrWhiteSpace(parent))
                throw new InvalidDataException("A prospective output directory has no existing ancestor.");
            missingSegments.Push(segment);
            existingAncestor = Normalize(parent);
        }

        EnsureNoReparsePointsToVolumeRoot(existingAncestor);
        var canonical = GetCanonicalExistingDirectory(existingAncestor);
        while (missingSegments.TryPop(out var segment))
        {
            canonical = Path.Combine(canonical, segment);
        }
        return Normalize(canonical);
    }

    public static string GetCanonicalExistingFile(string path)
    {
        var fullPath = Normalize(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("File not found.", fullPath);
        if (!OperatingSystem.IsWindows()) return fullPath;
        if ((File.GetAttributes(fullPath) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Files used as input must not be redirected.");
        using var handle = WindowsPathHandle.OpenFile(fullPath);
        var information = WindowsPathHandle.GetIdentity(handle);
        if (information.NumberOfLinks > 1)
            throw new InvalidDataException("Hard-linked files are not accepted as trusted input.");
        return Normalize(WindowsPathHandle.GetFinalPath(handle));
    }

    internal static string GetExistingDirectoryIdentity(string path)
    {
        var fullPath = Normalize(path);
        var snapshot = GetExistingDirectorySnapshot(fullPath);
        var canonical = snapshot.CanonicalPath;
        if (!canonical.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Directory identity must be captured through its canonical physical path.");
        return snapshot.Identity;
    }

    internal static ExistingDirectorySnapshot GetExistingDirectorySnapshot(string path)
    {
        var fullPath = Normalize(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Stable directory identity requires Windows.");
        using var handle = WindowsPathHandle.OpenDirectory(fullPath);
        var canonical = Normalize(WindowsPathHandle.GetFinalPath(handle));
        var identity = WindowsPathHandle.GetIdentity(handle);
        if (identity.FileIndex == 0)
            throw new InvalidDataException("The filesystem did not provide a stable directory identity.");
        return new ExistingDirectorySnapshot(
            canonical,
            $"{identity.VolumeSerialNumber:x8}:{identity.FileIndex:x16}");
    }

    internal static TrustedWriteBoundary AcquireTrustedWriteBoundary(
        string root,
        IEnumerable<string> targetFiles,
        CancellationToken cancellationToken = default) =>
        AcquireTrustedBoundary(
            root,
            targetFiles,
            [],
            createDirectoryGuards: true,
            cancellationToken);

    public static IDisposable AcquireTrustedDirectoryCreationBoundary(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var fullDirectory = Normalize(directory);
        var existingAncestor = fullDirectory;
        while (!Directory.Exists(existingAncestor))
        {
            var parent = Path.GetDirectoryName(existingAncestor);
            if (string.IsNullOrWhiteSpace(parent)
                || parent.Equals(existingAncestor, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException(
                    $"No existing ancestor could be found for the trusted directory: {fullDirectory}");
            }
            existingAncestor = parent;
        }

        BeforeTrustedDirectoryCreationTestHook?.Invoke(fullDirectory);
        var unusedLeaf = Path.Combine(
            fullDirectory,
            $".rimworld-ai-translator-directory-boundary-{Guid.NewGuid():N}.unused");
        return AcquireTrustedWriteBoundary(
            existingAncestor,
            [unusedLeaf],
            cancellationToken);
    }

    internal static TrustedWriteBoundary AcquireTrustedWriteBoundary(
        string root,
        IEnumerable<string> targetFiles,
        IEnumerable<string> protectedFiles,
        CancellationToken cancellationToken = default) =>
        AcquireTrustedBoundary(
            root,
            targetFiles,
            protectedFiles,
            createDirectoryGuards: true,
            cancellationToken);

    internal static TrustedWriteBoundary AcquireTrustedReadBoundary(
        string root,
        IEnumerable<string> protectedFiles,
        CancellationToken cancellationToken = default) =>
        AcquireTrustedBoundary(
            root,
            [],
            protectedFiles,
            createDirectoryGuards: false,
            cancellationToken);

    private static TrustedWriteBoundary AcquireTrustedBoundary(
        string root,
        IEnumerable<string> targetFiles,
        IEnumerable<string> protectedFiles,
        bool createDirectoryGuards,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Trusted writable directory locking requires Windows.");
        var fullRoot = Normalize(root);

        var targets = targetFiles
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var inputs = protectedFiles
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullRoot };
        var creatableDirectories = createDirectoryGuards
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var fullTarget = target;
            if (!IsStrictlyInside(fullTarget, fullRoot))
                throw new InvalidDataException("A write target is outside the trusted root.");
            var parent = Path.GetDirectoryName(fullTarget)
                ?? throw new InvalidDataException("A write target has no parent directory.");
            AddBoundaryDirectories(fullRoot, parent, directories, creatableDirectories);
        }
        foreach (var input in inputs)
        {
            if (!IsStrictlyInside(input, fullRoot))
                throw new InvalidDataException("A protected input is outside the trusted root.");
            var parent = Path.GetDirectoryName(input)
                ?? throw new InvalidDataException("A protected input has no parent directory.");
            AddBoundaryDirectories(fullRoot, parent, directories, null);
        }

        var locks = new List<SafeFileHandle>();
        var guardFiles = new List<FileStream>();
        var leafLocks = new Dictionary<string, TrustedLeaf>(StringComparer.OrdinalIgnoreCase);
        long aggregateLeafBytes = 0;
        try
        {
            BeforeTrustedBoundaryRootLockTestHook?.Invoke(fullRoot);
            foreach (var directory in directories
                         .OrderBy(value => value.Count(character => character is '\\' or '/'))
                         .ThenBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(directory))
                {
                    if (!creatableDirectories.Contains(directory))
                        throw new DirectoryNotFoundException("A protected input directory disappeared before it could be locked.");
                    Directory.CreateDirectory(directory);
                }

                SafeFileHandle? handle = null;
                try
                {
                    handle = WindowsPathHandle.OpenDirectoryWithoutDeleteSharing(directory);
                    var canonical = Normalize(WindowsPathHandle.GetFinalPath(handle));
                    var lockedIdentity = WindowsPathHandle.GetIdentity(handle);
                    if (!canonical.Equals(Normalize(directory), StringComparison.OrdinalIgnoreCase)
                        || !canonical.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                           && !IsStrictlyInside(canonical, fullRoot)
                        || lockedIdentity.FileIndex == 0
                        || (lockedIdentity.FileAttributes
                            & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
                           != FileAttributes.Directory)
                    {
                        throw new InvalidDataException(
                            "A writable directory resolved outside its exact regular-directory path.");
                    }
                    locks.Add(handle);
                    handle = null;
                }
                finally
                {
                    handle?.Dispose();
                }

                if (createDirectoryGuards)
                {
                    var guardPath = Path.Combine(
                        directory,
                        $".rimworld-ai-translator-write-boundary-{Guid.NewGuid():N}.lock");
                    var guard = new FileStream(
                        guardPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        4096,
                        FileOptions.DeleteOnClose | FileOptions.WriteThrough);
                    guardFiles.Add(guard);
                    var guardCanonical = Normalize(WindowsPathHandle.GetFinalPath(guard.SafeFileHandle));
                    var guardParent = Path.GetDirectoryName(guardCanonical)
                        ?? throw new InvalidDataException("The writable directory guard has no parent path.");
                    if (!Normalize(guardParent).Equals(Normalize(directory), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A writable directory guard resolved outside its expected directory.");
                }
            }

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CaptureLeaf(target, isWriteTarget: true, leafLocks, ref aggregateLeafBytes, cancellationToken);
                CaptureLeaf(target + ".bak", isWriteTarget: true, leafLocks, ref aggregateLeafBytes, cancellationToken);
            }
            foreach (var input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CaptureLeaf(input, isWriteTarget: false, leafLocks, ref aggregateLeafBytes, cancellationToken);
                if (!leafLocks[input].ExpectedExists)
                    throw new FileNotFoundException("A protected input disappeared before it could be locked.", input);
            }
            return new TrustedWriteBoundary(locks, guardFiles, leafLocks);
        }
        catch
        {
            foreach (var leaf in leafLocks.Values) leaf.Dispose();
            for (var index = locks.Count - 1; index >= 0; index--) locks[index].Dispose();
            for (var index = guardFiles.Count - 1; index >= 0; index--) guardFiles[index].Dispose();
            throw;
        }
    }

    private static void AddBoundaryDirectories(
        string fullRoot,
        string parent,
        ISet<string> directories,
        ISet<string>? creatableDirectories)
    {
        var relative = Path.GetRelativePath(fullRoot, parent);
        if (relative.Equals(".", StringComparison.Ordinal)) return;
        var current = fullRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                throw new InvalidDataException("A boundary path contains an unsafe directory segment.");
            current = Path.Combine(current, segment);
            directories.Add(current);
            creatableDirectories?.Add(current);
        }
    }

    private static void CaptureLeaf(
        string path,
        bool isWriteTarget,
        IDictionary<string, TrustedLeaf> leaves,
        ref long aggregateLeafBytes,
        CancellationToken cancellationToken)
    {
        if (leaves.TryGetValue(path, out var existing))
        {
            if (isWriteTarget) existing.IsWriteTarget = true;
            return;
        }
        if (Directory.Exists(path))
        {
            if (!isWriteTarget)
                throw new InvalidDataException("A protected input file path resolves to a directory.");
            var directoryHandle = WindowsPathHandle.OpenDirectoryWithoutDeleteSharing(path);
            try
            {
                var directoryIdentity = ValidateDirectoryLeafHandle(directoryHandle, path);
                leaves.Add(path, new TrustedLeaf(
                    path,
                    isWriteTarget,
                    expectedExists: false,
                    expectedDirectory: true,
                    directoryHandle,
                    directoryIdentity,
                    null));
                return;
            }
            catch
            {
                directoryHandle.Dispose();
                throw;
            }
        }
        if (!File.Exists(path))
        {
            leaves.Add(path, new TrustedLeaf(
                path,
                isWriteTarget,
                expectedExists: false,
                expectedDirectory: false,
                null,
                default,
                null));
            return;
        }

        var handle = WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(path);
        try
        {
            var identity = ValidateRegularLeafHandle(handle, path);
            var length = RandomAccess.GetLength(handle);
            if (length < 0 || length > MaximumTrustedLeafBytes)
                throw new InvalidDataException(
                    $"Protected file exceeds the {MaximumTrustedLeafBytes:N0}-byte boundary limit: {Path.GetFileName(path)}");
            aggregateLeafBytes = checked(aggregateLeafBytes + length);
            if (aggregateLeafBytes > MaximumTrustedBoundaryBytes)
                throw new InvalidDataException(
                    $"Protected files exceed the {MaximumTrustedBoundaryBytes:N0}-byte aggregate boundary limit.");
            var contentHash = ComputeContentHash(handle, MaximumTrustedLeafBytes, cancellationToken);
            leaves.Add(path, new TrustedLeaf(
                path,
                isWriteTarget,
                expectedExists: true,
                expectedDirectory: false,
                handle,
                identity,
                contentHash));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static WindowsPathHandle.FileIdentity ValidateRegularLeafHandle(
        SafeFileHandle handle,
        string expectedPath)
    {
        var identity = WindowsPathHandle.GetIdentity(handle);
        if (identity.FileIndex == 0
            || identity.NumberOfLinks != 1
            || (identity.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("Protected files must be regular, single-link files.");
        }
        var canonical = Normalize(WindowsPathHandle.GetFinalPath(handle));
        if (!canonical.Equals(Normalize(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A protected file resolved outside its expected leaf path.");
        return identity;
    }

    private static WindowsPathHandle.FileIdentity ValidateDirectoryLeafHandle(
        SafeFileHandle handle,
        string expectedPath)
    {
        var identity = WindowsPathHandle.GetIdentity(handle);
        if (identity.FileIndex == 0
            || (identity.FileAttributes & FileAttributes.Directory) == 0
            || (identity.FileAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("A protected directory leaf must be a regular directory.");
        }
        var canonical = Normalize(WindowsPathHandle.GetFinalPath(handle));
        if (!canonical.Equals(Normalize(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A protected directory leaf resolved outside its expected path.");
        return identity;
    }

    private static byte[] ComputeContentHash(
        SafeFileHandle handle,
        long maximumBytes = MaximumTrustedLeafBytes,
        CancellationToken cancellationToken = default)
    {
        var length = RandomAccess.GetLength(handle);
        if (length < 0 || length > maximumBytes)
            throw new InvalidDataException($"Protected file exceeds the {maximumBytes:N0}-byte hash limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, buffer, offset);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            offset += read;
            if (offset > maximumBytes)
                throw new InvalidDataException($"Protected file grew beyond the {maximumBytes:N0}-byte hash limit.");
        }
        return hash.GetHashAndReset();
    }

    private static string FormatIdentity(WindowsPathHandle.FileIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileIndex:x16}";

    public static bool IsWorkshopContentPath(string path)
    {
        string[] segments;
        try
        {
            segments = Normalize(path).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Path containment check failed closed ({exception.GetType().Name}).");
            return false;
        }

        for (var index = 0; index + 4 < segments.Length; index++)
        {
            if (segments[index].Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                && segments[index + 1].Equals("workshop", StringComparison.OrdinalIgnoreCase)
                && segments[index + 2].Equals("content", StringComparison.OrdinalIgnoreCase)
                && segments[index + 3].Equals("294100", StringComparison.OrdinalIgnoreCase)
                && segments[index + 4].All(char.IsDigit))
            {
                return true;
            }
        }
        return false;
    }

    public static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(pathRoot)
            && fullPath.Equals(pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void VerifyExistingRegularPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new InvalidDataException($"Could not verify a path component: {path}");
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException($"Writable paths must not contain redirected components: {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Could not verify a path component: {path}", exception);
        }
    }

    internal readonly record struct ExistingDirectorySnapshot(string CanonicalPath, string Identity);

    internal static class WindowsPathHandle
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;

        public static SafeFileHandle OpenDirectory(string path)
        {
            var handle = CreateFile(
                AddExtendedPrefix(path),
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The directory identity could not be opened.", new Win32Exception(error));
        }

        public static SafeFileHandle OpenDirectoryForIdentity(string path)
        {
            var handle = CreateFile(
                AddExtendedPrefix(path),
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The directory leaf identity could not be inspected.", new Win32Exception(error));
        }

        public static SafeFileHandle OpenFile(string path)
        {
            var handle = CreateFile(
                AddExtendedPrefix(path),
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The file identity could not be opened.", new Win32Exception(error));
        }

        public static SafeFileHandle OpenFileWithoutWriteOrDeleteSharing(string path)
        {
            var handle = CreateFile(
                AddExtendedPrefix(path),
                GenericRead | FileReadAttributes,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The protected file could not be locked.", new Win32Exception(error));
        }

        public static SafeFileHandle OpenFileForAtomicReplace(string path)
        {
            var handle = CreateFile(
                AddExtendedPrefix(path),
                GenericRead | FileReadAttributes,
                FileShareRead | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The prepared file could not be pinned for atomic replacement.", new Win32Exception(error));
        }

        public static SafeFileHandle OpenFileForIdentity(string path)
        {
            var handle = CreateFile(
                AddExtendedPrefix(path),
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The file identity could not be inspected.", new Win32Exception(error));
        }

        public static SafeFileHandle OpenDirectoryWithoutDeleteSharing(string path)
        {
            var handle = CreateFile(
                AddExtendedPrefix(path),
                FileReadAttributes,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The writable directory could not be locked.", new Win32Exception(error));
        }

        public static string GetFinalPath(SafeFileHandle handle)
        {
            var capacity = 512;
            while (capacity <= 32 * 1024)
            {
                var buffer = new char[capacity];
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
                if (length == 0)
                    throw new IOException("The canonical directory path could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
                if (length < buffer.Length) return RemoveExtendedPrefix(new string(buffer, 0, checked((int)length)));
                capacity = checked((int)length + 1);
            }
            throw new PathTooLongException("The canonical directory path exceeds the supported length.");
        }

        public static FileIdentity GetIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw new IOException("The directory identity could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
            return new FileIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
                information.NumberOfLinks,
                (FileAttributes)information.FileAttributes,
                DateTime.FromFileTimeUtc(
                    ((long)information.LastWriteTime.dwHighDateTime << 32)
                    | (uint)information.LastWriteTime.dwLowDateTime).Ticks);
        }

        private static string RemoveExtendedPrefix(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string dosPrefix = @"\\?\";
            if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path[uncPrefix.Length..];
            return path.StartsWith(dosPrefix, StringComparison.OrdinalIgnoreCase) ? path[dosPrefix.Length..] : path;
        }

        private static string AddExtendedPrefix(string path)
        {
            const string dosPrefix = @"\\?\";
            if (path.StartsWith(dosPrefix, StringComparison.OrdinalIgnoreCase)) return path;
            return path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..]
                : dosPrefix + path;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            [Out] char[] filePath,
            uint filePathLength,
            uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        internal readonly record struct FileIdentity(
            uint VolumeSerialNumber,
            ulong FileIndex,
            uint NumberOfLinks,
            FileAttributes FileAttributes,
            long LastWriteTimeUtcTicks);
    }

    internal sealed class TrustedLeaf(
        string path,
        bool isWriteTarget,
        bool expectedExists,
        bool expectedDirectory,
        SafeFileHandle? handle,
        WindowsPathHandle.FileIdentity identity,
        byte[]? contentHash) : IDisposable
    {
        private SafeFileHandle? handle = handle;

        public string Path { get; } = path;
        public bool IsWriteTarget { get; set; } = isWriteTarget;
        public bool ExpectedExists { get; private set; } = expectedExists;
        public bool ExpectedDirectory { get; private set; } = expectedDirectory;
        public WindowsPathHandle.FileIdentity Identity { get; private set; } = identity;
        public byte[]? ContentHash { get; private set; } = contentHash;
        public SafeFileHandle? Handle => handle;

        public void Adopt(
            SafeFileHandle adoptedHandle,
            SnapshotLeafFingerprint expectedFingerprint)
        {
            ArgumentNullException.ThrowIfNull(adoptedHandle);
            try
            {
                if (expectedFingerprint.Kind != SnapshotLeafKind.File
                    || expectedFingerprint.Sha256 is not { Length: 32 }
                    || !expectedFingerprint.VolumeSerialNumber.HasValue
                    || !expectedFingerprint.FileIndex.HasValue)
                {
                    throw new InvalidDataException("Adopted leaf evidence must identify one exact regular file.");
                }
                var adoptedIdentity = ValidateRegularLeafHandle(adoptedHandle, Path);
                var adoptedHash = ComputeContentHash(adoptedHandle);
                if (adoptedIdentity.VolumeSerialNumber != expectedFingerprint.VolumeSerialNumber.Value
                    || adoptedIdentity.FileIndex != expectedFingerprint.FileIndex.Value
                    || RandomAccess.GetLength(adoptedHandle) != expectedFingerprint.Length
                    || !CryptographicOperations.FixedTimeEquals(adoptedHash, expectedFingerprint.Sha256))
                {
                    throw new ConcurrentLeafChangeException(
                        "The committed leaf did not match its exact prepared evidence.",
                        Path);
                }
                if (Interlocked.CompareExchange(ref handle, adoptedHandle, null) is not null)
                    throw new InvalidOperationException("The protected leaf already owns a lock handle.");
                Identity = adoptedIdentity;
                ContentHash = adoptedHash;
                ExpectedExists = true;
                ExpectedDirectory = false;
            }
            catch
            {
                adoptedHandle.Dispose();
                throw;
            }
        }

        public void Release() => Interlocked.Exchange(ref handle, null)?.Dispose();
        public void Dispose() => Release();
    }

    internal sealed class TrustedWriteBoundary(
        List<SafeFileHandle> handles,
        List<FileStream> guardFiles,
        Dictionary<string, TrustedLeaf> leaves) : IDisposable
    {
        private List<SafeFileHandle>? handles = handles;
        private List<FileStream>? guardFiles = guardFiles;
        private Dictionary<string, TrustedLeaf>? leaves = leaves;

        internal IReadOnlySet<string> ActiveGuardPaths
        {
            get
            {
                var ownedGuards = guardFiles
                    ?? throw new ObjectDisposedException(nameof(TrustedWriteBoundary));
                return ownedGuards
                    .Select(guard => Normalize(guard.Name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void VerifyUnchanged()
        {
            var ownedLeaves = leaves ?? throw new ObjectDisposedException(nameof(TrustedWriteBoundary));
            foreach (var leaf in ownedLeaves.Values) VerifyLeaf(leaf);
        }

        public bool TargetExisted(string path)
        {
            var leaf = GetWriteLeaf(path);
            VerifyLeaf(leaf);
            return leaf.ExpectedExists;
        }

        internal SnapshotLeafFingerprint CaptureCurrentWriteFingerprint(
            string path,
            CancellationToken cancellationToken = default)
        {
            var leaf = GetWriteLeaf(path);
            if (leaf.ExpectedDirectory)
                throw new IOException("A protected output leaf is occupied by a directory.");
            if (!leaf.ExpectedExists)
            {
                if (File.Exists(leaf.Path) || Directory.Exists(leaf.Path))
                    throw new ConcurrentLeafChangeException(
                        "A protected missing leaf appeared before commit resolution.",
                        leaf.Path);
                return new SnapshotLeafFingerprint(SnapshotLeafKind.Missing);
            }

            var handle = leaf.Handle
                ?? throw new InvalidOperationException("A committed leaf has no active evidence handle.");
            var identity = ValidateRegularLeafHandle(handle, leaf.Path);
            var length = RandomAccess.GetLength(handle);
            var hash = ComputeContentHash(handle, MaximumTrustedLeafBytes, cancellationToken);
            if (!SameFile(identity, leaf.Identity)
                || leaf.ContentHash is not { Length: 32 }
                || !CryptographicOperations.FixedTimeEquals(hash, leaf.ContentHash))
            {
                throw new ConcurrentLeafChangeException(
                    "A committed leaf changed before durable resolution.",
                    leaf.Path);
            }
            return CreateFileFingerprint(identity, length, hash);
        }

        internal byte[] ReadCurrentWriteBytes(
            string path,
            long maximumBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, MaximumTrustedLeafBytes);
            var leaf = GetWriteLeaf(path);
            if (!leaf.ExpectedExists || leaf.ExpectedDirectory)
                throw new FileNotFoundException("The protected committed file is not available.", leaf.Path);
            var handle = leaf.Handle
                ?? throw new InvalidOperationException("A committed leaf has no active evidence handle.");
            var identity = ValidateRegularLeafHandle(handle, leaf.Path);
            var length = RandomAccess.GetLength(handle);
            if (length < 0 || length > maximumBytes)
                throw new InvalidDataException(
                    $"The protected committed file exceeds the {maximumBytes:N0}-byte read limit.");
            var bytes = new byte[checked((int)length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
                if (read <= 0)
                    throw new EndOfStreamException("The protected committed file ended before its recorded length.");
                offset = checked(offset + read);
            }
            var hash = SHA256.HashData(bytes);
            if (!SameFile(identity, leaf.Identity)
                || leaf.ContentHash is not { Length: 32 }
                || !CryptographicOperations.FixedTimeEquals(hash, leaf.ContentHash))
            {
                throw new ConcurrentLeafChangeException(
                    "The protected committed file changed before its pinned readback.",
                    leaf.Path);
            }
            return bytes;
        }

        public void CopyTargetToNew(
            string path,
            string destinationPath,
            CancellationToken cancellationToken = default)
            => _ = CopyTargetToNewPinned(path, destinationPath, cancellationToken);

        internal SnapshotLeafFingerprint CopyTargetToNewPinned(
            string path,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            var leaf = GetWriteLeaf(path);
            VerifyLeaf(leaf);
            if (!leaf.ExpectedExists)
                throw new FileNotFoundException("The protected target did not exist when its boundary was acquired.", leaf.Path);

            var destination = Normalize(destinationPath);
            var parent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException("A prepared file has no parent directory.");
            var expectedParent = Path.GetDirectoryName(leaf.Path)!;
            if (!parent.Equals(expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Prepared files must be created beside their protected target.");

            using var sourceHandle = WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(leaf.Path);
            var sourceIdentity = ValidateRegularLeafHandle(sourceHandle, leaf.Path);
            if (!SameFile(sourceIdentity, leaf.Identity))
                throw new InvalidDataException("The protected target identity changed before it was copied.");
            using var source = new FileStream(sourceHandle, FileAccess.Read, 64 * 1024, isAsync: false);
            uint? destinationVolume = null;
            ulong? destinationFileIndex = null;
            var completed = false;
            try
            {
                using var destinationStream = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough);
                if (!AtomicTemporaryFiles.TryGetOwnedRegularFileIdentity(
                        destinationStream.SafeFileHandle,
                        out var volumeSerialNumber,
                        out var fileIndex))
                {
                    throw new IOException("The protected-copy destination could not be identity-pinned.");
                }
                destinationVolume = volumeSerialNumber;
                destinationFileIndex = fileIndex;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long copied = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    copied = checked(copied + read);
                    if (copied > MaximumTrustedLeafBytes)
                        throw new InvalidDataException(
                            $"Protected target exceeds the {MaximumTrustedLeafBytes:N0}-byte copy limit.");
                    hash.AppendData(buffer, 0, read);
                    destinationStream.Write(buffer, 0, read);
                }
                destinationStream.Flush(true);
                var contentHash = hash.GetHashAndReset();
                if (leaf.ContentHash is not { Length: 32 }
                    || !CryptographicOperations.FixedTimeEquals(contentHash, leaf.ContentHash))
                {
                    throw new InvalidDataException(
                        "The protected target content changed while it was copied.");
                }
                completed = true;
                return new SnapshotLeafFingerprint(
                    SnapshotLeafKind.File,
                    copied,
                    contentHash,
                    VolumeSerialNumber: volumeSerialNumber,
                    FileIndex: fileIndex);
            }
            finally
            {
                if (!completed && destinationVolume.HasValue && destinationFileIndex.HasValue)
                {
                    _ = AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                        destination,
                        destinationVolume.Value,
                        destinationFileIndex.Value);
                }
            }
        }

        public void CommitPreparedFile(string preparedPath, string targetPath, bool keepBackup)
            => CommitPreparedFile(preparedPath, targetPath, keepBackup, null);

        internal void CommitPreparedFile(
            string preparedPath,
            string targetPath,
            bool keepBackup,
            AtomicCommitRecoveryPlan? recoveryPlan)
            => CommitPreparedFile(
                preparedPath,
                targetPath,
                keepBackup,
                recoveryPlan,
                validatedPreparedFingerprint: null);

        internal void CommitPreparedFile(
            string preparedPath,
            string targetPath,
            bool keepBackup,
            AtomicCommitRecoveryPlan? recoveryPlan,
            SnapshotLeafFingerprint? validatedPreparedFingerprint)
        {
            var target = GetWriteLeaf(targetPath);
            var backup = GetWriteLeaf(Normalize(targetPath) + ".bak");
            VerifyLeaf(target);
            VerifyLeaf(backup);
            if (target.ExpectedDirectory || backup.ExpectedDirectory)
                throw new IOException("A protected output leaf is occupied by a directory.");

            var prepared = Normalize(preparedPath);
            var targetParent = Path.GetDirectoryName(target.Path)!;
            if (!Path.GetDirectoryName(prepared)!.Equals(targetParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A prepared file must be committed from the protected target directory.");
            ValidateRecoveryPlan(recoveryPlan, prepared, target.Path, backup.Path, keepBackup);
            WindowsPathHandle.FileIdentity preparedIdentity;
            long preparedLength;
            byte[] preparedHash;
            using (var preparedHandle = WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(prepared))
            {
                preparedIdentity = ValidateRegularLeafHandle(preparedHandle, prepared);
                preparedLength = RandomAccess.GetLength(preparedHandle);
                preparedHash = ComputeContentHash(preparedHandle);
            }
            var preparedFingerprint = CreateFileFingerprint(
                preparedIdentity,
                preparedLength,
                preparedHash);
            ValidateValidatedPreparedFingerprint(
                validatedPreparedFingerprint,
                prepared,
                preparedIdentity,
                preparedLength,
                preparedHash);
            ValidatePreparedRecoveryFingerprint(
                recoveryPlan,
                preparedIdentity,
                preparedLength,
                preparedHash);
            var targetFingerprint = CaptureTrustedFingerprint(target);
            AfterAtomicLeafLockReleasedTestHook?.Invoke(prepared);
            if (!target.ExpectedExists)
            {
                AfterAtomicLeafLockReleasedTestHook?.Invoke(target.Path);
                if (File.Exists(target.Path) || Directory.Exists(target.Path))
                    throw new ConcurrentLeafChangeException(
                        "A previously absent protected target appeared before commit.",
                        target.Path);
                using (var preparedEvidence = TryOpenEvidence(prepared, preparedIdentity, preparedHash))
                {
                    if (preparedEvidence is null)
                        throw new InvalidDataException(
                            "The prepared file changed after validation and was not committed.");
                }
                try
                {
                    var expectedPrepared = recoveryPlan?.PreparedFingerprint
                        ?? preparedFingerprint;
                    if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                            prepared,
                            target.Path,
                            expectedPrepared,
                            out var committedHandle))
                    {
                        throw new ConcurrentLeafChangeException(
                            "The prepared output changed before handle-bound commit.",
                            prepared,
                            target.Path);
                    }
                    try
                    {
                        AfterAtomicCommitBeforeEvidencePinnedTestHook?.Invoke(target.Path);
                        target.Adopt(committedHandle!, expectedPrepared);
                        committedHandle = null;
                    }
                    catch
                    {
                        committedHandle?.Dispose();
                        if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                                target.Path,
                                prepared,
                                expectedPrepared))
                        {
                            throw new ConcurrentLeafChangeException(
                                "The absent-target commit failed and its exact output could not be quarantined away from the canonical target.",
                                target.Path,
                                prepared);
                        }
                        throw;
                    }
                }
                catch (IOException exception) when (
                    exception is not ConcurrentLeafChangeException
                    && (File.Exists(target.Path) || Directory.Exists(target.Path)))
                {
                    throw new ConcurrentLeafChangeException(
                        "A protected target appeared during commit.",
                        exception,
                        target.Path);
                }
                AfterAtomicEvidencePinnedTestHook?.Invoke(target.Path);
                return;
            }

            var displacedTarget = recoveryPlan?.DisplacedPath ?? Path.Combine(
                targetParent,
                $".{Path.GetFileName(target.Path)}.{Guid.NewGuid():N}.displaced.tmp");
            var rejectedOutput = recoveryPlan?.RejectedPath ?? Path.Combine(
                targetParent,
                $".{Path.GetFileName(target.Path)}.{Guid.NewGuid():N}.rejected.tmp");
            var targetReplaced = false;
            var preserveRecoveryLeaves = false;
            SafeFileHandle? targetReplaceEvidence = null;
            if (recoveryPlan is null)
            {
                targetReplaceEvidence = OpenAtomicReplaceEvidence(
                    target.Path,
                    target.Identity,
                    targetFingerprint.Length,
                    target.ContentHash!);
            }
            target.Release();
            AfterAtomicLeafLockReleasedTestHook?.Invoke(target.Path);
            try
            {
                using (var preparedEvidence = TryOpenEvidence(prepared, preparedIdentity, preparedHash))
                {
                    if (preparedEvidence is null)
                        throw new InvalidDataException(
                            "The prepared file changed after validation and was not committed.");
                }
                if (recoveryPlan is not null)
                {
                    if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                            target.Path,
                            displacedTarget,
                            recoveryPlan.TargetBefore))
                    {
                        throw new ConcurrentLeafChangeException(
                            "The protected target changed before handle-bound displacement.",
                            target.Path,
                            displacedTarget);
                    }
                    targetReplaced = true;
                    AfterAtomicTargetDisplacedTestHook?.Invoke(target.Path);
                    if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                            prepared,
                            target.Path,
                            recoveryPlan.PreparedFingerprint!))
                    {
                        throw new ConcurrentLeafChangeException(
                            "The prepared output changed before handle-bound replacement.",
                            prepared,
                            target.Path,
                            displacedTarget);
                    }
                }
                else
                {
                    try
                    {
                        using (OpenAtomicReplaceEvidence(
                                   prepared,
                                   preparedIdentity,
                                   preparedLength,
                                   preparedHash))
                        {
                            AfterAtomicFinalEvidenceReleasedTestHook?.Invoke(prepared);
                        }
                        try
                        {
                            File.Replace(
                                prepared,
                                target.Path,
                                displacedTarget,
                                ignoreMetadataErrors: true);
                        }
                        finally
                        {
                            targetReplaceEvidence?.Dispose();
                            targetReplaceEvidence = null;
                        }
                    }
                    catch (IOException exception) when (!PathHasEvidence(target.Path, target.Identity, target.ContentHash!))
                    {
                        throw new ConcurrentLeafChangeException(
                            "The protected target changed before atomic replacement.",
                            exception,
                            target.Path);
                    }
                }
                targetReplaced = true;
                AfterAtomicCommitBeforeEvidencePinnedTestHook?.Invoke(target.Path);
                var committedHandle = TryOpenEvidence(target.Path, preparedIdentity, preparedHash);
                var displacedMatchesTarget = PathHasEvidence(
                    displacedTarget,
                    target.Identity,
                    target.ContentHash!);
                if (committedHandle is null || !displacedMatchesTarget)
                {
                    committedHandle?.Dispose();
                    if (committedHandle is null)
                    {
                        preserveRecoveryLeaves = true;
                        targetReplaced = false;
                        if (displacedMatchesTarget)
                        {
                            try
                            {
                                var concurrentTarget = FileSnapshotJournal.CaptureRecoveryFingerprint(
                                    target.Path);
                                RestoreOwnedLeaf(
                                    displacedTarget,
                                    targetFingerprint,
                                    target.Path,
                                    rejectedOutput,
                                    concurrentTarget);
                            }
                            catch (Exception repairException) when (repairException is IOException or UnauthorizedAccessException)
                            {
                                throw new ConcurrentLeafChangeException(
                                    "The forged committed target could not be fully quarantined; every known recovery leaf was preserved.",
                                    repairException,
                                    target.Path,
                                    displacedTarget,
                                    rejectedOutput);
                            }
                            throw new ConcurrentLeafChangeException(
                                $"The forged committed target was quarantined at '{rejectedOutput}' and the original target was restored.",
                                target.Path,
                                rejectedOutput);
                        }
                        QuarantineCurrentLeaf(
                            target.Path,
                            rejectedOutput,
                            displacedTarget);
                        throw new ConcurrentLeafChangeException(
                            "The committed target and displaced leaf both diverged; the canonical target was quarantined and every known recovery leaf was preserved.",
                            target.Path,
                            displacedTarget,
                            rejectedOutput);
                    }
                    if (recoveryPlan is not null)
                    {
                        preserveRecoveryLeaves = true;
                        targetReplaced = false;
                        throw new ConcurrentLeafChangeException(
                            "The displaced target identity changed during handle-bound commit; all leaves were preserved.",
                            target.Path,
                            displacedTarget);
                    }
                    var concurrentDisplaced = FileSnapshotJournal.CaptureRecoveryFingerprint(
                        displacedTarget);
                    RestoreOwnedLeaf(
                        displacedTarget,
                        concurrentDisplaced,
                        target.Path,
                        rejectedOutput,
                        preparedFingerprint);
                    targetReplaced = false;
                    preserveRecoveryLeaves = true;
                    throw new ConcurrentLeafChangeException(
                        $"The concurrently displaced target was restored and the rejected output was preserved at '{rejectedOutput}'.",
                        target.Path,
                        rejectedOutput);
                }
                target.Adopt(
                    committedHandle,
                    recoveryPlan?.PreparedFingerprint ?? preparedFingerprint);
                AfterAtomicEvidencePinnedTestHook?.Invoke(target.Path);

                if (keepBackup)
                {
                    CommitDeterministicBackup(
                        displacedTarget,
                        target,
                        backup,
                        rejectedOutput,
                        recoveryPlan,
                        targetFingerprint,
                        preparedFingerprint);
                    targetReplaced = false;
                }
                else
                {
                    DeleteOwnedLeaf(
                        displacedTarget,
                        targetFingerprint);
                    targetReplaced = false;
                }
            }
            catch
            {
                if (targetReplaced && File.Exists(displacedTarget))
                {
                    target.Release();
                    try
                    {
                        RestoreOwnedLeaf(
                            displacedTarget,
                            recoveryPlan?.TargetBefore ?? targetFingerprint,
                            target.Path,
                            rejectedOutput,
                            recoveryPlan?.PreparedFingerprint ?? preparedFingerprint);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        preserveRecoveryLeaves = true;
                        System.Diagnostics.Trace.TraceError(
                            $"Atomic target rollback failed ({exception.GetType().Name}); displaced data was preserved.");
                    }
                }
                throw;
            }
            finally
            {
                targetReplaceEvidence?.Dispose();
                if (!preserveRecoveryLeaves)
                {
                    DeleteOwnedLeaf(rejectedOutput, preparedFingerprint);
                    DeleteOwnedLeaf(displacedTarget, targetFingerprint);
                }
            }
        }

        private static void CommitDeterministicBackup(
            string displacedTarget,
            TrustedLeaf target,
            TrustedLeaf backup,
            string rejectedOutput,
            AtomicCommitRecoveryPlan? recoveryPlan,
            SnapshotLeafFingerprint targetFingerprint,
            SnapshotLeafFingerprint preparedFingerprint)
        {
            var priorBackup = recoveryPlan?.PriorBackupPath ?? Path.Combine(
                Path.GetDirectoryName(backup.Path)!,
                $".{Path.GetFileName(backup.Path)}.{Guid.NewGuid():N}.prior.tmp");
            var originalRecovery = recoveryPlan?.OriginalRecoveryPath ?? Path.Combine(
                Path.GetDirectoryName(backup.Path)!,
                $".{Path.GetFileName(backup.Path)}.{Guid.NewGuid():N}.original.tmp");
            var preservePriorBackup = false;
            var preserveOriginalRecovery = false;
            var backupFingerprint = CaptureTrustedFingerprint(backup);
            var priorBackupDisplaced = false;
            var originalInstalledAsBackup = false;
            backup.Release();
            try
            {
                if (backup.ExpectedExists)
                {
                    if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                            backup.Path,
                            priorBackup,
                            recoveryPlan?.BackupBefore ?? backupFingerprint))
                    {
                        throw new ConcurrentLeafChangeException(
                            "The deterministic backup changed before handle-bound displacement.",
                            backup.Path,
                            priorBackup);
                    }
                    priorBackupDisplaced = true;
                    AfterAtomicLeafLockReleasedTestHook?.Invoke(backup.Path);
                    AfterAtomicBackupDisplacedTestHook?.Invoke(backup.Path);
                }
                else
                {
                    AfterAtomicLeafLockReleasedTestHook?.Invoke(backup.Path);
                    if (File.Exists(backup.Path) || Directory.Exists(backup.Path))
                    {
                        throw new ConcurrentLeafChangeException(
                            "A previously absent deterministic backup appeared before commit.",
                            backup.Path);
                    }
                }

                if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                        displacedTarget,
                        backup.Path,
                        recoveryPlan?.TargetBefore ?? targetFingerprint,
                        out var committedBackupHandle))
                {
                    throw new ConcurrentLeafChangeException(
                        "The displaced target changed before handle-bound backup commit.",
                        displacedTarget,
                        backup.Path,
                        priorBackup);
                }
                originalInstalledAsBackup = true;
                backup.Adopt(
                    committedBackupHandle!,
                    recoveryPlan?.TargetBefore ?? targetFingerprint);
                committedBackupHandle = null;
                AfterAtomicCommitBeforeEvidencePinnedTestHook?.Invoke(backup.Path);
                AfterAtomicEvidencePinnedTestHook?.Invoke(backup.Path);

                if (priorBackupDisplaced)
                {
                    DeleteOwnedLeaf(priorBackup, backupFingerprint);
                    priorBackupDisplaced = false;
                }
            }
            catch (Exception operationException)
            {
                backup.Release();
                try
                {
                    var originalPath = originalInstalledAsBackup
                        ? backup.Path
                        : displacedTarget;
                    if (File.Exists(originalPath))
                    {
                        target.Release();
                        RestoreOwnedLeaf(
                            originalPath,
                            targetFingerprint,
                            target.Path,
                            rejectedOutput,
                            preparedFingerprint);
                        originalInstalledAsBackup = false;
                    }
                    if (priorBackupDisplaced)
                    {
                        if (File.Exists(backup.Path) || Directory.Exists(backup.Path))
                        {
                            preservePriorBackup = true;
                        }
                        else if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                                     priorBackup,
                                     backup.Path,
                                     backupFingerprint))
                        {
                            preservePriorBackup = true;
                            throw new ConcurrentLeafChangeException(
                                "The prior deterministic backup could not be restored by its exact handle evidence.",
                                priorBackup,
                                backup.Path);
                        }
                        else
                        {
                            priorBackupDisplaced = false;
                        }
                    }
                }
                catch (Exception recoveryException) when (recoveryException is IOException or UnauthorizedAccessException)
                {
                    preservePriorBackup |= File.Exists(priorBackup);
                    preserveOriginalRecovery |= File.Exists(originalRecovery);
                    throw new ConcurrentLeafChangeException(
                        "The deterministic backup commit failed and exact rollback was incomplete; all known recovery leaves were preserved.",
                        new AggregateException(operationException, recoveryException),
                        target.Path,
                        backup.Path,
                        displacedTarget,
                        rejectedOutput,
                        priorBackup,
                        originalRecovery);
                }
                throw;
            }
            finally
            {
                if (!preservePriorBackup)
                    DeleteOwnedLeaf(priorBackup, backupFingerprint);
                if (!preserveOriginalRecovery)
                    DeleteOwnedLeaf(originalRecovery, targetFingerprint);
            }
        }

        private static void RestoreOwnedLeaf(
            string displacedPath,
            SnapshotLeafFingerprint displacedFingerprint,
            string targetPath,
            string rejectedPath,
            SnapshotLeafFingerprint? targetFingerprint)
        {
            if (File.Exists(targetPath))
            {
                if (targetFingerprint is null)
                {
                    throw new ConcurrentLeafChangeException(
                        "Atomic rollback preserved an unknown target instead of overwriting it.",
                        targetPath,
                        displacedPath);
                }
                BeforeAtomicRollbackHandleMoveTestHook?.Invoke(targetPath);
                if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                        targetPath,
                        rejectedPath,
                        targetFingerprint))
                {
                    throw new ConcurrentLeafChangeException(
                        "Atomic rollback preserved an unknown target instead of overwriting it.",
                        targetPath,
                        displacedPath);
                }
            }
            else if (Directory.Exists(targetPath))
            {
                throw new IOException("Atomic rollback target is occupied by a directory.");
            }
            BeforeAtomicRollbackHandleMoveTestHook?.Invoke(displacedPath);
            if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                    displacedPath,
                    targetPath,
                    displacedFingerprint))
            {
                throw new ConcurrentLeafChangeException(
                    "Atomic rollback source changed before handle-bound restoration.",
                    displacedPath,
                    targetPath);
            }
        }

        private static void ValidateRecoveryPlan(
            AtomicCommitRecoveryPlan? plan,
            string prepared,
            string target,
            string backup,
            bool keepBackup)
        {
            if (plan is null) return;
            if (!Normalize(plan.PreparedPath).Equals(prepared, StringComparison.OrdinalIgnoreCase)
                || !Normalize(plan.TargetPath).Equals(target, StringComparison.OrdinalIgnoreCase)
                || !Normalize(plan.BackupPath).Equals(backup, StringComparison.OrdinalIgnoreCase)
                || plan.KeepBackup != keepBackup)
            {
                throw new InvalidDataException("The durable atomic-commit reservation does not match the protected leaf.");
            }
            var parent = Path.GetDirectoryName(target)!;
            var paths = new[]
            {
                plan.PreparedPath,
                plan.DisplacedPath,
                plan.RejectedPath,
                plan.PriorBackupPath,
                plan.OriginalRecoveryPath
            }.Where(path => path is not null).Cast<string>().Select(Normalize).ToArray();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length
                || paths.Any(path => !Path.GetDirectoryName(path)!.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The durable atomic-commit reservation contains an escaped or duplicate path.");
            }
            foreach (var auxiliary in paths.Where(path => !path.Equals(prepared, StringComparison.OrdinalIgnoreCase)))
            {
                if (File.Exists(auxiliary) || Directory.Exists(auxiliary))
                    throw new ConcurrentLeafChangeException(
                        "A reserved atomic-commit intermediate appeared before mutation.",
                        auxiliary);
            }
        }

        private static void ValidatePreparedRecoveryFingerprint(
            AtomicCommitRecoveryPlan? plan,
            WindowsPathHandle.FileIdentity identity,
            long length,
            byte[] hash)
        {
            if (plan?.PreparedFingerprint is not { } expected) return;
            if (expected.Kind != SnapshotLeafKind.File
                || expected.VolumeSerialNumber != identity.VolumeSerialNumber
                || expected.FileIndex != identity.FileIndex
                || expected.Length != length
                || expected.Sha256 is not { Length: 32 }
                || !CryptographicOperations.FixedTimeEquals(expected.Sha256, hash))
            {
                throw new ConcurrentLeafChangeException(
                    "The prepared output changed after durable readiness was published.",
                    plan.PreparedPath);
            }
        }

        private static void ValidateValidatedPreparedFingerprint(
            SnapshotLeafFingerprint? expected,
            string path,
            WindowsPathHandle.FileIdentity identity,
            long length,
            byte[] hash)
        {
            if (expected is null) return;
            if (expected.Kind != SnapshotLeafKind.File
                || expected.VolumeSerialNumber != identity.VolumeSerialNumber
                || expected.FileIndex != identity.FileIndex
                || expected.Length != length
                || expected.Sha256 is not { Length: 32 }
                || !CryptographicOperations.FixedTimeEquals(expected.Sha256, hash))
            {
                throw new ConcurrentLeafChangeException(
                    "The prepared output no longer matches its validated creation evidence.",
                    path);
            }
        }

        private static SnapshotLeafFingerprint CaptureTrustedFingerprint(TrustedLeaf leaf)
        {
            if (!leaf.ExpectedExists)
                return new SnapshotLeafFingerprint(SnapshotLeafKind.Missing);
            if (leaf.ExpectedDirectory)
                throw new IOException("A protected output leaf is occupied by a directory.");
            var handle = leaf.Handle
                ?? throw new InvalidOperationException("A protected file leaf has no active evidence handle.");
            return CreateFileFingerprint(
                leaf.Identity,
                RandomAccess.GetLength(handle),
                leaf.ContentHash
                ?? throw new InvalidDataException("A protected file leaf has no content evidence."));
        }

        private static SnapshotLeafFingerprint CreateFileFingerprint(
            WindowsPathHandle.FileIdentity identity,
            long length,
            byte[] hash) => new(
            SnapshotLeafKind.File,
            length,
            hash,
            identity.LastWriteTimeUtcTicks,
            VolumeSerialNumber: identity.VolumeSerialNumber,
            FileIndex: identity.FileIndex);

        private static SafeFileHandle OpenAtomicReplaceEvidence(
            string path,
            WindowsPathHandle.FileIdentity expectedIdentity,
            long expectedLength,
            byte[] expectedHash)
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = WindowsPathHandle.OpenFileForAtomicReplace(path);
                var actualIdentity = ValidateRegularLeafHandle(handle, path);
                if (!SameFile(actualIdentity, expectedIdentity)
                    || RandomAccess.GetLength(handle) != expectedLength
                    || !CryptographicOperations.FixedTimeEquals(
                        ComputeContentHash(handle),
                        expectedHash))
                {
                    throw new ConcurrentLeafChangeException(
                        "The prepared output changed before its atomic replacement handle was pinned.",
                        path);
                }
                var result = handle
                    ?? throw new InvalidOperationException("Atomic replacement evidence handle was not opened.");
                handle = null;
                return result;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        private static void QuarantineCurrentLeaf(
            string path,
            string rejectedPath,
            params string[] recoveryPaths)
        {
            var current = FileSnapshotJournal.CaptureRecoveryFingerprint(path);
            if (current.Kind == SnapshotLeafKind.Missing) return;
            if (current.Kind == SnapshotLeafKind.File
                && AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                    path,
                    rejectedPath,
                    current))
            {
                return;
            }
            throw new ConcurrentLeafChangeException(
                "The unknown canonical leaf could not be quarantined without overwriting it.",
                [path, rejectedPath, .. recoveryPaths]);
        }

        private static bool PathHasEvidence(
            string path,
            WindowsPathHandle.FileIdentity expected,
            byte[] expectedHash)
        {
            if (!File.Exists(path) || Directory.Exists(path)) return false;
            try
            {
                using var handle = WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(path);
                var actual = ValidateRegularLeafHandle(handle, path);
                return SameFile(actual, expected)
                    && CryptographicOperations.FixedTimeEquals(
                        ComputeContentHash(handle),
                        expectedHash);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return false;
            }
        }

        private static SafeFileHandle? TryOpenEvidence(
            string path,
            WindowsPathHandle.FileIdentity expected,
            byte[] expectedHash)
        {
            if (!File.Exists(path) || Directory.Exists(path)) return null;
            SafeFileHandle? handle = null;
            try
            {
                handle = WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(path);
                var actual = ValidateRegularLeafHandle(handle, path);
                if (SameFile(actual, expected)
                    && CryptographicOperations.FixedTimeEquals(
                        ComputeContentHash(handle),
                        expectedHash))
                {
                    return handle;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Committed leaf evidence could not be pinned ({exception.GetType().Name}).");
            }
            handle?.Dispose();
            return null;
        }

        private static void DeleteOwnedLeaf(
            string path,
            SnapshotLeafFingerprint expected)
        {
            try
            {
                if (!File.Exists(path)) return;
                if (expected.Kind != SnapshotLeafKind.File
                    || expected.Sha256 is not { Length: 32 }
                    || !AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                        path,
                        expected.VolumeSerialNumber,
                        expected.FileIndex,
                        expected.Length,
                        expected.Sha256))
                {
                    throw new ConcurrentLeafChangeException(
                        "Atomic replacement cleanup preserved a leaf whose exact identity was not owned.",
                        path);
                }
            }
            catch (ConcurrentLeafChangeException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "Atomic replacement cleanup could not safely remove an owned recovery leaf.",
                    exception);
            }
        }

        public void ReleaseWriteLeafLocksForRollback()
        {
            var ownedLeaves = leaves ?? throw new ObjectDisposedException(nameof(TrustedWriteBoundary));
            foreach (var leaf in ownedLeaves.Values.Where(candidate => candidate.IsWriteTarget)) leaf.Release();
        }

        private TrustedLeaf GetWriteLeaf(string path)
        {
            var fullPath = Normalize(path);
            var ownedLeaves = leaves ?? throw new ObjectDisposedException(nameof(TrustedWriteBoundary));
            if (!ownedLeaves.TryGetValue(fullPath, out var leaf) || !leaf.IsWriteTarget)
                throw new InvalidOperationException("The file is not part of this trusted write boundary.");
            return leaf;
        }

        private void VerifyLeaf(TrustedLeaf leaf)
        {
            if (leaf.ExpectedDirectory)
            {
                var directoryHandle = leaf.Handle
                    ?? throw new InvalidOperationException("The protected directory leaf has already been released.");
                var directoryIdentity = ValidateDirectoryLeafHandle(directoryHandle, leaf.Path);
                if (!SameIdentity(directoryIdentity, leaf.Identity))
                    throw new InvalidDataException("A protected directory leaf identity changed after boundary acquisition.");
                using var directoryPathHandle = WindowsPathHandle.OpenDirectoryWithoutDeleteSharing(leaf.Path);
                var directoryPathIdentity = ValidateDirectoryLeafHandle(directoryPathHandle, leaf.Path);
                if (!SameIdentity(directoryPathIdentity, leaf.Identity))
                    throw new InvalidDataException("A protected path no longer names its locked directory leaf.");
                return;
            }
            if (!leaf.ExpectedExists)
            {
                if (File.Exists(leaf.Path) || Directory.Exists(leaf.Path))
                    throw new InvalidDataException("A protected leaf appeared after the boundary was acquired.");
                return;
            }

            var handle = leaf.Handle
                ?? throw new InvalidOperationException("The protected leaf has already entered its atomic replacement boundary.");
            var identity = ValidateRegularLeafHandle(handle, leaf.Path);
            if (!SameFile(identity, leaf.Identity))
                throw new InvalidDataException("A protected leaf identity changed after the boundary was acquired.");
            using var pathHandle = WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(leaf.Path);
            var pathIdentity = ValidateRegularLeafHandle(pathHandle, leaf.Path);
            if (!SameFile(pathIdentity, leaf.Identity))
                throw new InvalidDataException("A protected path no longer names its locked leaf.");
        }

        private static bool SameFile(
            WindowsPathHandle.FileIdentity left,
            WindowsPathHandle.FileIdentity right) =>
            left.VolumeSerialNumber == right.VolumeSerialNumber
            && left.FileIndex == right.FileIndex
            && left.NumberOfLinks == 1
            && right.NumberOfLinks == 1;

        private static bool SameIdentity(
            WindowsPathHandle.FileIdentity left,
            WindowsPathHandle.FileIdentity right) =>
            left.VolumeSerialNumber == right.VolumeSerialNumber
            && left.FileIndex == right.FileIndex;

        public void Dispose()
        {
            var ownedLeaves = Interlocked.Exchange(ref leaves, null);
            if (ownedLeaves is not null)
            {
                foreach (var leaf in ownedLeaves.Values) leaf.Dispose();
            }
            var owned = Interlocked.Exchange(ref handles, null);
            if (owned is not null)
            {
                for (var index = owned.Count - 1; index >= 0; index--) owned[index].Dispose();
            }
            var ownedGuards = Interlocked.Exchange(ref guardFiles, null);
            if (ownedGuards is null) return;
            for (var index = ownedGuards.Count - 1; index >= 0; index--) ownedGuards[index].Dispose();
        }
    }
}

internal sealed class ConcurrentLeafChangeException : IOException
{
    public ConcurrentLeafChangeException(string message, params string[] preservedPaths)
        : base(message)
    {
        PreservedPaths = new HashSet<string>(
            preservedPaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
    }

    public ConcurrentLeafChangeException(
        string message,
        Exception innerException,
        params string[] preservedPaths)
        : base(message, innerException)
    {
        PreservedPaths = new HashSet<string>(
            preservedPaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> PreservedPaths { get; }
}
