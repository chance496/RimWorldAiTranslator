using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.App;

internal sealed class SingleInstanceLease : IDisposable
{
    internal const string LockFileName = ".rimworld-ai-translator.instance.lock";

    private Semaphore? semaphore;
    private FileStream? fileLease;
    private SafeFileHandle? rootLease;

    private SingleInstanceLease(
        Semaphore semaphore,
        FileStream fileLease,
        SafeFileHandle rootLease)
    {
        this.semaphore = semaphore;
        this.fileLease = fileLease;
        this.rootLease = rootLease;
        LockFilePath = fileLease.Name;
    }

    internal string LockFilePath { get; }

    internal static bool TryAcquire(
        string dataRoot,
        out SingleInstanceLease? lease,
        string? testSemaphoreScope = null,
        Action<string>? afterRootIdentityCapturedTestHook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var expectedRoot = PathSafety.Normalize(dataRoot);
        using var creationBoundary =
            PathSafety.AcquireTrustedDirectoryCreationBoundary(expectedRoot);
        PathSafety.EnsureNoReparsePointsToVolumeRoot(expectedRoot);
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(expectedRoot);
        if (!canonicalRoot.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The single-instance root is redirected.");
        var rootIdentity = CaptureRootIdentity(canonicalRoot);
        afterRootIdentityCapturedTestHook?.Invoke(canonicalRoot);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(canonicalRoot).ToUpperInvariant();
        var rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot)));
        var scopeSuffix = string.IsNullOrWhiteSpace(testSemaphoreScope)
            ? string.Empty
            : "-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(testSemaphoreScope)));
        var candidate = new Semaphore(
            initialCount: 1,
            maximumCount: 1,
            name: $@"Local\RimWorldAiTranslator-{rootHash}{scopeSuffix}");
        if (!candidate.WaitOne(0))
        {
            candidate.Dispose();
            lease = null;
            return false;
        }

        SafeFileHandle? acquiredRootLease = null;
        try
        {
            acquiredRootLease = OpenRootNamespaceLease(canonicalRoot, rootIdentity);
            var lockFilePath = Path.Combine(canonicalRoot, LockFileName);
            PathSafety.EnsureNoReparsePoints(lockFilePath, canonicalRoot);
            FileStream fileLease;
            try
            {
                fileLease = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                acquiredRootLease.Dispose();
                acquiredRootLease = null;
                _ = candidate.Release();
                candidate.Dispose();
                lease = null;
                return false;
            }

            try
            {
                ValidateLockFileHandle(fileLease.SafeFileHandle, lockFilePath);
            }
            catch
            {
                fileLease.Dispose();
                throw;
            }

            lease = new SingleInstanceLease(candidate, fileLease, acquiredRootLease);
            return true;
        }
        catch
        {
            acquiredRootLease?.Dispose();
            _ = candidate.Release();
            candidate.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref fileLease, null)?.Dispose();
        Interlocked.Exchange(ref rootLease, null)?.Dispose();
        var owned = Interlocked.Exchange(ref semaphore, null);
        if (owned is null) return;
        try
        {
            _ = owned.Release();
        }
        catch (SemaphoreFullException exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Single-instance semaphore release was skipped ({exception.GetType().Name}).");
        }
        finally
        {
            owned.Dispose();
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static HandleIdentity CaptureRootIdentity(string canonicalRoot)
    {
        using var handle = CreateFile(
            ToExtendedWindowsPath(canonicalRoot),
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                "The application data-root identity could not be captured.",
                new Win32Exception(error));
        }

        var identity = GetIdentity(handle);
        ValidateRootHandle(handle, canonicalRoot, identity);
        return identity;
    }

    private static SafeFileHandle OpenRootNamespaceLease(
        string canonicalRoot,
        HandleIdentity expectedIdentity)
    {
        var handle = CreateFile(
            ToExtendedWindowsPath(canonicalRoot),
            FileReadAttributes,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "The application data-root namespace could not be locked.",
                new Win32Exception(error));
        }

        try
        {
            var identity = GetIdentity(handle);
            ValidateRootHandle(handle, canonicalRoot, identity);
            if (identity.VolumeSerialNumber != expectedIdentity.VolumeSerialNumber
                || identity.FileIndex != expectedIdentity.FileIndex)
            {
                throw new InvalidDataException(
                    "The application data-root namespace changed before it could be locked.");
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ValidateRootHandle(
        SafeFileHandle handle,
        string canonicalRoot,
        HandleIdentity identity)
    {
        if (identity.FileIndex == 0
            || (identity.FileAttributes & FileAttributes.Directory) == 0
            || (identity.FileAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || !GetFinalPath(handle).Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The application data-root namespace did not resolve to its canonical directory.");
        }
    }

    private static void ValidateLockFileHandle(SafeFileHandle handle, string expectedPath)
    {
        var identity = GetIdentity(handle);
        if (identity.NumberOfLinks != 1
            || (identity.FileAttributes
                & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || !GetFinalPath(handle).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The single-instance lock file must be a regular single-link file.");
        }
    }

    private static HandleIdentity GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("The single-instance lease identity could not be read.");
        return new HandleIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks,
            (FileAttributes)information.FileAttributes);
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new char[capacity];
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                throw new IOException("The single-instance lease path could not be read.");
            if (length < buffer.Length)
                return Path.GetFullPath(RemoveExtendedPrefix(new string(buffer, 0, checked((int)length))));
            capacity = checked((int)length + 1);
        }
        throw new PathTooLongException("The single-instance lease path is too long.");
    }

    private static string RemoveExtendedPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? path[4..]
                : path;

    private static string ToExtendedWindowsPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

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

    private readonly record struct HandleIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        uint NumberOfLinks,
        FileAttributes FileAttributes);
}
