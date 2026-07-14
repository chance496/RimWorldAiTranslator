using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace RimWorldAiTranslator.Tooling;

internal sealed record PinnedFileDigest(string RelativePath, long Bytes, string Sha256);

internal sealed class PinnedFileTree : IDisposable
{
    private const uint FileListDirectory = 0x0001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private static readonly HashSet<string> RepositoryOutputDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "bin",
            "obj",
            "TestResults"
        };

    private static readonly HashSet<string> RepositoryOutputRoots =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            "artifacts",
            "dist",
            "reviews"
        };

    private readonly string root;
    private readonly string label;
    private readonly Func<string, bool> excludePath;
    private readonly bool requireExactFileSet;
    private readonly FileShare directoryShare;
    private readonly Dictionary<string, PinnedFile> files;
    private readonly Dictionary<string, PinnedDirectory> directories;
    private bool disposed;

    private PinnedFileTree(
        string root,
        string label,
        Func<string, bool> excludePath,
        bool requireExactFileSet,
        FileShare directoryShare,
        Dictionary<string, PinnedFile> files,
        Dictionary<string, PinnedDirectory> directories)
    {
        this.root = root;
        this.label = label;
        this.excludePath = excludePath;
        this.requireExactFileSet = requireExactFileSet;
        this.directoryShare = directoryShare;
        this.files = files;
        this.directories = directories;
        Snapshot = files.Values
            .Select(file => file.Digest)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<PinnedFileDigest> Snapshot { get; }

    public static PinnedFileTree CaptureRepositoryInputs(RepositoryLayout repository) =>
        Capture(
            repository.Root,
            "active repository inputs",
            IsRepositoryOutputDirectory,
            requireExactFileSet: true,
            allowDirectoryWrites: false);

    public static PinnedFileTree CaptureExact(string root, string label) =>
        Capture(
            root,
            label,
            static _ => false,
            requireExactFileSet: true,
            allowDirectoryWrites: false);

    public static PinnedFileTree CaptureExistingFilesAllowAdditions(string root, string label) =>
        Capture(
            root,
            label,
            static _ => false,
            requireExactFileSet: false,
            allowDirectoryWrites: true);

    public void Verify()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var pinned in directories.Values)
        {
            if (GetIdentity(pinned.Handle) != pinned.Identity)
            {
                WriteViolation("directory-handle-identity", Path.GetRelativePath(root, pinned.Path));
                throw new InvalidDataException($"A pinned {label} directory handle changed identity.");
            }
            using var observed = OpenDirectoryHandle(pinned.Path, directoryShare, label);
            if (GetIdentity(observed) != pinned.Identity)
            {
                WriteViolation("directory-path-identity", Path.GetRelativePath(root, pinned.Path));
                throw new InvalidDataException($"A pinned {label} directory path no longer resolves to its handle.");
            }
        }

        foreach (var pinned in files.Values)
        {
            if (GetIdentity(pinned.Stream.SafeFileHandle) != pinned.Identity)
            {
                WriteViolation("file-handle-identity", pinned.Digest.RelativePath);
                throw new InvalidDataException($"A pinned {label} file handle changed identity.");
            }
            using var observed = new FileStream(
                pinned.Stream.Name,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            AssertRegularFile(observed.Name, label);
            if (GetIdentity(observed.SafeFileHandle) != pinned.Identity)
            {
                WriteViolation("file-path-identity", pinned.Digest.RelativePath);
                throw new InvalidDataException($"A pinned {label} file path no longer resolves to its handle.");
            }

            pinned.Stream.Position = 0;
            var actualHash = Convert.ToHexString(SHA256.HashData(pinned.Stream)).ToLowerInvariant();
            if (pinned.Stream.Length != pinned.Digest.Bytes
                || !actualHash.Equals(pinned.Digest.Sha256, StringComparison.Ordinal))
            {
                WriteViolation("file-content", pinned.Digest.RelativePath);
                throw new InvalidDataException($"A pinned {label} file changed during packaging.");
            }
        }

        if (!requireExactFileSet) return;
        var observedNamespace = EnumerateNamespace(root, excludePath, label);
        if (!files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(observedNamespace.Files)
            || !directories.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(observedNamespace.Directories))
        {
            var expectedFiles = files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedDirectories = directories.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var difference = expectedFiles.Except(observedNamespace.Files, StringComparer.OrdinalIgnoreCase)
                .Select(path => "missing-file:" + path)
                .Concat(observedNamespace.Files.Except(expectedFiles, StringComparer.OrdinalIgnoreCase)
                    .Select(path => "added-file:" + path))
                .Concat(expectedDirectories.Except(observedNamespace.Directories, StringComparer.OrdinalIgnoreCase)
                    .Select(path => "missing-directory:" + path))
                .Concat(observedNamespace.Directories.Except(expectedDirectories, StringComparer.OrdinalIgnoreCase)
                    .Select(path => "added-directory:" + path))
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(8)
                .ToArray();
            WriteViolation("namespace", string.Join("|", difference));
            throw new InvalidDataException($"The pinned {label} file or directory set changed during packaging.");
        }
    }

    private void WriteViolation(string kind, string relativePath)
    {
        Console.Error.WriteLine(
            $"PINNED_INPUT_VIOLATION label={label}; kind={kind}; relative={relativePath}");
    }

    internal static void AssertSameSnapshot(
        IReadOnlyList<PinnedFileDigest> expected,
        IReadOnlyList<PinnedFileDigest> actual,
        string label)
    {
        var expectedByPath = expected.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var actualByPath = actual.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (!expectedByPath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actualByPath.Keys))
            throw new InvalidDataException($"The {label} file set does not match its snapshot.");
        foreach (var pair in expectedByPath)
        {
            var observed = actualByPath[pair.Key];
            if (pair.Value.Bytes != observed.Bytes
                || !pair.Value.Sha256.Equals(observed.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The {label} content does not match its snapshot.");
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var file in files.Values) file.Stream.Dispose();
        foreach (var directory in directories.Values
                     .OrderByDescending(value => value.Path.Length))
        {
            directory.Handle.Dispose();
        }
    }

    private static PinnedFileTree Capture(
        string root,
        string label,
        Func<string, bool> excludePath,
        bool requireExactFileSet,
        bool allowDirectoryWrites)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Pinned packaging trees require Windows file identities and directory handles.");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The {label} root is missing.");

        var files = new Dictionary<string, PinnedFile>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, PinnedDirectory>(StringComparer.OrdinalIgnoreCase);
        var directoryShare = allowDirectoryWrites
            ? FileShare.Read | FileShare.Write
            : FileShare.Read;
        try
        {
            var pending = new Stack<(string Path, string RelativePath)>();
            pending.Push((root, "."));
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                var directoryHandle = OpenDirectoryHandle(current.Path, directoryShare, label);
                var directoryIdentity = GetIdentity(directoryHandle);
                if (!directories.TryAdd(
                        current.RelativePath,
                        new PinnedDirectory(current.Path, directoryHandle, directoryIdentity)))
                {
                    directoryHandle.Dispose();
                    throw new InvalidDataException($"The {label} contains duplicate case-insensitive directories.");
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(
                             current.Path,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var relative = Path.GetRelativePath(root, entry);
                    if (excludePath(relative)) continue;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        AssertDirectory(entry, label);
                        pending.Push((Path.GetFullPath(entry), relative));
                        continue;
                    }

                    AssertRegularFile(entry, label);
                    var stream = new FileStream(
                        entry,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.SequentialScan);
                    try
                    {
                        AssertRegularFile(stream.Name, label);
                        var identity = GetIdentity(stream.SafeFileHandle);
                        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                        stream.Position = 0;
                        var digest = new PinnedFileDigest(relative, stream.Length, hash);
                        if (!files.TryAdd(relative, new PinnedFile(stream, identity, digest)))
                            throw new InvalidDataException($"The {label} contains duplicate case-insensitive paths.");
                        stream = null!;
                    }
                    finally
                    {
                        stream?.Dispose();
                    }
                }
            }

            if (files.Count == 0)
                throw new InvalidDataException($"The {label} snapshot contains no regular files.");
            var tree = new PinnedFileTree(
                root,
                label,
                excludePath,
                requireExactFileSet,
                directoryShare,
                files,
                directories);
            tree.Verify();
            return tree;
        }
        catch
        {
            foreach (var file in files.Values) file.Stream.Dispose();
            foreach (var directory in directories.Values) directory.Handle.Dispose();
            throw;
        }
    }

    private static ObservedNamespace EnumerateNamespace(
        string root,
        Func<string, bool> excludePath,
        string label)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "." };
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((root, "."));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            AssertDirectory(directory.Path, label);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path, "*", SearchOption.TopDirectoryOnly))
            {
                var relative = Path.GetRelativePath(root, entry);
                if (excludePath(relative)) continue;
                if (Directory.Exists(entry))
                {
                    AssertDirectory(entry, label);
                    if (!directories.Add(relative))
                        throw new InvalidDataException($"The {label} contains duplicate case-insensitive directories.");
                    pending.Push((entry, relative));
                    continue;
                }
                AssertRegularFile(entry, label);
                if (!files.Add(relative))
                    throw new InvalidDataException($"The {label} contains duplicate case-insensitive paths.");
            }
        }
        return new ObservedNamespace(files, directories);
    }

    private static bool IsRepositoryOutputDirectory(string relativePath)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0
               && (RepositoryOutputRoots.Contains(segments[0])
                   || segments.Any(RepositoryOutputDirectoryNames.Contains));
    }

    private static SafeFileHandle OpenDirectoryHandle(string path, FileShare share, string label)
    {
        AssertDirectory(path, label);
        var handle = CreateFile(
            Path.GetFullPath(path),
            FileListDirectory,
            (uint)share,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"The {label} directory namespace could not be pinned.",
                new Win32Exception(error));
        }
        try
        {
            AssertDirectory(path, label);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FileIdentity GetIdentity(SafeFileHandle handle)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
            throw new IOException("A pinned path identity could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new FileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static void AssertDirectory(string path, string label)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException($"The {label} contains a redirected or non-directory path.");
        }
    }

    private static void AssertRegularFile(string path, string label)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException($"The {label} contains a non-regular file.");
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
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex);
    private sealed record ObservedNamespace(HashSet<string> Files, HashSet<string> Directories);
    private sealed record PinnedFile(FileStream Stream, FileIdentity Identity, PinnedFileDigest Digest);
    private sealed record PinnedDirectory(string Path, SafeFileHandle Handle, FileIdentity Identity);
}
