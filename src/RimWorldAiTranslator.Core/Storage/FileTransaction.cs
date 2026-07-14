using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

public static class FileTransaction
{
    public static void Execute(
        IEnumerable<string> paths,
        Action action,
        string operationName,
        CancellationToken cancellationToken = default) =>
        Execute(
            paths,
            action,
            operationName,
            File.Delete,
            (source, destination) => CopySnapshotBounded(source, destination, cancellationToken),
            null,
            null,
            cancellationToken);

    internal static void Execute(
        IEnumerable<string> paths,
        Action action,
        string operationName,
        Action beforeRollback) =>
        Execute(paths, action, operationName, File.Delete, CopySnapshotBounded, beforeRollback, null, CancellationToken.None);

    internal static void Execute(
        IEnumerable<string> paths,
        Action action,
        string operationName,
        Action beforeRollback,
        CancellationToken cancellationToken) =>
        Execute(
            paths,
            action,
            operationName,
            File.Delete,
            (source, destination) => CopySnapshotBounded(source, destination, cancellationToken),
            beforeRollback,
            null,
            cancellationToken);

    internal static void Execute(
        IEnumerable<string> paths,
        Action action,
        string operationName,
        Action beforeRollback,
        FileTransactionRecoverySession recoverySession,
        CancellationToken cancellationToken) =>
        Execute(
            paths,
            action,
            operationName,
            File.Delete,
            (source, destination) => CopySnapshotBounded(source, destination, cancellationToken),
            beforeRollback,
            recoverySession,
            cancellationToken);

    internal static void Execute(
        IEnumerable<string> paths,
        Action action,
        string operationName,
        Action beforeRollback,
        PathSafety.TrustedWriteBoundary trustedWriteBoundary,
        FileTransactionRecoverySession recoverySession,
        CancellationToken cancellationToken) =>
        Execute(
            paths,
            action,
            operationName,
            File.Delete,
            (source, destination) => CopySnapshotBounded(source, destination, cancellationToken),
            beforeRollback,
            recoverySession,
            cancellationToken,
            trustedWriteBoundary);

    internal static void Execute(
        IEnumerable<string> paths,
        Action action,
        string operationName,
        Action<string> deleteSnapshot) =>
        Execute(paths, action, operationName, deleteSnapshot, CopySnapshotBounded, null, null, CancellationToken.None);

    internal static void Execute(
        IEnumerable<string> paths,
        Action action,
        string operationName,
        Action<string> deleteSnapshot,
        Action<string, string> copySnapshot) =>
        Execute(paths, action, operationName, deleteSnapshot, copySnapshot, null, null, CancellationToken.None);

    private static void Execute(
        IEnumerable<string> paths,
        Action action,
        string operationName,
        Action<string> deleteSnapshot,
        Action<string, string> copySnapshot,
        Action? beforeRollback,
        FileTransactionRecoverySession? suppliedRecoverySession,
        CancellationToken cancellationToken,
        PathSafety.TrustedWriteBoundary? trustedWriteBoundary = null)
    {
        var materializedPaths = paths.Select(Path.GetFullPath).ToArray();
        var recoverySession = suppliedRecoverySession
                              ?? FileTransactionRecoverySession.CreateDisabled();
        var ownsRecoverySession = suppliedRecoverySession is null;
        FileSnapshotEntry[]? journal = null;
        IDisposable? activation = null;
        var preserveSnapshots = false;
        var operationCompleted = false;
        try
        {
            journal = FileSnapshotJournal.Capture(
                materializedPaths,
                operationName,
                copySnapshot,
                deleteSnapshot,
                recoverySession,
                cancellationToken);
            activation = recoverySession.Activate();
            action();
            activation.Dispose();
            activation = null;
            operationCompleted = true;
            recoverySession.MarkResolved(trustedWriteBoundary, CancellationToken.None);
        }
        catch (Exception operationError)
        {
            activation?.Dispose();
            activation = null;
            if (journal is null) throw;
            if (operationCompleted || recoverySession.ShouldDeferRecovery)
            {
                preserveSnapshots = true;
                recoverySession.DetachPreservedEvidence();
                throw new IOException(
                    $"{operationName} completed but durable resolution was deferred; recovery evidence was preserved.",
                    operationError);
            }
            beforeRollback?.Invoke();
            IReadOnlySet<string>? concurrentPaths = operationError is ConcurrentLeafChangeException concurrent
                ? concurrent.PreservedPaths
                : null;
            var rollback = recoverySession.IsEnabled
                ? recoverySession.RollbackDurably(concurrentPaths)
                : FileSnapshotJournal.Rollback(
                    FileSnapshotJournal.CaptureRollbackState(journal, CancellationToken.None),
                    concurrentPaths,
                    CancellationToken.None);
            if (rollback.Errors.Count > 0)
            {
                preserveSnapshots = true;
                recoverySession.DetachPreservedEvidence();
                throw new IOException(
                    $"{operationName} failed and rollback was incomplete ({operationError.GetType().Name}). Rollback errors: {string.Join(" | ", rollback.Errors)}. Recovery snapshots were preserved.",
                    operationError);
            }
            if (rollback.ConcurrentPaths.Count > 0)
            {
                preserveSnapshots = true;
                recoverySession.DetachPreservedEvidence();
                throw new ConcurrentLeafChangeException(
                    $"{operationName} stopped because rollback detected a concurrent save; current files and recovery snapshots were preserved: {string.Join(" | ", rollback.ConcurrentPaths)}.",
                    operationError,
                    [.. rollback.ConcurrentPaths, .. rollback.RecoveryPaths]);
            }
            if (!recoverySession.IsEnabled)
                recoverySession.MarkRollbackResolved(CancellationToken.None);
            if (operationError is OperationCanceledException) throw;
            throw new IOException(
                $"{operationName} failed; all files written by this run were rolled back ({operationError.GetType().Name}).",
                operationError);
        }
        finally
        {
            activation?.Dispose();
            if (journal is not null)
            {
                var cleanupFailures = recoverySession.IsEnabled
                    ? Array.Empty<string>()
                    : FileSnapshotJournal.Cleanup(
                        journal,
                        deleteSnapshot,
                        preserveSnapshots,
                        "Transaction");
                recoverySession.Finish(
                    preserveSnapshots || recoverySession.ShouldDeferRecovery,
                    cleanupFailures);
            }
            if (ownsRecoverySession) recoverySession.Dispose();
        }
    }

    public static async Task<T> ExecuteAsync<T>(
        IEnumerable<string> paths,
        Func<Task<T>> action,
        string operationName,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsyncCore(
                paths,
                action,
                operationName,
                File.Delete,
                (source, destination) => CopySnapshotBounded(source, destination, cancellationToken),
                null,
                cancellationToken)
            .ConfigureAwait(false);

    internal static Task<T> ExecuteAsync<T>(
        IEnumerable<string> paths,
        Func<Task<T>> action,
        string operationName,
        Action<string> deleteSnapshot) =>
        ExecuteAsyncCore(
            paths,
            action,
            operationName,
            deleteSnapshot,
            CopySnapshotBounded,
            null,
            CancellationToken.None);

    internal static async Task<T> ExecuteAsync<T>(
        IEnumerable<string> paths,
        Func<Task<T>> action,
        string operationName,
        Action<string> deleteSnapshot,
        Action<string, string> copySnapshot,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsyncCore(
                paths,
                action,
                operationName,
                deleteSnapshot,
                copySnapshot,
                null,
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<T> ExecuteAsync<T>(
        IEnumerable<string> paths,
        Func<Task<T>> action,
        string operationName,
        Action<string> deleteSnapshot,
        Action<string, string> copySnapshot,
        FileTransactionRecoverySession recoverySession,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsyncCore(
                paths,
                action,
                operationName,
                deleteSnapshot,
                copySnapshot,
                recoverySession,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<T> ExecuteAsyncCore<T>(
        IEnumerable<string> paths,
        Func<Task<T>> action,
        string operationName,
        Action<string> deleteSnapshot,
        Action<string, string> copySnapshot,
        FileTransactionRecoverySession? suppliedRecoverySession,
        CancellationToken cancellationToken)
    {
        var materializedPaths = paths.Select(Path.GetFullPath).ToArray();
        var recoverySession = suppliedRecoverySession
                              ?? FileTransactionRecoverySession.CreateDisabled();
        var ownsRecoverySession = suppliedRecoverySession is null;
        FileSnapshotEntry[]? journal = null;
        IDisposable? activation = null;
        var preserveSnapshots = false;
        var operationCompleted = false;
        try
        {
            journal = FileSnapshotJournal.Capture(
                materializedPaths,
                operationName,
                copySnapshot,
                deleteSnapshot,
                recoverySession,
                cancellationToken);
            activation = recoverySession.Activate();
            var result = await action().ConfigureAwait(false);
            activation.Dispose();
            activation = null;
            operationCompleted = true;
            recoverySession.MarkResolved(CancellationToken.None);
            return result;
        }
        catch (Exception operationError)
        {
            activation?.Dispose();
            activation = null;
            if (journal is null) throw;
            if (operationCompleted || recoverySession.ShouldDeferRecovery)
            {
                preserveSnapshots = true;
                recoverySession.DetachPreservedEvidence();
                throw new IOException(
                    $"{operationName} completed but durable resolution was deferred; recovery evidence was preserved.",
                    operationError);
            }
            var rollback = recoverySession.IsEnabled
                ? recoverySession.RollbackDurably()
                : FileSnapshotJournal.Rollback(
                    FileSnapshotJournal.CaptureRollbackState(journal, CancellationToken.None),
                    cancellationToken: CancellationToken.None);
            if (rollback.Errors.Count > 0)
            {
                preserveSnapshots = true;
                recoverySession.DetachPreservedEvidence();
                throw new IOException(
                    $"{operationName} failed and rollback was incomplete ({operationError.GetType().Name}). Rollback errors: {string.Join(" | ", rollback.Errors)}. Recovery snapshots were preserved.",
                    operationError);
            }
            if (rollback.ConcurrentPaths.Count > 0)
            {
                preserveSnapshots = true;
                recoverySession.DetachPreservedEvidence();
                throw new ConcurrentLeafChangeException(
                    $"{operationName} stopped because rollback detected a concurrent save; current files and recovery snapshots were preserved: {string.Join(" | ", rollback.ConcurrentPaths)}.",
                    operationError,
                    [.. rollback.ConcurrentPaths, .. rollback.RecoveryPaths]);
            }
            if (!recoverySession.IsEnabled)
                recoverySession.MarkRollbackResolved(CancellationToken.None);
            if (operationError is OperationCanceledException) throw;
            throw new IOException(
                $"{operationName} failed; all files written by this run were rolled back ({operationError.GetType().Name}).",
                operationError);
        }
        finally
        {
            activation?.Dispose();
            if (journal is not null)
            {
                var cleanupFailures = recoverySession.IsEnabled
                    ? Array.Empty<string>()
                    : FileSnapshotJournal.Cleanup(
                        journal,
                        deleteSnapshot,
                        preserveSnapshots,
                        "Transaction");
                recoverySession.Finish(
                    preserveSnapshots || recoverySession.ShouldDeferRecovery,
                    cleanupFailures);
            }
            if (ownsRecoverySession) recoverySession.Dispose();
        }
    }

    internal static FileTransactionRecoverySession AcquireRecoveryLease(
        FileTransactionRecoveryAuthority? authority,
        string targetRoot,
        CancellationToken cancellationToken = default) =>
        authority?.Acquire(targetRoot, cancellationToken)
        ?? FileTransactionRecoverySession.CreateDisabled();

    internal static void RecoverPending(
        FileTransactionRecoveryAuthority authority,
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.RecoverPending(targetRoot, cancellationToken);
    }

    private static void CopySnapshotBounded(string sourcePath, string destinationPath) =>
        CopySnapshotBounded(sourcePath, destinationPath, CancellationToken.None);

    private static void CopySnapshotBounded(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        AtomicFile.CopyFlushedBounded(
            sourcePath,
            destinationPath,
            PathSafety.MaximumTrustedLeafBytes,
            cancellationToken);
}
