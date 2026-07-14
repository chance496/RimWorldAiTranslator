namespace RimWorldAiTranslator.Core.Storage;

/// <summary>
/// Immutable target-tree path reservation published by the durable recovery
/// authority before any prepared or intermediate file is created.
/// </summary>
internal sealed class AtomicCommitRecoveryPlan
{
    internal required Guid TransactionId { get; init; }
    internal required int Sequence { get; init; }
    internal required string TargetPath { get; init; }
    internal required string BackupPath { get; init; }
    internal required string PreparedPath { get; init; }
    internal string? DisplacedPath { get; init; }
    internal string? RejectedPath { get; init; }
    internal string? PriorBackupPath { get; init; }
    internal string? OriginalRecoveryPath { get; init; }
    internal required bool KeepBackup { get; init; }
    internal required SnapshotLeafFingerprint TargetBefore { get; init; }
    internal required SnapshotLeafFingerprint BackupBefore { get; init; }
    internal SnapshotLeafFingerprint? PreparedFingerprint { get; set; }
}
