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
        Capture(paths, operationName, copySnapshot, deleteSnapshot, null, cancellationToken);

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
        string? localTransactionRoot = null;
        long aggregateBytes = 0;
        try
        {
            var targetCount = 0;
            foreach (var rawPath in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++targetCount > MaximumSnapshotTargets)
                    throw new InvalidDataException(
                        $"Snapshot target count exceeds the {MaximumSnapshotTargets:N0}-target limit.");
                var target = Path.GetFullPath(rawPath);
                foreach (var path in new[] { target, target + ".bak" })
                {
                    if (!seen.Add(path)) continue;
                    var initial = CaptureRecoveryFingerprint(path, cancellationToken);
                    if (initial.Kind == SnapshotLeafKind.File)
                    {
                        aggregateBytes = checked(aggregateBytes + initial.Length);
                        if (aggregateBytes > PathSafety.MaximumTrustedBoundaryBytes)
                            throw new InvalidDataException("Transaction backups exceed the aggregate size limit.");
                    }
                    entries.Add(new FileSnapshotEntry(path, initial, null));
                }
            }

            if (recoverySession?.IsEnabled == true)
            {
                entries = [.. recoverySession.PrepareCapture(entries, operationName, cancellationToken)];
            }
            else if (entries.Any(entry => entry.Existed))
            {
                localTransactionRoot = CreateLocalTransactionDirectory();
                var backups = Path.Combine(localTransactionRoot, "backups");
                entries = entries.Select((entry, index) => entry.Existed
                        ? entry with { SnapshotPath = Path.Combine(backups, $"{index:D5}.bin") }
                        : entry)
                    .ToList();
            }

            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[index];
                if (entry.SnapshotPath is null) continue;
                if (recoverySession?.IsEnabled == true)
                    recoverySession.CopySnapshot(entry, index, copySnapshot, cancellationToken);
                else
                    CopyAndVerifySnapshot(entry, copySnapshot, cancellationToken);
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
                "snapshot capture").ToList();
            if (localTransactionRoot is not null)
                TryDeleteEmptyLocalTransaction(localTransactionRoot, cleanupFailures);
            if (cleanupFailures.Count > 0)
            {
                throw new IOException(
                    $"{operationName} snapshot capture failed and temporary backup cleanup was incomplete.",
                    captureError);
            }
            if (captureError is OperationCanceledException) throw;
            throw new IOException($"{operationName} snapshot capture failed.", captureError);
        }
    }

    public static FileRollbackEntry[] CaptureRollbackState(
        IEnumerable<FileSnapshotEntry> entries,
        CancellationToken cancellationToken = default) =>
        entries.Select(entry =>
        {
            try
            {
                return new FileRollbackEntry(
                    entry,
                    CaptureRecoveryFingerprint(entry.Path, cancellationToken));
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                return new FileRollbackEntry(
                    entry,
                    new SnapshotLeafFingerprint(
                        SnapshotLeafKind.Unstable,
                        Failure: exception.GetType().Name));
            }
        }).ToArray();

    internal static SnapshotLeafFingerprint CaptureRecoveryFingerprint(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(fullPath))
        {
            if (File.Exists(fullPath))
                throw new InvalidDataException("A transaction path is both a file and a directory.");
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException("A transaction directory is redirected.");
            return new SnapshotLeafFingerprint(
                SnapshotLeafKind.Directory,
                LastWriteTimeUtcTicks: Directory.GetLastWriteTimeUtc(fullPath).Ticks);
        }
        if (!File.Exists(fullPath)) return new SnapshotLeafFingerprint(SnapshotLeafKind.Missing);

        var fileAttributes = File.GetAttributes(fullPath);
        if ((fileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Transaction backups require a regular local file.");
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > PathSafety.MaximumTrustedLeafBytes)
            throw new InvalidDataException(
                $"Transaction file exceeds the {PathSafety.MaximumTrustedLeafBytes:N0}-byte limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total = checked(total + read);
            if (total > PathSafety.MaximumTrustedLeafBytes)
                throw new InvalidDataException("A transaction file grew beyond its size limit.");
            hash.AppendData(buffer, 0, read);
        }
        return new SnapshotLeafFingerprint(
            SnapshotLeafKind.File,
            total,
            hash.GetHashAndReset(),
            File.GetLastWriteTimeUtc(fullPath).Ticks);
    }

    public static FileRollbackResult Rollback(
        IReadOnlyList<FileRollbackEntry> rollbackEntries,
        IReadOnlySet<string>? alreadyConcurrentPaths = null,
        CancellationToken cancellationToken = default)
    {
        var concurrent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var recovery = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (alreadyConcurrentPaths is not null) concurrent.UnionWith(alreadyConcurrentPaths);
        BeforeRollbackRestoreTestHook?.Invoke();
        for (var index = rollbackEntries.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rollback = rollbackEntries[index];
            var path = rollback.Snapshot.Path;
            if (concurrent.Contains(path)) continue;
            try
            {
                Restore(rollback, cancellationToken);
            }
            catch (ConcurrentLeafChangeException exception)
            {
                concurrent.Add(path);
                concurrent.UnionWith(exception.PreservedPaths);
                if (rollback.Snapshot.SnapshotPath is not null)
                    recovery.Add(rollback.Snapshot.SnapshotPath);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                errors.Add($"{Path.GetFileName(path)} ({exception.GetType().Name})");
                if (rollback.Snapshot.SnapshotPath is not null)
                    recovery.Add(rollback.Snapshot.SnapshotPath);
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
        if (preserveSnapshots) return [];
        var errors = new List<string>();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in entries.Select(entry => entry.SnapshotPath)
                     .Where(path => path is not null)
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(snapshot)) deleteSnapshot(snapshot);
                var backups = Path.GetDirectoryName(snapshot);
                var root = backups is null ? null : Path.GetDirectoryName(backups);
                if (root is not null) roots.Add(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{context} temporary backup ({exception.GetType().Name})");
            }
        }
        foreach (var root in roots) TryDeleteEmptyLocalTransaction(root, errors);
        return errors;
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

    private static void CopyAndVerifySnapshot(
        FileSnapshotEntry entry,
        Action<string, string> copySnapshot,
        CancellationToken cancellationToken)
    {
        copySnapshot(entry.Path, entry.SnapshotPath!);
        var snapshot = CaptureRecoveryFingerprint(entry.SnapshotPath!, cancellationToken);
        if (!HasSameContent(entry.InitialState, snapshot))
            throw new InvalidDataException("A file changed while its transaction backup was copied.");
    }

    private static void Restore(
        FileRollbackEntry rollback,
        CancellationToken cancellationToken)
    {
        var entry = rollback.Snapshot;
        var current = CaptureRecoveryFingerprint(entry.Path, cancellationToken);
        if (!SameState(current, rollback.ExpectedCurrent))
            throw new ConcurrentLeafChangeException(
                "A file changed while rollback was starting.",
                entry.Path);
        if (SameState(current, entry.InitialState)) return;

        if (entry.InitialState.Kind == SnapshotLeafKind.Missing)
        {
            if (current.Kind == SnapshotLeafKind.File)
            {
                if (File.GetAttributes(entry.Path).HasFlag(FileAttributes.ReadOnly))
                    throw new UnauthorizedAccessException("A created output became read-only during rollback.");
                File.Delete(entry.Path);
                return;
            }
            throw new ConcurrentLeafChangeException(
                "A created output could not be removed safely.",
                entry.Path);
        }
        if (entry.InitialState.Kind != SnapshotLeafKind.File
            || entry.SnapshotPath is null
            || !File.Exists(entry.SnapshotPath))
        {
            throw new InvalidDataException("A rollback backup is missing or unsupported.");
        }
        var snapshot = CaptureRecoveryFingerprint(entry.SnapshotPath, cancellationToken);
        if (!HasSameContent(snapshot, entry.InitialState))
            throw new InvalidDataException("A rollback backup failed validation.");

        var directory = Path.GetDirectoryName(entry.Path)
            ?? throw new InvalidDataException("A rollback target has no parent directory.");
        var prepared = Path.Combine(
            directory,
            $".{Path.GetFileName(entry.Path)}.{Guid.NewGuid():N}.restore.tmp");
        try
        {
            AtomicFile.CopyFlushedBounded(
                entry.SnapshotPath,
                prepared,
                PathSafety.MaximumTrustedLeafBytes,
                cancellationToken);
            if (File.Exists(entry.Path))
                File.Replace(prepared, entry.Path, null, ignoreMetadataErrors: true);
            else
                File.Move(prepared, entry.Path);
        }
        finally
        {
            if (File.Exists(prepared)) File.Delete(prepared);
        }
        var restored = CaptureRecoveryFingerprint(entry.Path, cancellationToken);
        if (!HasSameContent(restored, entry.InitialState))
            throw new IOException("A rollback target failed post-write validation.");
    }

    private static bool SameState(
        SnapshotLeafFingerprint left,
        SnapshotLeafFingerprint right) =>
        left.Kind == right.Kind
        && left.Kind switch
        {
            SnapshotLeafKind.Missing => true,
            SnapshotLeafKind.File => HasSameContent(left, right),
            SnapshotLeafKind.Directory => left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks,
            _ => false
        };

    private static string CreateLocalTransactionDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), "RimWorldAiTranslator-transactions");
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, $"transaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        FileTransactionRecoveryAuthority.WriteSmallFlushedFile(
            Path.Combine(root, FileTransactionRecoveryAuthority.OwnerMarkerName),
            FileTransactionRecoveryAuthority.OwnerMarkerContent);
        Directory.CreateDirectory(Path.Combine(root, "backups"));
        return root;
    }

    private static void TryDeleteEmptyLocalTransaction(
        string transactionRoot,
        ICollection<string> errors)
    {
        try
        {
            var name = Path.GetFileName(transactionRoot);
            var marker = Path.Combine(transactionRoot, FileTransactionRecoveryAuthority.OwnerMarkerName);
            if (!name.StartsWith("transaction-", StringComparison.Ordinal)
                || !File.Exists(marker)
                || !File.ReadAllText(marker)
                    .Equals(FileTransactionRecoveryAuthority.OwnerMarkerContent, StringComparison.Ordinal))
            {
                return;
            }
            var backups = Path.Combine(transactionRoot, "backups");
            if (Directory.Exists(backups)
                && !Directory.EnumerateFileSystemEntries(backups).Any())
            {
                Directory.Delete(backups);
            }
            if (File.Exists(marker)) File.Delete(marker);
            if (!Directory.EnumerateFileSystemEntries(transactionRoot).Any())
                Directory.Delete(transactionRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"temporary transaction cleanup ({exception.GetType().Name})");
        }
    }
}
