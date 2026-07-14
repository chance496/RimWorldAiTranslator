using System.Text.Json;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

/// <summary>
/// A transaction keeps one manifest and one started marker. Per-target commit
/// intent/state artifacts are intentionally not persisted.
/// </summary>
internal sealed class FileTransactionRecoverySession : IDisposable
{
    private static readonly AsyncLocal<FileTransactionRecoverySession?> Active = new();
    private readonly FileTransactionRecoveryAuthority? authority;
    private readonly string? targetRoot;
    private readonly FileStream? lease;
    private FileSnapshotEntry[] journal = [];
    private readonly Dictionary<string, SnapshotLeafFingerprint> applied =
        new(StringComparer.OrdinalIgnoreCase);
    private string operationName = string.Empty;
    private string? transactionPath;
    private bool resolved;
    private bool preserve;
    private bool disposed;

    private FileTransactionRecoverySession()
    {
    }

    internal FileTransactionRecoverySession(
        FileTransactionRecoveryAuthority authority,
        string targetRoot,
        FileStream lease)
    {
        this.authority = authority;
        this.targetRoot = targetRoot;
        this.lease = lease;
    }

    internal bool IsEnabled => authority is not null;
    internal bool ShouldDeferRecovery => false;
    internal bool ShouldPreserveSnapshots => IsEnabled && transactionPath is not null && !resolved;

    internal static FileTransactionRecoverySession CreateDisabled() => new();

    internal void AttachJournal(IReadOnlyList<FileSnapshotEntry> entries) =>
        journal = entries.ToArray();

    internal FileSnapshotEntry[] PrepareCapture(
        IReadOnlyList<FileSnapshotEntry> entries,
        string requestedOperationName,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled) return entries.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (transactionPath is not null)
            throw new InvalidOperationException("The recovery session already owns a transaction.");
        transactionPath = authority!.CreateTransactionDirectory();
        operationName = string.IsNullOrWhiteSpace(requestedOperationName)
            ? "File save"
            : requestedOperationName;
        var backups = Path.Combine(transactionPath, "backups");
        journal = entries.Select((entry, index) => entry.Existed
                ? entry with { SnapshotPath = Path.Combine(backups, $"{index:D5}.bin") }
                : entry with { SnapshotPath = null })
            .ToArray();
        return journal;
    }

    internal void CopySnapshot(
        FileSnapshotEntry entry,
        int index,
        Action<string, string> copySnapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.SnapshotPath is null) return;
        copySnapshot(entry.Path, entry.SnapshotPath);
        var copied = FileSnapshotJournal.CaptureRecoveryFingerprint(entry.SnapshotPath, cancellationToken);
        if (!FileSnapshotJournal.HasSameContent(entry.InitialState, copied))
            throw new InvalidDataException("A transaction backup changed while it was copied.");
    }

    internal void CompleteCapture(
        IReadOnlyList<FileSnapshotEntry> entries,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled) return;
        cancellationToken.ThrowIfCancellationRequested();
        journal = entries.ToArray();
        var manifest = new SimpleTransactionManifest
        {
            TransactionId = Path.GetFileName(transactionPath!),
            TargetRoot = targetRoot!,
            OperationName = operationName,
            CreatedUtc = DateTimeOffset.UtcNow,
            Entries = entries.Select(entry => new SimpleTransactionManifestEntry
            {
                RelativePath = GetRelativeTarget(entry.Path),
                Existed = entry.Existed,
                SnapshotFileName = entry.SnapshotPath is null
                    ? null
                    : Path.GetFileName(entry.SnapshotPath)
            }).ToList()
        };
        var manifestPath = Path.Combine(transactionPath!, FileTransactionRecoveryAuthority.ManifestFileName);
        var temporaryPath = manifestPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, manifest);
                stream.Flush(flushToDisk: true);
            }
            _ = FileTransactionRecoveryAuthority.ReadManifest(temporaryPath);
            File.Move(temporaryPath, manifestPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal IDisposable Activate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsEnabled)
        {
            FileTransactionRecoveryAuthority.WriteSmallFlushedFile(
                Path.Combine(transactionPath!, FileTransactionRecoveryAuthority.StartedMarkerName),
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        var prior = Active.Value;
        Active.Value = this;
        return new Activation(this, prior);
    }

    internal static void RecordApplied(
        string path,
        SnapshotLeafFingerprint committedFingerprint)
    {
        var session = Active.Value;
        if (session is null) return;
        session.applied[Path.GetFullPath(path)] = committedFingerprint;
    }

    internal void MarkResolved(CancellationToken cancellationToken = default) =>
        MarkResolved(null, cancellationToken);

    internal void MarkResolved(
        PathSafety.TrustedWriteBoundary? trustedWriteBoundary,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        resolved = true;
    }

    internal void MarkRollbackResolved(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        resolved = true;
    }

    internal FileRollbackResult RollbackDurably(
        IReadOnlySet<string>? concurrentPaths = null)
    {
        var observed = FileSnapshotJournal.CaptureRollbackState(
                journal,
                CancellationToken.None)
            .ToDictionary(
                entry => entry.Snapshot.Path,
                entry => entry.ExpectedCurrent,
                StringComparer.OrdinalIgnoreCase);
        var rollbackEntries = journal.Select(entry => new FileRollbackEntry(
                entry,
                applied.TryGetValue(entry.Path, out var committed)
                    ? committed
                    : observed[entry.Path]))
            .ToArray();
        var rollback = FileSnapshotJournal.Rollback(
            rollbackEntries,
            concurrentPaths,
            CancellationToken.None);
        if (rollback.Errors.Count == 0 && rollback.ConcurrentPaths.Count == 0)
            resolved = true;
        return rollback;
    }

    internal void DetachPreservedEvidence() => preserve = true;

    internal void Finish(bool preserveSnapshots, IReadOnlyList<string> cleanupFailures)
    {
        if (cleanupFailures.Count > 0 || preserveSnapshots) preserve = true;
        if (!IsEnabled || transactionPath is null || preserve || !resolved) return;
        authority!.DeleteOwnedTransactionDirectory(transactionPath);
        transactionPath = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ReferenceEquals(Active.Value, this)) Active.Value = null;
        lease?.Dispose();
    }

    private string GetRelativeTarget(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!PathSafety.IsStrictlyInside(fullPath, targetRoot!))
            throw new InvalidDataException("A transaction target is outside its declared output root.");
        return Path.GetRelativePath(targetRoot!, fullPath);
    }

    private sealed class Activation(
        FileTransactionRecoverySession owner,
        FileTransactionRecoverySession? prior) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ReferenceEquals(Active.Value, owner)) Active.Value = prior;
        }
    }
}
