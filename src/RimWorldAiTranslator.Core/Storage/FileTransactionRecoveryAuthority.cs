using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

/// <summary>
/// Owns the small amount of durable state needed to offer an explicit restore
/// after an interrupted local file transaction. It never infers or commits an
/// interrupted operation automatically.
/// </summary>
public sealed class FileTransactionRecoveryAuthority
{
    internal const string OwnerMarkerName = "owner.marker";
    internal const string OwnerMarkerContent = "RimWorldAiTranslator.FileTransaction.v1";
    internal const string ManifestFileName = "transaction.json";
    internal const string StartedMarkerName = "started.marker";
    private const int MaximumPendingTransactions = 128;

    public FileTransactionRecoveryAuthority(string trustedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);
        var requested = PathSafety.Normalize(trustedRoot);
        if (!Directory.Exists(requested))
            throw new DirectoryNotFoundException("The transaction recovery directory does not exist.");
        if (PathSafety.IsNetworkPath(requested))
            throw new InvalidDataException("Transaction recovery requires a local directory.");
        PathSafety.EnsureNoReparsePointsToVolumeRoot(requested);
        Root = requested;
    }

    public string Root { get; }

    public IReadOnlyList<PendingFileTransaction> FindPending(
        string? targetRoot = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedTarget = string.IsNullOrWhiteSpace(targetRoot)
            ? null
            : ValidateTargetRoot(targetRoot);
        var pending = new List<PendingFileTransaction>();
        foreach (var directory in Directory.EnumerateDirectories(
                     Root,
                     "transaction-*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pending.Count >= MaximumPendingTransactions)
                throw new InvalidDataException("Too many interrupted file transactions require attention.");
            if (!IsOwnedTransactionDirectory(directory)) continue;

            var manifestPath = Path.Combine(directory, ManifestFileName);
            var startedPath = Path.Combine(directory, StartedMarkerName);
            if (!File.Exists(startedPath))
            {
                DeleteOwnedTransactionDirectory(directory);
                continue;
            }

            SimpleTransactionManifest manifest;
            try
            {
                manifest = ReadManifest(manifestPath);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException
                                              or InvalidDataException)
            {
                pending.Add(new PendingFileTransaction(
                    Path.GetFileName(directory),
                    string.Empty,
                    "Interrupted file save",
                    0,
                    DateTimeOffset.MinValue,
                    directory,
                    IsRestorable: false));
                continue;
            }

            string manifestRoot;
            try
            {
                manifestRoot = ValidateTargetRoot(manifest.TargetRoot);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or InvalidOperationException)
            {
                pending.Add(new PendingFileTransaction(
                    manifest.TransactionId,
                    string.Empty,
                    manifest.OperationName,
                    manifest.Entries.Count,
                    manifest.CreatedUtc,
                    directory,
                    IsRestorable: false));
                continue;
            }
            if (normalizedTarget is not null
                && !manifestRoot.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            pending.Add(new PendingFileTransaction(
                manifest.TransactionId,
                manifestRoot,
                manifest.OperationName,
                manifest.Entries.Count,
                manifest.CreatedUtc,
                directory,
                IsRestorable: true));
        }
        return pending;
    }

    public void RecoverPending(
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        var pending = FindPending(targetRoot, cancellationToken);
        if (pending.Count == 0) return;
        throw new IncompleteFileTransactionException(
            "A previous save or apply operation did not finish. Restore its backup only after user confirmation.",
            pending.Count);
    }

    public void Restore(
        PendingFileTransaction pending,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        cancellationToken.ThrowIfCancellationRequested();
        var transactionPath = ValidateOwnedTransactionPath(pending.TransactionPath);
        if (!pending.IsRestorable)
            throw new InvalidDataException("The interrupted save metadata is damaged and cannot be restored automatically.");
        var manifest = ReadManifest(Path.Combine(transactionPath, ManifestFileName));
        var targetRoot = ValidateTargetRoot(manifest.TargetRoot);
        var errors = new List<string>();

        for (var index = manifest.Entries.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = manifest.Entries[index];
            try
            {
                var target = ResolveRelativeTarget(targetRoot, entry.RelativePath);
                if (entry.Existed)
                {
                    if (string.IsNullOrWhiteSpace(entry.SnapshotFileName))
                        throw new InvalidDataException("An interrupted save backup is missing from its manifest.");
                    var snapshot = Path.Combine(transactionPath, "backups", entry.SnapshotFileName);
                    RestoreSnapshot(snapshot, target, cancellationToken);
                }
                else if (File.Exists(target))
                {
                    if (File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                        throw new UnauthorizedAccessException("A created output is read-only.");
                    File.Delete(target);
                }
                else if (Directory.Exists(target))
                {
                    throw new IOException("A directory occupies an interrupted file output path.");
                }
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                errors.Add($"{Path.GetFileName(entry.RelativePath)} ({exception.GetType().Name})");
            }
        }

        if (errors.Count > 0)
            throw new IOException($"Backup restore was incomplete: {string.Join(" | ", errors)}");
        DeleteOwnedTransactionDirectory(transactionPath);
    }

    internal FileTransactionRecoverySession Acquire(
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedTarget = ValidateTargetRoot(targetRoot);
        RecoverPending(normalizedTarget, cancellationToken);
        var lockName = "target-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTarget.ToUpperInvariant())))[..24].ToLowerInvariant() + ".lock";
        var lease = new FileStream(
            Path.Combine(Root, lockName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.None);
        return new FileTransactionRecoverySession(this, normalizedTarget, lease);
    }

    internal string CreateTransactionDirectory()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var path = Path.Combine(Root, $"transaction-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(path);
                WriteSmallFlushedFile(Path.Combine(path, OwnerMarkerName), OwnerMarkerContent);
                Directory.CreateDirectory(Path.Combine(path, "backups"));
                return path;
            }
            catch (IOException) when (attempt < 7)
            {
                // A GUID collision is exceptionally unlikely; retry without widening scope.
            }
        }
        throw new IOException("A transaction recovery directory could not be created.");
    }

    internal void DeleteOwnedTransactionDirectory(string transactionPath)
    {
        var validated = ValidateOwnedTransactionPath(transactionPath);
        if (!IsOwnedTransactionDirectory(validated))
            throw new InvalidDataException("Refusing to remove an unowned recovery directory.");
        Directory.Delete(validated, recursive: true);
    }

    internal static void WriteSmallFlushedFile(string path, string content)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(content);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4_096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal static SimpleTransactionManifest ReadManifest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024);
        if (stream.Length is <= 0 or > 8 * 1024 * 1024)
            throw new InvalidDataException("Interrupted save metadata has an invalid size.");
        var manifest = JsonSerializer.Deserialize<SimpleTransactionManifest>(stream)
            ?? throw new InvalidDataException("Interrupted save metadata is empty.");
        if (manifest.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(manifest.TransactionId)
            || string.IsNullOrWhiteSpace(manifest.TargetRoot)
            || manifest.Entries is null
            || manifest.Entries.Count > FileSnapshotJournal.MaximumSnapshotTargets * 2)
        {
            throw new InvalidDataException("Interrupted save metadata uses an unsupported format.");
        }
        foreach (var entry in manifest.Entries)
        {
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.RelativePath)
                || entry.Existed
                && (string.IsNullOrWhiteSpace(entry.SnapshotFileName)
                    || !Path.GetFileName(entry.SnapshotFileName)
                        .Equals(entry.SnapshotFileName, StringComparison.Ordinal)
                    || !entry.SnapshotFileName.EndsWith(".bin", StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Interrupted save metadata contains an invalid backup entry.");
            }
        }
        if (string.IsNullOrWhiteSpace(manifest.OperationName))
            manifest.OperationName = "Interrupted file save";
        return manifest;
    }

    private string ValidateTargetRoot(string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        var target = PathSafety.Normalize(targetRoot);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException("The transaction target directory does not exist.");
        if (PathSafety.IsNetworkPath(target))
            throw new InvalidDataException("File transactions require a local target directory.");
        if (PathSafety.IsWorkshopContentPath(target))
            throw new InvalidOperationException("Steam Workshop content is read-only.");
        if (target.Equals(Root, StringComparison.OrdinalIgnoreCase)
            || PathSafety.IsStrictlyInside(target, Root)
            || PathSafety.IsStrictlyInside(Root, target))
        {
            throw new InvalidDataException("The recovery and target directories must not overlap.");
        }
        PathSafety.EnsureNoReparsePoints(target, target);
        return target;
    }

    private string ValidateOwnedTransactionPath(string path)
    {
        var normalized = PathSafety.Normalize(path);
        var parent = Path.GetDirectoryName(normalized);
        if (parent is null
            || !parent.Equals(Root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(normalized).StartsWith("transaction-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The recovery entry is outside the application-owned recovery directory.");
        }
        return normalized;
    }

    private bool IsOwnedTransactionDirectory(string path)
    {
        try
        {
            var validated = ValidateOwnedTransactionPath(path);
            var marker = Path.Combine(validated, OwnerMarkerName);
            return File.Exists(marker)
                   && File.ReadAllText(marker, Encoding.UTF8)
                       .Equals(OwnerMarkerContent, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
        {
            return false;
        }
    }

    private static string ResolveRelativeTarget(string targetRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Interrupted save metadata contains an invalid target path.");
        var target = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
        if (!PathSafety.IsStrictlyInside(target, targetRoot))
            throw new InvalidDataException("Interrupted save metadata escapes its target directory.");
        return target;
    }

    private static void RestoreSnapshot(
        string snapshot,
        string target,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(snapshot) || Directory.Exists(snapshot))
            throw new InvalidDataException("An interrupted save backup is missing.");
        var directory = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("An interrupted save target has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException("An interrupted save target is read-only.");
        var prepared = AtomicTemporaryFiles.CreatePath(target);
        try
        {
            AtomicFile.CopyFlushedBounded(
                snapshot,
                prepared,
                PathSafety.MaximumTrustedLeafBytes,
                cancellationToken);
            if (File.Exists(target))
                File.Replace(prepared, target, null, ignoreMetadataErrors: true);
            else
                File.Move(prepared, target);
        }
        finally
        {
            if (File.Exists(prepared)) File.Delete(prepared);
        }
    }
}

public sealed class PendingFileTransaction
{
    internal PendingFileTransaction(
        string id,
        string targetRoot,
        string operationName,
        int targetCount,
        DateTimeOffset createdUtc,
        string transactionPath,
        bool IsRestorable)
    {
        Id = id;
        TargetRoot = targetRoot;
        OperationName = operationName;
        TargetCount = targetCount;
        CreatedUtc = createdUtc;
        TransactionPath = transactionPath;
        this.IsRestorable = IsRestorable;
    }

    public string Id { get; }
    public string TargetRoot { get; }
    public string OperationName { get; }
    public int TargetCount { get; }
    public DateTimeOffset CreatedUtc { get; }
    public bool IsRestorable { get; }
    internal string TransactionPath { get; }
}

public sealed class IncompleteFileTransactionException(string message, int pendingCount)
    : InvalidOperationException(message)
{
    public int PendingCount { get; } = pendingCount;
}

internal sealed class SimpleTransactionManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string TransactionId { get; set; } = string.Empty;
    public string TargetRoot { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public List<SimpleTransactionManifestEntry> Entries { get; set; } = [];
}

internal sealed class SimpleTransactionManifestEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public bool Existed { get; set; }
    public string? SnapshotFileName { get; set; }
}
