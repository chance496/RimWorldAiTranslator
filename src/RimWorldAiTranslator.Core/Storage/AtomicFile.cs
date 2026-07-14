using System.Text;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    internal static Action<string>? BeforeTemporaryFlushTestHook { get; set; }
    internal static Action<string>? AfterTemporaryFlushBeforeValidationPinTestHook { get; set; }
    internal static Action<string>? AfterTemporaryValidationBeforeCommitTestHook { get; set; }
    internal static Action<string>? AfterCopyChunkWrittenTestHook { get; set; }

    public static void WriteUtf8(string path, string text, bool keepBackup = true)
    {
        WriteBytes(path, Utf8NoBom.GetBytes(text), keepBackup);
    }

    public static void WriteUtf8Validated(
        string path,
        string text,
        Action<string> validateTemporaryFile,
        bool keepBackup = true)
    {
        ArgumentNullException.ThrowIfNull(validateTemporaryFile);
        WriteBytes(path, Utf8NoBom.GetBytes(text), keepBackup, validateTemporaryFile);
    }

    public static void WriteBytes(string path, byte[] bytes, bool keepBackup = true)
        => WriteBytes(path, bytes, keepBackup, null);

    public static void WriteBytesValidated(
        string path,
        byte[] bytes,
        Action<string> validateTemporaryFile,
        bool keepBackup = true)
    {
        ArgumentNullException.ThrowIfNull(validateTemporaryFile);
        WriteBytes(path, bytes, keepBackup, validateTemporaryFile);
    }

    internal static void WriteBytesValidated(
        string path,
        byte[] bytes,
        Action<string> validateTemporaryFile,
        PathSafety.TrustedWriteBoundary writeBoundary,
        bool keepBackup = true)
    {
        ArgumentNullException.ThrowIfNull(validateTemporaryFile);
        ArgumentNullException.ThrowIfNull(writeBoundary);
        WriteBytes(path, bytes, keepBackup, validateTemporaryFile, writeBoundary);
    }

    private static void WriteBytes(
        string path,
        byte[] bytes,
        bool keepBackup,
        Action<string>? validateTemporaryFile,
        PathSafety.TrustedWriteBoundary? writeBoundary = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException($"Path has no parent directory: {fullPath}");
        var boundaryRoot = writeBoundary is null
            ? FindNearestExistingDirectory(directory)
            : null;
        using var localBoundary = writeBoundary is null
            ? PathSafety.AcquireTrustedWriteBoundary(boundaryRoot!, [fullPath])
            : null;
        var activeBoundary = writeBoundary ?? localBoundary
            ?? throw new InvalidOperationException("An atomic write has no trusted parent boundary.");
        var targetExisted = activeBoundary.TargetExisted(fullPath);
        if (Directory.Exists(fullPath))
            throw new IOException($"Refusing to replace a directory with a file: {fullPath}");
        if (targetExisted && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException($"Refusing to replace a read-only file: {fullPath}");

        var recoveryPlan = FileTransactionRecoverySession.ReservePreparedWrite(fullPath, keepBackup);
        var tempPath = recoveryPlan?.PreparedPath ?? AtomicTemporaryFiles.CreatePath(fullPath);
        SnapshotLeafFingerprint? ownedTemporary = null;
        uint? createdTemporaryVolume = null;
        ulong? createdTemporaryFileIndex = null;
        try
        {
            WriteNewFlushed(
                tempPath,
                bytes,
                (volumeSerialNumber, fileIndex) =>
                {
                    createdTemporaryVolume = volumeSerialNumber;
                    createdTemporaryFileIndex = fileIndex;
                });
            ownedTemporary = new SnapshotLeafFingerprint(
                SnapshotLeafKind.File,
                bytes.LongLength,
                SHA256.HashData(bytes),
                VolumeSerialNumber: createdTemporaryVolume,
                FileIndex: createdTemporaryFileIndex);

            AfterTemporaryFlushBeforeValidationPinTestHook?.Invoke(tempPath);
            SnapshotLeafFingerprint validatedPrepared;
            using (OpenValidatedPreparedPin(
                       tempPath,
                       ownedTemporary,
                       out validatedPrepared))
            {
                validateTemporaryFile?.Invoke(tempPath);
                FileTransactionRecoverySession.MarkPreparedWriteReady(
                    recoveryPlan,
                    validatedPrepared);
            }
            AfterTemporaryValidationBeforeCommitTestHook?.Invoke(tempPath);
            activeBoundary.CommitPreparedFile(
                tempPath,
                fullPath,
                keepBackup,
                recoveryPlan,
                validatedPrepared);
        }
        finally
        {
            if (ownedTemporary is not null)
            {
                TryDeleteOwned(tempPath, ownedTemporary);
            }
            else if (createdTemporaryVolume.HasValue && createdTemporaryFileIndex.HasValue)
            {
                TryDeleteOwned(
                    tempPath,
                    createdTemporaryVolume.Value,
                    createdTemporaryFileIndex.Value);
            }
        }
    }

    private static string FindNearestExistingDirectory(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException(
                    $"No existing ancestor could be found for the atomic output directory: {directory}");
            }
            current = parent;
        }
        return current;
    }

    public static void CopyFlushed(string sourcePath, string destinationPath)
        => CopyFlushedBounded(sourcePath, destinationPath, long.MaxValue, CancellationToken.None);

    internal static void CopyFlushedBounded(
        string sourcePath,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (source.Length > maximumBytes)
            throw new InvalidDataException(
                $"Snapshot source exceeds the {maximumBytes:N0}-byte limit: {Path.GetFileName(sourcePath)}");
        uint? destinationVolume = null;
        ulong? destinationFileIndex = null;
        var completed = false;
        try
        {
            using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough);
            if (!AtomicTemporaryFiles.TryGetOwnedRegularFileIdentity(
                    destination.SafeFileHandle,
                    out var volumeSerialNumber,
                    out var fileIndex))
            {
                throw new IOException("The newly created snapshot temporary file could not be identity-pinned.");
            }
            destinationVolume = volumeSerialNumber;
            destinationFileIndex = fileIndex;
            var buffer = new byte[64 * 1024];
            long copied = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                copied = checked(copied + read);
                if (copied > maximumBytes)
                    throw new InvalidDataException(
                        $"Snapshot source grew beyond the {maximumBytes:N0}-byte limit: {Path.GetFileName(sourcePath)}");
                destination.Write(buffer, 0, read);
                AfterCopyChunkWrittenTestHook?.Invoke(destinationPath);
            }
            destination.Flush(true);
            completed = true;
        }
        finally
        {
            if (!completed && destinationVolume.HasValue && destinationFileIndex.HasValue)
            {
                _ = AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                    destinationPath,
                    destinationVolume.Value,
                    destinationFileIndex.Value);
            }
        }
    }

    private static SafeFileHandle OpenValidatedPreparedPin(
        string path,
        SnapshotLeafFingerprint expected,
        out SnapshotLeafFingerprint validated)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = PathSafety.WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(path);
            if (expected.Kind != SnapshotLeafKind.File
                || expected.VolumeSerialNumber is null
                || expected.FileIndex is null
                || expected.Sha256 is not { Length: 32 }
                || !AtomicTemporaryFiles.TryGetOwnedRegularFileIdentity(
                    handle,
                    out var volumeSerialNumber,
                    out var fileIndex)
                || volumeSerialNumber != expected.VolumeSerialNumber.Value
                || fileIndex != expected.FileIndex.Value
                || RandomAccess.GetLength(handle) != expected.Length
                || !CryptographicOperations.FixedTimeEquals(
                    ComputePinnedSha256(handle, expected.Length),
                    expected.Sha256))
            {
                throw new ConcurrentLeafChangeException(
                    "The flushed atomic output changed before its validation handle was pinned.",
                    path);
            }

            validated = FileSnapshotJournal.CaptureRecoveryFingerprint(path);
            if (validated.Kind != SnapshotLeafKind.File
                || validated.VolumeSerialNumber != expected.VolumeSerialNumber
                || validated.FileIndex != expected.FileIndex
                || validated.Length != expected.Length
                || validated.Sha256 is not { Length: 32 }
                || !CryptographicOperations.FixedTimeEquals(validated.Sha256, expected.Sha256))
            {
                throw new ConcurrentLeafChangeException(
                    "The flushed atomic output changed while its validation evidence was captured.",
                    path);
            }

            var result = handle
                ?? throw new InvalidOperationException(
                    "The validated atomic output pin was not opened.");
            handle = null;
            return result;
        }
        catch (ConcurrentLeafChangeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConcurrentLeafChangeException(
                "The flushed atomic output could not be pinned as the exact file that was created.",
                exception,
                path);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static byte[] ComputePinnedSha256(SafeFileHandle handle, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            var read = RandomAccess.Read(
                handle,
                buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)),
                offset);
            if (read <= 0)
                throw new EndOfStreamException(
                    "The pinned atomic output ended before its validated length.");
            hash.AppendData(buffer, 0, read);
            offset = checked(offset + read);
        }
        return hash.GetHashAndReset();
    }

    private static void WriteNewFlushed(
        string path,
        byte[] bytes,
        Action<uint, ulong> ownershipCaptured)
    {
        ArgumentNullException.ThrowIfNull(ownershipCaptured);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        if (!AtomicTemporaryFiles.TryGetOwnedRegularFileIdentity(
                stream.SafeFileHandle,
                out var volumeSerialNumber,
                out var fileIndex))
        {
            throw new IOException("The newly created atomic temporary file could not be identity-pinned.");
        }
        ownershipCaptured(volumeSerialNumber, fileIndex);
        stream.Write(bytes);
        BeforeTemporaryFlushTestHook?.Invoke(path);
        stream.Flush(true);
    }

    private static void TryDeleteOwned(string path, SnapshotLeafFingerprint fingerprint)
    {
        try
        {
            if (File.Exists(path)
                && !AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                    path,
                    fingerprint.VolumeSerialNumber,
                    fingerprint.FileIndex,
                    fingerprint.Length,
                    fingerprint.Sha256))
            {
                System.Diagnostics.Debug.WriteLine(
                    "Atomic file cleanup preserved a leaf whose exact identity was not owned.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Atomic file cleanup skipped ({exception.GetType().Name}).");
        }
    }

    private static void TryDeleteOwned(
        string path,
        uint volumeSerialNumber,
        ulong fileIndex)
    {
        try
        {
            if (File.Exists(path)
                && !AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                    path,
                    volumeSerialNumber,
                    fileIndex))
            {
                System.Diagnostics.Debug.WriteLine(
                    "Atomic file cleanup preserved a leaf whose creation identity was no longer owned.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Atomic file cleanup skipped ({exception.GetType().Name}).");
        }
    }
}
