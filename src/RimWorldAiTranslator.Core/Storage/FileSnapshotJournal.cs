using System.Security.Cryptography;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

internal enum SnapshotLeafKind
{
    Missing,
    File,
    Directory,
    Unstable
}

internal sealed record SnapshotLeafFingerprint(
    SnapshotLeafKind Kind,
    long Length = 0,
    byte[]? Sha256 = null,
    long LastWriteTimeUtcTicks = 0,
    uint? VolumeSerialNumber = null,
    ulong? FileIndex = null,
    string? Failure = null);

internal sealed record FileSnapshotEntry(
    string Path,
    SnapshotLeafFingerprint InitialState,
    string? SnapshotPath)
{
    public bool Existed => InitialState.Kind == SnapshotLeafKind.File;
}

internal sealed record FileRollbackEntry(
    FileSnapshotEntry Snapshot,
    SnapshotLeafFingerprint ExpectedCurrent);

internal sealed record FileRollbackResult(
    IReadOnlySet<string> ConcurrentPaths,
    IReadOnlyList<string> Errors,
    IReadOnlySet<string> RecoveryPaths);

internal static class FileSnapshotJournal
{
    internal const int MaximumSnapshotTargets = 16_384;
    internal static Action? BeforeRollbackRestoreTestHook { get; set; }

    public static FileSnapshotEntry[] Capture(
        IEnumerable<string> paths,
        string operationName,
        Action<string, string> copySnapshot,
        Action<string> deleteSnapshot,
        CancellationToken cancellationToken = default) =>
        Capture(
            paths,
            operationName,
            copySnapshot,
            deleteSnapshot,
            null,
            cancellationToken);

    internal static FileSnapshotEntry[] Capture(
        IEnumerable<string> paths,
        string operationName,
        Action<string, string> copySnapshot,
        Action<string> deleteSnapshot,
        FileTransactionRecoverySession? recoverySession,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<FileSnapshotEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregateBytes = 0;
        var targetCount = 0;
        try
        {
            foreach (var rawPath in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                targetCount++;
                if (targetCount > MaximumSnapshotTargets)
                    throw new InvalidDataException(
                        $"Snapshot target count exceeds the {MaximumSnapshotTargets:N0}-target limit.");
                var targetPath = Path.GetFullPath(rawPath);
                foreach (var path in new[] { targetPath, targetPath + ".bak" })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seen.Add(path)) continue;
                    var initialState = CaptureInitialState(path);
                    var existed = initialState.Kind == SnapshotLeafKind.File;
                    string? snapshot = null;
                    long sourceLength = 0;
                    if (existed)
                    {
                        sourceLength = new FileInfo(path).Length;
                        ValidateSnapshotLength(path, sourceLength, ref aggregateBytes);
                        var directory = Path.GetDirectoryName(path)
                            ?? throw new InvalidDataException($"Snapshot target has no parent directory: {path}");
                        snapshot = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.transaction.bak");
                    }

                    entries.Add(new FileSnapshotEntry(path, initialState, snapshot));
                }
            }

            if (recoverySession?.IsEnabled == true)
            {
                entries = [.. recoverySession.PrepareCapture(
                    entries,
                    operationName,
                    cancellationToken)];
            }

            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[index];
                if (entry.SnapshotPath is null) continue;
                if (recoverySession?.IsEnabled == true)
                {
                    recoverySession.CopySnapshot(
                        entry,
                        index,
                        copySnapshot,
                        cancellationToken);
                }
                else
                {
                    copySnapshot(entry.Path, entry.SnapshotPath);
                    cancellationToken.ThrowIfCancellationRequested();
                    var snapshotState = CaptureRecoveryFingerprint(entry.SnapshotPath, cancellationToken);
                    if (!HasSameContent(entry.InitialState, snapshotState))
                        throw new InvalidDataException(
                            $"Snapshot source changed while it was being captured: {Path.GetFileName(entry.Path)}");
                }
            }
            recoverySession?.CompleteCapture(entries, cancellationToken);
            return entries.ToArray();
        }
        catch (Exception captureError)
        {
            var cleanupFailures = Cleanup(
                entries,
                deleteSnapshot,
                preserveSnapshots: recoverySession?.ShouldPreserveSnapshots == true,
                "snapshot capture");
            if (cleanupFailures.Count > 0)
            {
                throw new IOException(
                    $"{operationName} snapshot capture failed ({captureError.GetType().Name}). Recovery snapshot cleanup was incomplete: {string.Join(" | ", cleanupFailures)}",
                    captureError);
            }
            if (recoverySession?.ShouldPreserveSnapshots == true)
                throw new IOException(
                    $"{operationName} snapshot capture failed; durable recovery evidence was preserved ({captureError.GetType().Name}).",
                    captureError);
            if (captureError is OperationCanceledException) throw;
            throw new IOException(
                $"{operationName} snapshot capture failed; all partial snapshots were cleaned up ({captureError.GetType().Name}).",
                captureError);
        }
    }

    public static FileRollbackEntry[] CaptureRollbackState(
        IEnumerable<FileSnapshotEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var result = new List<FileRollbackEntry>();
        long aggregateBytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotLeafFingerprint fingerprint;
            try
            {
                fingerprint = CaptureFingerprint(entry.Path, ref aggregateBytes, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                fingerprint = new SnapshotLeafFingerprint(
                    SnapshotLeafKind.Unstable,
                    Failure: exception.GetType().Name);
            }
            result.Add(new FileRollbackEntry(entry, fingerprint));
        }
        return result.ToArray();
    }

    internal static SnapshotLeafFingerprint CaptureRecoveryFingerprint(
        string path,
        CancellationToken cancellationToken = default)
    {
        long aggregateBytes = 0;
        return CaptureFingerprint(Path.GetFullPath(path), ref aggregateBytes, cancellationToken);
    }

    public static FileRollbackResult Rollback(
        IReadOnlyList<FileRollbackEntry> rollbackEntries,
        IReadOnlySet<string>? alreadyConcurrentPaths = null,
        CancellationToken cancellationToken = default)
    {
        var concurrent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recovery = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        if (alreadyConcurrentPaths is not null) concurrent.UnionWith(alreadyConcurrentPaths);

        BeforeRollbackRestoreTestHook?.Invoke();
        for (var index = rollbackEntries.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rollback = rollbackEntries[index];
            var path = Path.GetFullPath(rollback.Snapshot.Path);
            if (concurrent.Contains(path)) continue;
            try
            {
                RestoreCas(rollback, recovery, cancellationToken);
            }
            catch (ConcurrentLeafChangeException exception)
            {
                concurrent.Add(path);
                concurrent.UnionWith(exception.PreservedPaths);
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(path)} ({exception.GetType().Name})");
            }
        }
        return new FileRollbackResult(concurrent, errors, recovery);
    }

    public static IReadOnlyList<string> Cleanup(
        IEnumerable<FileSnapshotEntry> entries,
        Action<string> deleteSnapshot,
        bool preserveSnapshots,
        string context)
    {
        var failures = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.SnapshotPath is null || !File.Exists(entry.SnapshotPath)) continue;
            if (preserveSnapshots)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"{context} recovery snapshot preserved after incomplete rollback: {entry.SnapshotPath}");
                continue;
            }

            try
            {
                deleteSnapshot(entry.SnapshotPath);
            }
            catch (Exception exception)
            {
                failures.Add($"{entry.SnapshotPath} ({exception.GetType().Name})");
                System.Diagnostics.Trace.TraceWarning(
                    $"{context} snapshot cleanup failed: {entry.SnapshotPath} ({exception.GetType().Name}).");
                continue;
            }

            if (File.Exists(entry.SnapshotPath))
            {
                failures.Add($"{entry.SnapshotPath} (still exists)");
                System.Diagnostics.Trace.TraceWarning(
                    $"{context} snapshot cleanup returned without removing: {entry.SnapshotPath}.");
            }
        }
        return failures;
    }

    private static void RestoreCas(
        FileRollbackEntry rollback,
        ISet<string> recoveryPaths,
        CancellationToken cancellationToken)
    {
        var entry = rollback.Snapshot;
        var path = Path.GetFullPath(entry.Path);
        var expected = rollback.ExpectedCurrent;
        if (expected.Kind == SnapshotLeafKind.Unstable)
            throw new ConcurrentLeafChangeException(
                $"Rollback state could not be pinned ({expected.Failure ?? "Unknown"}).",
                path);

        if (expected.Kind == SnapshotLeafKind.Directory)
        {
            if (entry.InitialState.Kind == SnapshotLeafKind.Directory
                && SameDirectoryIdentity(entry.InitialState, expected))
            {
                var current = CaptureInitialState(path);
                if (SameDirectoryIdentity(expected, current)) return;
                throw new ConcurrentLeafChangeException(
                    "A protected directory changed after rollback state was captured.",
                    path);
            }
            throw new IOException(
                "Rollback target is a directory; automatic file restoration is unsafe.");
        }

        if (expected.Kind == SnapshotLeafKind.Missing)
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new ConcurrentLeafChangeException("A rollback target appeared after failure.", path);
            if (entry.InitialState.Kind == SnapshotLeafKind.Missing) return;
            if (entry.InitialState.Kind == SnapshotLeafKind.Directory)
                throw new IOException(
                    "An original rollback directory disappeared; automatic restoration is unsafe.");
            RestoreMissingTarget(entry, cancellationToken);
            return;
        }

        if (!File.Exists(path) || Directory.Exists(path))
            throw new ConcurrentLeafChangeException("A rollback target disappeared after failure.", path);
        if (entry.InitialState.Kind == SnapshotLeafKind.Directory)
            throw new IOException(
                "An original rollback directory was replaced by a file; automatic restoration is unsafe.");
        if (entry.InitialState.Kind == SnapshotLeafKind.File)
        {
            RestoreExistingTarget(entry, expected, recoveryPaths, cancellationToken);
            return;
        }
        RemoveCreatedTarget(path, expected, recoveryPaths, cancellationToken);
    }

    private static void RestoreMissingTarget(
        FileSnapshotEntry entry,
        CancellationToken cancellationToken)
    {
        var restore = CreatePreparedRestore(entry, cancellationToken);
        try
        {
            File.Move(restore, entry.Path);
        }
        catch (IOException exception) when (File.Exists(entry.Path) || Directory.Exists(entry.Path))
        {
            throw new ConcurrentLeafChangeException(
                "A rollback target appeared while its original content was being restored.",
                exception,
                entry.Path);
        }
        finally
        {
            TryDelete(restore);
        }
    }

    private static void RestoreExistingTarget(
        FileSnapshotEntry entry,
        SnapshotLeafFingerprint expected,
        ISet<string> recoveryPaths,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(entry.Path)!;
        var restore = CreatePreparedRestore(entry, cancellationToken);
        var displaced = Path.Combine(directory, $".{Path.GetFileName(entry.Path)}.{Guid.NewGuid():N}.rollback-current.tmp");
        var rejected = Path.Combine(directory, $".{Path.GetFileName(entry.Path)}.{Guid.NewGuid():N}.rollback-rejected.tmp");
        try
        {
            File.Replace(restore, entry.Path, displaced, ignoreMetadataErrors: true);
            if (MatchesFingerprint(displaced, expected, cancellationToken))
            {
                File.Delete(displaced);
                return;
            }

            try
            {
                if (File.Exists(entry.Path))
                    File.Replace(displaced, entry.Path, rejected, ignoreMetadataErrors: true);
                else if (!Directory.Exists(entry.Path))
                    File.Move(displaced, entry.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(displaced)) recoveryPaths.Add(displaced);
                if (File.Exists(rejected)) recoveryPaths.Add(rejected);
                throw new ConcurrentLeafChangeException(
                    "Rollback detected concurrent content and could not restore it in place; recovery files were preserved.",
                    exception,
                    entry.Path,
                    displaced,
                    rejected);
            }

            if (File.Exists(rejected))
            {
                if (entry.SnapshotPath is not null
                    && FilesHaveSameContent(rejected, entry.SnapshotPath, cancellationToken))
                    File.Delete(rejected);
                else
                {
                    recoveryPaths.Add(rejected);
                }
            }
            throw new ConcurrentLeafChangeException(
                "Rollback preserved a concurrent save instead of overwriting it.",
                entry.Path,
                rejected);
        }
        catch (IOException exception) when (exception is not ConcurrentLeafChangeException
                                            && !File.Exists(displaced))
        {
            throw new ConcurrentLeafChangeException(
                "Rollback target changed before atomic restoration.",
                exception,
                entry.Path);
        }
        finally
        {
            TryDelete(restore);
            if (File.Exists(displaced)) recoveryPaths.Add(displaced);
        }
    }

    private static void RemoveCreatedTarget(
        string path,
        SnapshotLeafFingerprint expected,
        ISet<string> recoveryPaths,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        var displaced = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.rollback-created.tmp");
        try
        {
            File.Move(path, displaced);
        }
        catch (IOException exception)
        {
            throw new ConcurrentLeafChangeException(
                "A transaction-created target changed before rollback.",
                exception,
                path);
        }

        if (MatchesFingerprint(displaced, expected, cancellationToken))
        {
            File.Delete(displaced);
            return;
        }

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) File.Move(displaced, path);
            else recoveryPaths.Add(displaced);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            recoveryPaths.Add(displaced);
            throw new ConcurrentLeafChangeException(
                "A concurrent save was preserved in a rollback recovery file.",
                exception,
                path,
                displaced);
        }
        throw new ConcurrentLeafChangeException(
            "Rollback preserved a concurrent save instead of deleting it.",
            path,
            displaced);
    }

    private static string CreatePreparedRestore(
        FileSnapshotEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.SnapshotPath is null)
            throw new InvalidDataException("Rollback snapshot is missing.");
        var directory = Path.GetDirectoryName(entry.Path)!;
        var restore = Path.Combine(directory, $".{Path.GetFileName(entry.Path)}.{Guid.NewGuid():N}.restore.tmp");
        AtomicFile.CopyFlushedBounded(
            entry.SnapshotPath,
            restore,
            PathSafety.MaximumTrustedLeafBytes,
            cancellationToken);
        return restore;
    }

    private static SnapshotLeafFingerprint CaptureFingerprint(
        string path,
        ref long aggregateBytes,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            if (File.Exists(path))
                throw new InvalidDataException("Rollback path is both a file and directory.");
            return CaptureDirectoryFingerprint(path);
        }
        if (!File.Exists(path)) return new SnapshotLeafFingerprint(SnapshotLeafKind.Missing);

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Rollback fingerprints require a regular non-redirected file.");

        FileStream stream;
        if (OperatingSystem.IsWindows())
        {
            var handle = PathSafety.WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(path);
            try
            {
                var identity = PathSafety.WindowsPathHandle.GetIdentity(handle);
                if (identity.FileIndex == 0
                    || identity.NumberOfLinks != 1
                    || (identity.FileAttributes
                        & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        "Rollback fingerprints require a unique regular file identity.");
                }
                stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        else
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
        }
        using (stream)
        {
        var length = stream.Length;
        ValidateSnapshotLength(path, length, ref aggregateBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long readTotal = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            readTotal = checked(readTotal + read);
            if (readTotal > PathSafety.MaximumTrustedLeafBytes)
                throw new InvalidDataException("Rollback fingerprint exceeds its file-size limit.");
            hash.AppendData(buffer, 0, read);
        }
        var lastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(path).Ticks;
        uint? volumeSerialNumber = null;
        ulong? fileIndex = null;
        if (OperatingSystem.IsWindows())
        {
            var identity = PathSafety.WindowsPathHandle.GetIdentity(stream.SafeFileHandle);
            volumeSerialNumber = identity.VolumeSerialNumber;
            fileIndex = identity.FileIndex;
        }
        return new SnapshotLeafFingerprint(
            SnapshotLeafKind.File,
            readTotal,
            hash.GetHashAndReset(),
            lastWriteTimeUtcTicks,
            volumeSerialNumber,
            fileIndex);
        }
    }

    private static SnapshotLeafFingerprint CaptureInitialState(string path)
    {
        if (Directory.Exists(path))
        {
            if (File.Exists(path))
                throw new InvalidDataException("Snapshot path is both a file and directory.");
            return CaptureDirectoryFingerprint(path);
        }
        return File.Exists(path)
            ? CaptureRecoveryFingerprint(path)
            : new SnapshotLeafFingerprint(SnapshotLeafKind.Missing);
    }

    private static SnapshotLeafFingerprint CaptureDirectoryFingerprint(string path)
    {
        var lastWriteTimeUtcTicks = Directory.GetLastWriteTimeUtc(path).Ticks;
        if (!OperatingSystem.IsWindows())
            return new SnapshotLeafFingerprint(
                SnapshotLeafKind.Directory,
                LastWriteTimeUtcTicks: lastWriteTimeUtcTicks);

        using var handle = PathSafety.WindowsPathHandle.OpenDirectoryForIdentity(path);
        var identity = PathSafety.WindowsPathHandle.GetIdentity(handle);
        if (identity.FileIndex == 0
            || (identity.FileAttributes & FileAttributes.Directory) == 0
            || (identity.FileAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Rollback directory has no stable regular-directory identity.");
        return new SnapshotLeafFingerprint(
            SnapshotLeafKind.Directory,
            LastWriteTimeUtcTicks: lastWriteTimeUtcTicks,
            VolumeSerialNumber: identity.VolumeSerialNumber,
            FileIndex: identity.FileIndex);
    }

    private static bool SameDirectoryIdentity(
        SnapshotLeafFingerprint initial,
        SnapshotLeafFingerprint current) =>
        initial.Kind == SnapshotLeafKind.Directory
        && current.Kind == SnapshotLeafKind.Directory
        && (OperatingSystem.IsWindows()
            ? initial.VolumeSerialNumber is not null
              && initial.FileIndex is not null
              && initial.VolumeSerialNumber == current.VolumeSerialNumber
              && initial.FileIndex == current.FileIndex
            : initial.LastWriteTimeUtcTicks == current.LastWriteTimeUtcTicks);

    private static bool MatchesFingerprint(
        string path,
        SnapshotLeafFingerprint expected,
        CancellationToken cancellationToken)
    {
        long aggregate = 0;
        try
        {
            var actual = CaptureFingerprint(path, ref aggregate, cancellationToken);
            return actual.Kind == expected.Kind
                   && actual.Length == expected.Length
                   && actual.LastWriteTimeUtcTicks == expected.LastWriteTimeUtcTicks
                   && actual.VolumeSerialNumber == expected.VolumeSerialNumber
                   && actual.FileIndex == expected.FileIndex
                   && actual.Sha256 is not null
                   && expected.Sha256 is not null
                   && CryptographicOperations.FixedTimeEquals(actual.Sha256, expected.Sha256);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
        {
            return false;
        }
    }

    private static bool FilesHaveSameContent(
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        long aggregate = 0;
        var leftFingerprint = CaptureFingerprint(left, ref aggregate, cancellationToken);
        var rightFingerprint = CaptureFingerprint(right, ref aggregate, cancellationToken);
        return leftFingerprint.Kind == SnapshotLeafKind.File
               && rightFingerprint.Kind == SnapshotLeafKind.File
               && leftFingerprint.Length == rightFingerprint.Length
               && leftFingerprint.Sha256 is not null
               && rightFingerprint.Sha256 is not null
               && CryptographicOperations.FixedTimeEquals(leftFingerprint.Sha256, rightFingerprint.Sha256);
    }

    internal static bool HasSameContent(
        SnapshotLeafFingerprint left,
        SnapshotLeafFingerprint right) =>
        left.Kind == SnapshotLeafKind.File
        && right.Kind == SnapshotLeafKind.File
        && left.Length == right.Length
        && left.Sha256 is { Length: 32 }
        && right.Sha256 is { Length: 32 }
        && CryptographicOperations.FixedTimeEquals(left.Sha256, right.Sha256);

    private static void ValidateSnapshotLength(string path, long length, ref long aggregateBytes)
    {
        if (length < 0 || length > PathSafety.MaximumTrustedLeafBytes)
            throw new InvalidDataException(
                $"Snapshot file exceeds the {PathSafety.MaximumTrustedLeafBytes:N0}-byte limit: {Path.GetFileName(path)}");
        aggregateBytes = checked(aggregateBytes + length);
        if (aggregateBytes > PathSafety.MaximumTrustedBoundaryBytes)
            throw new InvalidDataException(
                $"Snapshot files exceed the {PathSafety.MaximumTrustedBoundaryBytes:N0}-byte aggregate limit.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Rollback cleanup preserved a recovery file ({exception.GetType().Name}): {path}");
        }
    }
}
