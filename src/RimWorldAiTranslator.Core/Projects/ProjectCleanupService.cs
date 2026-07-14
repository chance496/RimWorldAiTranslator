using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Utilities;

namespace RimWorldAiTranslator.Core.Projects;

public sealed record ProjectCleanupPlan(
    IReadOnlyList<string> SafePaths,
    IReadOnlyList<string> UnsafePaths,
    IReadOnlyList<string> MarkerErrors)
{
    public string ProjectIdentity { get; init; } = string.Empty;
    public IReadOnlyList<string> ReviewRoots { get; init; } = [];
    public IReadOnlyDictionary<string, string> SafePathIdentities { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

public sealed class ProjectCleanupService
{
    private const string OwnershipMarkerFileName = ".rimworld-ai-project.json";
    private const int OwnershipMarkerVersion = 2;
    private readonly Func<string, DriveType> driveTypeResolver;
    internal Action<string>? BeforeDirectoryProbeTestHook { get; set; }
    internal Action<string>? BeforeDirectoryQuarantineTestHook { get; set; }

    public ProjectCleanupService()
        : this(GetDriveType)
    {
    }

    internal ProjectCleanupService(Func<string, DriveType> driveTypeResolver)
    {
        this.driveTypeResolver = driveTypeResolver
            ?? throw new ArgumentNullException(nameof(driveTypeResolver));
    }

    public ProjectCleanupPlan BuildPlan(TranslationProject project, IEnumerable<string> reviewRoots)
    {
        var roots = reviewRoots
            .Where(value => !string.IsNullOrWhiteSpace(value) && IsLocalFilesystemPath(value))
            .Select(PathSafety.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var safe = new List<string>();
        var unsafePaths = new List<string>();
        var markerErrors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safePathIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var recorded = project.Runs.Select(run => run.ReviewRoot).Prepend(project.LatestReviewRoot);

        foreach (var path in recorded.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!IsLocalFilesystemPath(path))
            {
                unsafePaths.Add(path);
                continue;
            }
            string full;
            try
            {
                full = PathSafety.Normalize(path);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Invalid recorded review path rejected ({exception.GetType().Name}).");
                unsafePaths.Add(path);
                continue;
            }

            if (!DirectoryExistsLocal(full)) continue;

            if (!seen.Add(full))
            {
                continue;
            }

            var owned = GetAppOwnedReviewDirectory(full, roots, project.ModRoot);
            if (owned is null)
            {
                unsafePaths.Add(full);
            }
            else if (ValidateOwnershipMarker(project, owned, out var markerFailure) == OwnershipMarkerStatus.Valid)
            {
                if (TryCaptureDirectoryIdentity(owned, out var identity, out var identityFailure))
                {
                    safe.Add(owned);
                    safePathIdentities.Add(owned, identity);
                }
                else
                {
                    unsafePaths.Add(full);
                    markerErrors.Add($"{full} : {identityFailure}");
                }
            }
            else
            {
                unsafePaths.Add(full);
                markerErrors.Add($"{full} : {markerFailure}");
            }
        }

        foreach (var root in roots.Where(DirectoryExistsLocal))
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var markerPath = Path.Combine(directory, OwnershipMarkerFileName);
                if (!File.Exists(markerPath))
                {
                    continue;
                }

                var owned = GetAppOwnedReviewDirectory(directory, roots, project.ModRoot);
                if (owned is null || !seen.Add(owned))
                {
                    continue;
                }

                var markerStatus = ValidateOwnershipMarker(project, owned, out var markerFailure);
                if (markerStatus == OwnershipMarkerStatus.Valid)
                {
                    if (TryCaptureDirectoryIdentity(owned, out var identity, out var identityFailure))
                    {
                        safe.Add(owned);
                        safePathIdentities.Add(owned, identity);
                    }
                    else
                    {
                        markerErrors.Add($"{directory} : {identityFailure}");
                    }
                }
                else if (markerStatus == OwnershipMarkerStatus.Invalid)
                {
                    markerErrors.Add($"{directory} : {markerFailure}");
                }
            }
        }

        return new ProjectCleanupPlan(safe, unsafePaths, markerErrors)
        {
            ProjectIdentity = CreateProjectIdentity(project),
            ReviewRoots = new ReadOnlyCollection<string>(roots),
            SafePathIdentities = new ReadOnlyDictionary<string, string>(safePathIdentities)
        };
    }

    public IReadOnlyList<string> Remove(TranslationProject project, IEnumerable<string> reviewRoots, IEnumerable<string> paths)
    {
        var roots = reviewRoots
            .Where(value => !string.IsNullOrWhiteSpace(value) && IsLocalFilesystemPath(value))
            .Select(PathSafety.Normalize)
            .ToArray();
        var exactPaths = paths
            .Where(value => !string.IsNullOrWhiteSpace(value) && IsLocalFilesystemPath(value))
            .Select(PathSafety.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in exactPaths)
        {
            if (TryCaptureDirectoryIdentity(path, out var identity, out _)) identities[path] = identity;
        }
        var exactPlan = new ProjectCleanupPlan(exactPaths, [], [])
        {
            ProjectIdentity = CreateProjectIdentity(project),
            ReviewRoots = new ReadOnlyCollection<string>(roots),
            SafePathIdentities = new ReadOnlyDictionary<string, string>(identities)
        };
        return Remove(project, exactPlan);
    }

    public IReadOnlyList<string> ValidateRemovalPlan(
        TranslationProject project,
        ProjectCleanupPlan confirmedPlan) =>
        ProcessRemovalPlan(project, confirmedPlan, deleteDirectories: false);

    public IReadOnlyList<string> Remove(
        TranslationProject project,
        ProjectCleanupPlan confirmedPlan) =>
        ProcessRemovalPlan(project, confirmedPlan, deleteDirectories: true);

    private IReadOnlyList<string> ProcessRemovalPlan(
        TranslationProject project,
        ProjectCleanupPlan confirmedPlan,
        bool deleteDirectories)
    {
        var failures = new List<string>();
        if (!IsLocalFilesystemPath(project.ModRoot))
        {
            failures.Add("Project cleanup requires a local project root.");
            return failures;
        }
        if (string.IsNullOrWhiteSpace(confirmedPlan.ProjectIdentity)
            || !confirmedPlan.ProjectIdentity.Equals(CreateProjectIdentity(project), StringComparison.Ordinal))
        {
            failures.Add("The project record changed after the cleanup plan was confirmed.");
            return failures;
        }

        foreach (var path in confirmedPlan.SafePaths)
        {
            if (!IsLocalFilesystemPath(path))
            {
                failures.Add("Project cleanup paths must be local.");
                continue;
            }
            if (!confirmedPlan.SafePathIdentities.TryGetValue(path, out var expectedIdentity))
            {
                failures.Add($"Confirmed directory identity is missing: {path}");
                continue;
            }

            var verified = GetAppOwnedReviewDirectory(path, confirmedPlan.ReviewRoots, project.ModRoot);
            if (verified is null)
            {
                failures.Add($"Safety boundary check failed: {path}");
                continue;
            }

            if (!TryCaptureDirectoryIdentity(verified, out var currentIdentity, out var identityFailure)
                || !currentIdentity.Equals(expectedIdentity, StringComparison.Ordinal))
            {
                failures.Add($"Confirmed directory identity changed: {path} ({identityFailure})");
                continue;
            }

            if (ValidateOwnershipMarker(project, verified, out var markerFailure) != OwnershipMarkerStatus.Valid)
            {
                failures.Add($"Ownership marker check failed: {path} ({markerFailure})");
                continue;
            }

            var rechecked = GetAppOwnedReviewDirectory(verified, confirmedPlan.ReviewRoots, project.ModRoot);
            if (rechecked is null || !rechecked.Equals(verified, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Safety boundary recheck failed: {path}");
                continue;
            }

            if (!TryCaptureDirectoryIdentity(rechecked, out var recheckedIdentity, out identityFailure)
                || !recheckedIdentity.Equals(expectedIdentity, StringComparison.Ordinal))
            {
                failures.Add($"Directory identity changed during deletion checks: {path} ({identityFailure})");
                continue;
            }

            if (!deleteDirectories) continue;

            var cleanup = ExactDirectoryCleanup.QuarantineAndDelete(
                rechecked,
                expectedIdentity,
                BeforeDirectoryQuarantineTestHook);
            if (!cleanup.Removed)
            {
                failures.Add(
                    $"{verified} : {cleanup.Failure} Preserved path: {cleanup.PreservedPath ?? verified}");
            }
        }

        return failures;
    }

    public static void WriteOwnershipMarker(TranslationProject project, string reviewRoot) =>
        WriteOwnershipMarker(project, reviewRoot, GetDriveType);

    internal static void WriteOwnershipMarker(
        TranslationProject project,
        string reviewRoot,
        Func<string, DriveType> driveTypeResolver)
    {
        ArgumentNullException.ThrowIfNull(driveTypeResolver);
        if (string.IsNullOrWhiteSpace(project.Id)
            || string.IsNullOrWhiteSpace(project.ModRoot)
            || !Path.IsPathFullyQualified(project.ModRoot))
        {
            throw new InvalidDataException("A project ID and an absolute mod root are required for an ownership marker.");
        }
        if (!IsLocalFilesystemPath(project.ModRoot, driveTypeResolver)
            || !IsLocalFilesystemPath(reviewRoot, driveTypeResolver))
            throw new InvalidDataException("Project ownership markers require local paths.");

        using var creationBoundary =
            PathSafety.AcquireTrustedDirectoryCreationBoundary(reviewRoot);
        PathSafety.EnsureNoReparsePointsToVolumeRoot(reviewRoot);
        PathSafety.EnsureNoReparsePointsToVolumeRoot(project.ModRoot);
        var reviewSnapshot = PathSafety.GetExistingDirectorySnapshot(reviewRoot);
        var modSnapshot = PathSafety.GetExistingDirectorySnapshot(project.ModRoot);
        var marker = JsonSerializer.Serialize(new
        {
            version = OwnershipMarkerVersion,
            projectId = project.Id,
            modRoot = modSnapshot.CanonicalPath,
            modRootIdentity = modSnapshot.Identity,
            reviewRoot = reviewSnapshot.CanonicalPath,
            reviewRootIdentity = reviewSnapshot.Identity,
            createdAt = DateTimeOffset.Now.ToString("O")
        });
        AtomicFile.WriteUtf8Validated(
            Path.Combine(reviewRoot, OwnershipMarkerFileName),
            marker,
            temporaryPath => ValidatedArtifactFile.ValidateJson(temporaryPath, marker));
    }

    private OwnershipMarkerStatus ValidateOwnershipMarker(
        TranslationProject project,
        string reviewRoot,
        out string failure)
    {
        failure = "invalid ownership marker";
        try
        {
            if (string.IsNullOrWhiteSpace(project.Id)
                || string.IsNullOrWhiteSpace(project.ModRoot)
                || !Path.IsPathFullyQualified(project.ModRoot)
                || !IsLocalFilesystemPath(project.ModRoot)
                || !IsLocalFilesystemPath(reviewRoot))
            {
                failure = "invalid project record";
                return OwnershipMarkerStatus.Invalid;
            }

            var markerPath = Path.Combine(reviewRoot, OwnershipMarkerFileName);
            PathSafety.EnsureNoReparsePoints(markerPath, reviewRoot);
            if (!File.Exists(markerPath))
            {
                failure = "ownership marker is missing";
                return OwnershipMarkerStatus.Invalid;
            }

            if (new FileInfo(markerPath).Length > 16 * 1024)
            {
                failure = "ownership marker is too large";
                return OwnershipMarkerStatus.Invalid;
            }

            using var marker = JsonDocument.Parse(BoundedFileReader.ReadAllBytes(
                markerPath,
                16 * 1024,
                "project ownership marker"));
            var root = marker.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                failure = "ownership marker root is not an object";
                return OwnershipMarkerStatus.Invalid;
            }

            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion)
                || schemaVersion is not (1 or OwnershipMarkerVersion)
                || !root.TryGetProperty("projectId", out var projectId)
                || projectId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(projectId.GetString())
                || !root.TryGetProperty("modRoot", out var modRoot)
                || modRoot.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(modRoot.GetString())
                || !Path.IsPathFullyQualified(modRoot.GetString()!)
                || !root.TryGetProperty("createdAt", out var createdAt)
                || createdAt.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParseExact(
                    createdAt.GetString(),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
            {
                failure = "ownership marker schema is invalid";
                return OwnershipMarkerStatus.Invalid;
            }

            if (!string.Equals(projectId.GetString(), project.Id, StringComparison.Ordinal))
            {
                failure = "ownership marker belongs to another project";
                return OwnershipMarkerStatus.DifferentProject;
            }

            PathSafety.EnsureNoReparsePointsToVolumeRoot(project.ModRoot);
            var currentModSnapshot = PathSafety.GetExistingDirectorySnapshot(project.ModRoot);
            if (!PathSafety.Normalize(modRoot.GetString()!).Equals(
                    currentModSnapshot.CanonicalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                failure = "ownership marker mod root does not match the project record";
                return OwnershipMarkerStatus.Invalid;
            }

            if (schemaVersion == 1)
            {
                if (root.TryGetProperty("workshopId", out var workshopId)
                    && (workshopId.ValueKind != JsonValueKind.String
                        || (!string.IsNullOrWhiteSpace(workshopId.GetString())
                            && !string.IsNullOrWhiteSpace(project.WorkshopId)
                            && !string.Equals(
                                workshopId.GetString(),
                                project.WorkshopId,
                                StringComparison.OrdinalIgnoreCase))))
                {
                    failure = "ownership marker workshop ID does not match the project record";
                    return OwnershipMarkerStatus.Invalid;
                }

                failure = string.Empty;
                return OwnershipMarkerStatus.Valid;
            }

            if (!root.TryGetProperty("modRootIdentity", out var modRootIdentity)
                || modRootIdentity.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(modRootIdentity.GetString())
                || !root.TryGetProperty("reviewRoot", out var markerReviewRoot)
                || markerReviewRoot.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(markerReviewRoot.GetString())
                || !Path.IsPathFullyQualified(markerReviewRoot.GetString()!)
                || !root.TryGetProperty("reviewRootIdentity", out var reviewRootIdentity)
                || reviewRootIdentity.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(reviewRootIdentity.GetString()))
            {
                failure = "ownership marker schema is invalid";
                return OwnershipMarkerStatus.Invalid;
            }

            if (!string.Equals(modRootIdentity.GetString(), currentModSnapshot.Identity, StringComparison.Ordinal))
            {
                failure = "ownership marker mod root does not match the project record";
                return OwnershipMarkerStatus.Invalid;
            }

            PathSafety.EnsureNoReparsePointsToVolumeRoot(reviewRoot);
            var currentReviewSnapshot = PathSafety.GetExistingDirectorySnapshot(reviewRoot);
            if (!PathSafety.Normalize(markerReviewRoot.GetString()!).Equals(
                    currentReviewSnapshot.CanonicalPath,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(reviewRootIdentity.GetString(), currentReviewSnapshot.Identity, StringComparison.Ordinal))
            {
                failure = "ownership marker review root does not match its directory";
                return OwnershipMarkerStatus.Invalid;
            }

            failure = string.Empty;
            return OwnershipMarkerStatus.Valid;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or InvalidDataException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            failure = exception.GetType().Name;
            return OwnershipMarkerStatus.Invalid;
        }
    }

    private static string CreateProjectIdentity(TranslationProject project)
    {
        var recordedRoots = project.Runs.Select(run => NormalizeIdentityPath(run.ReviewRoot))
            .Prepend(NormalizeIdentityPath(project.LatestReviewRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return StableIdentity.Sha256(JsonSerializer.Serialize(new
        {
            projectId = project.Id,
            modRoot = NormalizeIdentityPath(project.ModRoot),
            recordedRoots
        }));
    }

    private static string NormalizeIdentityPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return PathSafety.Normalize(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return $"<invalid:{exception.GetType().Name}>";
        }
    }

    private bool TryCaptureDirectoryIdentity(string path, out string identity, out string failure)
    {
        if (!IsLocalFilesystemPath(path))
        {
            identity = string.Empty;
            failure = "NetworkPath";
            return false;
        }
        try
        {
            identity = PathSafety.GetExistingDirectoryIdentity(path);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            identity = string.Empty;
            failure = exception.GetType().Name;
            return false;
        }
    }

    private string? GetAppOwnedReviewDirectory(string path, IEnumerable<string> reviewRoots, string modRoot)
    {
        var roots = reviewRoots.ToArray();
        if (!IsLocalFilesystemPath(path)
            || !IsLocalFilesystemPath(modRoot)
            || roots.Any(root => !IsLocalFilesystemPath(root))
            || !DirectoryExistsLocal(path))
        {
            return null;
        }

        string full;
        PathSafety.ExistingDirectorySnapshot candidateSnapshot;
        try
        {
            full = PathSafety.Normalize(path);
            PathSafety.EnsureNoReparsePointsToVolumeRoot(full);
            candidateSnapshot = PathSafety.GetExistingDirectorySnapshot(full);
            if (!candidateSnapshot.CanonicalPath.Equals(full, StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Invalid owned-review candidate rejected ({exception.GetType().Name}).");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(modRoot))
        {
            string modFull;
            PathSafety.ExistingDirectorySnapshot modSnapshot;
            try
            {
                modFull = PathSafety.Normalize(modRoot);
                PathSafety.EnsureNoReparsePointsToVolumeRoot(modFull);
                modSnapshot = PathSafety.GetExistingDirectorySnapshot(modFull);
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or IOException
                                              or InvalidDataException
                                              or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Invalid project mod root rejected ({exception.GetType().Name}).");
                return null;
            }
            if (candidateSnapshot.Identity.Equals(modSnapshot.Identity, StringComparison.Ordinal)
                || PathSafety.IsStrictlyInside(candidateSnapshot.CanonicalPath, modSnapshot.CanonicalPath)
                || PathSafety.IsStrictlyInside(modSnapshot.CanonicalPath, candidateSnapshot.CanonicalPath))
            {
                return null;
            }
        }

        foreach (var root in roots)
        {
            if (!PathSafety.IsStrictlyInside(full, root))
            {
                continue;
            }

            try
            {
                PathSafety.EnsureNoReparsePointsToVolumeRoot(root);
                var rootSnapshot = PathSafety.GetExistingDirectorySnapshot(root);
                if (!PathSafety.IsStrictlyInside(candidateSnapshot.CanonicalPath, rootSnapshot.CanonicalPath))
                {
                    continue;
                }
                return full;
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or ArgumentException
                                              or NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine($"Invalid review root rejected ({exception.GetType().Name}).");
            }
        }

        return null;
    }

    private bool DirectoryExistsLocal(string path)
    {
        if (!IsLocalFilesystemPath(path)) return false;
        BeforeDirectoryProbeTestHook?.Invoke(path);
        return Directory.Exists(path);
    }

    private bool IsLocalFilesystemPath(string? path) =>
        IsLocalFilesystemPath(path, driveTypeResolver);

    private static bool IsLocalFilesystemPath(
        string? path,
        Func<string, DriveType> driveTypeResolver)
    {
        if (string.IsNullOrWhiteSpace(path) || PathSafety.IsNetworkPath(path)) return false;
        try
        {
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrWhiteSpace(volumeRoot)
                   && driveTypeResolver(volumeRoot) != DriveType.Network;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Local cleanup volume classification failed ({exception.GetType().Name}).");
            return false;
        }
    }

    private static DriveType GetDriveType(string volumeRoot) =>
        new DriveInfo(volumeRoot).DriveType;

    private enum OwnershipMarkerStatus
    {
        Valid,
        DifferentProject,
        Invalid
    }
}
