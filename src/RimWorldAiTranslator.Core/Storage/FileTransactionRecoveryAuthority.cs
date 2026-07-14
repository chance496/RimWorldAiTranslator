using System.Security.Cryptography;
using System.Text;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

/// <summary>
/// Explicit trust anchor for restart recovery records. The authority directory is
/// application-owned and must be separate from every target tree it protects.
/// </summary>
public sealed class FileTransactionRecoveryAuthority
{
    private readonly PathSafety.ExistingDirectorySnapshot rootSnapshot;

    public FileTransactionRecoveryAuthority(string trustedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Durable transaction recovery requires Windows file identities.");

        var requested = PathSafety.Normalize(trustedRoot);
        if (!Directory.Exists(requested))
            throw new DirectoryNotFoundException("The transaction recovery authority does not exist.");
        PathSafety.EnsureNoReparsePointsToVolumeRoot(requested);
        rootSnapshot = PathSafety.GetExistingDirectorySnapshot(requested);
        if (!rootSnapshot.CanonicalPath.Equals(requested, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The transaction recovery authority must be its canonical physical path.");
        Root = requested;
    }

    public string Root { get; }

    public void RecoverPending(
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        using var session = Acquire(targetRoot, cancellationToken);
    }

    internal FileTransactionRecoverySession Acquire(
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyUnchanged();
        var targetSnapshot = ValidateTargetRoot(targetRoot);
        var shardPath = GetShardPath(targetSnapshot.CanonicalPath);
        if (File.Exists(shardPath))
            throw new InvalidDataException("The transaction recovery shard path is occupied by a file.");
        using var shardCreationBoundary =
            PathSafety.AcquireTrustedDirectoryCreationBoundary(shardPath, cancellationToken);
        PathSafety.EnsureNoReparsePoints(shardPath, Root);
        var shardSnapshot = PathSafety.GetExistingDirectorySnapshot(shardPath);
        if (!shardSnapshot.CanonicalPath.Equals(shardPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The transaction recovery shard resolved outside its expected path.");
        var trustedGuardPaths = shardCreationBoundary is PathSafety.TrustedWriteBoundary trustedBoundary
            ? trustedBoundary.ActiveGuardPaths
            : throw new InvalidOperationException("The trusted shard boundary did not expose its active guards.");
        VerifyUnchanged();
        return new FileTransactionRecoverySession(
            this,
            targetSnapshot,
            shardSnapshot,
            trustedGuardPaths,
            cancellationToken);
    }

    internal void VerifyUnchanged()
    {
        PathSafety.EnsureNoReparsePointsToVolumeRoot(Root);
        var current = PathSafety.GetExistingDirectorySnapshot(Root);
        if (!current.CanonicalPath.Equals(rootSnapshot.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || !current.Identity.Equals(rootSnapshot.Identity, StringComparison.Ordinal))
        {
            throw new ConcurrentLeafChangeException(
                "The transaction recovery authority changed while it was in use.",
                Root);
        }
    }

    private PathSafety.ExistingDirectorySnapshot ValidateTargetRoot(string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        var requested = PathSafety.Normalize(targetRoot);
        if (!Directory.Exists(requested))
            throw new DirectoryNotFoundException("The transaction target root does not exist.");
        PathSafety.EnsureNoReparsePointsToVolumeRoot(requested);
        var targetSnapshot = PathSafety.GetExistingDirectorySnapshot(requested);
        if (!targetSnapshot.CanonicalPath.Equals(requested, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The transaction target root must be its canonical physical path.");
        if (requested.Equals(Root, StringComparison.OrdinalIgnoreCase)
            || PathSafety.IsStrictlyInside(requested, Root)
            || PathSafety.IsStrictlyInside(Root, requested))
        {
            throw new InvalidDataException(
                "The transaction recovery authority and target root must not overlap.");
        }
        return targetSnapshot;
    }

    private string GetShardPath(string targetRoot)
    {
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes(targetRoot.ToUpperInvariant()));
        return Path.Combine(Root, "target-" + Convert.ToHexString(identity).ToLowerInvariant());
    }
}
