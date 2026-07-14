using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

internal static class AtomicTemporaryFiles
{
    internal static int LastMoveError { get; private set; }
    internal static string? LastMoveFinalPath { get; private set; }
    internal static Action<string>? BeforePinnedMoveTestHook { get; set; }
    internal static Action<string>? BeforePinnedDeleteTestHook { get; set; }
    private const int MaximumCandidatesPerTarget = 256;
    private const int MaximumInventoryEntriesPerTarget = 4_096;
    private static readonly HashSet<string> AllowedKinds =
        new(StringComparer.Ordinal)
        {
            "tmp",
            "restore.tmp",
            "discard.tmp"
        };

    internal static string CreatePath(string targetPath, string kind = "tmp")
    {
        if (!AllowedKinds.Contains(kind))
            throw new ArgumentException("Unsupported atomic temporary-file kind.", nameof(kind));
        var fullTarget = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTarget)
            ?? throw new InvalidOperationException("Atomic target has no parent directory.");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullTarget)}.p{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.{Guid.NewGuid():N}.{kind}");
    }

    internal static int CleanupExitedProcessFiles(string targetPath)
        => CleanupExitedProcessFiles(targetPath, protectedExactPaths: null);

    internal static int CleanupExitedProcessFiles(
        string targetPath,
        IReadOnlyCollection<string>? protectedExactPaths)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTarget);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (protectedExactPaths is not null)
        {
            foreach (var protectedPath in protectedExactPaths)
            {
                if (!string.IsNullOrWhiteSpace(protectedPath))
                    protectedPaths.Add(Path.GetFullPath(protectedPath));
            }
        }
        var prefix = "." + Path.GetFileName(fullTarget) + ".p";
        var candidates = new List<(string Path, SnapshotLeafFingerprint Fingerprint)>();
        var inventoryLimitReached = false;
        try
        {
            var inspected = 0;
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         prefix + "*",
                         SearchOption.TopDirectoryOnly))
            {
                inspected++;
                if (inspected > MaximumInventoryEntriesPerTarget)
                {
                    inventoryLimitReached = true;
                    break;
                }
                if (protectedPaths.Contains(Path.GetFullPath(path))) continue;
                if (!TryGetOwnerProcessId(Path.GetFileName(path), prefix, out var ownerProcessId)
                    || !OwnerProcessHasExited(ownerProcessId))
                {
                    continue;
                }
                if (candidates.Count >= MaximumCandidatesPerTarget)
                {
                    inventoryLimitReached = true;
                    break;
                }
                SnapshotLeafFingerprint fingerprint;
                try
                {
                    fingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine(
                        $"Atomic temporary-file ownership capture skipped ({exception.GetType().Name}).");
                    continue;
                }
                if (fingerprint.Kind != SnapshotLeafKind.File
                    || fingerprint.VolumeSerialNumber is null
                    || fingerprint.FileIndex is null
                    || fingerprint.Sha256 is not { Length: 32 })
                {
                    continue;
                }
                candidates.Add((path, fingerprint));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Atomic temporary-file inventory skipped ({exception.GetType().Name}).");
            return 0;
        }

        if (inventoryLimitReached)
            Debug.WriteLine("Atomic temporary-file cleanup stopped at its bounded inventory limit.");

        var removed = 0;
        foreach (var candidate in candidates)
        {
            if (protectedPaths.Contains(Path.GetFullPath(candidate.Path))) continue;
            if (!TryGetOwnerProcessId(Path.GetFileName(candidate.Path), prefix, out var ownerProcessId)
                || !OwnerProcessHasExited(ownerProcessId))
            {
                continue;
            }
            if (TryDeleteRegularFileByHandle(
                    candidate.Path,
                    candidate.Fingerprint.VolumeSerialNumber,
                    candidate.Fingerprint.FileIndex,
                    candidate.Fingerprint.Length,
                    candidate.Fingerprint.Sha256))
            {
                removed++;
            }
        }

        if (removed > 0)
            Debug.WriteLine($"Removed {removed.ToString(CultureInfo.InvariantCulture)} exited-process atomic temporary file(s).");
        return removed;
    }

    private static bool TryGetOwnerProcessId(string fileName, string prefix, out int processId)
    {
        processId = 0;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var remainder = fileName[prefix.Length..];
        var processSeparator = remainder.IndexOf('.');
        if (processSeparator <= 0
            || !int.TryParse(
                remainder.AsSpan(0, processSeparator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processId)
            || processId <= 0)
        {
            return false;
        }

        remainder = remainder[(processSeparator + 1)..];
        var identitySeparator = remainder.IndexOf('.');
        if (identitySeparator != 32
            || !Guid.TryParseExact(remainder[..identitySeparator], "N", out _))
        {
            return false;
        }

        return AllowedKinds.Contains(remainder[(identitySeparator + 1)..]);
    }

    private static bool OwnerProcessHasExited(int processId)
    {
        if (processId == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception exception)
        {
            Debug.WriteLine($"Atomic temporary-file owner probe skipped ({exception.GetType().Name}).");
            return false;
        }
    }

    internal static bool TryGetOwnedRegularFileIdentity(
        SafeFileHandle handle,
        out uint volumeSerialNumber,
        out ulong fileIndex)
    {
        volumeSerialNumber = 0;
        fileIndex = 0;
        if (!OperatingSystem.IsWindows() || handle.IsInvalid || handle.IsClosed) return false;
        if (!GetFileInformationByHandle(handle, out var information)) return false;
        const uint rejectedAttributes = FileAttributeDirectory | FileAttributeDevice | FileAttributeReparsePoint;
        if ((information.FileAttributes & rejectedAttributes) != 0 || information.NumberOfLinks != 1)
            return false;
        volumeSerialNumber = information.VolumeSerialNumber;
        fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return true;
    }

    internal static bool TryDeleteRegularFileByHandle(
        string path,
        uint? expectedVolumeSerialNumber,
        ulong? expectedFileIndex)
        => TryDeleteRegularFileByHandle(
            path,
            expectedVolumeSerialNumber,
            expectedFileIndex,
            expectedLength: null,
            expectedSha256: null);

    internal static bool TryDeleteRegularFileByHandle(
        string path,
        uint? expectedVolumeSerialNumber,
        ulong? expectedFileIndex,
        long? expectedLength,
        byte[]? expectedSha256)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var handle = CreateFile(
            ToExtendedWindowsPath(path),
            DeleteAccess | FileReadAttributes | GenericRead,
            shareMode: 0,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Debug.WriteLine("Atomic temporary-file exclusive open was unavailable.");
            return false;
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            Debug.WriteLine("Atomic temporary-file handle information was unavailable.");
            return false;
        }
        const uint rejectedAttributes = FileAttributeDirectory | FileAttributeDevice | FileAttributeReparsePoint;
        if ((information.FileAttributes & rejectedAttributes) != 0 || information.NumberOfLinks != 1)
            return false;
        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        if ((expectedVolumeSerialNumber.HasValue
             && information.VolumeSerialNumber != expectedVolumeSerialNumber.Value)
            || (expectedFileIndex.HasValue && fileIndex != expectedFileIndex.Value))
        {
            return false;
        }
        var fileLength = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (expectedLength.HasValue && fileLength != expectedLength.Value) return false;
        if (expectedSha256 is not null)
        {
            if (expectedSha256.Length != 32) return false;
            var actualHash = ComputeSha256(handle, fileLength);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedSha256)) return false;
        }

        BeforePinnedDeleteTestHook?.Invoke(path);
        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            Debug.WriteLine("Atomic temporary-file delete disposition was unavailable.");
            return false;
        }
        return true;
    }

    internal static bool TryMoveRegularFileByHandle(
        string sourcePath,
        string destinationPath,
        SnapshotLeafFingerprint expected)
    {
        var moved = TryMoveRegularFileByHandle(
            sourcePath,
            destinationPath,
            expected,
            out var movedHandle);
        movedHandle?.Dispose();
        return moved;
    }

    internal static bool TryMoveRegularFileByHandle(
        string sourcePath,
        string destinationPath,
        SnapshotLeafFingerprint expected,
        out SafeFileHandle? movedHandle)
    {
        movedHandle = null;
        LastMoveError = 0;
        LastMoveFinalPath = null;
        if (!OperatingSystem.IsWindows()
            || expected.Kind != SnapshotLeafKind.File
            || expected.Sha256 is not { Length: 32 }
            || File.Exists(destinationPath)
            || Directory.Exists(destinationPath))
        {
            LastMoveError = -1;
            return false;
        }
        SafeFileHandle? handle = CreateFile(
            ToExtendedWindowsPath(sourcePath),
            DeleteAccess | FileReadAttributes | GenericRead,
            shareMode: 0,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
            {
                LastMoveError = Marshal.GetLastWin32Error();
                return false;
            }
            const uint rejectedAttributes = FileAttributeDirectory | FileAttributeDevice | FileAttributeReparsePoint;
            var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            var fileLength = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
            if ((information.FileAttributes & rejectedAttributes) != 0
                || information.NumberOfLinks != 1
                || information.VolumeSerialNumber != expected.VolumeSerialNumber
                || fileIndex != expected.FileIndex
                || fileLength != expected.Length)
            {
                LastMoveError = -3;
                return false;
            }
            var actualHash = ComputeSha256(handle, fileLength);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expected.Sha256))
            {
                LastMoveError = -4;
                return false;
            }
            BeforePinnedMoveTestHook?.Invoke(sourcePath);
            var destinationFullPath = Path.GetFullPath(destinationPath);
            var destination = destinationFullPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\??\UNC\" + destinationFullPath[2..]
                : @"\??\" + destinationFullPath;
            var nameBytes = checked(destination.Length * sizeof(char));
            var nameOffset = Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.FileName)).ToInt32();
            var bufferSize = checked(nameOffset + nameBytes + sizeof(char));
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var header = new FileRenameInfo
                {
                    ReplaceIfExists = 0,
                    RootDirectory = IntPtr.Zero,
                    FileNameLength = (uint)nameBytes,
                    FileName = '\0'
                };
                Marshal.StructureToPtr(header, buffer, fDeleteOld: false);
                Marshal.Copy(destination.ToCharArray(), 0, buffer + nameOffset, destination.Length);
                Marshal.WriteInt16(buffer + nameOffset + nameBytes, 0);
                if (!SetFileInformationByHandle(
                        handle,
                        FileInfoByHandleClass.FileRenameInfo,
                        buffer,
                        (uint)bufferSize))
                {
                    LastMoveError = Marshal.GetLastWin32Error();
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            LastMoveFinalPath = PathSafety.WindowsPathHandle.GetFinalPath(handle);
            movedHandle = handle;
            handle = null;
            return true;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static byte[] ComputeSha256(SafeFileHandle handle, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            var read = RandomAccess.Read(
                handle,
                buffer,
                offset);
            if (read <= 0)
                throw new EndOfStreamException("A pinned regular file ended before its recorded length.");
            hash.AppendData(buffer, 0, read);
            offset = checked(offset + read);
        }
        return hash.GetHashAndReset();
    }

    private static string ToExtendedWindowsPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private const uint DeleteAccess = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;

    private enum FileInfoByHandleClass
    {
        FileRenameInfo = 3,
        FileDispositionInfo = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInfo
    {
        public int ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);
}
