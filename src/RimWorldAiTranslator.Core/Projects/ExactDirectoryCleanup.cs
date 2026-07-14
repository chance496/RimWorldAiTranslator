using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Projects;

internal static class ExactDirectoryCleanup
{
    private const int MaximumEntries = 32_768;
    private const int MaximumDepth = 64;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const long MaximumInventoryBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumQuarantineRenameAttempts = 8;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    internal static ExactDirectoryCleanupResult QuarantineAndDelete(
        string sourcePath,
        string expectedIdentity,
        Action<string>? beforeQuarantineTestHook = null,
        Action<string>? afterQuarantineInventoryTestHook = null,
        Func<int, int?>? quarantineRenameErrorTestHook = null)
    {
        var source = PathSafety.Normalize(sourcePath);
        var parent = Path.GetDirectoryName(source)
            ?? throw new InvalidDataException("A cleanup directory has no parent.");
        var quarantine = Path.Combine(parent, $".rimworld-ai-cleanup-{Guid.NewGuid():N}");
        SafeFileHandle? parentHandle = null;
        FileStream? parentNamespaceLease = null;
        SafeFileHandle? rootHandle = null;
        try
        {
            // The quarantine is a direct child of this exact, non-reparse parent. Holding
            // the parent handle pins its identity. Current Windows versions can still rename
            // that directory through a path operation, so a delete-on-close child lease is
            // also held without delete sharing to pin the parent namespace itself.
            parentHandle = OpenParentForQuarantine(parent);
            ValidatePinnedParentHandle(parentHandle, parent);
            parentNamespaceLease = OpenParentNamespaceLease(parent);
            ValidatePinnedParentHandle(parentHandle, parent);

            Dictionary<string, DirectoryEntryIdentity>? before = null;
            Exception? inventoryFailure = null;
            try
            {
                before = CaptureInventory(source, expectedIdentity, rootHandle: null, shareDelete: false);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or ArgumentException
                                              or NotSupportedException)
            {
                inventoryFailure = exception;
            }
            beforeQuarantineTestHook?.Invoke(source);
            ValidatePinnedParentHandle(parentHandle, parent);

            rootHandle = OpenDirectoryForDelete(source);
            ValidateDirectoryHandle(rootHandle, source, expectedIdentity);
            if (File.Exists(quarantine) || Directory.Exists(quarantine))
                return Failed(source, "The cleanup quarantine path was unexpectedly occupied.");

            var renamed = false;
            var renameError = 0;
            for (var attempt = 1; attempt <= MaximumQuarantineRenameAttempts; attempt++)
            {
                ValidateDirectoryHandle(rootHandle, source, expectedIdentity);
                ValidatePinnedParentHandle(parentHandle, parent);
                if (File.Exists(quarantine) || Directory.Exists(quarantine))
                    return Failed(source, "The cleanup quarantine path became occupied before rename.");

                var simulatedError = quarantineRenameErrorTestHook?.Invoke(attempt);
                renamed = simulatedError is null && TryRenameByHandle(rootHandle, quarantine);
                if (renamed) break;
                renameError = simulatedError ?? Marshal.GetLastWin32Error();
                if (!IsRetryableQuarantineRenameError(renameError)
                    || attempt == MaximumQuarantineRenameAttempts)
                {
                    break;
                }
                Thread.Sleep(Math.Min(25 << (attempt - 1), 200));
            }
            if (!renamed)
                return Failed(source, $"The verified directory could not be quarantined ({renameError}).");

            var finalPath = PathSafety.Normalize(PathSafety.WindowsPathHandle.GetFinalPath(rootHandle));
            if (!finalPath.Equals(PathSafety.Normalize(quarantine), StringComparison.OrdinalIgnoreCase))
            {
                return Failed(
                    finalPath,
                    "The verified directory moved to an unexpected physical path; it was preserved.");
            }
            ValidatePinnedParentHandle(parentHandle, parent);

            if (inventoryFailure is not null || before is null)
            {
                return Failed(
                    quarantine,
                    $"The directory inventory could not be proven safe ({inventoryFailure?.GetType().Name ?? "Unknown"}); the quarantine was preserved.");
            }

            var after = CaptureInventory(quarantine, expectedIdentity, rootHandle, shareDelete: false);
            if (!InventoriesMatch(before, after))
            {
                return Failed(
                    quarantine,
                    "The directory contents changed before quarantine was established; the quarantine was preserved.");
            }
            afterQuarantineInventoryTestHook?.Invoke(quarantine);

            var deletionHandles = new List<DeletionHandle>(after.Count);
            var pinnedBytes = 0L;
            try
            {
                foreach (var entry in after.Values
                             .OrderBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    var path = Path.Combine(quarantine, entry.RelativePath);
                    SafeFileHandle? handle = OpenEntryForDelete(path, entry.IsDirectory);
                    try
                    {
                        ValidateEntryHandle(handle, path, entry, ref pinnedBytes);
                        deletionHandles.Add(new DeletionHandle(entry, handle));
                        handle = null;
                    }
                    finally
                    {
                        handle?.Dispose();
                    }
                }

                var finalInventory = CaptureInventory(
                    quarantine,
                    expectedIdentity,
                    rootHandle,
                    shareDelete: true);
                if (!InventoriesMatch(after, finalInventory))
                {
                    return Failed(
                        quarantine,
                        "The quarantined directory contents changed before deletion; the quarantine was preserved.");
                }

                foreach (var owned in deletionHandles
                             .OrderByDescending(value => GetDepth(value.Entry.RelativePath))
                             .ThenBy(value => value.Entry.IsDirectory ? 1 : 0)
                             .ThenBy(value => value.Entry.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    var handle = owned.Handle
                        ?? throw new InvalidOperationException("A cleanup handle was already released.");
                    if (!TryMarkForDeletion(handle))
                    {
                        var error = Marshal.GetLastWin32Error();
                        return Failed(
                            quarantine,
                            $"A verified quarantined entry could not be deleted ({error}); the remaining quarantine was preserved.");
                    }
                    handle.Dispose();
                    owned.Handle = null;
                }

                if (!TryMarkForDeletion(rootHandle))
                {
                    var error = Marshal.GetLastWin32Error();
                    return Failed(
                        quarantine,
                        $"The quarantine could not be removed ({error}); unrecognized concurrent entries were preserved.");
                }
                rootHandle.Dispose();
                rootHandle = null;
                return new ExactDirectoryCleanupResult(true, null, string.Empty);
            }
            finally
            {
                foreach (var owned in deletionHandles) owned.Handle?.Dispose();
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            var preserved = GetPreservedPath(source, quarantine, rootHandle);
            return Failed(
                preserved,
                $"Exact directory cleanup was unavailable ({exception.GetType().Name}); the directory was preserved.");
        }
        finally
        {
            rootHandle?.Dispose();
            parentNamespaceLease?.Dispose();
            parentHandle?.Dispose();
        }
    }

    private static Dictionary<string, DirectoryEntryIdentity> CaptureInventory(
        string root,
        string expectedRootIdentity,
        SafeFileHandle? rootHandle,
        bool shareDelete)
    {
        var inventory = new Dictionary<string, DirectoryEntryIdentity>(StringComparer.OrdinalIgnoreCase);
        var inventoryBytes = 0L;
        if (rootHandle is null)
        {
            using var inspectedRoot = OpenEntryForInspection(root, isDirectory: true, shareDelete);
            ValidateDirectoryHandle(inspectedRoot, root, expectedRootIdentity);
            CaptureChildren(
                root,
                string.Empty,
                inspectedRoot,
                shareDelete,
                inventory,
                ref inventoryBytes,
                depth: 0);
        }
        else
        {
            ValidateDirectoryHandle(rootHandle, root, expectedRootIdentity);
            CaptureChildren(
                root,
                string.Empty,
                rootHandle,
                shareDelete,
                inventory,
                ref inventoryBytes,
                depth: 0);
        }
        return inventory;
    }

    private static void CaptureChildren(
        string directoryPath,
        string relativeDirectory,
        SafeFileHandle directoryHandle,
        bool shareDelete,
        IDictionary<string, DirectoryEntryIdentity> inventory,
        ref long inventoryBytes,
        int depth)
    {
        if (depth >= MaximumDepth)
            throw new InvalidDataException("The cleanup directory exceeds the bounded traversal depth.");
        ValidateExactFinalPath(directoryHandle, directoryPath);

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     "*",
                     new EnumerationOptions
                     {
                         RecurseSubdirectories = false,
                         IgnoreInaccessible = false,
                         ReturnSpecialDirectories = false,
                         AttributesToSkip = 0
                     }))
        {
            if (inventory.Count >= MaximumEntries)
                throw new InvalidDataException("The cleanup directory exceeds the bounded entry limit.");
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
                throw new InvalidDataException("The cleanup directory contains an invalid entry name.");
            var relative = string.IsNullOrEmpty(relativeDirectory)
                ? name
                : Path.Combine(relativeDirectory, name);

            SafeFileHandle? handle = null;
            try
            {
                handle = OpenEntryForInspection(path, isDirectory: null, shareDelete);
                var identity = GetValidatedEntryIdentity(
                    handle,
                    path,
                    relative,
                    ref inventoryBytes);
                if (!inventory.TryAdd(relative, identity))
                    throw new InvalidDataException("The cleanup directory contains an ambiguous entry path.");
                if (identity.IsDirectory)
                {
                    CaptureChildren(
                        path,
                        relative,
                        handle,
                        shareDelete,
                        inventory,
                        ref inventoryBytes,
                        checked(depth + 1));
                }
            }
            finally
            {
                handle?.Dispose();
            }
        }
    }

    private static DirectoryEntryIdentity GetValidatedEntryIdentity(
        SafeFileHandle handle,
        string path,
        string relativePath,
        ref long inventoryBytes)
    {
        var information = PathSafety.WindowsPathHandle.GetIdentity(handle);
        var rejected = FileAttributes.ReparsePoint | FileAttributes.Device;
        if ((information.FileAttributes & rejected) != 0 || information.FileIndex == 0)
            throw new InvalidDataException("The cleanup directory contains a redirected or identity-less entry.");
        var isDirectory = (information.FileAttributes & FileAttributes.Directory) != 0;
        if (!isDirectory && information.NumberOfLinks != 1)
            throw new InvalidDataException("The cleanup directory contains a hard-linked file.");
        ValidateExactFinalPath(handle, path);
        var length = 0L;
        var sha256 = string.Empty;
        if (!isDirectory)
        {
            length = RandomAccess.GetLength(handle);
            if (length < 0 || length > MaximumFileBytes)
                throw new InvalidDataException("A cleanup file exceeds the bounded content-verification limit.");
            if (length > MaximumInventoryBytes - inventoryBytes)
                throw new InvalidDataException("The cleanup directory exceeds the bounded content-verification limit.");
            inventoryBytes += length;
            sha256 = ComputeSha256(handle, length);
        }
        return new DirectoryEntryIdentity(
            relativePath,
            information.VolumeSerialNumber,
            information.FileIndex,
            isDirectory,
            length,
            sha256);
    }

    private static void ValidateDirectoryHandle(
        SafeFileHandle handle,
        string path,
        string expectedIdentity)
    {
        var information = PathSafety.WindowsPathHandle.GetIdentity(handle);
        var rejected = FileAttributes.ReparsePoint | FileAttributes.Device;
        if ((information.FileAttributes & FileAttributes.Directory) == 0
            || (information.FileAttributes & rejected) != 0
            || information.FileIndex == 0
            || !FormatIdentity(information).Equals(expectedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The cleanup directory identity changed before quarantine.");
        }
        ValidateExactFinalPath(handle, path);
    }

    private static void ValidateEntryHandle(
        SafeFileHandle handle,
        string path,
        DirectoryEntryIdentity expected,
        ref long pinnedBytes)
    {
        var actual = GetValidatedEntryIdentity(
            handle,
            path,
            expected.RelativePath,
            ref pinnedBytes);
        if (actual != expected)
            throw new InvalidDataException("A quarantined entry changed before it could be pinned for deletion.");
    }

    private static string ComputeSha256(SafeFileHandle handle, long expectedLength)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var offset = 0L;
            while (offset < expectedLength)
            {
                var requested = (int)Math.Min(buffer.Length, expectedLength - offset);
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
                if (read <= 0)
                    throw new EndOfStreamException("A cleanup file changed while its content was verified.");
                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            if (RandomAccess.GetLength(handle) != expectedLength)
                throw new InvalidDataException("A cleanup file length changed while its content was verified.");
            Span<byte> trailingByte = stackalloc byte[1];
            if (RandomAccess.Read(handle, trailingByte, expectedLength) != 0)
                throw new InvalidDataException("A cleanup file grew while its content was verified.");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ValidateExactFinalPath(SafeFileHandle handle, string expectedPath)
    {
        var finalPath = PathSafety.Normalize(PathSafety.WindowsPathHandle.GetFinalPath(handle));
        if (!finalPath.Equals(PathSafety.Normalize(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A cleanup entry resolved outside its exact expected path.");
    }

    private static bool InventoriesMatch(
        IReadOnlyDictionary<string, DirectoryEntryIdentity> expected,
        IReadOnlyDictionary<string, DirectoryEntryIdentity> actual)
    {
        if (expected.Count != actual.Count) return false;
        foreach (var (path, expectedEntry) in expected)
        {
            if (!actual.TryGetValue(path, out var actualEntry)
                || expectedEntry != actualEntry)
            {
                return false;
            }
        }
        return true;
    }

    private static SafeFileHandle OpenParentForQuarantine(string path) =>
        OpenEntry(path, FileReadAttributes, FileShareRead | FileShareWrite, isDirectory: true);

    private static FileStream OpenParentNamespaceLease(string parent)
    {
        var leasePath = Path.Combine(
            parent,
            $".rimworld-ai-cleanup-namespace-{Guid.NewGuid():N}.lock");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                leasePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                System.IO.FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            var information = PathSafety.WindowsPathHandle.GetIdentity(stream.SafeFileHandle);
            var rejected = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device;
            if ((information.FileAttributes & rejected) != 0
                || information.FileIndex == 0
                || information.NumberOfLinks != 1
                || RandomAccess.GetLength(stream.SafeFileHandle) != 0)
            {
                throw new InvalidDataException("The cleanup parent namespace lease is not a unique empty file.");
            }
            ValidateExactFinalPath(stream.SafeFileHandle, leasePath);
            var result = stream;
            stream = null;
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static void ValidatePinnedParentHandle(SafeFileHandle handle, string expectedPath)
    {
        var information = PathSafety.WindowsPathHandle.GetIdentity(handle);
        if ((information.FileAttributes
             & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
            != FileAttributes.Directory
            || information.FileIndex == 0)
        {
            throw new InvalidDataException("The cleanup quarantine parent is redirected or identity-less.");
        }
        ValidateExactFinalPath(handle, expectedPath);
    }

    private static SafeFileHandle OpenDirectoryForDelete(string path) =>
        OpenEntry(path, DeleteAccess | FileReadAttributes, FileShareRead | FileShareWrite, isDirectory: true);

    private static SafeFileHandle OpenEntryForDelete(string path, bool isDirectory) =>
        OpenEntry(path, DeleteAccess | FileReadData | FileReadAttributes, FileShareRead, isDirectory);

    private static SafeFileHandle OpenEntryForInspection(
        string path,
        bool? isDirectory,
        bool shareDelete) =>
        OpenEntry(
            path,
            FileReadData | FileReadAttributes,
            FileShareRead | (shareDelete ? FileShareDelete : 0),
            isDirectory);

    private static SafeFileHandle OpenEntry(
        string path,
        uint desiredAccess,
        uint shareMode,
        bool? isDirectory)
    {
        var flags = FileFlagOpenReparsePoint;
        if (isDirectory != false) flags |= FileFlagBackupSemantics;
        var handle = CreateFile(
            ToExtendedPath(path),
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("An exact cleanup entry handle could not be opened.", new Win32Exception(error));
    }

    private static bool TryRenameByHandle(SafeFileHandle handle, string destinationPath)
    {
        var destination = ToNtPath(destinationPath);
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
            return SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileRenameInfo,
                buffer,
                (uint)bufferSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsRetryableQuarantineRenameError(int error) =>
        error is ErrorAccessDenied or ErrorSharingViolation or ErrorLockViolation;

    private static bool TryMarkForDeletion(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        return SetFileInformationByHandle(
            handle,
            FileInfoByHandleClass.FileDispositionInfo,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInfo>());
    }

    private static string FormatIdentity(PathSafety.WindowsPathHandle.FileIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileIndex:x16}";

    private static int GetDepth(string relativePath) =>
        relativePath.Count(character => character is '\\' or '/');

    private static ExactDirectoryCleanupResult Failed(string preservedPath, string failure) =>
        new(false, Path.GetFullPath(preservedPath), failure);

    private static string GetPreservedPath(
        string source,
        string quarantine,
        SafeFileHandle? rootHandle)
    {
        if (rootHandle is { IsClosed: false, IsInvalid: false })
        {
            try
            {
                return PathSafety.Normalize(PathSafety.WindowsPathHandle.GetFinalPath(rootHandle));
            }
            catch (Exception exception) when (exception is IOException
                                              or ObjectDisposedException
                                              or InvalidDataException
                                              or ArgumentException
                                              or NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Cleanup preserved-path inspection failed ({exception.GetType().Name}).");
            }
        }
        return Directory.Exists(quarantine) ? quarantine : source;
    }

    private static string ToExtendedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private static string ToNtPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\??\UNC\" + fullPath[2..]
            : @"\??\" + fullPath;
    }

    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadData = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    private enum FileInfoByHandleClass
    {
        FileRenameInfo = 3,
        FileDispositionInfo = 4
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
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    private sealed record DirectoryEntryIdentity(
        string RelativePath,
        uint VolumeSerialNumber,
        ulong FileIndex,
        bool IsDirectory,
        long Length,
        string Sha256);

    private sealed class DeletionHandle(DirectoryEntryIdentity entry, SafeFileHandle handle)
    {
        public DirectoryEntryIdentity Entry { get; } = entry;
        public SafeFileHandle? Handle { get; set; } = handle;
    }
}

internal sealed record ExactDirectoryCleanupResult(
    bool Removed,
    string? PreservedPath,
    string Failure);
