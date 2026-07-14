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
        AcquireTrustedBoundary(root, targetFiles, [], createTargetDirectories: true, cancellationToken);

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
        AcquireTrustedBoundary(root, targetFiles, protectedFiles, createTargetDirectories: true, cancellationToken);

    internal static TrustedWriteBoundary AcquireTrustedReadBoundary(
        string root,
        IEnumerable<string> protectedFiles,
        CancellationToken cancellationToken = default) =>
        AcquireTrustedBoundary(root, [], protectedFiles, createTargetDirectories: false, cancellationToken);

    private static TrustedWriteBoundary AcquireTrustedBoundary(
        string root,
        IEnumerable<string> targetFiles,
        IEnumerable<string> protectedFiles,
        bool createTargetDirectories,
        CancellationToken cancellationToken)
    {
        var fullRoot = Normalize(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException("The trusted output root does not exist.");
        if (IsNetworkPath(fullRoot))
            throw new InvalidDataException("Writable output roots must use a local filesystem.");
        EnsureNoReparsePointsToVolumeRoot(fullRoot);
        var targets = targetFiles
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var inputs = protectedFiles
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsStrictlyInside(target, fullRoot))
                throw new InvalidDataException("A write target is outside the trusted root.");
            var parent = Path.GetDirectoryName(target)
                ?? throw new InvalidDataException("A write target has no parent directory.");
            if (!Directory.Exists(parent))
            {
                if (!createTargetDirectories)
                    throw new DirectoryNotFoundException("A write target parent directory does not exist.");
                Directory.CreateDirectory(parent);
            }
            EnsureNoReparsePoints(parent, fullRoot);
        }
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsStrictlyInside(input, fullRoot))
                throw new InvalidDataException("A protected input is outside the trusted root.");
            var parent = Path.GetDirectoryName(input)
                ?? throw new InvalidDataException("A protected input has no parent directory.");
            if (!Directory.Exists(parent))
                throw new DirectoryNotFoundException("A protected input directory is missing.");
            if (!File.Exists(input) || Directory.Exists(input))
                throw new FileNotFoundException("A protected input file is missing.", input);
            EnsureNoReparsePoints(parent, fullRoot);
        }

        return TrustedWriteBoundary.Capture(fullRoot, targets, inputs, cancellationToken);
    }

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


    internal sealed class TrustedWriteBoundary : IDisposable
    {
        private readonly string root;
        private readonly Dictionary<string, SimpleLeafState> leaves;
        private bool disposed;

        private TrustedWriteBoundary(
            string root,
            Dictionary<string, SimpleLeafState> leaves)
        {
            this.root = root;
            this.leaves = leaves;
        }

        internal static TrustedWriteBoundary Capture(
            string root,
            IReadOnlyList<string> targetFiles,
            IReadOnlyList<string> protectedFiles,
            CancellationToken cancellationToken)
        {
            var leaves = new Dictionary<string, SimpleLeafState>(StringComparer.OrdinalIgnoreCase);
            long aggregateBytes = 0;
            foreach (var target in targetFiles)
            {
                CaptureLeaf(target, isWriteTarget: true);
                CaptureLeaf(target + ".bak", isWriteTarget: true);
            }
            foreach (var input in protectedFiles)
            {
                CaptureLeaf(input, isWriteTarget: false);
                if (leaves[input].Initial.Kind != SnapshotLeafKind.File)
                    throw new FileNotFoundException("A protected input file is missing.", input);
            }
            return new TrustedWriteBoundary(root, leaves);

            void CaptureLeaf(string path, bool isWriteTarget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (leaves.TryGetValue(path, out var existing))
                {
                    existing.IsWriteTarget |= isWriteTarget;
                    return;
                }
                var fingerprint = CaptureFingerprint(path, cancellationToken);
                aggregateBytes = checked(aggregateBytes + fingerprint.Length);
                if (aggregateBytes > MaximumTrustedBoundaryBytes)
                    throw new InvalidDataException(
                        $"Protected files exceed the {MaximumTrustedBoundaryBytes:N0}-byte aggregate boundary limit.");
                leaves.Add(path, new SimpleLeafState(path, isWriteTarget, fingerprint));
            }
        }

        public void VerifyUnchanged()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var leaf in leaves.Values)
                VerifyLeaf(leaf, CancellationToken.None);
        }

        public bool TargetExisted(string path)
        {
            var leaf = RequireLeaf(path, requireWriteTarget: true);
            VerifyLeaf(leaf, CancellationToken.None);
            return leaf.Initial.Kind == SnapshotLeafKind.File;
        }

        internal SnapshotLeafFingerprint CaptureCurrentWriteFingerprint(
            string path,
            CancellationToken cancellationToken = default)
        {
            var leaf = RequireLeaf(path, requireWriteTarget: true);
            VerifyLeaf(leaf, cancellationToken);
            return CaptureFingerprint(leaf.Path, cancellationToken);
        }

        internal byte[] ReadCurrentWriteBytes(
            string path,
            long maximumBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            var leaf = RequireLeaf(path, requireWriteTarget: true);
            VerifyLeaf(leaf, cancellationToken);
            using var stream = new FileStream(
                leaf.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > maximumBytes)
                throw new InvalidDataException(
                    $"File exceeds the {maximumBytes:N0}-byte read limit: {Path.GetFileName(path)}");
            using var memory = new MemoryStream((int)Math.Min(stream.Length, int.MaxValue));
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total = checked(total + read);
                if (total > maximumBytes)
                    throw new InvalidDataException("A file grew beyond its read limit.");
                memory.Write(buffer, 0, read);
            }
            VerifyLeaf(leaf, cancellationToken);
            return memory.ToArray();
        }

        public void CopyTargetToNew(
            string path,
            string destinationPath,
            CancellationToken cancellationToken = default) =>
            _ = CopyTargetToNewPinned(path, destinationPath, cancellationToken);

        internal SnapshotLeafFingerprint CopyTargetToNewPinned(
            string path,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            var leaf = RequireLeaf(path, requireWriteTarget: true);
            VerifyLeaf(leaf, cancellationToken);
            if (leaf.Expected.Kind != SnapshotLeafKind.File)
                throw new FileNotFoundException("The requested source file is missing.", leaf.Path);
            var destination = Normalize(destinationPath);
            if (!IsStrictlyInside(destination, root))
                throw new InvalidDataException("A copied output is outside the trusted root.");
            AtomicFile.CopyFlushedBounded(leaf.Path, destination, MaximumTrustedLeafBytes, cancellationToken);
            VerifyLeaf(leaf, cancellationToken);
            return CaptureFingerprint(destination, cancellationToken);
        }

        public void CommitPreparedFile(
            string preparedPath,
            string targetPath,
            bool keepBackup)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var target = RequireLeaf(targetPath, requireWriteTarget: true);
            var backup = RequireLeaf(target.Path + ".bak", requireWriteTarget: true);
            VerifyLeaf(target, CancellationToken.None);
            VerifyLeaf(backup, CancellationToken.None);
            if (target.Expected.Kind == SnapshotLeafKind.Directory)
                throw new IOException("Refusing to replace a directory with a file.");
            if (target.Expected.Kind == SnapshotLeafKind.File
                && File.GetAttributes(target.Path).HasFlag(FileAttributes.ReadOnly))
            {
                throw new UnauthorizedAccessException("Refusing to replace a read-only file.");
            }

            var prepared = Normalize(preparedPath);
            var targetDirectory = Path.GetDirectoryName(target.Path)
                ?? throw new InvalidDataException("A target file has no parent directory.");
            var preparedDirectory = Path.GetDirectoryName(prepared)
                ?? throw new InvalidDataException("A prepared file has no parent directory.");
            if (!preparedDirectory.Equals(targetDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A prepared file must be a same-volume sibling of its target.");
            var preparedFingerprint = CaptureFingerprint(prepared, CancellationToken.None);
            if (preparedFingerprint.Kind != SnapshotLeafKind.File)
                throw new InvalidDataException("A prepared output is not a regular file.");
            if (target.Expected.Kind == SnapshotLeafKind.File)
            {
                var previousTarget = target.Expected;
                File.Replace(
                    prepared,
                    target.Path,
                    keepBackup ? backup.Path : null,
                    ignoreMetadataErrors: true);
                FileTransactionRecoverySession.RecordApplied(target.Path, preparedFingerprint);
                if (keepBackup)
                    FileTransactionRecoverySession.RecordApplied(backup.Path, previousTarget);
            }
            else
            {
                File.Move(prepared, target.Path);
                FileTransactionRecoverySession.RecordApplied(target.Path, preparedFingerprint);
            }

            target.Expected = CaptureFingerprint(target.Path, CancellationToken.None);
            backup.Expected = CaptureFingerprint(backup.Path, CancellationToken.None);
        }

        public void ReleaseWriteLeafLocksForRollback()
        {
            // The simplified boundary does not hold long-lived leaf or directory handles.
        }

        public void Dispose() => disposed = true;

        private SimpleLeafState RequireLeaf(string path, bool requireWriteTarget)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var normalized = Normalize(path);
            if (!leaves.TryGetValue(normalized, out var leaf)
                || requireWriteTarget && !leaf.IsWriteTarget)
            {
                throw new InvalidDataException("A file is outside the captured transaction target list.");
            }
            return leaf;
        }

        private static void VerifyLeaf(
            SimpleLeafState leaf,
            CancellationToken cancellationToken)
        {
            SnapshotLeafFingerprint current;
            try
            {
                current = CaptureFingerprint(leaf.Path, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                throw new ConcurrentLeafChangeException(
                    "A protected file could not be verified before use.",
                    exception,
                    leaf.Path);
            }
            if (SameState(current, leaf.Expected)) return;
            throw new ConcurrentLeafChangeException(
                "A protected file was modified by another program.",
                leaf.Path);
        }

        private static SnapshotLeafFingerprint CaptureFingerprint(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                if (File.Exists(path))
                    throw new InvalidDataException("A protected path is both a file and a directory.");
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new InvalidDataException("A protected directory is redirected.");
                return new SnapshotLeafFingerprint(
                    SnapshotLeafKind.Directory,
                    LastWriteTimeUtcTicks: Directory.GetLastWriteTimeUtc(path).Ticks);
            }
            if (!File.Exists(path)) return new SnapshotLeafFingerprint(SnapshotLeafKind.Missing);

            var fileAttributes = File.GetAttributes(path);
            if ((fileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException("A protected file is not a regular local file.");
            var beforeLength = new FileInfo(path).Length;
            var beforeWrite = File.GetLastWriteTimeUtc(path).Ticks;
            if (beforeLength < 0 || beforeLength > MaximumTrustedLeafBytes)
                throw new InvalidDataException(
                    $"Protected file exceeds the {MaximumTrustedLeafBytes:N0}-byte limit: {Path.GetFileName(path)}");
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total = checked(total + read);
                if (total > MaximumTrustedLeafBytes)
                    throw new InvalidDataException("A protected file grew beyond its size limit.");
                hash.AppendData(buffer, 0, read);
            }
            var afterLength = new FileInfo(path).Length;
            var afterWrite = File.GetLastWriteTimeUtc(path).Ticks;
            if (beforeLength != afterLength || beforeWrite != afterWrite || total != afterLength)
                throw new ConcurrentLeafChangeException(
                    "A protected file changed while it was being read.",
                    path);
            return new SnapshotLeafFingerprint(
                SnapshotLeafKind.File,
                total,
                hash.GetHashAndReset(),
                afterWrite);
        }

        private static bool SameState(
            SnapshotLeafFingerprint left,
            SnapshotLeafFingerprint right) =>
            left.Kind == right.Kind
            && left.Kind switch
            {
                SnapshotLeafKind.Missing => true,
                SnapshotLeafKind.Directory =>
                    left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks,
                SnapshotLeafKind.File => SameFileContent(left, right),
                _ => false
            };

        private static bool SameFileContent(
            SnapshotLeafFingerprint left,
            SnapshotLeafFingerprint right) =>
            left.Kind == SnapshotLeafKind.File
            && right.Kind == SnapshotLeafKind.File
            && left.Length == right.Length
            && left.Sha256 is { Length: 32 }
            && right.Sha256 is { Length: 32 }
            && CryptographicOperations.FixedTimeEquals(left.Sha256, right.Sha256);

        private sealed class SimpleLeafState(
            string path,
            bool isWriteTarget,
            SnapshotLeafFingerprint initial)
        {
            public string Path { get; } = path;
            public bool IsWriteTarget { get; set; } = isWriteTarget;
            public SnapshotLeafFingerprint Initial { get; } = initial;
            public SnapshotLeafFingerprint Expected { get; set; } = initial;
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
