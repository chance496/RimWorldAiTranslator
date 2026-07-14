using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

internal sealed partial class FileTransactionRecoverySession : IDisposable
{
    private const int CurrentFormatVersion = 3;
    private const int MaximumRecoveryTransactions = 32;
    private const int MaximumRecoveryIntents = FileSnapshotJournal.MaximumSnapshotTargets;
    // Per logical write: reserve+intent (2), target+backup snapshot ready/data
    // (4), and target+backup rollback plan/ready/applied/done (8), plus
    // transaction-level external pending/final evidence and core markers.
    private const int MaximumRecoveryArtifactFiles =
        FileSnapshotJournal.MaximumSnapshotTargets * 14 + 6;
    private const int MaximumArtifactBytes = 32 * 1024 * 1024;
    private const string BaseFileName = "base.json";
    private const string PreparedFileName = "prepared.json";
    private const string ExternalPendingFileName = "external-pending.json";
    private const string ExternalFinalFileName = "external-final.json";
    private const string ExternalFinalCommitDisposition = "commit";
    private const string ExternalFinalRollbackDisposition = "rollback";
    private const string ResolvedFileName = "resolved.json";
    private const string CleanupFileName = "cleanup.json";
    private const string LockFileName = "recovery.lock";
    private const string TransactionPrefix = "transaction-";
    private const string ReservationPrefix = "reserve-";
    private const string IntentPrefix = "intent-";
    private const string RollbackPlanPrefix = "rollback-plan-";
    private const string RollbackReadyPrefix = "rollback-ready-";
    private const string RollbackAppliedPrefix = "rollback-applied-";
    private const string RollbackDonePrefix = "rollback-done-";
    private const string JsonSuffix = ".json";
    private static readonly AsyncLocal<FileTransactionRecoverySession?> ActiveSession = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private readonly object sync = new();
    private readonly bool enabled;
    private readonly FileTransactionRecoveryAuthority? authority;
    private readonly string targetRoot = string.Empty;
    private readonly PathSafety.ExistingDirectorySnapshot targetRootSnapshot;
    private readonly string shardPath = string.Empty;
    private readonly PathSafety.ExistingDirectorySnapshot shardSnapshot;
    private readonly FileStream? shardLock;
    private RecoveryBase? activeBase;
    private string? activeTransactionPath;
    private PathSafety.ExistingDirectorySnapshot activeTransactionSnapshot;
    private FileArtifact? activeBaseArtifact;
    private readonly List<FileArtifact> activeSnapshotReadyArtifacts = [];
    private readonly List<FileArtifact> activeSnapshotDataArtifacts = [];
    private readonly Dictionary<int, SnapshotLeafFingerprint> activeSnapshotStates = [];
    private FileArtifact? activePreparedArtifact;
    private RecoveryExternalPending? activeExternalPending;
    private FileArtifact? activeExternalPendingArtifact;
    private FileArtifact? activeExternalFinalArtifact;
    private bool activeExternalFinalAllowsCommit;
    private readonly List<FileArtifact> activeReservationArtifacts = [];
    private readonly List<FileArtifact> activeIntentArtifacts = [];
    private readonly List<ResolvedCommitReservation> activeCommitReservations = [];
    private AtomicCommitRecoveryPlan? activePendingCommitPlan;
    private RecoveryCommitReservation? activePendingReservation;
    private FileArtifact? activePendingReservationArtifact;
    private readonly List<FileArtifact> activeRollbackArtifacts = [];
    private string? previousRollbackDoneSha256;
    private int activeRollbackDoneCount;
    private FileArtifact? activeResolvedArtifact;
    private Dictionary<string, int>? entryIndexesByPath;
    private SnapshotLeafFingerprint[]? expectedCommittedStates;
    private int nextIntentSequence;
    private string? previousIntentSha256;
    private bool preserveEvidence;
    private bool publicationOutcomeUncertain;
    private bool finished;
    private bool disposed;

    internal static Action<string>? BaseManifestPublishedTestHook { get; set; }
    internal static Action<string>? SnapshotReadyPublishedTestHook { get; set; }
    internal static Action<string>? IntentPublishedTestHook { get; set; }
    internal static Action<string>? ReservationPublishedTestHook { get; set; }
    internal static Action<string>? PreparedReadyPublishedTestHook { get; set; }
    internal static Action<string>? ExternalPendingPublishedTestHook { get; set; }
    internal static Action<string>? ExternalFinalPublishedTestHook { get; set; }
    internal static Action<string>? RollbackPlanPublishedTestHook { get; set; }
    internal static Action<string>? RollbackReadyPublishedTestHook { get; set; }
    internal static Action<string>? RollbackAppliedPublishedTestHook { get; set; }
    internal static Action<string>? RollbackDonePublishedTestHook { get; set; }
    internal static Action<string>? ArtifactMovedBeforeVerificationTestHook { get; set; }
    internal static Action<string>? AfterCleanupArtifactDeletedTestHook { get; set; }

    internal bool IsEnabled => enabled;
    internal bool ShouldDeferRecovery => enabled && publicationOutcomeUncertain;
    internal bool ShouldPreserveSnapshots => enabled && (activeTransactionPath is not null || publicationOutcomeUncertain);

    private FileTransactionRecoverySession()
    {
    }

    internal FileTransactionRecoverySession(
        FileTransactionRecoveryAuthority authority,
        PathSafety.ExistingDirectorySnapshot targetRootSnapshot,
        PathSafety.ExistingDirectorySnapshot shardSnapshot,
        IReadOnlySet<string> trustedTransientFiles,
        CancellationToken cancellationToken)
    {
        this.authority = authority;
        this.targetRootSnapshot = targetRootSnapshot;
        targetRoot = targetRootSnapshot.CanonicalPath;
        this.shardSnapshot = shardSnapshot;
        shardPath = shardSnapshot.CanonicalPath;
        var lockPath = Path.Combine(shardPath, LockFileName);
        ValidateExistingRegularOrMissing(lockPath, "transaction recovery lock");
        FileStream? openedLock = null;
        try
        {
            openedLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            ValidateOpenRegularFile(openedLock, lockPath, "transaction recovery lock");
            shardLock = openedLock;
            openedLock = null;
            enabled = true;
            RecoverPendingTransactions(trustedTransientFiles, cancellationToken);
            VerifyRootsUnchanged();
        }
        catch
        {
            openedLock?.Dispose();
            shardLock?.Dispose();
            throw;
        }
    }

    internal static FileTransactionRecoverySession CreateDisabled() => new();

    internal FileSnapshotEntry[] PrepareCapture(
        IReadOnlyList<FileSnapshotEntry> journal,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!enabled) return [.. journal];
        lock (sync)
        {
            ThrowIfDisposed();
            if (activeBase is not null)
                throw new InvalidOperationException("A recovery session already owns a transaction.");
            cancellationToken.ThrowIfCancellationRequested();
            VerifyRootsUnchanged();
            if (journal.Count == 0)
                throw new InvalidDataException("A durable transaction requires at least one target.");
            if (journal.Count > FileSnapshotJournal.MaximumSnapshotTargets * 2)
                throw new InvalidDataException("The durable transaction exceeds its target-entry limit.");

            var entries = new List<RecoveryEntry>(journal.Count);
            var preparedJournal = new List<FileSnapshotEntry>(journal.Count);
            var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < journal.Count; index++)
            {
                var snapshot = journal[index];
                cancellationToken.ThrowIfCancellationRequested();
                var target = RequireTargetPath(snapshot.Path);
                if (!byPath.TryAdd(target, entries.Count))
                    throw new InvalidDataException("A durable transaction contains a duplicate target path.");
                var current = FileSnapshotJournal.CaptureRecoveryFingerprint(target, cancellationToken);
                if (!SameFingerprint(snapshot.InitialState, current))
                    throw new ConcurrentLeafChangeException(
                        "A transaction target changed after its recovery snapshot was captured.",
                        target);

                string? snapshotFileName = null;
                if (snapshot.InitialState.Kind == SnapshotLeafKind.File)
                {
                    snapshotFileName = SnapshotDataFileName(index);
                }

                entries.Add(new RecoveryEntry
                {
                    RelativePath = ToTargetRelativePath(target),
                    InitialState = snapshot.InitialState,
                    SnapshotFileName = snapshotFileName
                });
                preparedJournal.Add(new FileSnapshotEntry(target, snapshot.InitialState, null));
            }

            var transactionId = Guid.NewGuid();
            var transactionPath = Path.Combine(
                shardPath,
                TransactionPrefix + transactionId.ToString("N"));
            using var transactionCreationBoundary =
                PathSafety.AcquireTrustedDirectoryCreationBoundary(
                    transactionPath,
                    cancellationToken);
            PathSafety.EnsureNoReparsePoints(transactionPath, authority!.Root);
            var transactionSnapshot = PathSafety.GetExistingDirectorySnapshot(transactionPath);
            if (!transactionSnapshot.CanonicalPath.Equals(transactionPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The transaction record directory resolved outside its expected path.");

            var document = new RecoveryBase
            {
                Version = CurrentFormatVersion,
                TransactionId = transactionId,
                OperationName = SanitizeOperationName(operationName),
                TargetRoot = targetRoot,
                TargetRootIdentity = targetRootSnapshot.Identity,
                Entries = entries
            };
            var basePath = Path.Combine(transactionPath, BaseFileName);
            var baseArtifact = PublishArtifact(basePath, document, cancellationToken);

            for (var index = 0; index < preparedJournal.Count; index++)
            {
                if (entries[index].SnapshotFileName is null) continue;
                preparedJournal[index] = preparedJournal[index] with
                {
                    SnapshotPath = Path.Combine(transactionPath, entries[index].SnapshotFileName!)
                };
            }

            activeBase = document;
            activeTransactionPath = transactionPath;
            activeTransactionSnapshot = transactionSnapshot;
            activeBaseArtifact = baseArtifact;
            entryIndexesByPath = byPath;
            expectedCommittedStates = entries.Select(entry => entry.InitialState).ToArray();
            nextIntentSequence = 0;
            previousIntentSha256 = null;
            BaseManifestPublishedTestHook?.Invoke(basePath);
            return [.. preparedJournal];
        }
    }

    internal void CopySnapshot(
        FileSnapshotEntry entry,
        int entryIndex,
        Action<string, string> copySnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(copySnapshot);
        if (!enabled)
            throw new InvalidOperationException("Durable snapshot capture requires an enabled recovery session.");
        lock (sync)
        {
            ThrowIfDisposed();
            if (activeBase is null
                || activeTransactionPath is null
                || activeBaseArtifact is null
                || entryIndex < 0
                || entryIndex >= activeBase.Entries.Count
                || activeBase.Entries[entryIndex].SnapshotFileName is null
                || entry.SnapshotPath is null)
            {
                throw new InvalidOperationException("The durable snapshot capture was not prepared.");
            }
            VerifyRootsUnchanged();
            var expectedPath = Path.Combine(
                activeTransactionPath,
                activeBase.Entries[entryIndex].SnapshotFileName!);
            if (!PathSafety.Normalize(entry.SnapshotPath).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A durable snapshot path changed after base publication.");
            if (File.Exists(expectedPath) || Directory.Exists(expectedPath))
                throw new ConcurrentLeafChangeException("A durable snapshot target already exists.", expectedPath);

            _ = AtomicTemporaryFiles.CleanupExitedProcessFiles(expectedPath);
            var temporary = AtomicTemporaryFiles.CreatePath(expectedPath);
            SnapshotLeafFingerprint? temporaryFingerprint = null;
            try
            {
                var sourceBefore = FileSnapshotJournal.CaptureRecoveryFingerprint(
                    entry.Path,
                    cancellationToken);
                if (!SameFingerprint(entry.InitialState, sourceBefore))
                    throw new ConcurrentLeafChangeException(
                        "A snapshot source changed before its durable copy was opened.",
                        entry.Path);
                copySnapshot(entry.Path, temporary);
                cancellationToken.ThrowIfCancellationRequested();
                var snapshotState = FileSnapshotJournal.CaptureRecoveryFingerprint(
                    temporary,
                    cancellationToken);
                temporaryFingerprint = snapshotState;
                var sourceAfter = FileSnapshotJournal.CaptureRecoveryFingerprint(
                    entry.Path,
                    cancellationToken);
                if (!SameFingerprint(sourceBefore, sourceAfter))
                    throw new ConcurrentLeafChangeException(
                        "A snapshot source changed while its durable copy was being captured.",
                        entry.Path);
                if (!FileSnapshotJournal.HasSameContent(entry.InitialState, snapshotState))
                    throw new InvalidDataException(
                        $"Snapshot source changed while it was being captured: {Path.GetFileName(entry.Path)}");
                var ready = new RecoverySnapshotReady
                {
                    Version = CurrentFormatVersion,
                    TransactionId = activeBase.TransactionId,
                    EntryIndex = entryIndex,
                    BaseSha256 = activeBaseArtifact.Sha256,
                    SnapshotState = snapshotState
                };
                var readyArtifact = PublishArtifact(
                    Path.Combine(activeTransactionPath, SnapshotReadyFileName(entryIndex)),
                    ready,
                    cancellationToken);
                activeSnapshotReadyArtifacts.Add(readyArtifact);
                activeSnapshotStates.Add(entryIndex, snapshotState);
                SnapshotReadyPublishedTestHook?.Invoke(temporary);
                if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                        temporary,
                        expectedPath,
                        snapshotState))
                {
                    throw new ConcurrentLeafChangeException(
                        "A durable snapshot changed before handle-bound publication.",
                        temporary,
                        expectedPath);
                }
                var finalState = FileSnapshotJournal.CaptureRecoveryFingerprint(
                    expectedPath,
                    cancellationToken);
                if (!SameFingerprint(snapshotState, finalState))
                    throw new ConcurrentLeafChangeException(
                        "A durable snapshot changed during atomic publication.",
                        expectedPath);
                activeSnapshotDataArtifacts.Add(new FileArtifact(
                    expectedPath,
                    finalState,
                    Convert.ToHexString(finalState.Sha256!)));
            }
            finally
            {
                if (File.Exists(temporary)
                    && (temporaryFingerprint is null
                        || !AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                            temporary,
                            temporaryFingerprint.VolumeSerialNumber,
                            temporaryFingerprint.FileIndex,
                            temporaryFingerprint.Length,
                            temporaryFingerprint.Sha256)))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "Durable snapshot publication temporary-file cleanup was deferred.");
                }
            }
        }
    }

    internal void CompleteCapture(
        IReadOnlyList<FileSnapshotEntry> journal,
        CancellationToken cancellationToken)
    {
        if (!enabled) return;
        lock (sync)
        {
            ThrowIfDisposed();
            if (activeBase is null
                || activeTransactionPath is null
                || activeBaseArtifact is null
                || activePreparedArtifact is not null
                || journal.Count != activeBase.Entries.Count)
            {
                throw new InvalidOperationException("The durable snapshot capture is incomplete or already sealed.");
            }
            VerifyRootsUnchanged();
            for (var index = 0; index < journal.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = journal[index];
                var current = FileSnapshotJournal.CaptureRecoveryFingerprint(entry.Path, cancellationToken);
                if (!SameFingerprint(entry.InitialState, current))
                    throw new ConcurrentLeafChangeException(
                        "A transaction target changed while recovery snapshots were being captured.",
                        entry.Path);
                if (entry.InitialState.Kind != SnapshotLeafKind.File) continue;
                if (entry.SnapshotPath is null
                    || !activeSnapshotStates.TryGetValue(index, out var expectedSnapshot)
                    || !SameFingerprint(
                        expectedSnapshot,
                        FileSnapshotJournal.CaptureRecoveryFingerprint(entry.SnapshotPath, cancellationToken)))
                {
                    throw new InvalidDataException("A durable recovery snapshot was not published completely.");
                }
            }
            var orderedReadyHashes = activeSnapshotReadyArtifacts
                .Select(artifact => artifact.Sha256)
                .ToArray();
            activePreparedArtifact = PublishArtifact(
                Path.Combine(activeTransactionPath, PreparedFileName),
                new RecoveryPrepared
                {
                    Version = CurrentFormatVersion,
                    TransactionId = activeBase.TransactionId,
                    BaseSha256 = activeBaseArtifact.Sha256,
                    SnapshotReadyHashes = [.. orderedReadyHashes]
                },
                cancellationToken);
        }
    }

    internal IDisposable Activate()
    {
        if (!enabled) return NoopDisposable.Instance;
        lock (sync)
        {
            ThrowIfDisposed();
            if (activeBase is null || activePreparedArtifact is null)
                throw new InvalidOperationException("The durable transaction was not started.");
            var previous = ActiveSession.Value;
            ActiveSession.Value = this;
            return new Activation(this, previous);
        }
    }

    internal void AuthorizeExternalMutations(
        IReadOnlyDictionary<string, long> targetMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetMaximumBytes);
        if (!enabled)
            throw new InvalidOperationException("External mutation authorization requires durable recovery.");
        lock (sync)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(ActiveSession.Value, this))
                throw new InvalidOperationException(
                    "External mutation authorization requires the active transaction context.");
            if (activeBase is null
                || activeTransactionPath is null
                || activeBaseArtifact is null
                || activePreparedArtifact is null
                || entryIndexesByPath is null
                || activeExternalPendingArtifact is not null
                || activeReservationArtifacts.Count != 0
                || activeIntentArtifacts.Count != 0)
            {
                throw new InvalidOperationException(
                    "External mutation authorization requires a fresh sealed transaction.");
            }
            if (targetMaximumBytes.Count != 2)
            {
                throw new InvalidDataException("External mutation authorization has an invalid target count.");
            }

            VerifyRootsUnchanged();
            var entries = new List<RecoveryExternalEntry>(targetMaximumBytes.Count);
            var seenIndexes = new HashSet<int>();
            foreach (var pair in targetMaximumBytes.OrderBy(
                         pair => Path.GetFullPath(pair.Key),
                         StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pair.Value <= 0 || pair.Value > PathSafety.MaximumTrustedLeafBytes)
                    throw new InvalidDataException("External mutation authorization has an invalid size limit.");
                var target = RequireTargetPath(pair.Key);
                if (!entryIndexesByPath.TryGetValue(target, out var index)
                    || !seenIndexes.Add(index)
                    || target.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "External mutation authorization must name distinct declared non-backup targets.");
                }
                var initial = activeBase.Entries[index].InitialState;
                if (initial.Kind is not (SnapshotLeafKind.File or SnapshotLeafKind.Missing))
                    throw new InvalidDataException("External mutation targets must start as regular files or be absent.");
                entries.Add(new RecoveryExternalEntry
                {
                    EntryIndex = index,
                    MaximumBytes = pair.Value,
                    InitialState = initial
                });
            }

            var document = new RecoveryExternalPending
            {
                Version = CurrentFormatVersion,
                TransactionId = activeBase.TransactionId,
                BaseSha256 = activeBaseArtifact.Sha256,
                PreparedSha256 = activePreparedArtifact.Sha256,
                Entries = entries
            };
            var path = Path.Combine(activeTransactionPath, ExternalPendingFileName);
            activeExternalPendingArtifact = PublishArtifact(path, document, cancellationToken);
            activeExternalPending = document;
            ExternalPendingPublishedTestHook?.Invoke(path);
        }
    }

    internal void CompleteExternalMutations(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled)
            throw new InvalidOperationException(
                "External mutation completion requires durable recovery.");
        throw new InvalidOperationException(
            "External mutation completion requires exact fingerprints captured while the validated files remain pinned.");
    }

    internal void CompleteExternalMutations(
        IReadOnlyDictionary<string, SnapshotLeafFingerprint> finalFingerprints,
        CancellationToken cancellationToken = default) =>
        CompleteExternalMutationsCore(
            finalFingerprints,
            ExternalFinalCommitDisposition,
            cancellationToken);

    internal void CompleteExternalMutationsForRollback(
        IReadOnlyDictionary<string, SnapshotLeafFingerprint> observedFingerprints,
        CancellationToken cancellationToken = default) =>
        CompleteExternalMutationsCore(
            observedFingerprints,
            ExternalFinalRollbackDisposition,
            cancellationToken);

    private void CompleteExternalMutationsCore(
        IReadOnlyDictionary<string, SnapshotLeafFingerprint> suppliedFingerprints,
        string disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suppliedFingerprints);
        if (!enabled)
            throw new InvalidOperationException("External mutation completion requires durable recovery.");
        lock (sync)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(ActiveSession.Value, this))
                throw new InvalidOperationException(
                    "External mutation completion requires the active transaction context.");
            if (activeBase is null
                || activeTransactionPath is null
                || activeBaseArtifact is null
                || activePreparedArtifact is null
                || activeExternalPending is null
                || activeExternalPendingArtifact is null
                || activeExternalFinalArtifact is not null
                || expectedCommittedStates is null)
            {
                throw new InvalidOperationException("No external mutation authorization is active.");
            }

            VerifyRootsUnchanged();
            var allowsCommit = string.Equals(
                disposition,
                ExternalFinalCommitDisposition,
                StringComparison.Ordinal);
            if (!allowsCommit
                && !string.Equals(
                    disposition,
                    ExternalFinalRollbackDisposition,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("External mutation final evidence has an invalid disposition.");
            }
            if (suppliedFingerprints.Count != activeExternalPending.Entries.Count)
            {
                throw new InvalidDataException(
                    "External mutation final evidence has an invalid target count.");
            }

            var suppliedByTarget = new Dictionary<string, SnapshotLeafFingerprint>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in suppliedFingerprints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                    throw new InvalidDataException(
                        "External mutation final evidence contains an invalid target binding.");
                var target = RequireTargetPath(pair.Key);
                var fingerprint = pair.Value with
                {
                    Sha256 = pair.Value.Sha256 is null ? null : [.. pair.Value.Sha256]
                };
                if (!suppliedByTarget.TryAdd(target, fingerprint))
                    throw new InvalidDataException(
                        "External mutation final evidence contains a duplicate target binding.");
            }

            var entries = new List<RecoveryExternalFinalEntry>(activeExternalPending.Entries.Count);
            foreach (var authorized in activeExternalPending.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveTargetRelativePath(
                    activeBase.Entries[authorized.EntryIndex].RelativePath);
                if (!suppliedByTarget.Remove(target, out var supplied)
                    || !IsValidFingerprint(supplied, allowDirectory: false)
                    || supplied.Length > authorized.MaximumBytes
                    || (allowsCommit && supplied.Kind != SnapshotLeafKind.File)
                    || (!allowsCommit
                        && supplied.Kind is not (SnapshotLeafKind.File or SnapshotLeafKind.Missing)))
                {
                    throw new InvalidDataException(
                        "An external mutation result is not an authorized bounded endpoint.");
                }
                var current = FileSnapshotJournal.CaptureRecoveryFingerprint(target, cancellationToken);
                if (!SameFingerprint(supplied, current))
                    throw new ConcurrentLeafChangeException(
                        "An external mutation result changed before exact final evidence could be published.",
                        target,
                        activeTransactionPath);
                entries.Add(new RecoveryExternalFinalEntry
                {
                    EntryIndex = authorized.EntryIndex,
                    FinalState = supplied
                });
            }
            if (suppliedByTarget.Count != 0)
                throw new InvalidDataException(
                    "External mutation final evidence contains an unauthorized target.");

            var document = new RecoveryExternalFinal
            {
                Version = CurrentFormatVersion,
                TransactionId = activeBase.TransactionId,
                BaseSha256 = activeBaseArtifact.Sha256,
                PreparedSha256 = activePreparedArtifact.Sha256,
                PendingSha256 = activeExternalPendingArtifact.Sha256,
                Disposition = disposition,
                Entries = entries
            };
            var path = Path.Combine(activeTransactionPath, ExternalFinalFileName);
            activeExternalFinalArtifact = PublishArtifact(path, document, cancellationToken);
            activeExternalFinalAllowsCommit = allowsCommit;
            if (allowsCommit)
            {
                foreach (var entry in entries)
                    expectedCommittedStates[entry.EntryIndex] = entry.FinalState;
            }
            ExternalFinalPublishedTestHook?.Invoke(path);
        }
    }

    internal static AtomicCommitRecoveryPlan? ReservePreparedWrite(
        string targetPath,
        bool keepBackup) =>
        ActiveSession.Value?.ReservePreparedWriteCore(targetPath, keepBackup);

    internal static void MarkPreparedWriteReady(AtomicCommitRecoveryPlan? plan)
    {
        if (plan is null) return;
        var session = ActiveSession.Value
            ?? throw new InvalidOperationException(
                "A durable prepared-write reservation lost its active session.");
        session.MarkPreparedWriteReadyCore(plan, validatedPreparedFingerprint: null);
    }

    internal static void MarkPreparedWriteReady(
        AtomicCommitRecoveryPlan? plan,
        SnapshotLeafFingerprint validatedPreparedFingerprint)
    {
        if (plan is null) return;
        ArgumentNullException.ThrowIfNull(validatedPreparedFingerprint);
        var session = ActiveSession.Value
            ?? throw new InvalidOperationException(
                "A durable prepared-write reservation lost its active session.");
        session.MarkPreparedWriteReadyCore(plan, validatedPreparedFingerprint);
    }

    internal void MarkResolved(CancellationToken cancellationToken = default)
        => MarkResolved(trustedWriteBoundary: null, cancellationToken);

    internal void MarkResolved(
        PathSafety.TrustedWriteBoundary? trustedWriteBoundary,
        CancellationToken cancellationToken = default)
    {
        MarkResolvedCore(
            expectedCommittedStates,
            "committed",
            "a leaf commit did not match its published intent",
            trustedWriteBoundary,
            cancellationToken);
    }

    internal void MarkRollbackResolved(CancellationToken cancellationToken = default)
    {
        MarkResolvedCore(
            activeBase?.Entries.Select(entry => entry.InitialState).ToArray(),
            "rolled-back",
            "a rolled-back leaf did not match its immutable initial state",
            trustedWriteBoundary: null,
            cancellationToken);
    }

    private void MarkResolvedCore(
        IReadOnlyList<SnapshotLeafFingerprint>? expectedStates,
        string outcome,
        string mismatchReason,
        PathSafety.TrustedWriteBoundary? trustedWriteBoundary,
        CancellationToken cancellationToken)
    {
        if (!enabled) return;
        lock (sync)
        {
            ThrowIfDisposed();
            if (activeBase is null
                || activeTransactionPath is null
                || activeBaseArtifact is null
                || activePreparedArtifact is null
                || activeResolvedArtifact is not null)
            {
                return;
            }
            if (outcome.Equals("committed", StringComparison.Ordinal)
                && activePendingCommitPlan is not null)
            {
                throw new InvalidOperationException(
                    "A durable commit cannot resolve while a prepared-write reservation is incomplete.");
            }
            if (outcome.Equals("committed", StringComparison.Ordinal)
                && activeExternalPendingArtifact is not null
                && activeExternalFinalArtifact is null)
            {
                throw new InvalidOperationException(
                    "A durable external mutation cannot resolve before exact final evidence is published.");
            }
            if (outcome.Equals("committed", StringComparison.Ordinal)
                && activeExternalFinalArtifact is not null
                && !activeExternalFinalAllowsCommit)
            {
                throw new InvalidOperationException(
                    "Rollback-only external mutation evidence cannot resolve as a committed transaction.");
            }
            VerifyRootsUnchanged();
            if (expectedStates is null
                || expectedStates.Count != activeBase.Entries.Count)
            {
                throw new InvalidOperationException("The durable transaction lost its expected commit state.");
            }
            CleanupForwardIntermediates(
                activeCommitReservations,
                activeTransactionPath,
                cancellationToken);
            for (var index = 0; index < activeBase.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveTargetRelativePath(activeBase.Entries[index].RelativePath);
                var current = trustedWriteBoundary is null
                    ? FileSnapshotJournal.CaptureRecoveryFingerprint(target, cancellationToken)
                    : trustedWriteBoundary.CaptureCurrentWriteFingerprint(target, cancellationToken);
                if (!SameFingerprint(expectedStates[index], current))
                    throw new ConcurrentLeafChangeException(
                        $"The durable transaction could not be resolved because {mismatchReason}.",
                        target,
                        activeTransactionPath);
            }
            var marker = new RecoveryResolved
            {
                Version = CurrentFormatVersion,
                TransactionId = activeBase.TransactionId,
                BaseSha256 = activeBaseArtifact.Sha256,
                PreparedSha256 = activePreparedArtifact.Sha256,
                Outcome = outcome,
                ReservationCount = activeReservationArtifacts.Count,
                IntentCount = nextIntentSequence,
                LastIntentSha256 = previousIntentSha256,
                RollbackCount = activeRollbackDoneCount,
                LastRollbackDoneSha256 = previousRollbackDoneSha256
            };
            activeResolvedArtifact = PublishArtifact(
                Path.Combine(activeTransactionPath, ResolvedFileName),
                marker,
                cancellationToken);
        }
    }

    internal void DetachPreservedEvidence()
    {
        if (!enabled) return;
        lock (sync)
        {
            ThrowIfDisposed();
            preserveEvidence = true;
        }
    }

    internal void Finish(
        bool preserveRecoveryEvidence,
        IReadOnlyList<string> snapshotCleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(snapshotCleanupFailures);
        if (!enabled) return;
        lock (sync)
        {
            ThrowIfDisposed();
            if (finished) return;
            finished = true;
            preserveEvidence |= preserveRecoveryEvidence || snapshotCleanupFailures.Count > 0;
            if (preserveEvidence
                || activeTransactionPath is null
                || activeBaseArtifact is null
                || activeResolvedArtifact is null)
            {
                return;
            }

            try
            {
                var artifacts = new List<FileArtifact>(
                    activeSnapshotReadyArtifacts.Count
                    + activeSnapshotDataArtifacts.Count
                    + activeReservationArtifacts.Count
                    + activeIntentArtifacts.Count
                    + activeRollbackArtifacts.Count
                    + 5)
                {
                    activeBaseArtifact
                };
                artifacts.AddRange(activeSnapshotReadyArtifacts);
                artifacts.AddRange(activeSnapshotDataArtifacts);
                artifacts.Add(activePreparedArtifact!);
                if (activeExternalPendingArtifact is not null)
                    artifacts.Add(activeExternalPendingArtifact);
                if (activeExternalFinalArtifact is not null)
                    artifacts.Add(activeExternalFinalArtifact);
                artifacts.AddRange(activeReservationArtifacts);
                artifacts.AddRange(activeIntentArtifacts);
                artifacts.AddRange(activeRollbackArtifacts);
                artifacts.Add(activeResolvedArtifact);
                DeleteTransactionRecord(
                    activeTransactionPath,
                    activeTransactionSnapshot,
                    artifacts);
                ClearActiveTransaction();
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Resolved transaction recovery-record cleanup was deferred ({exception.GetType().Name}).");
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        shardLock?.Dispose();
    }

    private AtomicCommitRecoveryPlan ReservePreparedWriteCore(
        string targetPath,
        bool keepBackup)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (activeBase is null
                || activeTransactionPath is null
                || activeBaseArtifact is null
                || activePreparedArtifact is null
                || entryIndexesByPath is null
                || expectedCommittedStates is null)
            {
                throw new InvalidOperationException("No durable transaction is active.");
            }
            if (activeExternalPendingArtifact is not null)
                throw new InvalidOperationException(
                    "Prepared writes cannot be mixed with an authorized external mutation.");
            if (nextIntentSequence >= MaximumRecoveryIntents)
                throw new InvalidDataException("The durable transaction exceeds its commit-intent limit.");
            if (activePendingCommitPlan is not null)
                throw new InvalidOperationException("A durable prepared-write reservation is already pending.");
            VerifyRootsUnchanged();

            var target = RequireTargetPath(targetPath);
            var backupPath = target + ".bak";
            if (!entryIndexesByPath.TryGetValue(target, out var targetIndex)
                || !entryIndexesByPath.TryGetValue(backupPath, out var backupIndex))
            {
                throw new InvalidDataException("An atomic write targeted a file outside the declared transaction.");
            }

            var targetBefore = FileSnapshotJournal.CaptureRecoveryFingerprint(target);
            var backupBefore = FileSnapshotJournal.CaptureRecoveryFingerprint(backupPath);
            if (!SameFingerprint(expectedCommittedStates[targetIndex], targetBefore)
                || !SameFingerprint(expectedCommittedStates[backupIndex], backupBefore))
            {
                throw new ConcurrentLeafChangeException(
                    "A transaction target changed before its prepared path was reserved.",
                    target,
                    backupPath);
            }
            if (targetBefore.Kind == SnapshotLeafKind.Directory
                || backupBefore.Kind == SnapshotLeafKind.Directory)
            {
                throw new IOException("A transaction target or deterministic backup is occupied by a directory.");
            }

            var prepared = RequireTargetPath(AtomicTemporaryFiles.CreatePath(target));
            var displaced = targetBefore.Kind == SnapshotLeafKind.File
                ? CreateReservedSibling(target, "displaced.tmp")
                : null;
            var rejected = targetBefore.Kind == SnapshotLeafKind.File
                ? CreateReservedSibling(target, "rejected.tmp")
                : null;
            var prior = targetBefore.Kind == SnapshotLeafKind.File && keepBackup
                ? CreateReservedSibling(backupPath, "prior.tmp")
                : null;
            var original = targetBefore.Kind == SnapshotLeafKind.File && keepBackup
                ? CreateReservedSibling(backupPath, "original.tmp")
                : null;
            var reservedPaths = new[] { prepared, displaced, rejected, prior, original }
                .Where(path => path is not null)
                .Cast<string>()
                .ToArray();
            if (reservedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != reservedPaths.Length)
                throw new InvalidDataException("A durable commit reservation contains duplicate target-tree paths.");
            foreach (var reservedPath in reservedPaths)
            {
                if (FileSnapshotJournal.CaptureRecoveryFingerprint(reservedPath).Kind
                    != SnapshotLeafKind.Missing)
                {
                    throw new ConcurrentLeafChangeException(
                        "A durable commit reservation path already exists.",
                        reservedPath);
                }
            }

            var plan = new AtomicCommitRecoveryPlan
            {
                TransactionId = activeBase.TransactionId,
                Sequence = nextIntentSequence,
                TargetPath = target,
                BackupPath = backupPath,
                PreparedPath = prepared,
                DisplacedPath = displaced,
                RejectedPath = rejected,
                PriorBackupPath = prior,
                OriginalRecoveryPath = original,
                KeepBackup = keepBackup,
                TargetBefore = targetBefore,
                BackupBefore = backupBefore
            };
            var reservation = new RecoveryCommitReservation
            {
                Version = CurrentFormatVersion,
                TransactionId = activeBase.TransactionId,
                Sequence = nextIntentSequence,
                BaseSha256 = activeBaseArtifact.Sha256,
                PreviousIntentSha256 = previousIntentSha256,
                TargetEntryIndex = targetIndex,
                BackupEntryIndex = backupIndex,
                KeepBackup = keepBackup,
                TargetBefore = targetBefore,
                BackupBefore = backupBefore,
                PreparedRelativePath = ToTargetRelativePath(prepared),
                DisplacedRelativePath = displaced is null ? null : ToTargetRelativePath(displaced),
                RejectedRelativePath = rejected is null ? null : ToTargetRelativePath(rejected),
                PriorBackupRelativePath = prior is null ? null : ToTargetRelativePath(prior),
                OriginalRecoveryRelativePath = original is null ? null : ToTargetRelativePath(original)
            };
            var reservationPath = Path.Combine(
                activeTransactionPath,
                ReservationFileName(nextIntentSequence));
            var reservationArtifact = PublishArtifact(
                reservationPath,
                reservation,
                CancellationToken.None);
            activeReservationArtifacts.Add(reservationArtifact);
            activePendingCommitPlan = plan;
            activePendingReservation = reservation;
            activePendingReservationArtifact = reservationArtifact;
            activeCommitReservations.Add(new ResolvedCommitReservation(
                plan,
                reservation,
                reservationArtifact,
                null,
                null));
            ReservationPublishedTestHook?.Invoke(reservationPath);
            return plan;
        }
    }

    private void MarkPreparedWriteReadyCore(
        AtomicCommitRecoveryPlan plan,
        SnapshotLeafFingerprint? validatedPreparedFingerprint)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(activePendingCommitPlan, plan)
                || activePendingReservation is null
                || activePendingReservationArtifact is null
                || activeBase is null
                || activeTransactionPath is null
                || activeBaseArtifact is null
                || expectedCommittedStates is null)
            {
                throw new InvalidOperationException("The durable prepared-write reservation is not active.");
            }
            VerifyRootsUnchanged();
            if (validatedPreparedFingerprint is null)
            {
                FlushPreparedFile(plan.PreparedPath);
            }
            else if (validatedPreparedFingerprint.Kind != SnapshotLeafKind.File
                     || validatedPreparedFingerprint.VolumeSerialNumber is null
                     || validatedPreparedFingerprint.FileIndex is null
                     || validatedPreparedFingerprint.Sha256 is not { Length: 32 })
            {
                throw new InvalidDataException(
                    "Validated prepared-write evidence must identify one regular file.");
            }
            var reservation = activePendingReservation;
            var targetBefore = FileSnapshotJournal.CaptureRecoveryFingerprint(plan.TargetPath);
            var backupBefore = FileSnapshotJournal.CaptureRecoveryFingerprint(plan.BackupPath);
            if (!SameFingerprint(reservation.TargetBefore, targetBefore)
                || !SameFingerprint(reservation.BackupBefore, backupBefore))
            {
                throw new ConcurrentLeafChangeException(
                    "A transaction target changed while its prepared output was being created.",
                    plan.TargetPath,
                    plan.BackupPath);
            }
            foreach (var auxiliaryPath in EnumerateAuxiliaryPaths(plan))
            {
                if (FileSnapshotJournal.CaptureRecoveryFingerprint(auxiliaryPath).Kind
                    != SnapshotLeafKind.Missing)
                {
                    throw new ConcurrentLeafChangeException(
                        "A reserved transaction intermediate appeared before commit readiness.",
                        auxiliaryPath);
                }
            }
            var targetAfter = FileSnapshotJournal.CaptureRecoveryFingerprint(plan.PreparedPath);
            if (targetAfter.Kind != SnapshotLeafKind.File)
                throw new InvalidDataException("A prepared transaction output is not a regular file.");
            if (validatedPreparedFingerprint is not null
                && !SameFingerprint(validatedPreparedFingerprint, targetAfter))
            {
                throw new ConcurrentLeafChangeException(
                    "The validated prepared output changed before recovery readiness was published.",
                    plan.PreparedPath);
            }
            plan.PreparedFingerprint = targetAfter;
            var backupAfter = plan.KeepBackup && targetBefore.Kind == SnapshotLeafKind.File
                ? targetBefore
                : backupBefore;
            var intent = new RecoveryIntent
            {
                Version = CurrentFormatVersion,
                TransactionId = activeBase.TransactionId,
                Sequence = nextIntentSequence,
                BaseSha256 = activeBaseArtifact.Sha256,
                PreviousIntentSha256 = previousIntentSha256,
                ReservationSha256 = activePendingReservationArtifact.Sha256,
                TargetEntryIndex = reservation.TargetEntryIndex,
                BackupEntryIndex = reservation.BackupEntryIndex,
                KeepBackup = plan.KeepBackup,
                TargetBefore = targetBefore,
                BackupBefore = backupBefore,
                TargetAfter = targetAfter,
                BackupAfter = backupAfter
            };
            var intentPath = Path.Combine(
                activeTransactionPath,
                IntentFileName(nextIntentSequence));
            var artifact = PublishArtifact(intentPath, intent, CancellationToken.None);
            activeIntentArtifacts.Add(artifact);
            activeCommitReservations[^1] = new ResolvedCommitReservation(
                plan,
                reservation,
                activePendingReservationArtifact,
                intent,
                artifact);
            previousIntentSha256 = artifact.Sha256;
            expectedCommittedStates[reservation.TargetEntryIndex] = targetAfter;
            expectedCommittedStates[reservation.BackupEntryIndex] = backupAfter;
            nextIntentSequence++;
            activePendingCommitPlan = null;
            activePendingReservation = null;
            activePendingReservationArtifact = null;
            IntentPublishedTestHook?.Invoke(intentPath);
            PreparedReadyPublishedTestHook?.Invoke(plan.PreparedPath);
        }
    }

    internal FileRollbackResult RollbackDurably(
        IReadOnlySet<string>? alreadyConcurrentPaths = null)
    {
        if (!enabled)
            throw new InvalidOperationException("Durable rollback requires an enabled recovery authority.");
        lock (sync)
        {
            ThrowIfDisposed();
            if (publicationOutcomeUncertain)
                throw new IOException("Durable recovery was deferred after an ambiguous artifact publication.");
            if (activeTransactionPath is null)
                throw new InvalidOperationException("No durable transaction is active.");
            var plan = PreflightTransaction(activeTransactionPath, CancellationToken.None);
            ExecuteDurableRollback(plan, CancellationToken.None);
            if (alreadyConcurrentPaths is { Count: > 0 })
            {
                return new FileRollbackResult(
                    new HashSet<string>(alreadyConcurrentPaths, StringComparer.OrdinalIgnoreCase),
                    [],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            PublishRollbackResolution(plan, isActive: true, CancellationToken.None);
            return new FileRollbackResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                [],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private void ExecuteDurableRollback(
        RecoveryPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Document is null
            || plan.BaseArtifact is null
            || plan.PreparedArtifact is null
            || plan.ResolvedEntries is null)
        {
            throw new InvalidOperationException("A durable rollback plan is incomplete.");
        }
        foreach (var mutation in plan.RollbackMutations.OrderBy(item => item.Plan.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContinueRollbackMutation(plan, mutation, cancellationToken);
        }
        foreach (var rollback in plan.RollbackEntries.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = plan.ResolvedEntries.Single(candidate =>
                candidate.TargetPath.Equals(
                    rollback.Snapshot.Path,
                    StringComparison.OrdinalIgnoreCase));
            var mutation = CreateRollbackMutation(plan, entry, rollback.ExpectedCurrent, cancellationToken);
            plan.RollbackMutations.Add(mutation);
            ContinueRollbackMutation(plan, mutation, cancellationToken);
        }
        CleanupForwardIntermediates(plan.CommitReservations, plan.TransactionPath, cancellationToken);
        ValidateRollbackPostflight(plan, cancellationToken);
    }

    private ResolvedRollbackMutation CreateRollbackMutation(
        RecoveryPlan recovery,
        ResolvedRecoveryEntry entry,
        SnapshotLeafFingerprint expectedCurrent,
        CancellationToken cancellationToken)
    {
        var sequence = recovery.RollbackMutations.Count;
        var kind = entry.Entry.InitialState.Kind switch
        {
            SnapshotLeafKind.File when expectedCurrent.Kind == SnapshotLeafKind.Missing => "restore-missing",
            SnapshotLeafKind.File when expectedCurrent.Kind == SnapshotLeafKind.File => "restore-existing",
            SnapshotLeafKind.Missing when expectedCurrent.Kind == SnapshotLeafKind.File => "remove-created",
            _ => throw new InvalidDataException("A durable rollback does not support this target transition.")
        };
        var preparedPath = entry.Entry.InitialState.Kind == SnapshotLeafKind.File
            ? RequireTargetPath(AtomicTemporaryFiles.CreatePath(entry.TargetPath, "restore.tmp"))
            : null;
        var displacedPath = kind is "restore-existing" or "remove-created"
            ? CreateRollbackReservedSibling(
                entry.TargetPath,
                recovery.Document!.TransactionId,
                sequence,
                "rollback-displaced.tmp")
            : null;
        foreach (var path in new[] { preparedPath, displacedPath }.Where(path => path is not null))
        {
            if (FileSnapshotJournal.CaptureRecoveryFingerprint(path!, cancellationToken).Kind
                != SnapshotLeafKind.Missing)
            {
                throw new ConcurrentLeafChangeException(
                    "A rollback reservation path already exists.",
                    path!,
                    recovery.TransactionPath);
            }
        }
        var document = new RecoveryRollbackPlan
        {
            Version = CurrentFormatVersion,
            TransactionId = recovery.Document!.TransactionId,
            Sequence = sequence,
            BaseSha256 = recovery.BaseArtifact!.Sha256,
            PreparedSha256 = recovery.PreparedArtifact!.Sha256,
            ForwardIntentCount = recovery.IntentArtifacts.Count,
            LastForwardIntentSha256 = recovery.IntentArtifacts.LastOrDefault()?.Sha256,
            PreviousRollbackDoneSha256 = recovery.RollbackMutations.LastOrDefault()?.DoneArtifact?.Sha256,
            EntryIndex = entry.Index,
            Kind = kind,
            ExpectedCurrent = expectedCurrent,
            PreparedRelativePath = preparedPath is null ? null : ToTargetRelativePath(preparedPath),
            DisplacedRelativePath = displacedPath is null ? null : ToTargetRelativePath(displacedPath)
        };
        var artifact = PublishArtifact(
            Path.Combine(recovery.TransactionPath, RollbackPlanFileName(sequence)),
            document,
            cancellationToken);
        TrackRollbackArtifact(recovery, artifact);
        RollbackPlanPublishedTestHook?.Invoke(artifact.Path);
        return new ResolvedRollbackMutation
        {
            Plan = document,
            PlanArtifact = artifact,
            TargetPath = entry.TargetPath,
            PreparedPath = preparedPath,
            DisplacedPath = displacedPath
        };
    }

    private void ContinueRollbackMutation(
        RecoveryPlan recovery,
        ResolvedRollbackMutation mutation,
        CancellationToken cancellationToken)
    {
        var entry = recovery.ResolvedEntries![mutation.Plan.EntryIndex];
        if (mutation.Ready is null)
        {
            SnapshotLeafFingerprint predicted;
            if (mutation.PreparedPath is null)
            {
                predicted = new SnapshotLeafFingerprint(SnapshotLeafKind.Missing);
            }
            else
            {
                PrepareRollbackRestore(recovery, entry, mutation.PreparedPath, cancellationToken);
                predicted = FileSnapshotJournal.CaptureRecoveryFingerprint(
                    mutation.PreparedPath,
                    cancellationToken);
            }
            var ready = new RecoveryRollbackReady
            {
                Version = CurrentFormatVersion,
                TransactionId = recovery.Document!.TransactionId,
                Sequence = mutation.Plan.Sequence,
                PlanSha256 = mutation.PlanArtifact.Sha256,
                PredictedRestored = predicted
            };
            var readyArtifact = PublishArtifact(
                Path.Combine(recovery.TransactionPath, RollbackReadyFileName(mutation.Plan.Sequence)),
                ready,
                cancellationToken);
            mutation.Ready = ready;
            mutation.ReadyArtifact = readyArtifact;
            TrackRollbackArtifact(recovery, readyArtifact);
            RollbackReadyPublishedTestHook?.Invoke(readyArtifact.Path);
        }

        if (mutation.Applied is null)
        {
            ApplyRollbackMutation(mutation, recovery.TransactionPath, cancellationToken);
            var actual = FileSnapshotJournal.CaptureRecoveryFingerprint(
                mutation.TargetPath,
                cancellationToken);
            var displaced = mutation.DisplacedPath is null
                ? new SnapshotLeafFingerprint(SnapshotLeafKind.Missing)
                : FileSnapshotJournal.CaptureRecoveryFingerprint(
                    mutation.DisplacedPath,
                    cancellationToken);
            if (!SameFingerprint(actual, mutation.Ready!.PredictedRestored)
                || !SameFingerprint(
                    displaced,
                    mutation.DisplacedPath is null
                        ? new SnapshotLeafFingerprint(SnapshotLeafKind.Missing)
                        : mutation.Plan.ExpectedCurrent))
            {
                throw new ConcurrentLeafChangeException(
                    $"A rollback mutation did not reach its exact published endpoint (target={actual.Kind}/{actual.FileIndex}, expected={mutation.Ready.PredictedRestored.Kind}/{mutation.Ready.PredictedRestored.FileIndex}, displaced={displaced.Kind}/{displaced.FileIndex}, displacedExpected={mutation.Plan.ExpectedCurrent.Kind}/{mutation.Plan.ExpectedCurrent.FileIndex}, final={AtomicTemporaryFiles.LastMoveFinalPath}).",
                    mutation.TargetPath,
                    mutation.DisplacedPath ?? recovery.TransactionPath,
                    recovery.TransactionPath);
            }
            var applied = new RecoveryRollbackApplied
            {
                Version = CurrentFormatVersion,
                TransactionId = recovery.Document!.TransactionId,
                Sequence = mutation.Plan.Sequence,
                ReadySha256 = mutation.ReadyArtifact!.Sha256,
                ActualRestored = actual,
                DisplacedState = displaced
            };
            var appliedArtifact = PublishArtifact(
                Path.Combine(recovery.TransactionPath, RollbackAppliedFileName(mutation.Plan.Sequence)),
                applied,
                cancellationToken);
            mutation.Applied = applied;
            mutation.AppliedArtifact = appliedArtifact;
            TrackRollbackArtifact(recovery, appliedArtifact);
            RollbackAppliedPublishedTestHook?.Invoke(appliedArtifact.Path);
        }

        DeleteKnownIntermediate(
            mutation.PreparedPath,
            mutation.Ready!.PredictedRestored,
            recovery.TransactionPath);
        DeleteKnownIntermediate(
            mutation.DisplacedPath,
            mutation.Plan.ExpectedCurrent,
            recovery.TransactionPath);
        if (mutation.Done is null)
        {
            var actual = FileSnapshotJournal.CaptureRecoveryFingerprint(
                mutation.TargetPath,
                cancellationToken);
            if (!SameFingerprint(actual, mutation.Applied!.ActualRestored))
                throw new ConcurrentLeafChangeException(
                    "A rollback target changed before completion evidence was published.",
                    mutation.TargetPath,
                    recovery.TransactionPath);
            var done = new RecoveryRollbackDone
            {
                Version = CurrentFormatVersion,
                TransactionId = recovery.Document!.TransactionId,
                Sequence = mutation.Plan.Sequence,
                AppliedSha256 = mutation.AppliedArtifact!.Sha256,
                PreviousDoneSha256 = recovery.RollbackMutations
                    .Where(candidate => candidate.Plan.Sequence < mutation.Plan.Sequence)
                    .OrderBy(candidate => candidate.Plan.Sequence)
                    .LastOrDefault()?.DoneArtifact?.Sha256,
                ActualRestored = actual
            };
            var doneArtifact = PublishArtifact(
                Path.Combine(recovery.TransactionPath, RollbackDoneFileName(mutation.Plan.Sequence)),
                done,
                cancellationToken);
            mutation.Done = done;
            mutation.DoneArtifact = doneArtifact;
            TrackRollbackArtifact(recovery, doneArtifact);
            previousRollbackDoneSha256 = doneArtifact.Sha256;
            activeRollbackDoneCount = Math.Max(activeRollbackDoneCount, mutation.Plan.Sequence + 1);
            RollbackDonePublishedTestHook?.Invoke(doneArtifact.Path);
        }
    }

    private static void PrepareRollbackRestore(
        RecoveryPlan recovery,
        ResolvedRecoveryEntry entry,
        string preparedPath,
        CancellationToken cancellationToken)
    {
        if (entry.SnapshotPath is null)
            throw new InvalidDataException("A durable rollback snapshot is missing.");
        if (Directory.Exists(preparedPath))
            throw new ConcurrentLeafChangeException(
                "A rollback prepared reservation is occupied by a directory.",
                preparedPath,
                recovery.TransactionPath);
        if (File.Exists(preparedPath))
        {
            var abandoned = FileSnapshotJournal.CaptureRecoveryFingerprint(
                preparedPath,
                cancellationToken);
            if (abandoned.Kind != SnapshotLeafKind.File
                || !AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                    preparedPath,
                    abandoned.VolumeSerialNumber,
                    abandoned.FileIndex,
                    abandoned.Length,
                    abandoned.Sha256))
            {
                throw new ConcurrentLeafChangeException(
                    "An interrupted rollback prepared leaf could not be removed by exact identity.",
                    preparedPath,
                    recovery.TransactionPath);
            }
        }
        var snapshotArtifact = recovery.Artifacts.Single(artifact =>
            artifact.Path.Equals(entry.SnapshotPath, StringComparison.OrdinalIgnoreCase));
        var snapshotBefore = FileSnapshotJournal.CaptureRecoveryFingerprint(
            entry.SnapshotPath,
            cancellationToken);
        if (!SameFingerprint(snapshotBefore, snapshotArtifact.Fingerprint))
            throw new ConcurrentLeafChangeException(
                "A durable rollback snapshot changed before restore copy.",
                entry.SnapshotPath,
                recovery.TransactionPath);
        AtomicFile.CopyFlushedBounded(
            entry.SnapshotPath,
            preparedPath,
            PathSafety.MaximumTrustedLeafBytes,
            cancellationToken);
        var snapshotAfter = FileSnapshotJournal.CaptureRecoveryFingerprint(
            entry.SnapshotPath,
            cancellationToken);
        var prepared = FileSnapshotJournal.CaptureRecoveryFingerprint(
            preparedPath,
            cancellationToken);
        if (!SameFingerprint(snapshotBefore, snapshotAfter)
            || !FileSnapshotJournal.HasSameContent(entry.Entry.InitialState, prepared))
        {
            throw new ConcurrentLeafChangeException(
                "A durable rollback snapshot changed during restore copy.",
                entry.SnapshotPath,
                preparedPath,
                recovery.TransactionPath);
        }
    }

    private static void ApplyRollbackMutation(
        ResolvedRollbackMutation mutation,
        string transactionPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = FileSnapshotJournal.CaptureRecoveryFingerprint(mutation.TargetPath, cancellationToken);
        var prepared = mutation.PreparedPath is null
            ? new SnapshotLeafFingerprint(SnapshotLeafKind.Missing)
            : FileSnapshotJournal.CaptureRecoveryFingerprint(mutation.PreparedPath, cancellationToken);
        var displaced = mutation.DisplacedPath is null
            ? new SnapshotLeafFingerprint(SnapshotLeafKind.Missing)
            : FileSnapshotJournal.CaptureRecoveryFingerprint(mutation.DisplacedPath, cancellationToken);

        if (mutation.DisplacedPath is not null
            && SameFingerprint(target, mutation.Plan.ExpectedCurrent)
            && displaced.Kind == SnapshotLeafKind.Missing)
        {
            if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                    mutation.TargetPath,
                    mutation.DisplacedPath,
                    mutation.Plan.ExpectedCurrent))
            {
                throw new ConcurrentLeafChangeException(
                    $"A rollback target changed before handle-bound displacement (win32={AtomicTemporaryFiles.LastMoveError}).",
                    mutation.TargetPath,
                    mutation.DisplacedPath,
                    transactionPath);
            }
            target = new SnapshotLeafFingerprint(SnapshotLeafKind.Missing);
            displaced = mutation.Plan.ExpectedCurrent;
        }

        if (mutation.PreparedPath is not null
            && target.Kind == SnapshotLeafKind.Missing
            && SameFingerprint(prepared, mutation.Ready!.PredictedRestored))
        {
            if (!AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                    mutation.PreparedPath,
                    mutation.TargetPath,
                    mutation.Ready.PredictedRestored))
            {
                throw new ConcurrentLeafChangeException(
                    $"A rollback prepared leaf changed before handle-bound restoration (win32={AtomicTemporaryFiles.LastMoveError}).",
                    mutation.PreparedPath,
                    mutation.TargetPath,
                    transactionPath);
            }
        }
    }

    private static void ValidateRollbackPostflight(
        RecoveryPlan recovery,
        CancellationToken cancellationToken)
    {
        var completed = recovery.RollbackMutations.ToDictionary(
            mutation => mutation.Plan.EntryIndex,
            mutation => mutation.Done?.ActualRestored
                        ?? throw new InvalidDataException("A rollback mutation has no durable completion record."));
        foreach (var entry in recovery.ResolvedEntries!)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = completed.TryGetValue(entry.Index, out var restored)
                ? restored
                : entry.Entry.InitialState;
            var current = FileSnapshotJournal.CaptureRecoveryFingerprint(
                entry.TargetPath,
                cancellationToken);
            if (!SameFingerprint(current, expected)
                || !HasSameInitialContentOrAbsence(current, entry.Entry.InitialState))
            {
                throw new ConcurrentLeafChangeException(
                    "A durable rollback failed its whole-transaction postflight.",
                    entry.TargetPath,
                    recovery.TransactionPath);
            }
        }
    }

    private static bool HasSameInitialContentOrAbsence(
        SnapshotLeafFingerprint current,
        SnapshotLeafFingerprint initial) =>
        initial.Kind switch
        {
            SnapshotLeafKind.Missing => current.Kind == SnapshotLeafKind.Missing,
            SnapshotLeafKind.File => FileSnapshotJournal.HasSameContent(current, initial),
            SnapshotLeafKind.Directory => SameFingerprint(current, initial),
            _ => false
        };

    private void PublishRollbackResolution(
        RecoveryPlan plan,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var lastDone = plan.RollbackMutations
            .OrderBy(mutation => mutation.Plan.Sequence)
            .LastOrDefault()?.DoneArtifact?.Sha256;
        var marker = new RecoveryResolved
        {
            Version = CurrentFormatVersion,
            TransactionId = plan.Document!.TransactionId,
            BaseSha256 = plan.BaseArtifact!.Sha256,
            PreparedSha256 = plan.PreparedArtifact!.Sha256,
            Outcome = "rolled-back",
            ReservationCount = plan.ReservationArtifacts.Count,
            IntentCount = plan.IntentArtifacts.Count,
            LastIntentSha256 = plan.IntentArtifacts.LastOrDefault()?.Sha256,
            RollbackCount = plan.RollbackMutations.Count,
            LastRollbackDoneSha256 = lastDone
        };
        var artifact = PublishArtifact(
            Path.Combine(plan.TransactionPath, ResolvedFileName),
            marker,
            cancellationToken);
        plan.ResolvedArtifact = artifact;
        plan.Artifacts.Add(artifact);
        if (isActive)
        {
            activeResolvedArtifact = artifact;
            previousRollbackDoneSha256 = lastDone;
            activeRollbackDoneCount = plan.RollbackMutations.Count;
        }
    }

    private static string CreateRollbackReservedSibling(
        string targetPath,
        Guid transactionId,
        int sequence,
        string kind)
    {
        var parent = Path.GetDirectoryName(targetPath)!;
        return Path.Combine(
            parent,
            $".{Path.GetFileName(targetPath)}.rtx-{transactionId:N}-{sequence:D8}-{Guid.NewGuid():N}.{kind}");
    }

    private void TrackRollbackArtifact(RecoveryPlan recovery, FileArtifact artifact)
    {
        recovery.Artifacts.Add(artifact);
        if (activeTransactionPath is not null
            && activeTransactionPath.Equals(recovery.TransactionPath, StringComparison.OrdinalIgnoreCase))
        {
            activeRollbackArtifacts.Add(artifact);
        }
    }

    private void RecoverPendingTransactions(
        IReadOnlySet<string> trustedTransientFiles,
        CancellationToken cancellationToken)
    {
        VerifyRootsUnchanged();
        var unexpectedShardDirectories = Directory.EnumerateDirectories(
                shardPath,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path => !IsTransactionDirectoryName(Path.GetFileName(path), out _))
            .Take(1)
            .ToArray();
        if (unexpectedShardDirectories.Length > 0)
            throw new InvalidDataException("The recovery authority shard contains an unknown directory.");
        var unexpectedShardFiles = Directory.EnumerateFiles(
                shardPath,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Equals(LockFileName, StringComparison.Ordinal)
                           && !trustedTransientFiles.Contains(Path.GetFullPath(path)))
            .Take(1)
            .ToArray();
        if (unexpectedShardFiles.Length > 0)
            throw new InvalidDataException("The recovery authority shard contains an unknown file.");

        var transactionPaths = Directory.EnumerateDirectories(
                shardPath,
                TransactionPrefix + "*",
                SearchOption.TopDirectoryOnly)
            .Take(MaximumRecoveryTransactions + 1)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (transactionPaths.Length > MaximumRecoveryTransactions)
            throw new InvalidDataException(
                $"The recovery authority contains more than {MaximumRecoveryTransactions} pending transactions.");

        var plans = new List<RecoveryPlan>(transactionPaths.Length);
        foreach (var transactionPath in transactionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plans.Add(PreflightTransaction(transactionPath, cancellationToken));
        }
        if (plans.Count(plan => plan.RequiresRollback) > 1)
            throw new InvalidDataException("More than one unresolved transaction targets the same root.");

        VerifyRootsUnchanged();
        foreach (var plan in plans.Where(plan => plan.RequiresRollback))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteDurableRollback(plan, cancellationToken);
            PublishRollbackResolution(plan, isActive: false, cancellationToken);
        }

        foreach (var plan in plans.Where(plan => !plan.IsEmpty))
        {
            if (plan.CleanupDocument is not null && plan.CleanupArtifact is not null)
            {
                ContinueCleanup(
                    plan.TransactionPath,
                    plan.TransactionSnapshot,
                    plan.CleanupDocument,
                    plan.CleanupArtifact);
            }
            else
            {
                CleanupForwardIntermediates(
                    plan.CommitReservations,
                    plan.TransactionPath,
                    cancellationToken);
                CleanupRollbackIntermediates(plan, cancellationToken);
                DeleteTransactionRecord(
                    plan.TransactionPath,
                    plan.TransactionSnapshot,
                    plan.Artifacts);
            }
        }
        foreach (var plan in plans.Where(plan => plan.IsEmpty))
            DeleteEmptyTransactionDirectory(plan.TransactionPath, plan.TransactionSnapshot);
        VerifyRootsUnchanged();
    }

    private static void CleanupRollbackIntermediates(
        RecoveryPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var mutation in plan.RollbackMutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutation.Ready is not null)
                DeleteKnownIntermediate(
                    mutation.PreparedPath,
                    mutation.Ready.PredictedRestored,
                    plan.TransactionPath);
            DeleteKnownIntermediate(
                mutation.DisplacedPath,
                mutation.Plan.ExpectedCurrent,
                plan.TransactionPath);
        }
    }

    private RecoveryPlan PreflightTransaction(
        string transactionPath,
        CancellationToken cancellationToken)
    {
        if (!IsTransactionDirectoryName(Path.GetFileName(transactionPath), out var transactionId))
            throw new InvalidDataException("A recovery transaction directory has an invalid identity.");
        PathSafety.EnsureNoReparsePoints(transactionPath, authority!.Root);
        var transactionSnapshot = PathSafety.GetExistingDirectorySnapshot(transactionPath);
        if (!transactionSnapshot.CanonicalPath.Equals(
                PathSafety.Normalize(transactionPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A recovery transaction directory resolved outside its expected path.");
        }
        if (Directory.EnumerateDirectories(transactionPath, "*", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidDataException("A recovery transaction record contains an unexpected subdirectory.");

        CleanupOrphanPublicationFiles(transactionPath);
        var files = Directory.EnumerateFiles(transactionPath, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumRecoveryArtifactFiles + 1)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (files.Length > MaximumRecoveryArtifactFiles)
            throw new InvalidDataException("A recovery transaction record exceeds its bounded file inventory.");
        if (files.Length == 0)
            return RecoveryPlan.Empty(transactionPath, transactionSnapshot);

        var cleanupPath = files.SingleOrDefault(path =>
            Path.GetFileName(path).Equals(CleanupFileName, StringComparison.Ordinal));
        if (cleanupPath is not null)
            return PreflightCleanup(
                transactionPath,
                transactionSnapshot,
                transactionId,
                cleanupPath,
                files,
                cancellationToken);

        var basePath = Path.Combine(transactionPath, BaseFileName);
        if (!File.Exists(basePath))
            throw new InvalidDataException("A recovery transaction is missing its immutable base record.");
        var intentPaths = new SortedDictionary<int, string>();
        var reservationPaths = new SortedDictionary<int, string>();
        var rollbackPlanPaths = new SortedDictionary<int, string>();
        var rollbackReadyPaths = new SortedDictionary<int, string>();
        var rollbackAppliedPaths = new SortedDictionary<int, string>();
        var rollbackDonePaths = new SortedDictionary<int, string>();
        var snapshotReadyPaths = new SortedDictionary<int, string>();
        var snapshotDataPaths = new SortedDictionary<int, string>();
        string? preparedPath = null;
        string? externalPendingPath = null;
        string? externalFinalPath = null;
        string? resolvedPath = null;
        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals(BaseFileName, StringComparison.Ordinal)) continue;
            if (fileName.Equals(PreparedFileName, StringComparison.Ordinal))
            {
                if (preparedPath is not null)
                    throw new InvalidDataException("A recovery transaction contains duplicate capture-seal evidence.");
                preparedPath = path;
                continue;
            }
            if (fileName.Equals(ExternalPendingFileName, StringComparison.Ordinal))
            {
                if (externalPendingPath is not null)
                    throw new InvalidDataException("A recovery transaction contains duplicate external-mutation authorization.");
                externalPendingPath = path;
                continue;
            }
            if (fileName.Equals(ExternalFinalFileName, StringComparison.Ordinal))
            {
                if (externalFinalPath is not null)
                    throw new InvalidDataException("A recovery transaction contains duplicate external-mutation final evidence.");
                externalFinalPath = path;
                continue;
            }
            if (fileName.Equals(ResolvedFileName, StringComparison.Ordinal))
            {
                if (resolvedPath is not null)
                    throw new InvalidDataException("A recovery transaction contains duplicate resolution evidence.");
                resolvedPath = path;
                continue;
            }
            if (TryParseReservationFileName(fileName, out var reservationSequence))
            {
                if (!reservationPaths.TryAdd(reservationSequence, path))
                    throw new InvalidDataException("A recovery transaction contains a duplicate commit reservation.");
                continue;
            }
            if (TryParseIntentFileName(fileName, out var sequence))
            {
                if (!intentPaths.TryAdd(sequence, path))
                    throw new InvalidDataException("A recovery transaction contains a duplicate commit intent.");
                continue;
            }
            if (TryParseRollbackPlanFileName(fileName, out var rollbackPlanSequence))
            {
                if (!rollbackPlanPaths.TryAdd(rollbackPlanSequence, path))
                    throw new InvalidDataException("A recovery transaction contains a duplicate rollback plan.");
                continue;
            }
            if (TryParseRollbackReadyFileName(fileName, out var rollbackReadySequence))
            {
                if (!rollbackReadyPaths.TryAdd(rollbackReadySequence, path))
                    throw new InvalidDataException("A recovery transaction contains duplicate rollback readiness.");
                continue;
            }
            if (TryParseRollbackAppliedFileName(fileName, out var rollbackAppliedSequence))
            {
                if (!rollbackAppliedPaths.TryAdd(rollbackAppliedSequence, path))
                    throw new InvalidDataException("A recovery transaction contains duplicate rollback application evidence.");
                continue;
            }
            if (TryParseRollbackDoneFileName(fileName, out var rollbackDoneSequence))
            {
                if (!rollbackDonePaths.TryAdd(rollbackDoneSequence, path))
                    throw new InvalidDataException("A recovery transaction contains duplicate rollback completion evidence.");
                continue;
            }
            if (TryParseSnapshotReadyFileName(fileName, out var readyIndex))
            {
                if (!snapshotReadyPaths.TryAdd(readyIndex, path))
                    throw new InvalidDataException("A recovery transaction contains duplicate snapshot evidence.");
                continue;
            }
            if (TryParseSnapshotDataFileName(fileName, out var dataIndex))
            {
                if (!snapshotDataPaths.TryAdd(dataIndex, path))
                    throw new InvalidDataException("A recovery transaction contains duplicate snapshot data.");
                continue;
            }
            throw new InvalidDataException(
                "A recovery transaction contains an unknown artifact.");
        }
        if (intentPaths.Count > MaximumRecoveryIntents)
            throw new InvalidDataException("A recovery transaction exceeds its commit-intent limit.");
        if (reservationPaths.Count > MaximumRecoveryIntents
            || reservationPaths.Count < intentPaths.Count
            || reservationPaths.Count > intentPaths.Count + 1)
        {
            throw new InvalidDataException("A recovery transaction has an invalid reservation/intent cardinality.");
        }
        for (var sequence = 0; sequence < reservationPaths.Count; sequence++)
        {
            if (!reservationPaths.ContainsKey(sequence))
                throw new InvalidDataException("A recovery transaction has a missing commit-reservation sequence.");
        }
        for (var sequence = 0; sequence < intentPaths.Count; sequence++)
        {
            if (!intentPaths.ContainsKey(sequence))
                throw new InvalidDataException("A recovery transaction has a missing commit-intent sequence.");
        }
        ValidateRollbackArtifactCardinality(
            rollbackPlanPaths,
            rollbackReadyPaths,
            rollbackAppliedPaths,
            rollbackDonePaths);

        var (document, baseArtifact) = ReadArtifact<RecoveryBase>(basePath, cancellationToken);
        var resolvedEntries = ValidateBase(document, transactionId, transactionPath);
        if (snapshotReadyPaths.Keys.Any(index => index < 0 || index >= document.Entries.Count)
            || snapshotDataPaths.Keys.Any(index => index < 0 || index >= document.Entries.Count))
        {
            throw new InvalidDataException("A recovery snapshot artifact references an unknown entry.");
        }
        var artifacts = new List<FileArtifact>(files.Length) { baseArtifact };
        var snapshotReadyArtifacts = new List<FileArtifact>();
        var snapshotStates = new Dictionary<int, SnapshotLeafFingerprint>();
        foreach (var pair in snapshotReadyPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (ready, artifact) = ReadArtifact<RecoverySnapshotReady>(pair.Value, cancellationToken);
            ValidateSnapshotReady(document, baseArtifact, ready, pair.Key);
            if (!snapshotStates.TryAdd(pair.Key, ready.SnapshotState))
                throw new InvalidDataException("A recovery transaction contains duplicate snapshot state evidence.");
            snapshotReadyArtifacts.Add(artifact);
            artifacts.Add(artifact);
        }
        foreach (var resolved in resolvedEntries)
        {
            var hasReady = snapshotStates.TryGetValue(resolved.Index, out var expectedSnapshot);
            var hasData = snapshotDataPaths.TryGetValue(resolved.Index, out var snapshotPath);
            if (resolved.Entry.InitialState.Kind != SnapshotLeafKind.File)
            {
                if (hasReady || hasData)
                    throw new InvalidDataException("A non-file target has unexpected recovery snapshot artifacts.");
                continue;
            }
            if (hasData && !hasReady)
                throw new InvalidDataException("A recovery snapshot has no published identity evidence.");
            if (hasReady && hasData)
            {
                var expectedSnapshotPath = Path.Combine(
                    transactionPath,
                    resolved.Entry.SnapshotFileName!);
                if (!PathSafety.Normalize(snapshotPath!).Equals(
                        expectedSnapshotPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A recovery snapshot resolved outside its declared artifact path.");
                }
                var actual = FileSnapshotJournal.CaptureRecoveryFingerprint(snapshotPath!, cancellationToken);
                if (!SameFingerprint(expectedSnapshot, actual))
                    throw new ConcurrentLeafChangeException(
                        "A recovery snapshot changed after its identity was published.",
                        snapshotPath!);
                resolved.SnapshotPath = snapshotPath;
                artifacts.Add(new FileArtifact(
                    snapshotPath!,
                    actual,
                    Convert.ToHexString(actual.Sha256!)));
            }
        }

        FileArtifact? preparedArtifact = null;
        if (preparedPath is not null)
        {
            var (prepared, artifact) = ReadArtifact<RecoveryPrepared>(preparedPath, cancellationToken);
            ValidatePrepared(document, baseArtifact, prepared, snapshotReadyArtifacts);
            foreach (var resolved in resolvedEntries.Where(entry =>
                         entry.Entry.InitialState.Kind == SnapshotLeafKind.File))
            {
                if (resolved.SnapshotPath is null && resolvedPath is null)
                    throw new IOException("A sealed recovery transaction is missing a required snapshot.");
            }
            preparedArtifact = artifact;
            artifacts.Add(artifact);
        }
        else
        {
            if (reservationPaths.Count > 0
                || intentPaths.Count > 0
                || externalPendingPath is not null
                || externalFinalPath is not null
                || rollbackPlanPaths.Count > 0
                || resolvedPath is not null)
                throw new InvalidDataException("An unsealed snapshot capture contains commit or resolution evidence.");
            foreach (var resolved in resolvedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = FileSnapshotJournal.CaptureRecoveryFingerprint(
                    resolved.TargetPath,
                    cancellationToken);
                if (!SameFingerprint(current, resolved.Entry.InitialState))
                    throw new ConcurrentLeafChangeException(
                        "An incomplete snapshot capture found a changed target; recovery evidence was preserved.",
                        resolved.TargetPath,
                        transactionPath);
            }
        }

        Dictionary<int, long>? externalLimits = null;
        Dictionary<int, SnapshotLeafFingerprint>? externalFinalStates = null;
        var externalFinalAllowsCommit = false;
        if (externalPendingPath is not null)
        {
            if (preparedArtifact is null
                || reservationPaths.Count != 0
                || intentPaths.Count != 0)
            {
                throw new InvalidDataException(
                    "External mutation authorization cannot be mixed with an unsealed or prepared-write transaction.");
            }
            var (pending, pendingArtifact) = ReadArtifact<RecoveryExternalPending>(
                externalPendingPath,
                cancellationToken);
            externalLimits = ValidateExternalPending(
                document,
                baseArtifact,
                preparedArtifact,
                pending);
            artifacts.Add(pendingArtifact);
            if (externalFinalPath is not null)
            {
                var (final, finalArtifact) = ReadArtifact<RecoveryExternalFinal>(
                    externalFinalPath,
                    cancellationToken);
                externalFinalStates = ValidateExternalFinal(
                    document,
                    baseArtifact,
                    preparedArtifact,
                    pendingArtifact,
                    externalLimits,
                    final,
                    out externalFinalAllowsCommit);
                artifacts.Add(finalArtifact);
            }
        }
        else if (externalFinalPath is not null)
        {
            throw new InvalidDataException(
                "External mutation final evidence has no pending authorization.");
        }

        var reservationArtifacts = new List<FileArtifact>(reservationPaths.Count);
        var intentArtifacts = new List<FileArtifact>(intentPaths.Count);
        var commitReservations = new List<ResolvedCommitReservation>(reservationPaths.Count);
        var committedStates = document.Entries.Select(entry => entry.InitialState).ToArray();
        var lastTransitions = new Dictionary<int, LeafTransition>();
        if (externalFinalStates is not null)
        {
            foreach (var pair in externalFinalStates)
            {
                lastTransitions[pair.Key] = new LeafTransition(
                    document.Entries[pair.Key].InitialState,
                    pair.Value);
                committedStates[pair.Key] = pair.Value;
            }
        }
        string? previousIntentHash = null;
        for (var sequence = 0; sequence < reservationPaths.Count; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (reservation, reservationArtifact) = ReadArtifact<RecoveryCommitReservation>(
                reservationPaths[sequence],
                cancellationToken);
            var plan = ValidateReservation(
                document,
                baseArtifact,
                reservation,
                sequence,
                previousIntentHash,
                committedStates);
            reservationArtifacts.Add(reservationArtifact);
            artifacts.Add(reservationArtifact);
            if (!intentPaths.TryGetValue(sequence, out var intentPath))
            {
                ValidatePendingReservation(plan, reservation, transactionPath, cancellationToken);
                commitReservations.Add(new ResolvedCommitReservation(
                    plan,
                    reservation,
                    reservationArtifact,
                    null,
                    null));
                continue;
            }
            var (intent, artifact) = ReadArtifact<RecoveryIntent>(intentPath, cancellationToken);
            ValidateIntent(
                document,
                baseArtifact,
                reservation,
                reservationArtifact,
                intent,
                sequence,
                previousIntentHash,
                committedStates,
                lastTransitions);
            plan.PreparedFingerprint = intent.TargetAfter;
            ValidateReadyReservation(plan, reservation, intent, transactionPath, cancellationToken);
            previousIntentHash = artifact.Sha256;
            intentArtifacts.Add(artifact);
            artifacts.Add(artifact);
            commitReservations.Add(new ResolvedCommitReservation(
                plan,
                reservation,
                reservationArtifact,
                intent,
                artifact));
        }

        if (rollbackPlanPaths.Count > 0 && preparedArtifact is null)
            throw new InvalidDataException("Rollback evidence exists without a sealed recovery snapshot set.");
        var rollbackMutations = new List<ResolvedRollbackMutation>(rollbackPlanPaths.Count);
        string? previousRollbackDoneHash = null;
        for (var sequence = 0; sequence < rollbackPlanPaths.Count; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (rollbackPlan, rollbackPlanArtifact) = ReadArtifact<RecoveryRollbackPlan>(
                rollbackPlanPaths[sequence],
                cancellationToken);
            var mutation = ValidateRollbackPlan(
                document,
                baseArtifact,
                preparedArtifact!,
                rollbackPlan,
                rollbackPlanArtifact,
                sequence,
                intentArtifacts.Count,
                previousIntentHash,
                previousRollbackDoneHash);
            artifacts.Add(rollbackPlanArtifact);
            if (rollbackReadyPaths.TryGetValue(sequence, out var rollbackReadyPath))
            {
                var (ready, readyArtifact) = ReadArtifact<RecoveryRollbackReady>(
                    rollbackReadyPath,
                    cancellationToken);
                ValidateRollbackReady(document, mutation, ready, readyArtifact);
                mutation.Ready = ready;
                mutation.ReadyArtifact = readyArtifact;
                artifacts.Add(readyArtifact);
            }
            if (rollbackAppliedPaths.TryGetValue(sequence, out var rollbackAppliedPath))
            {
                var (applied, appliedArtifact) = ReadArtifact<RecoveryRollbackApplied>(
                    rollbackAppliedPath,
                    cancellationToken);
                ValidateRollbackApplied(document, mutation, applied, appliedArtifact);
                mutation.Applied = applied;
                mutation.AppliedArtifact = appliedArtifact;
                artifacts.Add(appliedArtifact);
            }
            if (rollbackDonePaths.TryGetValue(sequence, out var rollbackDonePath))
            {
                var (done, doneArtifact) = ReadArtifact<RecoveryRollbackDone>(
                    rollbackDonePath,
                    cancellationToken);
                ValidateRollbackDone(
                    document,
                    mutation,
                    done,
                    doneArtifact,
                    previousRollbackDoneHash);
                mutation.Done = done;
                mutation.DoneArtifact = doneArtifact;
                previousRollbackDoneHash = doneArtifact.Sha256;
                artifacts.Add(doneArtifact);
            }
            rollbackMutations.Add(mutation);
        }

        FileArtifact? resolvedArtifact = null;
        if (resolvedPath is not null)
        {
            if (preparedArtifact is null)
                throw new InvalidDataException("A resolved recovery transaction has no capture seal.");
            var (resolved, artifact) = ReadArtifact<RecoveryResolved>(resolvedPath, cancellationToken);
            ValidateResolved(
                document,
                baseArtifact,
                preparedArtifact,
                resolved,
                reservationArtifacts.Count,
                intentArtifacts.Count,
                previousIntentHash,
                rollbackDonePaths.Count,
                previousRollbackDoneHash);
            if (resolved.Outcome.Equals("committed", StringComparison.Ordinal)
                && externalPendingPath is not null
                && externalFinalStates is null)
            {
                throw new InvalidDataException(
                    "A committed external mutation has no exact final evidence.");
            }
            if (resolved.Outcome.Equals("committed", StringComparison.Ordinal)
                && externalFinalStates is not null
                && !externalFinalAllowsCommit)
            {
                throw new InvalidDataException(
                    "A committed external mutation is bound only to rollback evidence.");
            }
            resolvedArtifact = artifact;
            artifacts.Add(artifact);
        }

        var rollbackEntries = new List<FileRollbackEntry>();
        if (resolvedArtifact is not null && rollbackMutations.Any(mutation => mutation.Done is null))
            throw new InvalidDataException("A resolved transaction contains an incomplete rollback mutation.");
        var rollbackByEntry = new Dictionary<int, ResolvedRollbackMutation>();
        foreach (var mutation in rollbackMutations)
        {
            if (!rollbackByEntry.TryAdd(mutation.Plan.EntryIndex, mutation))
                throw new InvalidDataException("A recovery transaction rolls back the same target more than once.");
            ValidateRollbackRuntimeState(
                mutation,
                resolvedArtifact is not null,
                transactionPath,
                cancellationToken);
        }
        if (preparedArtifact is not null && resolvedArtifact is null)
        {
            foreach (var resolved in resolvedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rollbackByEntry.ContainsKey(resolved.Index)) continue;
                var current = FileSnapshotJournal.CaptureRecoveryFingerprint(
                    resolved.TargetPath,
                    cancellationToken);
                var recognized = lastTransitions.TryGetValue(resolved.Index, out var transition)
                    ? SameFingerprint(current, transition.Before)
                      || SameFingerprint(current, transition.After)
                    : SameFingerprint(current, resolved.Entry.InitialState);
                recognized |= IsRecognizedForwardIntermediate(
                    resolved.Index,
                    current,
                    commitReservations,
                    cancellationToken);
                if (!recognized)
                    throw new ConcurrentLeafChangeException(
                        "Automatic transaction recovery stopped because a target is not transaction-owned. Current files and recovery evidence were preserved.",
                        resolved.TargetPath,
                        transactionPath);
                if (!SameFingerprint(current, resolved.Entry.InitialState))
                {
                    rollbackEntries.Add(new FileRollbackEntry(
                        new FileSnapshotEntry(
                            resolved.TargetPath,
                            resolved.Entry.InitialState,
                            resolved.SnapshotPath),
                        current));
                }
            }
        }
        VerifyRootsUnchanged();
        return new RecoveryPlan(
            transactionPath,
            transactionSnapshot,
            document,
            baseArtifact,
            preparedArtifact,
            reservationArtifacts,
            intentArtifacts,
            commitReservations,
            resolvedArtifact,
            resolvedEntries,
            rollbackEntries,
            rollbackMutations,
            artifacts,
            null,
            null);
    }

    private static bool IsRecognizedForwardIntermediate(
        int entryIndex,
        SnapshotLeafFingerprint current,
        IReadOnlyList<ResolvedCommitReservation> reservations,
        CancellationToken cancellationToken)
    {
        if (current.Kind != SnapshotLeafKind.Missing) return false;
        foreach (var reservation in reservations.AsEnumerable().Reverse())
        {
            if (reservation.Intent is null) continue;
            if (reservation.Reservation.TargetEntryIndex == entryIndex
                && reservation.Plan.DisplacedPath is not null
                && SameFingerprint(
                    FileSnapshotJournal.CaptureRecoveryFingerprint(
                        reservation.Plan.DisplacedPath,
                        cancellationToken),
                    reservation.Reservation.TargetBefore)
                && SameFingerprint(
                    FileSnapshotJournal.CaptureRecoveryFingerprint(
                        reservation.Plan.PreparedPath,
                        cancellationToken),
                    reservation.Intent.TargetAfter))
            {
                return true;
            }
            if (reservation.Reservation.BackupEntryIndex == entryIndex
                && reservation.Plan.PriorBackupPath is not null
                && reservation.Plan.DisplacedPath is not null
                && SameFingerprint(
                    FileSnapshotJournal.CaptureRecoveryFingerprint(
                        reservation.Plan.PriorBackupPath,
                        cancellationToken),
                    reservation.Reservation.BackupBefore)
                && SameFingerprint(
                    FileSnapshotJournal.CaptureRecoveryFingerprint(
                        reservation.Plan.DisplacedPath,
                        cancellationToken),
                    reservation.Reservation.TargetBefore))
            {
                return true;
            }
        }
        return false;
    }

    private RecoveryPlan PreflightCleanup(
        string transactionPath,
        PathSafety.ExistingDirectorySnapshot transactionSnapshot,
        Guid transactionId,
        string cleanupPath,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var (cleanup, cleanupArtifact) = ReadArtifact<RecoveryCleanup>(
            cleanupPath,
            cancellationToken);
        if (cleanup.Version != CurrentFormatVersion
            || cleanup.TransactionId != transactionId
            || !PathSafety.Normalize(cleanup.TargetRoot).Equals(targetRoot, StringComparison.OrdinalIgnoreCase)
            || !cleanup.TargetRootIdentity.Equals(targetRootSnapshot.Identity, StringComparison.Ordinal)
            || !cleanup.TransactionDirectoryIdentity.Equals(
                transactionSnapshot.Identity,
                StringComparison.Ordinal)
            || cleanup.Artifacts is null
            || cleanup.Artifacts.Count == 0
            || cleanup.Artifacts.Count > MaximumRecoveryArtifactFiles)
        {
            throw new InvalidDataException("A recovery cleanup seal is invalid or belongs to another target root.");
        }

        var expected = new Dictionary<string, RecoveryCleanupArtifact>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in cleanup.Artifacts)
        {
            if (!IsAllowedRecoveryArtifactFileName(artifact.FileName)
                || !IsValidFingerprint(artifact.Fingerprint, allowDirectory: false)
                || artifact.Fingerprint.Kind != SnapshotLeafKind.File)
            {
                throw new InvalidDataException("A recovery cleanup seal contains an invalid artifact.");
            }
            var path = PathSafety.Normalize(Path.Combine(transactionPath, artifact.FileName));
            if (!Path.GetDirectoryName(path)!.Equals(transactionPath, StringComparison.OrdinalIgnoreCase)
                || !expected.TryAdd(path, artifact))
            {
                throw new InvalidDataException("A recovery cleanup seal contains a duplicate or escaped path.");
            }
        }
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Equals(cleanupPath, StringComparison.OrdinalIgnoreCase)) continue;
            var normalized = PathSafety.Normalize(path);
            if (!expected.TryGetValue(normalized, out var expectedArtifact))
                throw new InvalidDataException("A recovery cleanup seal does not cover an authority artifact.");
            var current = FileSnapshotJournal.CaptureRecoveryFingerprint(path, cancellationToken);
            if (!SameFingerprint(expectedArtifact.Fingerprint, current))
                throw new ConcurrentLeafChangeException(
                    "A recovery artifact changed after cleanup was sealed.",
                    path,
                    cleanupPath);
        }
        VerifyRootsUnchanged();
        return RecoveryPlan.CleanupPending(
            transactionPath,
            transactionSnapshot,
            cleanup,
            cleanupArtifact);
    }

    private List<ResolvedRecoveryEntry> ValidateBase(
        RecoveryBase document,
        Guid transactionId,
        string transactionPath)
    {
        if (document.Version != CurrentFormatVersion
            || document.TransactionId == Guid.Empty
            || document.TransactionId != transactionId
            || !PathSafety.Normalize(document.TargetRoot).Equals(targetRoot, StringComparison.OrdinalIgnoreCase)
            || !document.TargetRootIdentity.Equals(targetRootSnapshot.Identity, StringComparison.Ordinal)
            || document.Entries is null
            || document.Entries.Count == 0
            || document.Entries.Count > FileSnapshotJournal.MaximumSnapshotTargets * 2)
        {
            throw new InvalidDataException("A recovery base record is invalid or belongs to another target root.");
        }

        var resolved = new List<ResolvedRecoveryEntry>(document.Entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < document.Entries.Count; index++)
        {
            var entry = document.Entries[index];
            var target = ResolveTargetRelativePath(entry.RelativePath);
            if (!seen.Add(target) || !IsValidFingerprint(entry.InitialState, allowDirectory: true))
                throw new InvalidDataException("A recovery base record contains an invalid target entry.");
            string? snapshot = null;
            if (entry.InitialState.Kind == SnapshotLeafKind.File)
            {
                if (!string.Equals(
                        entry.SnapshotFileName,
                        SnapshotDataFileName(index),
                        StringComparison.Ordinal))
                    throw new InvalidDataException("A recovery base record contains invalid snapshot evidence.");
                _ = Path.Combine(transactionPath, entry.SnapshotFileName!);
            }
            else if (entry.SnapshotFileName is not null)
            {
                throw new InvalidDataException("A non-file recovery target has unexpected snapshot evidence.");
            }
            resolved.Add(new ResolvedRecoveryEntry(index, entry, target, snapshot));
        }
        return resolved;
    }

    private static void ValidateSnapshotReady(
        RecoveryBase document,
        FileArtifact baseArtifact,
        RecoverySnapshotReady ready,
        int expectedIndex)
    {
        if (ready.Version != CurrentFormatVersion
            || ready.TransactionId != document.TransactionId
            || ready.EntryIndex != expectedIndex
            || expectedIndex < 0
            || expectedIndex >= document.Entries.Count
            || !ready.BaseSha256.Equals(baseArtifact.Sha256, StringComparison.Ordinal)
            || document.Entries[expectedIndex].InitialState.Kind != SnapshotLeafKind.File
            || !IsValidFingerprint(ready.SnapshotState, allowDirectory: false)
            || ready.SnapshotState.Kind != SnapshotLeafKind.File
            || !FileSnapshotJournal.HasSameContent(
                document.Entries[expectedIndex].InitialState,
                ready.SnapshotState))
        {
            throw new InvalidDataException("A recovery snapshot identity record is invalid.");
        }
    }

    private static void ValidatePrepared(
        RecoveryBase document,
        FileArtifact baseArtifact,
        RecoveryPrepared prepared,
        IReadOnlyList<FileArtifact> snapshotReadyArtifacts)
    {
        var expectedSnapshotCount = document.Entries.Count(entry =>
            entry.InitialState.Kind == SnapshotLeafKind.File);
        if (prepared.Version != CurrentFormatVersion
            || prepared.TransactionId != document.TransactionId
            || !prepared.BaseSha256.Equals(baseArtifact.Sha256, StringComparison.Ordinal)
            || prepared.SnapshotReadyHashes is null
            || prepared.SnapshotReadyHashes.Count != expectedSnapshotCount
            || prepared.SnapshotReadyHashes.Count != snapshotReadyArtifacts.Count
            || !prepared.SnapshotReadyHashes.SequenceEqual(
                snapshotReadyArtifacts.Select(artifact => artifact.Sha256),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException("A recovery snapshot capture seal is invalid.");
        }
    }

    private static Dictionary<int, long> ValidateExternalPending(
        RecoveryBase document,
        FileArtifact baseArtifact,
        FileArtifact preparedArtifact,
        RecoveryExternalPending pending)
    {
        if (pending.Version != CurrentFormatVersion
            || pending.TransactionId != document.TransactionId
            || !pending.BaseSha256.Equals(baseArtifact.Sha256, StringComparison.Ordinal)
            || !pending.PreparedSha256.Equals(preparedArtifact.Sha256, StringComparison.Ordinal)
            || pending.Entries is null
            || pending.Entries.Count != 2)
        {
            throw new InvalidDataException("External mutation authorization has invalid transaction evidence.");
        }

        var result = new Dictionary<int, long>();
        foreach (var entry in pending.Entries)
        {
            if (entry.EntryIndex < 0
                || entry.EntryIndex >= document.Entries.Count
                || entry.MaximumBytes <= 0
                || entry.MaximumBytes > PathSafety.MaximumTrustedLeafBytes
                || !result.TryAdd(entry.EntryIndex, entry.MaximumBytes)
                || entry.InitialState is null
                || document.Entries[entry.EntryIndex].RelativePath.EndsWith(
                    ".bak",
                    StringComparison.OrdinalIgnoreCase)
                || !SameFingerprint(
                    entry.InitialState,
                    document.Entries[entry.EntryIndex].InitialState)
                || entry.InitialState.Kind is not (SnapshotLeafKind.File or SnapshotLeafKind.Missing))
            {
                throw new InvalidDataException("External mutation authorization contains an invalid target binding.");
            }
        }
        return result;
    }

    private static Dictionary<int, SnapshotLeafFingerprint> ValidateExternalFinal(
        RecoveryBase document,
        FileArtifact baseArtifact,
        FileArtifact preparedArtifact,
        FileArtifact pendingArtifact,
        IReadOnlyDictionary<int, long> externalLimits,
        RecoveryExternalFinal final,
        out bool allowsCommit)
    {
        allowsCommit = string.Equals(
            final.Disposition,
            ExternalFinalCommitDisposition,
            StringComparison.Ordinal);
        var allowsRollback = string.Equals(
            final.Disposition,
            ExternalFinalRollbackDisposition,
            StringComparison.Ordinal);
        if (final.Version != CurrentFormatVersion
            || final.TransactionId != document.TransactionId
            || !final.BaseSha256.Equals(baseArtifact.Sha256, StringComparison.Ordinal)
            || !final.PreparedSha256.Equals(preparedArtifact.Sha256, StringComparison.Ordinal)
            || !final.PendingSha256.Equals(pendingArtifact.Sha256, StringComparison.Ordinal)
            || (!allowsCommit && !allowsRollback)
            || final.Entries is null
            || final.Entries.Count != externalLimits.Count)
        {
            throw new InvalidDataException("External mutation final evidence has invalid transaction binding.");
        }

        var result = new Dictionary<int, SnapshotLeafFingerprint>();
        foreach (var entry in final.Entries)
        {
            if (!externalLimits.TryGetValue(entry.EntryIndex, out var maximumBytes)
                || entry.FinalState is null
                || !result.TryAdd(entry.EntryIndex, entry.FinalState)
                || (allowsCommit && entry.FinalState.Kind != SnapshotLeafKind.File)
                || (allowsRollback
                    && entry.FinalState.Kind is not (SnapshotLeafKind.File or SnapshotLeafKind.Missing))
                || entry.FinalState.Length > maximumBytes
                || !IsValidFingerprint(entry.FinalState, allowDirectory: false))
            {
                throw new InvalidDataException("External mutation final evidence contains an invalid endpoint.");
            }
        }
        return result;
    }

    private static void ValidateRollbackArtifactCardinality(
        IReadOnlyDictionary<int, string> plans,
        IReadOnlyDictionary<int, string> ready,
        IReadOnlyDictionary<int, string> applied,
        IReadOnlyDictionary<int, string> done)
    {
        foreach (var collection in new[] { plans, ready, applied, done })
        {
            for (var sequence = 0; sequence < collection.Count; sequence++)
            {
                if (!collection.ContainsKey(sequence))
                    throw new InvalidDataException("A recovery transaction has a missing rollback artifact sequence.");
            }
        }
        if (ready.Count > plans.Count
            || applied.Count > ready.Count
            || done.Count > applied.Count
            || plans.Count > done.Count + 1
            || ready.Count > done.Count + 1
            || applied.Count > done.Count + 1)
        {
            throw new InvalidDataException("A recovery transaction has an invalid rollback artifact state machine.");
        }
    }

    private ResolvedRollbackMutation ValidateRollbackPlan(
        RecoveryBase document,
        FileArtifact baseArtifact,
        FileArtifact preparedArtifact,
        RecoveryRollbackPlan plan,
        FileArtifact planArtifact,
        int expectedSequence,
        int forwardIntentCount,
        string? lastForwardIntentHash,
        string? previousRollbackDoneHash)
    {
        if (plan.Version != CurrentFormatVersion
            || plan.TransactionId != document.TransactionId
            || plan.Sequence != expectedSequence
            || !plan.BaseSha256.Equals(baseArtifact.Sha256, StringComparison.Ordinal)
            || !plan.PreparedSha256.Equals(preparedArtifact.Sha256, StringComparison.Ordinal)
            || plan.ForwardIntentCount != forwardIntentCount
            || !string.Equals(plan.LastForwardIntentSha256, lastForwardIntentHash, StringComparison.Ordinal)
            || !string.Equals(plan.PreviousRollbackDoneSha256, previousRollbackDoneHash, StringComparison.Ordinal)
            || plan.EntryIndex < 0
            || plan.EntryIndex >= document.Entries.Count
            || !IsValidFingerprint(plan.ExpectedCurrent, allowDirectory: false))
        {
            throw new InvalidDataException("A rollback plan has invalid identity, ordering, or state evidence.");
        }
        var entry = document.Entries[plan.EntryIndex];
        var target = ResolveTargetRelativePath(entry.RelativePath);
        var prepared = ResolveOptionalReservationPath(plan.PreparedRelativePath);
        var displaced = ResolveOptionalReservationPath(plan.DisplacedRelativePath);
        var expectedKind = entry.InitialState.Kind switch
        {
            SnapshotLeafKind.File when plan.ExpectedCurrent.Kind == SnapshotLeafKind.Missing => "restore-missing",
            SnapshotLeafKind.File when plan.ExpectedCurrent.Kind == SnapshotLeafKind.File => "restore-existing",
            SnapshotLeafKind.Missing when plan.ExpectedCurrent.Kind == SnapshotLeafKind.File => "remove-created",
            _ => string.Empty
        };
        if (!plan.Kind.Equals(expectedKind, StringComparison.Ordinal)
            || (entry.InitialState.Kind == SnapshotLeafKind.File) != (prepared is not null)
            || (plan.Kind is "restore-existing" or "remove-created") != (displaced is not null)
            || (prepared is not null
                && !Path.GetDirectoryName(prepared)!.Equals(
                    Path.GetDirectoryName(target),
                    StringComparison.OrdinalIgnoreCase))
            || (displaced is not null
                && !Path.GetDirectoryName(displaced)!.Equals(
                    Path.GetDirectoryName(target),
                    StringComparison.OrdinalIgnoreCase))
            || (prepared is not null && prepared.Equals(displaced, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("A rollback plan has an invalid operation or auxiliary path shape.");
        }
        _ = planArtifact;
        return new ResolvedRollbackMutation
        {
            Plan = plan,
            PlanArtifact = planArtifact,
            TargetPath = target,
            PreparedPath = prepared,
            DisplacedPath = displaced
        };
    }

    private static void ValidateRollbackReady(
        RecoveryBase document,
        ResolvedRollbackMutation mutation,
        RecoveryRollbackReady ready,
        FileArtifact readyArtifact)
    {
        var initial = document.Entries[mutation.Plan.EntryIndex].InitialState;
        if (ready.Version != CurrentFormatVersion
            || ready.TransactionId != document.TransactionId
            || ready.Sequence != mutation.Plan.Sequence
            || !ready.PlanSha256.Equals(mutation.PlanArtifact.Sha256, StringComparison.Ordinal)
            || !IsValidFingerprint(ready.PredictedRestored, allowDirectory: false)
            || (initial.Kind == SnapshotLeafKind.File
                && (ready.PredictedRestored.Kind != SnapshotLeafKind.File
                    || !FileSnapshotJournal.HasSameContent(initial, ready.PredictedRestored)))
            || (initial.Kind == SnapshotLeafKind.Missing
                && ready.PredictedRestored.Kind != SnapshotLeafKind.Missing))
        {
            throw new InvalidDataException("Rollback readiness evidence is invalid.");
        }
        _ = readyArtifact;
    }

    private static void ValidateRollbackApplied(
        RecoveryBase document,
        ResolvedRollbackMutation mutation,
        RecoveryRollbackApplied applied,
        FileArtifact appliedArtifact)
    {
        if (mutation.Ready is null
            || mutation.ReadyArtifact is null
            || applied.Version != CurrentFormatVersion
            || applied.TransactionId != document.TransactionId
            || applied.Sequence != mutation.Plan.Sequence
            || !applied.ReadySha256.Equals(mutation.ReadyArtifact.Sha256, StringComparison.Ordinal)
            || !SameFingerprint(applied.ActualRestored, mutation.Ready.PredictedRestored)
            || !SameFingerprint(
                applied.DisplacedState,
                mutation.DisplacedPath is null
                    ? new SnapshotLeafFingerprint(SnapshotLeafKind.Missing)
                    : mutation.Plan.ExpectedCurrent))
        {
            throw new InvalidDataException("Rollback application evidence is invalid.");
        }
        _ = appliedArtifact;
    }

    private static void ValidateRollbackDone(
        RecoveryBase document,
        ResolvedRollbackMutation mutation,
        RecoveryRollbackDone done,
        FileArtifact doneArtifact,
        string? previousDoneHash)
    {
        if (mutation.Applied is null
            || mutation.AppliedArtifact is null
            || done.Version != CurrentFormatVersion
            || done.TransactionId != document.TransactionId
            || done.Sequence != mutation.Plan.Sequence
            || !done.AppliedSha256.Equals(mutation.AppliedArtifact.Sha256, StringComparison.Ordinal)
            || !string.Equals(done.PreviousDoneSha256, previousDoneHash, StringComparison.Ordinal)
            || !SameFingerprint(done.ActualRestored, mutation.Applied.ActualRestored))
        {
            throw new InvalidDataException("Rollback completion evidence is invalid.");
        }
        _ = doneArtifact;
    }

    private static void ValidateRollbackRuntimeState(
        ResolvedRollbackMutation mutation,
        bool transactionResolved,
        string transactionPath,
        CancellationToken cancellationToken)
    {
        var target = FileSnapshotJournal.CaptureRecoveryFingerprint(
            mutation.TargetPath,
            cancellationToken);
        var prepared = mutation.PreparedPath is null
            ? new SnapshotLeafFingerprint(SnapshotLeafKind.Missing)
            : FileSnapshotJournal.CaptureRecoveryFingerprint(
                mutation.PreparedPath,
                cancellationToken);
        var displaced = mutation.DisplacedPath is null
            ? new SnapshotLeafFingerprint(SnapshotLeafKind.Missing)
            : FileSnapshotJournal.CaptureRecoveryFingerprint(
                mutation.DisplacedPath,
                cancellationToken);
        if (transactionResolved)
        {
            if (prepared.Kind != SnapshotLeafKind.Missing
                && (mutation.Ready is null
                    || !SameFingerprint(prepared, mutation.Ready.PredictedRestored)))
            {
                throw new ConcurrentLeafChangeException(
                    "A resolved rollback prepared leaf changed and was preserved.",
                    mutation.PreparedPath!,
                    transactionPath);
            }
            if (displaced.Kind != SnapshotLeafKind.Missing
                && !SameFingerprint(displaced, mutation.Plan.ExpectedCurrent))
            {
                throw new ConcurrentLeafChangeException(
                    "A resolved rollback displaced leaf changed and was preserved.",
                    mutation.DisplacedPath!,
                    transactionPath);
            }
            return;
        }

        if (mutation.Ready is null)
        {
            var preparedCanResume = mutation.PreparedPath is null
                ? prepared.Kind == SnapshotLeafKind.Missing
                : prepared.Kind is SnapshotLeafKind.Missing or SnapshotLeafKind.File;
            if (!SameFingerprint(target, mutation.Plan.ExpectedCurrent)
                || !preparedCanResume
                || displaced.Kind != SnapshotLeafKind.Missing)
            {
                throw new ConcurrentLeafChangeException(
                    "An unready rollback leaf is outside its plan-bound resumable state and was preserved.",
                    mutation.TargetPath,
                    mutation.PreparedPath ?? mutation.DisplacedPath ?? transactionPath,
                    transactionPath);
            }
            return;
        }

        if (mutation.Applied is not null)
        {
            if (!SameFingerprint(target, mutation.Applied.ActualRestored)
                || prepared.Kind != SnapshotLeafKind.Missing
                || (displaced.Kind != SnapshotLeafKind.Missing
                    && !SameFingerprint(displaced, mutation.Plan.ExpectedCurrent)))
            {
                throw new ConcurrentLeafChangeException(
                    "An applied rollback mutation changed before completion and was preserved.",
                    mutation.TargetPath,
                    mutation.DisplacedPath ?? transactionPath,
                    transactionPath);
            }
            return;
        }

        var before = SameFingerprint(target, mutation.Plan.ExpectedCurrent)
                     && (mutation.PreparedPath is null
                         || SameFingerprint(prepared, mutation.Ready.PredictedRestored))
                     && displaced.Kind == SnapshotLeafKind.Missing;
        var targetDisplaced = mutation.DisplacedPath is not null
                              && target.Kind == SnapshotLeafKind.Missing
                              && (mutation.PreparedPath is null
                                  || SameFingerprint(prepared, mutation.Ready.PredictedRestored))
                              && SameFingerprint(displaced, mutation.Plan.ExpectedCurrent);
        var after = SameFingerprint(target, mutation.Ready.PredictedRestored)
                    && prepared.Kind == SnapshotLeafKind.Missing
                    && (mutation.DisplacedPath is null
                        ? displaced.Kind == SnapshotLeafKind.Missing
                        : SameFingerprint(displaced, mutation.Plan.ExpectedCurrent));
        if (!before && !targetDisplaced && !after)
        {
            throw new ConcurrentLeafChangeException(
                "A rollback mutation is outside every published crash endpoint and was preserved.",
                mutation.TargetPath,
                mutation.PreparedPath ?? mutation.DisplacedPath ?? transactionPath,
                transactionPath);
        }
    }

    private AtomicCommitRecoveryPlan ValidateReservation(
        RecoveryBase document,
        FileArtifact baseArtifact,
        RecoveryCommitReservation reservation,
        int expectedSequence,
        string? previousIntentHash,
        SnapshotLeafFingerprint[] committedStates)
    {
        if (reservation.Version != CurrentFormatVersion
            || reservation.TransactionId != document.TransactionId
            || reservation.Sequence != expectedSequence
            || !reservation.BaseSha256.Equals(baseArtifact.Sha256, StringComparison.Ordinal)
            || !string.Equals(reservation.PreviousIntentSha256, previousIntentHash, StringComparison.Ordinal)
            || reservation.TargetEntryIndex < 0
            || reservation.TargetEntryIndex >= document.Entries.Count
            || reservation.BackupEntryIndex < 0
            || reservation.BackupEntryIndex >= document.Entries.Count
            || reservation.TargetEntryIndex == reservation.BackupEntryIndex
            || !IsValidFingerprint(reservation.TargetBefore, allowDirectory: false)
            || !IsValidFingerprint(reservation.BackupBefore, allowDirectory: false)
            || !SameFingerprint(committedStates[reservation.TargetEntryIndex], reservation.TargetBefore)
            || !SameFingerprint(committedStates[reservation.BackupEntryIndex], reservation.BackupBefore))
        {
            throw new InvalidDataException("A recovery commit reservation has invalid identity or state evidence.");
        }
        var target = ResolveTargetRelativePath(document.Entries[reservation.TargetEntryIndex].RelativePath);
        var backup = ResolveTargetRelativePath(document.Entries[reservation.BackupEntryIndex].RelativePath);
        if (!backup.Equals(target + ".bak", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A recovery commit reservation has an invalid backup target.");
        var prepared = ResolveTargetRelativePath(reservation.PreparedRelativePath);
        var displaced = ResolveOptionalReservationPath(reservation.DisplacedRelativePath);
        var rejected = ResolveOptionalReservationPath(reservation.RejectedRelativePath);
        var prior = ResolveOptionalReservationPath(reservation.PriorBackupRelativePath);
        var original = ResolveOptionalReservationPath(reservation.OriginalRecoveryRelativePath);
        var parent = Path.GetDirectoryName(target)!;
        var paths = new[] { prepared, displaced, rejected, prior, original }
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length
            || paths.Any(path => !Path.GetDirectoryName(path)!.Equals(parent, StringComparison.OrdinalIgnoreCase))
            || prepared.Equals(target, StringComparison.OrdinalIgnoreCase)
            || prepared.Equals(backup, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A recovery commit reservation contains an escaped or duplicate path.");
        }
        var targetExisted = reservation.TargetBefore.Kind == SnapshotLeafKind.File;
        if ((targetExisted && (displaced is null || rejected is null))
            || (!targetExisted && (displaced is not null || rejected is not null))
            || (targetExisted && reservation.KeepBackup && (prior is null || original is null))
            || ((!targetExisted || !reservation.KeepBackup) && (prior is not null || original is not null)))
        {
            throw new InvalidDataException("A recovery commit reservation has an invalid intermediate-path shape.");
        }
        return new AtomicCommitRecoveryPlan
        {
            TransactionId = document.TransactionId,
            Sequence = expectedSequence,
            TargetPath = target,
            BackupPath = backup,
            PreparedPath = prepared,
            DisplacedPath = displaced,
            RejectedPath = rejected,
            PriorBackupPath = prior,
            OriginalRecoveryPath = original,
            KeepBackup = reservation.KeepBackup,
            TargetBefore = reservation.TargetBefore,
            BackupBefore = reservation.BackupBefore
        };
    }

    private string? ResolveOptionalReservationPath(string? relativePath) =>
        relativePath is null ? null : ResolveTargetRelativePath(relativePath);

    private static void ValidatePendingReservation(
        AtomicCommitRecoveryPlan plan,
        RecoveryCommitReservation reservation,
        string transactionPath,
        CancellationToken cancellationToken)
    {
        var paths = new[] { plan.PreparedPath }.Concat(EnumerateAuxiliaryPaths(plan));
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FileSnapshotJournal.CaptureRecoveryFingerprint(path, cancellationToken).Kind
                != SnapshotLeafKind.Missing)
            {
                throw new ConcurrentLeafChangeException(
                    "An unready prepared output or intermediate was preserved because its exact ownership was never published.",
                    path,
                    transactionPath);
            }
        }
        _ = reservation;
    }

    private static void ValidateReadyReservation(
        AtomicCommitRecoveryPlan plan,
        RecoveryCommitReservation reservation,
        RecoveryIntent intent,
        string transactionPath,
        CancellationToken cancellationToken)
    {
        ValidateKnownIntermediate(
            plan.PreparedPath,
            intent.TargetAfter,
            "prepared output",
            transactionPath,
            cancellationToken);
        ValidateKnownIntermediate(
            plan.DisplacedPath,
            reservation.TargetBefore,
            "displaced target",
            transactionPath,
            cancellationToken);
        ValidateKnownIntermediate(
            plan.RejectedPath,
            intent.TargetAfter,
            "rejected output",
            transactionPath,
            cancellationToken);
        ValidateKnownIntermediate(
            plan.PriorBackupPath,
            reservation.BackupBefore,
            "prior backup",
            transactionPath,
            cancellationToken);
        ValidateKnownIntermediate(
            plan.OriginalRecoveryPath,
            reservation.TargetBefore,
            "original recovery leaf",
            transactionPath,
            cancellationToken);
    }

    private static void ValidateKnownIntermediate(
        string? path,
        SnapshotLeafFingerprint expected,
        string context,
        string transactionPath,
        CancellationToken cancellationToken)
    {
        if (path is null) return;
        var current = FileSnapshotJournal.CaptureRecoveryFingerprint(path, cancellationToken);
        if (current.Kind == SnapshotLeafKind.Missing || SameFingerprint(current, expected)) return;
        throw new ConcurrentLeafChangeException(
            $"A transaction {context} contains unknown content and was preserved.",
            path,
            transactionPath);
    }

    private static void CleanupForwardIntermediates(
        IReadOnlyList<ResolvedCommitReservation> reservations,
        string transactionPath,
        CancellationToken cancellationToken)
    {
        foreach (var resolved in reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolved.Intent is null) continue;
            DeleteKnownIntermediate(
                resolved.Plan.PreparedPath,
                resolved.Intent.TargetAfter,
                transactionPath);
            DeleteKnownIntermediate(
                resolved.Plan.DisplacedPath,
                resolved.Reservation.TargetBefore,
                transactionPath);
            DeleteKnownIntermediate(
                resolved.Plan.RejectedPath,
                resolved.Intent.TargetAfter,
                transactionPath);
            DeleteKnownIntermediate(
                resolved.Plan.PriorBackupPath,
                resolved.Reservation.BackupBefore,
                transactionPath);
            DeleteKnownIntermediate(
                resolved.Plan.OriginalRecoveryPath,
                resolved.Reservation.TargetBefore,
                transactionPath);
        }
    }

    private static void DeleteKnownIntermediate(
        string? path,
        SnapshotLeafFingerprint expected,
        string transactionPath)
    {
        if (path is null || !File.Exists(path)) return;
        if (expected.Kind != SnapshotLeafKind.File
            || !AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                path,
                expected.VolumeSerialNumber,
                expected.FileIndex,
                expected.Length,
                expected.Sha256))
        {
            throw new ConcurrentLeafChangeException(
                "A transaction intermediate changed before exact cleanup and was preserved.",
                path,
                transactionPath);
        }
    }

    private static void ValidateIntent(
        RecoveryBase document,
        FileArtifact baseArtifact,
        RecoveryCommitReservation reservation,
        FileArtifact reservationArtifact,
        RecoveryIntent intent,
        int expectedSequence,
        string? previousIntentHash,
        SnapshotLeafFingerprint[] committedStates,
        IDictionary<int, LeafTransition> lastTransitions)
    {
        if (intent.Version != CurrentFormatVersion
            || intent.TransactionId != document.TransactionId
            || intent.Sequence != expectedSequence
            || !intent.BaseSha256.Equals(baseArtifact.Sha256, StringComparison.Ordinal)
            || !string.Equals(intent.PreviousIntentSha256, previousIntentHash, StringComparison.Ordinal)
            || !intent.ReservationSha256.Equals(reservationArtifact.Sha256, StringComparison.Ordinal)
            || intent.TargetEntryIndex != reservation.TargetEntryIndex
            || intent.BackupEntryIndex != reservation.BackupEntryIndex
            || intent.KeepBackup != reservation.KeepBackup
            || intent.TargetEntryIndex < 0
            || intent.TargetEntryIndex >= document.Entries.Count
            || intent.BackupEntryIndex < 0
            || intent.BackupEntryIndex >= document.Entries.Count
            || intent.TargetEntryIndex == intent.BackupEntryIndex)
        {
            throw new InvalidDataException("A recovery commit intent has invalid identity or ordering.");
        }
        var targetEntry = document.Entries[intent.TargetEntryIndex];
        var backupEntry = document.Entries[intent.BackupEntryIndex];
        if (!backupEntry.RelativePath.Equals(targetEntry.RelativePath + ".bak", StringComparison.OrdinalIgnoreCase)
            || !IsValidFingerprint(intent.TargetBefore, allowDirectory: false)
            || !IsValidFingerprint(intent.BackupBefore, allowDirectory: false)
            || !IsValidFingerprint(intent.TargetAfter, allowDirectory: false)
            || !IsValidFingerprint(intent.BackupAfter, allowDirectory: false)
            || intent.TargetAfter.Kind != SnapshotLeafKind.File
            || !SameFingerprint(committedStates[intent.TargetEntryIndex], intent.TargetBefore)
            || !SameFingerprint(committedStates[intent.BackupEntryIndex], intent.BackupBefore)
            || !SameFingerprint(intent.TargetBefore, reservation.TargetBefore)
            || !SameFingerprint(intent.BackupBefore, reservation.BackupBefore))
        {
            throw new InvalidDataException("A recovery commit intent has invalid target state evidence.");
        }
        var expectedBackupAfter = intent.KeepBackup && intent.TargetBefore.Kind == SnapshotLeafKind.File
            ? intent.TargetBefore
            : intent.BackupBefore;
        if (!SameFingerprint(expectedBackupAfter, intent.BackupAfter))
            throw new InvalidDataException("A recovery commit intent has invalid deterministic-backup evidence.");

        lastTransitions[intent.TargetEntryIndex] = new LeafTransition(intent.TargetBefore, intent.TargetAfter);
        lastTransitions[intent.BackupEntryIndex] = new LeafTransition(intent.BackupBefore, intent.BackupAfter);
        committedStates[intent.TargetEntryIndex] = intent.TargetAfter;
        committedStates[intent.BackupEntryIndex] = intent.BackupAfter;
    }

    private static void ValidateResolved(
        RecoveryBase document,
        FileArtifact baseArtifact,
        FileArtifact preparedArtifact,
        RecoveryResolved resolved,
        int reservationCount,
        int intentCount,
        string? lastIntentHash,
        int rollbackCount,
        string? lastRollbackDoneHash)
    {
        if (resolved.Version != CurrentFormatVersion
            || resolved.TransactionId != document.TransactionId
            || !resolved.BaseSha256.Equals(baseArtifact.Sha256, StringComparison.Ordinal)
            || !resolved.PreparedSha256.Equals(preparedArtifact.Sha256, StringComparison.Ordinal)
            || resolved.Outcome is not ("committed" or "rolled-back")
            || (resolved.Outcome == "committed" && reservationCount != intentCount)
            || resolved.ReservationCount != reservationCount
            || resolved.IntentCount != intentCount
            || !string.Equals(resolved.LastIntentSha256, lastIntentHash, StringComparison.Ordinal)
            || resolved.RollbackCount != rollbackCount
            || !string.Equals(
                resolved.LastRollbackDoneSha256,
                lastRollbackDoneHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("A transaction resolution marker is invalid.");
        }
    }

    private string CreateReservedSibling(string logicalPath, string kind)
    {
        var parent = Path.GetDirectoryName(logicalPath)
            ?? throw new InvalidDataException("A durable commit path has no parent directory.");
        var name = $".{Path.GetFileName(logicalPath)}.rtx-{activeBase!.TransactionId:N}-{nextIntentSequence:D8}-{Guid.NewGuid():N}.{kind}";
        return RequireTargetPath(Path.Combine(parent, name));
    }

    private static IEnumerable<string> EnumerateAuxiliaryPaths(AtomicCommitRecoveryPlan plan)
    {
        if (plan.DisplacedPath is not null) yield return plan.DisplacedPath;
        if (plan.RejectedPath is not null) yield return plan.RejectedPath;
        if (plan.PriorBackupPath is not null) yield return plan.PriorBackupPath;
        if (plan.OriginalRecoveryPath is not null) yield return plan.OriginalRecoveryPath;
    }

    private string RequireTargetPath(string path)
    {
        var full = PathSafety.Normalize(path);
        if (!PathSafety.IsStrictlyInside(full, targetRoot))
            throw new InvalidDataException("A transaction path is outside its target root.");
        PathSafety.EnsureNoReparsePoints(Path.GetDirectoryName(full)!, targetRoot);
        return full;
    }

    private string ResolveTargetRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("A recovery record contains an invalid relative path.");
        }
        return RequireTargetPath(Path.Combine(targetRoot, relativePath));
    }

    private string ToTargetRelativePath(string path) =>
        Path.GetRelativePath(targetRoot, RequireTargetPath(path));

    private void VerifyRootsUnchanged()
    {
        if (!enabled) return;
        authority!.VerifyUnchanged();
        var currentTarget = PathSafety.GetExistingDirectorySnapshot(targetRoot);
        if (!currentTarget.CanonicalPath.Equals(targetRootSnapshot.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || !currentTarget.Identity.Equals(targetRootSnapshot.Identity, StringComparison.Ordinal))
        {
            throw new ConcurrentLeafChangeException(
                "The transaction target root changed while recovery was active.",
                targetRoot);
        }
        PathSafety.EnsureNoReparsePoints(shardPath, authority.Root);
        var currentShard = PathSafety.GetExistingDirectorySnapshot(shardPath);
        if (!currentShard.CanonicalPath.Equals(shardSnapshot.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || !currentShard.Identity.Equals(shardSnapshot.Identity, StringComparison.Ordinal))
        {
            throw new ConcurrentLeafChangeException(
                "The transaction recovery shard changed while it was active.",
                shardPath);
        }
    }

    private static bool IsValidFingerprint(
        SnapshotLeafFingerprint? fingerprint,
        bool allowDirectory)
    {
        if (fingerprint is null || fingerprint.Kind == SnapshotLeafKind.Unstable) return false;
        if (fingerprint.Kind == SnapshotLeafKind.Missing)
            return fingerprint.Length == 0
                   && fingerprint.Sha256 is null
                   && fingerprint.VolumeSerialNumber is null
                   && fingerprint.FileIndex is null;
        if (fingerprint.Kind == SnapshotLeafKind.Directory)
            return allowDirectory
                   && fingerprint.Sha256 is null
                   && fingerprint.VolumeSerialNumber.HasValue
                   && fingerprint.FileIndex is > 0;
        return fingerprint.Kind == SnapshotLeafKind.File
               && fingerprint.Length >= 0
               && fingerprint.Length <= PathSafety.MaximumTrustedLeafBytes
               && fingerprint.Sha256 is { Length: 32 }
               && fingerprint.VolumeSerialNumber.HasValue
               && fingerprint.FileIndex is > 0;
    }

    private static bool SameFingerprint(
        SnapshotLeafFingerprint? left,
        SnapshotLeafFingerprint? right)
    {
        if (left is null || right is null || left.Kind != right.Kind) return false;
        if (left.Kind == SnapshotLeafKind.Missing) return true;
        if (left.Kind == SnapshotLeafKind.Directory)
            return left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks
                   && left.VolumeSerialNumber == right.VolumeSerialNumber
                   && left.FileIndex == right.FileIndex;
        return left.Kind == SnapshotLeafKind.File
               && left.Length == right.Length
               && left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks
               && left.VolumeSerialNumber == right.VolumeSerialNumber
               && left.FileIndex == right.FileIndex
               && left.Sha256 is { Length: 32 }
               && right.Sha256 is { Length: 32 }
               && CryptographicOperations.FixedTimeEquals(left.Sha256, right.Sha256);
    }

    private static string SanitizeOperationName(string operationName)
    {
        var value = new string((operationName ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(128)
            .ToArray());
        return string.IsNullOrWhiteSpace(value) ? "File transaction" : value;
    }

    private void ClearActiveTransaction()
    {
        activeBase = null;
        activeTransactionPath = null;
        activeBaseArtifact = null;
        activeSnapshotReadyArtifacts.Clear();
        activeSnapshotDataArtifacts.Clear();
        activeSnapshotStates.Clear();
        activePreparedArtifact = null;
        activeExternalPending = null;
        activeExternalPendingArtifact = null;
        activeExternalFinalArtifact = null;
        activeExternalFinalAllowsCommit = false;
        activeReservationArtifacts.Clear();
        activeIntentArtifacts.Clear();
        activeCommitReservations.Clear();
        activePendingCommitPlan = null;
        activePendingReservation = null;
        activePendingReservationArtifact = null;
        activeRollbackArtifacts.Clear();
        previousRollbackDoneSha256 = null;
        activeRollbackDoneCount = 0;
        activeResolvedArtifact = null;
        entryIndexesByPath = null;
        expectedCommittedStates = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class Activation(
        FileTransactionRecoverySession current,
        FileTransactionRecoverySession? previous) : IDisposable
    {
        private FileTransactionRecoverySession? current = current;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref current, null);
            if (owned is null) return;
            if (ReferenceEquals(ActiveSession.Value, owned)) ActiveSession.Value = previous;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose()
        {
        }
    }

}
