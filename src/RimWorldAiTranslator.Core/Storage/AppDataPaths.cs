using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

public sealed class AppDataPaths
{
    public AppDataPaths(string? root = null)
    {
        var requested = Path.GetFullPath(string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimWorldAiTranslator")
            : root);
        EnsureLocalFilesystemPath(requested);
        var canonicalRoot = Directory.Exists(requested)
            ? GetCanonicalExistingRoot(requested)
            : PathSafety.GetCanonicalProspectiveDirectory(requested);
        EnsureLocalFilesystemPath(canonicalRoot);
        Root = canonicalRoot;
    }

    public string Root { get; }
    public string Projects => Path.Combine(Root, "projects.json");
    public string Settings => Path.Combine(Root, "settings.json");
    public string ModCatalog => Path.Combine(Root, "mod-catalog.json");
    public string ProjectStats => Path.Combine(Root, "project-stats.json");
    public string Reviews => Path.Combine(Root, "reviews");
    public string Logs => Path.Combine(Root, "logs");
    public string Temp => Path.Combine(Root, "temp");
    public string RmkBuilderStaging => Path.Combine(Root, "rmk-builder-staging");
    public string RecoveryAuthority => Path.Combine(Root, "recovery-authority");

    public void EnsureExists()
    {
        EnsureLocalFilesystemPath(Root);
        using var creationBoundary =
            PathSafety.AcquireTrustedDirectoryCreationBoundary(Root);
        PathSafety.EnsureNoReparsePointsToVolumeRoot(Root);
        var rootSnapshot = PathSafety.GetExistingDirectorySnapshot(Root);
        EnsureLocalFilesystemPath(rootSnapshot.CanonicalPath);
        if (!rootSnapshot.CanonicalPath.Equals(Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The application data root changed while it was being created.");
        using var rootLease = PathSafety.WindowsPathHandle.OpenDirectoryWithoutDeleteSharing(Root);
        var leasedRoot = PathSafety.Normalize(PathSafety.WindowsPathHandle.GetFinalPath(rootLease));
        var leasedIdentity = PathSafety.WindowsPathHandle.GetIdentity(rootLease);
        var formattedLeasedIdentity =
            $"{leasedIdentity.VolumeSerialNumber:x8}:{leasedIdentity.FileIndex:x16}";
        if (!leasedRoot.Equals(rootSnapshot.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || !formattedLeasedIdentity.Equals(rootSnapshot.Identity, StringComparison.Ordinal)
            || (leasedIdentity.FileAttributes
                & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
               != FileAttributes.Directory)
        {
            throw new InvalidDataException(
                "The application data root changed before its child directories could be validated.");
        }
        foreach (var child in new[] { Reviews, Logs, Temp, RmkBuilderStaging, RecoveryAuthority })
            EnsureOwnedChildDirectory(child, rootSnapshot);
    }

    private static void EnsureOwnedChildDirectory(
        string childPath,
        PathSafety.ExistingDirectorySnapshot rootSnapshot)
    {
        var expected = PathSafety.Normalize(childPath);
        EnsureLocalFilesystemPath(expected);
        if (!PathSafety.IsStrictlyInside(expected, rootSnapshot.CanonicalPath))
            throw new InvalidDataException("An application data directory is outside the exact data root.");

        Directory.CreateDirectory(expected);
        PathSafety.EnsureNoReparsePoints(expected, rootSnapshot.CanonicalPath);
        PathSafety.EnsureNoReparsePointsToVolumeRoot(expected);
        var childSnapshot = PathSafety.GetExistingDirectorySnapshot(expected);
        EnsureLocalFilesystemPath(childSnapshot.CanonicalPath);
        if (!childSnapshot.CanonicalPath.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || !PathSafety.IsStrictlyInside(childSnapshot.CanonicalPath, rootSnapshot.CanonicalPath))
        {
            throw new InvalidDataException(
                "An application data child directory is redirected outside its exact expected path.");
        }

        using var childLease =
            PathSafety.WindowsPathHandle.OpenDirectoryWithoutDeleteSharing(expected);
        var leasedChild = PathSafety.Normalize(PathSafety.WindowsPathHandle.GetFinalPath(childLease));
        var leasedChildIdentity = PathSafety.WindowsPathHandle.GetIdentity(childLease);
        var formattedLeasedChildIdentity =
            $"{leasedChildIdentity.VolumeSerialNumber:x8}:{leasedChildIdentity.FileIndex:x16}";
        if (!leasedChild.Equals(childSnapshot.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || !formattedLeasedChildIdentity.Equals(childSnapshot.Identity, StringComparison.Ordinal)
            || (leasedChildIdentity.FileAttributes
                & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
               != FileAttributes.Directory)
        {
            throw new InvalidDataException(
                "An application data child directory changed while its identity was validated.");
        }

        var currentRoot = PathSafety.GetExistingDirectorySnapshot(rootSnapshot.CanonicalPath);
        if (!currentRoot.CanonicalPath.Equals(rootSnapshot.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || !currentRoot.Identity.Equals(rootSnapshot.Identity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The application data root identity changed while its child directories were validated.");
        }
    }

    private static string GetCanonicalExistingRoot(string requested)
    {
        PathSafety.EnsureNoReparsePointsToVolumeRoot(requested);
        return PathSafety.GetCanonicalExistingDirectory(requested);
    }

    private static void EnsureLocalFilesystemPath(string path)
    {
        if (PathSafety.IsNetworkPath(path))
            throw new InvalidDataException("The application data root must use a local filesystem path.");
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(volumeRoot))
            throw new InvalidDataException("The application data root has no filesystem volume.");
        if (new DriveInfo(volumeRoot).DriveType == DriveType.Network)
            throw new InvalidDataException("The application data root must not use a mapped network drive.");
    }
}
