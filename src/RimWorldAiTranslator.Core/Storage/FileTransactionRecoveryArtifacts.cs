using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

internal sealed partial class FileTransactionRecoverySession
{
    private void CleanupOrphanPublicationFiles(string transactionPath)
    {
        var inventory = Directory.EnumerateFiles(transactionPath, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumRecoveryArtifactFiles + 257)
            .ToArray();
        if (inventory.Length > MaximumRecoveryArtifactFiles + 256)
            throw new InvalidDataException("A recovery transaction publication inventory exceeds its bound.");
        foreach (var path in inventory)
        {
            var fileName = Path.GetFileName(path);
            if (!TryGetAtomicPublicationTarget(fileName, out var targetFileName)) continue;
            var targetPath = Path.Combine(transactionPath, targetFileName);
            _ = AtomicTemporaryFiles.CleanupExitedProcessFiles(targetPath);
        }
    }

    private static bool TryGetAtomicPublicationTarget(
        string fileName,
        out string targetFileName)
    {
        targetFileName = string.Empty;
        if (fileName.Length == 0
            || fileName[0] != '.'
            || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }
        var processMarker = fileName.LastIndexOf(".p", StringComparison.Ordinal);
        if (processMarker <= 1) return false;
        var remainder = fileName[(processMarker + 2)..];
        var processSeparator = remainder.IndexOf('.');
        if (processSeparator <= 0
            || !int.TryParse(
                remainder.AsSpan(0, processSeparator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processId)
            || processId <= 0)
        {
            return false;
        }
        remainder = remainder[(processSeparator + 1)..];
        var identitySeparator = remainder.IndexOf('.');
        if (identitySeparator != 32
            || !Guid.TryParseExact(remainder[..identitySeparator], "N", out _)
            || !remainder[(identitySeparator + 1)..].Equals("tmp", StringComparison.Ordinal))
        {
            return false;
        }
        var candidate = fileName[1..processMarker];
        if (!candidate.Equals(BaseFileName, StringComparison.Ordinal)
            && !candidate.Equals(PreparedFileName, StringComparison.Ordinal)
            && !candidate.Equals(ExternalPendingFileName, StringComparison.Ordinal)
            && !candidate.Equals(ExternalFinalFileName, StringComparison.Ordinal)
            && !candidate.Equals(ResolvedFileName, StringComparison.Ordinal)
            && !candidate.Equals(CleanupFileName, StringComparison.Ordinal)
            && !TryParseReservationFileName(candidate, out _)
            && !TryParseIntentFileName(candidate, out _)
            && !TryParseRollbackPlanFileName(candidate, out _)
            && !TryParseRollbackReadyFileName(candidate, out _)
            && !TryParseRollbackAppliedFileName(candidate, out _)
            && !TryParseRollbackDoneFileName(candidate, out _)
            && !TryParseSnapshotReadyFileName(candidate, out _)
            && !TryParseSnapshotDataFileName(candidate, out _))
        {
            return false;
        }
        targetFileName = candidate;
        return true;
    }

    private FileArtifact PublishArtifact<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyRootsUnchanged();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length <= 0 || bytes.Length > MaximumArtifactBytes)
            throw new InvalidDataException("A transaction recovery artifact exceeds its bounded size.");
        if (File.Exists(path) || Directory.Exists(path))
            throw new ConcurrentLeafChangeException("A transaction recovery artifact already exists.", path);
        var temporary = AtomicTemporaryFiles.CreatePath(path);
        uint? temporaryVolume = null;
        ulong? temporaryFileIndex = null;
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                if (!AtomicTemporaryFiles.TryGetOwnedRegularFileIdentity(
                        stream.SafeFileHandle,
                        out var volumeSerialNumber,
                        out var fileIndex))
                {
                    throw new IOException("A recovery publication temporary file could not be identity-pinned.");
                }
                temporaryVolume = volumeSerialNumber;
                temporaryFileIndex = fileIndex;
                stream.Write(bytes);
                stream.Flush(true);
            }
            var expectedHash = SHA256.HashData(bytes);
            var temporaryFingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(
                temporary,
                cancellationToken);
            if (temporaryFingerprint.Kind != SnapshotLeafKind.File
                || temporaryFingerprint.Length != bytes.Length
                || temporaryFingerprint.Sha256 is not { Length: 32 }
                || !CryptographicOperations.FixedTimeEquals(temporaryFingerprint.Sha256, expectedHash)
                || !AtomicTemporaryFiles.TryMoveRegularFileByHandle(
                    temporary,
                    path,
                    temporaryFingerprint))
            {
                throw new ConcurrentLeafChangeException(
                    "A recovery publication temporary file changed before handle-bound publication.",
                    temporary,
                    path);
            }
            ArtifactMovedBeforeVerificationTestHook?.Invoke(path);
            var fingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(path, cancellationToken);
            if (fingerprint.Kind != SnapshotLeafKind.File
                || fingerprint.Length != bytes.Length
                || fingerprint.Sha256 is not { Length: 32 }
                || !CryptographicOperations.FixedTimeEquals(fingerprint.Sha256, expectedHash))
            {
                throw new InvalidDataException("An atomically published recovery artifact failed verification.");
            }
            VerifyRootsUnchanged();
            return new FileArtifact(path, fingerprint, Convert.ToHexString(expectedHash));
        }
        catch
        {
            publicationOutcomeUncertain = true;
            preserveEvidence = true;
            throw;
        }
        finally
        {
            if (File.Exists(temporary)
                && (!temporaryVolume.HasValue
                    || !temporaryFileIndex.HasValue
                    || !AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                        temporary,
                        temporaryVolume.Value,
                        temporaryFileIndex.Value)))
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Transaction recovery publication temporary-file cleanup was deferred.");
            }
        }
    }

    private static (T Document, FileArtifact Artifact) ReadArtifact<T>(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = PathSafety.WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(path);
        var identity = PathSafety.WindowsPathHandle.GetIdentity(handle);
        var canonical = PathSafety.Normalize(PathSafety.WindowsPathHandle.GetFinalPath(handle));
        if (!canonical.Equals(PathSafety.Normalize(path), StringComparison.OrdinalIgnoreCase)
            || identity.FileIndex == 0
            || identity.NumberOfLinks != 1
            || (identity.FileAttributes
                & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("A transaction recovery artifact is not a unique regular file.");
        }
        using var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        if (stream.Length <= 0 || stream.Length > MaximumArtifactBytes)
            throw new InvalidDataException("A transaction recovery artifact has an invalid size.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
            if (read == 0)
                throw new EndOfStreamException("A transaction recovery artifact changed while being read.");
            offset += read;
        }
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("A transaction recovery artifact grew while being read.");
        T? document;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            var duplicateError = StoredJsonDocumentValidator.ValidateNoDuplicateProperties(
                json.RootElement);
            if (duplicateError is not null)
                throw new InvalidDataException(
                    $"A transaction recovery artifact contains duplicate properties ({duplicateError}).");
            document = JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "A transaction recovery artifact is malformed; recovery evidence was preserved.",
                exception);
        }
        if (document is null)
            throw new InvalidDataException("A transaction recovery artifact is empty.");
        var hash = SHA256.HashData(bytes);
        var fingerprint = new SnapshotLeafFingerprint(
            SnapshotLeafKind.File,
            bytes.Length,
            hash,
            File.GetLastWriteTimeUtc(path).Ticks,
            VolumeSerialNumber: identity.VolumeSerialNumber,
            FileIndex: identity.FileIndex);
        return (document, new FileArtifact(path, fingerprint, Convert.ToHexString(hash)));
    }

    private static void FlushPreparedFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        ValidateOpenRegularFile(stream, path, "prepared transaction output");
        stream.Flush(true);
    }

    private static void ValidateOpenRegularFile(
        FileStream stream,
        string expectedPath,
        string context)
    {
        var identity = PathSafety.WindowsPathHandle.GetIdentity(stream.SafeFileHandle);
        var canonical = PathSafety.Normalize(
            PathSafety.WindowsPathHandle.GetFinalPath(stream.SafeFileHandle));
        if (!canonical.Equals(PathSafety.Normalize(expectedPath), StringComparison.OrdinalIgnoreCase)
            || identity.FileIndex == 0
            || identity.NumberOfLinks != 1
            || (identity.FileAttributes
                & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException($"The {context} is not a unique regular file.");
        }
    }

    private static void ValidateExistingRegularOrMissing(string path, string context)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException($"The {context} path is unsafe.");
    }

    private void DeleteTransactionRecord(
        string transactionPath,
        PathSafety.ExistingDirectorySnapshot expectedDirectory,
        IReadOnlyList<FileArtifact> artifacts)
    {
        var expectedPaths = new HashSet<string>(
            artifacts.Select(artifact => PathSafety.Normalize(artifact.Path)),
            StringComparer.OrdinalIgnoreCase);
        var actualFiles = Directory.EnumerateFiles(transactionPath, "*", SearchOption.TopDirectoryOnly)
            .Select(PathSafety.Normalize)
            .ToArray();
        if (actualFiles.Length != expectedPaths.Count
            || actualFiles.Any(path => !expectedPaths.Contains(path)))
        {
            throw new InvalidDataException("A transaction recovery record changed before cleanup.");
        }
        var transactionId = ParseTransactionDirectoryIdentity(transactionPath);
        var cleanup = new RecoveryCleanup
        {
            Version = CurrentFormatVersion,
            TransactionId = transactionId,
            TargetRoot = targetRoot,
            TargetRootIdentity = targetRootSnapshot.Identity,
            TransactionDirectoryIdentity = expectedDirectory.Identity,
            Artifacts = artifacts
                .Select(artifact => new RecoveryCleanupArtifact
                {
                    FileName = Path.GetFileName(artifact.Path),
                    Fingerprint = artifact.Fingerprint
                })
                .ToList()
        };
        var cleanupArtifact = PublishArtifact(
            Path.Combine(transactionPath, CleanupFileName),
            cleanup,
            CancellationToken.None);
        ContinueCleanup(
            transactionPath,
            expectedDirectory,
            cleanup,
            cleanupArtifact);
    }

    private static void ContinueCleanup(
        string transactionPath,
        PathSafety.ExistingDirectorySnapshot expectedDirectory,
        RecoveryCleanup cleanup,
        FileArtifact cleanupArtifact)
    {
        var expected = new Dictionary<string, RecoveryCleanupArtifact>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in cleanup.Artifacts)
        {
            var path = Path.Combine(transactionPath, artifact.FileName);
            if (!expected.TryAdd(PathSafety.Normalize(path), artifact))
                throw new InvalidDataException("A recovery cleanup plan contains duplicate artifacts.");
        }
        var cleanupPath = PathSafety.Normalize(cleanupArtifact.Path);
        var actualFiles = Directory.EnumerateFiles(transactionPath, "*", SearchOption.TopDirectoryOnly)
            .Select(PathSafety.Normalize)
            .ToArray();
        if (!actualFiles.Contains(cleanupPath, StringComparer.OrdinalIgnoreCase)
            || actualFiles.Any(path => !path.Equals(cleanupPath, StringComparison.OrdinalIgnoreCase)
                                       && !expected.ContainsKey(path)))
        {
            throw new InvalidDataException("A recovery cleanup plan does not cover the transaction directory.");
        }
        foreach (var path in actualFiles.Where(path =>
                     !path.Equals(cleanupPath, StringComparison.OrdinalIgnoreCase)))
        {
            var expectedArtifact = expected[path];
            var current = FileSnapshotJournal.CaptureRecoveryFingerprint(path);
            if (!SameFingerprint(expectedArtifact.Fingerprint, current))
                throw new ConcurrentLeafChangeException(
                    "A recovery artifact changed after cleanup was sealed.",
                    path,
                    cleanupPath);
        }

        foreach (var artifact in cleanup.Artifacts)
        {
            var path = Path.Combine(transactionPath, artifact.FileName);
            if (!File.Exists(path)) continue;
            if (!AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                    path,
                    artifact.Fingerprint.VolumeSerialNumber,
                    artifact.Fingerprint.FileIndex,
                    artifact.Fingerprint.Length,
                    artifact.Fingerprint.Sha256))
            {
                throw new IOException("A sealed recovery artifact could not be removed safely.");
            }
            AfterCleanupArtifactDeletedTestHook?.Invoke(path);
        }
        if (!AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                cleanupArtifact.Path,
                cleanupArtifact.Fingerprint.VolumeSerialNumber,
                cleanupArtifact.Fingerprint.FileIndex,
                cleanupArtifact.Fingerprint.Length,
                cleanupArtifact.Fingerprint.Sha256))
        {
            throw new IOException("The recovery cleanup seal could not be removed safely.");
        }
        DeleteEmptyTransactionDirectory(transactionPath, expectedDirectory);
    }

    private static void DeleteEmptyTransactionDirectory(
        string transactionPath,
        PathSafety.ExistingDirectorySnapshot expectedDirectory)
    {
        if (Directory.EnumerateFileSystemEntries(transactionPath).Any())
            throw new IOException("A transaction recovery directory is not empty after cleanup.");
        var current = PathSafety.GetExistingDirectorySnapshot(transactionPath);
        if (!current.CanonicalPath.Equals(expectedDirectory.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || !current.Identity.Equals(expectedDirectory.Identity, StringComparison.Ordinal))
        {
            throw new ConcurrentLeafChangeException(
                "A transaction recovery directory changed before cleanup.",
                transactionPath);
        }
        Directory.Delete(transactionPath, recursive: false);
    }

    private static bool IsTransactionDirectoryName(string name, out Guid transactionId)
    {
        transactionId = Guid.Empty;
        return name.StartsWith(TransactionPrefix, StringComparison.Ordinal)
               && Guid.TryParseExact(name[TransactionPrefix.Length..], "N", out transactionId);
    }

    private static Guid ParseTransactionDirectoryIdentity(string transactionPath)
    {
        if (!IsTransactionDirectoryName(Path.GetFileName(transactionPath), out var transactionId))
            throw new InvalidDataException("A transaction recovery directory has an invalid identity.");
        return transactionId;
    }

    private static bool IsAllowedRecoveryArtifactFileName(string fileName) =>
        fileName.Equals(BaseFileName, StringComparison.Ordinal)
        || fileName.Equals(PreparedFileName, StringComparison.Ordinal)
        || fileName.Equals(ExternalPendingFileName, StringComparison.Ordinal)
        || fileName.Equals(ExternalFinalFileName, StringComparison.Ordinal)
        || fileName.Equals(ResolvedFileName, StringComparison.Ordinal)
        || TryParseReservationFileName(fileName, out _)
        || TryParseIntentFileName(fileName, out _)
        || TryParseRollbackPlanFileName(fileName, out _)
        || TryParseRollbackReadyFileName(fileName, out _)
        || TryParseRollbackAppliedFileName(fileName, out _)
        || TryParseRollbackDoneFileName(fileName, out _)
        || TryParseSnapshotReadyFileName(fileName, out _)
        || TryParseSnapshotDataFileName(fileName, out _);

    private static string IntentFileName(int sequence) =>
        IntentPrefix + sequence.ToString("D8", CultureInfo.InvariantCulture) + JsonSuffix;

    private static string ReservationFileName(int sequence) =>
        ReservationPrefix + sequence.ToString("D8", CultureInfo.InvariantCulture) + JsonSuffix;

    private static string RollbackPlanFileName(int sequence) =>
        RollbackPlanPrefix + sequence.ToString("D8", CultureInfo.InvariantCulture) + JsonSuffix;

    private static string RollbackReadyFileName(int sequence) =>
        RollbackReadyPrefix + sequence.ToString("D8", CultureInfo.InvariantCulture) + JsonSuffix;

    private static string RollbackAppliedFileName(int sequence) =>
        RollbackAppliedPrefix + sequence.ToString("D8", CultureInfo.InvariantCulture) + JsonSuffix;

    private static string RollbackDoneFileName(int sequence) =>
        RollbackDonePrefix + sequence.ToString("D8", CultureInfo.InvariantCulture) + JsonSuffix;

    private static string SnapshotDataFileName(int entryIndex) =>
        "snapshot-data-" + entryIndex.ToString("D8", CultureInfo.InvariantCulture) + ".bin";

    private static string SnapshotReadyFileName(int entryIndex) =>
        "snapshot-ready-" + entryIndex.ToString("D8", CultureInfo.InvariantCulture) + JsonSuffix;

    private static bool TryParseIntentFileName(string fileName, out int sequence)
    {
        sequence = -1;
        if (!fileName.StartsWith(IntentPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(JsonSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        var text = fileName.AsSpan(IntentPrefix.Length, fileName.Length - IntentPrefix.Length - JsonSuffix.Length);
        return text.Length == 8
               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
               && sequence >= 0;
    }

    private static bool TryParseReservationFileName(string fileName, out int sequence) =>
        TryParseIndexedFileName(fileName, ReservationPrefix, JsonSuffix, out sequence);

    private static bool TryParseRollbackPlanFileName(string fileName, out int sequence) =>
        TryParseIndexedFileName(fileName, RollbackPlanPrefix, JsonSuffix, out sequence);

    private static bool TryParseRollbackReadyFileName(string fileName, out int sequence) =>
        TryParseIndexedFileName(fileName, RollbackReadyPrefix, JsonSuffix, out sequence);

    private static bool TryParseRollbackAppliedFileName(string fileName, out int sequence) =>
        TryParseIndexedFileName(fileName, RollbackAppliedPrefix, JsonSuffix, out sequence);

    private static bool TryParseRollbackDoneFileName(string fileName, out int sequence) =>
        TryParseIndexedFileName(fileName, RollbackDonePrefix, JsonSuffix, out sequence);

    private static bool TryParseSnapshotDataFileName(string fileName, out int entryIndex) =>
        TryParseIndexedFileName(fileName, "snapshot-data-", ".bin", out entryIndex);

    private static bool TryParseSnapshotReadyFileName(string fileName, out int entryIndex) =>
        TryParseIndexedFileName(fileName, "snapshot-ready-", JsonSuffix, out entryIndex);

    private static bool TryParseIndexedFileName(
        string fileName,
        string prefix,
        string suffix,
        out int index)
    {
        index = -1;
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var text = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return text.Length == 8
               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out index)
               && index >= 0;
    }

    private sealed class RecoveryBase
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public string OperationName { get; set; } = string.Empty;
        public string TargetRoot { get; set; } = string.Empty;
        public string TargetRootIdentity { get; set; } = string.Empty;
        public List<RecoveryEntry> Entries { get; set; } = [];
    }

    private sealed class RecoveryEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public SnapshotLeafFingerprint InitialState { get; set; } = new(SnapshotLeafKind.Unstable);
        public string? SnapshotFileName { get; set; }
    }

    private sealed class RecoverySnapshotReady
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public int EntryIndex { get; set; }
        public string BaseSha256 { get; set; } = string.Empty;
        public SnapshotLeafFingerprint SnapshotState { get; set; } = new(SnapshotLeafKind.Unstable);
    }

    private sealed class RecoveryPrepared
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public string BaseSha256 { get; set; } = string.Empty;
        public List<string> SnapshotReadyHashes { get; set; } = [];
    }

    private sealed class RecoveryExternalPending
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public string BaseSha256 { get; set; } = string.Empty;
        public string PreparedSha256 { get; set; } = string.Empty;
        public List<RecoveryExternalEntry> Entries { get; set; } = [];
    }

    private sealed class RecoveryExternalEntry
    {
        public int EntryIndex { get; set; }
        public long MaximumBytes { get; set; }
        public SnapshotLeafFingerprint InitialState { get; set; } = new(SnapshotLeafKind.Unstable);
    }

    private sealed class RecoveryExternalFinal
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public string BaseSha256 { get; set; } = string.Empty;
        public string PreparedSha256 { get; set; } = string.Empty;
        public string PendingSha256 { get; set; } = string.Empty;
        public string Disposition { get; set; } = string.Empty;
        public List<RecoveryExternalFinalEntry> Entries { get; set; } = [];
    }

    private sealed class RecoveryExternalFinalEntry
    {
        public int EntryIndex { get; set; }
        public SnapshotLeafFingerprint FinalState { get; set; } = new(SnapshotLeafKind.Unstable);
    }

    private sealed class RecoveryCommitReservation
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public int Sequence { get; set; }
        public string BaseSha256 { get; set; } = string.Empty;
        public string? PreviousIntentSha256 { get; set; }
        public int TargetEntryIndex { get; set; }
        public int BackupEntryIndex { get; set; }
        public bool KeepBackup { get; set; }
        public SnapshotLeafFingerprint TargetBefore { get; set; } = new(SnapshotLeafKind.Unstable);
        public SnapshotLeafFingerprint BackupBefore { get; set; } = new(SnapshotLeafKind.Unstable);
        public string PreparedRelativePath { get; set; } = string.Empty;
        public string? DisplacedRelativePath { get; set; }
        public string? RejectedRelativePath { get; set; }
        public string? PriorBackupRelativePath { get; set; }
        public string? OriginalRecoveryRelativePath { get; set; }
    }

    private sealed class RecoveryIntent
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public int Sequence { get; set; }
        public string BaseSha256 { get; set; } = string.Empty;
        public string? PreviousIntentSha256 { get; set; }
        public string ReservationSha256 { get; set; } = string.Empty;
        public int TargetEntryIndex { get; set; }
        public int BackupEntryIndex { get; set; }
        public bool KeepBackup { get; set; }
        public SnapshotLeafFingerprint TargetBefore { get; set; } = new(SnapshotLeafKind.Unstable);
        public SnapshotLeafFingerprint BackupBefore { get; set; } = new(SnapshotLeafKind.Unstable);
        public SnapshotLeafFingerprint TargetAfter { get; set; } = new(SnapshotLeafKind.Unstable);
        public SnapshotLeafFingerprint BackupAfter { get; set; } = new(SnapshotLeafKind.Unstable);
    }

    private sealed class RecoveryResolved
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public string BaseSha256 { get; set; } = string.Empty;
        public string PreparedSha256 { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public int ReservationCount { get; set; }
        public int IntentCount { get; set; }
        public string? LastIntentSha256 { get; set; }
        public int RollbackCount { get; set; }
        public string? LastRollbackDoneSha256 { get; set; }
    }

    private sealed class RecoveryRollbackPlan
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public int Sequence { get; set; }
        public string BaseSha256 { get; set; } = string.Empty;
        public string PreparedSha256 { get; set; } = string.Empty;
        public int ForwardIntentCount { get; set; }
        public string? LastForwardIntentSha256 { get; set; }
        public string? PreviousRollbackDoneSha256 { get; set; }
        public int EntryIndex { get; set; }
        public string Kind { get; set; } = string.Empty;
        public SnapshotLeafFingerprint ExpectedCurrent { get; set; } = new(SnapshotLeafKind.Unstable);
        public string? PreparedRelativePath { get; set; }
        public string? DisplacedRelativePath { get; set; }
    }

    private sealed class RecoveryRollbackReady
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public int Sequence { get; set; }
        public string PlanSha256 { get; set; } = string.Empty;
        public SnapshotLeafFingerprint PredictedRestored { get; set; } = new(SnapshotLeafKind.Unstable);
    }

    private sealed class RecoveryRollbackApplied
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public int Sequence { get; set; }
        public string ReadySha256 { get; set; } = string.Empty;
        public SnapshotLeafFingerprint ActualRestored { get; set; } = new(SnapshotLeafKind.Unstable);
        public SnapshotLeafFingerprint DisplacedState { get; set; } = new(SnapshotLeafKind.Unstable);
    }

    private sealed class RecoveryRollbackDone
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public int Sequence { get; set; }
        public string AppliedSha256 { get; set; } = string.Empty;
        public string? PreviousDoneSha256 { get; set; }
        public SnapshotLeafFingerprint ActualRestored { get; set; } = new(SnapshotLeafKind.Unstable);
    }

    private sealed class RecoveryCleanup
    {
        public int Version { get; set; }
        public Guid TransactionId { get; set; }
        public string TargetRoot { get; set; } = string.Empty;
        public string TargetRootIdentity { get; set; } = string.Empty;
        public string TransactionDirectoryIdentity { get; set; } = string.Empty;
        public List<RecoveryCleanupArtifact> Artifacts { get; set; } = [];
    }

    private sealed class RecoveryCleanupArtifact
    {
        public string FileName { get; set; } = string.Empty;
        public SnapshotLeafFingerprint Fingerprint { get; set; } = new(SnapshotLeafKind.Unstable);
    }

    private sealed record FileArtifact(
        string Path,
        SnapshotLeafFingerprint Fingerprint,
        string Sha256);

    private sealed class ResolvedRecoveryEntry(
        int index,
        RecoveryEntry entry,
        string targetPath,
        string? snapshotPath)
    {
        internal int Index { get; } = index;
        internal RecoveryEntry Entry { get; } = entry;
        internal string TargetPath { get; } = targetPath;
        internal string? SnapshotPath { get; set; } = snapshotPath;
    }

    private sealed record LeafTransition(
        SnapshotLeafFingerprint Before,
        SnapshotLeafFingerprint After);

    private sealed class ResolvedCommitReservation(
        AtomicCommitRecoveryPlan plan,
        RecoveryCommitReservation reservation,
        FileArtifact reservationArtifact,
        RecoveryIntent? intent,
        FileArtifact? intentArtifact)
    {
        internal AtomicCommitRecoveryPlan Plan { get; } = plan;
        internal RecoveryCommitReservation Reservation { get; } = reservation;
        internal FileArtifact ReservationArtifact { get; } = reservationArtifact;
        internal RecoveryIntent? Intent { get; } = intent;
        internal FileArtifact? IntentArtifact { get; } = intentArtifact;
    }

    private sealed class ResolvedRollbackMutation
    {
        internal required RecoveryRollbackPlan Plan { get; init; }
        internal required FileArtifact PlanArtifact { get; init; }
        internal RecoveryRollbackReady? Ready { get; set; }
        internal FileArtifact? ReadyArtifact { get; set; }
        internal RecoveryRollbackApplied? Applied { get; set; }
        internal FileArtifact? AppliedArtifact { get; set; }
        internal RecoveryRollbackDone? Done { get; set; }
        internal FileArtifact? DoneArtifact { get; set; }
        internal required string TargetPath { get; init; }
        internal string? PreparedPath { get; init; }
        internal string? DisplacedPath { get; init; }
    }

    private sealed class RecoveryPlan
    {
        internal RecoveryPlan(
            string transactionPath,
            PathSafety.ExistingDirectorySnapshot transactionSnapshot,
            RecoveryBase? document,
            FileArtifact? baseArtifact,
            FileArtifact? preparedArtifact,
            List<FileArtifact> reservationArtifacts,
            List<FileArtifact> intentArtifacts,
            List<ResolvedCommitReservation> commitReservations,
            FileArtifact? resolvedArtifact,
            List<ResolvedRecoveryEntry>? resolvedEntries,
            List<FileRollbackEntry> rollbackEntries,
            List<ResolvedRollbackMutation> rollbackMutations,
            List<FileArtifact> artifacts,
            RecoveryCleanup? cleanupDocument,
            FileArtifact? cleanupArtifact)
        {
            TransactionPath = transactionPath;
            TransactionSnapshot = transactionSnapshot;
            Document = document;
            BaseArtifact = baseArtifact;
            PreparedArtifact = preparedArtifact;
            ReservationArtifacts = reservationArtifacts;
            IntentArtifacts = intentArtifacts;
            CommitReservations = commitReservations;
            ResolvedArtifact = resolvedArtifact;
            ResolvedEntries = resolvedEntries;
            RollbackEntries = rollbackEntries;
            RollbackMutations = rollbackMutations;
            Artifacts = artifacts;
            CleanupDocument = cleanupDocument;
            CleanupArtifact = cleanupArtifact;
        }

        internal string TransactionPath { get; }
        internal PathSafety.ExistingDirectorySnapshot TransactionSnapshot { get; }
        internal RecoveryBase? Document { get; }
        internal FileArtifact? BaseArtifact { get; }
        internal FileArtifact? PreparedArtifact { get; }
        internal List<FileArtifact> ReservationArtifacts { get; }
        internal List<FileArtifact> IntentArtifacts { get; }
        internal List<ResolvedCommitReservation> CommitReservations { get; }
        internal FileArtifact? ResolvedArtifact { get; set; }
        internal List<ResolvedRecoveryEntry>? ResolvedEntries { get; }
        internal List<FileRollbackEntry> RollbackEntries { get; }
        internal List<ResolvedRollbackMutation> RollbackMutations { get; }
        internal List<FileArtifact> Artifacts { get; }
        internal RecoveryCleanup? CleanupDocument { get; }
        internal FileArtifact? CleanupArtifact { get; }
        internal bool IsEmpty => Document is null && CleanupDocument is null;
        internal bool IsResolved => ResolvedArtifact is not null;
        internal bool RequiresRollback => !IsEmpty && PreparedArtifact is not null && !IsResolved;

        internal static RecoveryPlan Empty(
            string transactionPath,
            PathSafety.ExistingDirectorySnapshot transactionSnapshot) =>
            new(
                transactionPath,
                transactionSnapshot,
                null,
                null,
                null,
                [],
                [],
                [],
                null,
                null,
                [],
                [],
                [],
                null,
                null);

        internal static RecoveryPlan CleanupPending(
            string transactionPath,
            PathSafety.ExistingDirectorySnapshot transactionSnapshot,
            RecoveryCleanup cleanup,
            FileArtifact cleanupArtifact) =>
            new(
                transactionPath,
                transactionSnapshot,
                null,
                null,
                null,
                [],
                [],
                [],
                null,
                null,
                [],
                [],
                [],
                cleanup,
                cleanupArtifact);
    }
}
