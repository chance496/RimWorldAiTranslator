using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Core.Rmk;

public sealed record RmkModListEntry(string WorkshopId, string ModName, string RelativeLocation, string PackageId);

public sealed record RmkTarget(
    string Root,
    string YamlPath,
    string LanguageRoot,
    string WorkbookPath,
    string WorkshopId,
    string ModName,
    string Version,
    string SourceRoot,
    string SourceKind);

public sealed record RmkBuildResult(string Output, string LoadFoldersPath, string ModListPath);

public sealed record RmkBuilderExecutionPlan(
    string WorkspaceRoot,
    string WorkspaceIdentity,
    string BuilderPath,
    string BuilderSha256,
    long BuilderLength);

internal sealed class RmkWorkspaceDiscoveryLimits
{
    public int MaximumContainers { get; init; } = 256;
    public int MaximumCandidateDirectories { get; init; } = 100_000;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumContainers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCandidateDirectories);
    }
}

public sealed partial class RmkWorkspaceService
{
    public const string WorkshopId = "3079466972";
    private const long MaximumBuilderBytes = 256L * 1024 * 1024;
    private const long MaximumLoadFoldersBytes = 64L * 1024 * 1024;
    private const long MaximumMetadataBytes = 1L * 1024 * 1024;
    private const int MaximumModListEntries = 100_000;
    private const int MaximumTargetTraversalDepth = 64;
    private const int MaximumTargetTraversalDirectories = 25_000;
    private const int MaximumTargetTraversalFiles = 100_000;
    private const int MaximumBuilderStageEntries = 32_000;
    private const int MaximumBuilderStagingRecoveryEntries = 32_768;
    private const long MaximumBuilderStageInputBytes = 1_536L * 1024 * 1024;
    private const long MaximumBuilderStageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
    private readonly RmkWorkspaceDiscoveryLimits discoveryLimits;
    private readonly Func<string, IEnumerable<string>> enumerateWorkspaceCandidates;
    private readonly FileTransactionRecoveryAuthority? recoveryAuthority;
    private readonly string? builderStagingRoot;

    internal static Action? BeforeBuilderJobAssignmentTestHook { get; set; }
    internal static Action<string, string>? AfterBuilderOutputsValidatedBeforeEvidenceTestHook { get; set; }
    internal static Action<string, string>? PopulateBuilderStageTestHook { get; set; }
    internal static Func<IReadOnlyDictionary<string, string>>? BuilderEnvironmentTestHook { get; set; }
    internal static Action<string, string>? BeforeBuilderOutputsPublishedTestHook { get; set; }
    internal Action<string>? BeforeCreateTargetDirectoryCommitTestHook { get; set; }
    internal Action<string>? BeforeCreateTargetReadbackTestHook { get; set; }

    public RmkWorkspaceService()
        : this(new RmkWorkspaceDiscoveryLimits())
    {
    }

    public RmkWorkspaceService(FileTransactionRecoveryAuthority recoveryAuthority)
        : this(
            new RmkWorkspaceDiscoveryLimits(),
            recoveryAuthority: recoveryAuthority
                ?? throw new ArgumentNullException(nameof(recoveryAuthority)),
            builderStagingRoot: GetDefaultBuilderStagingRoot(recoveryAuthority))
    {
    }

    public RmkWorkspaceService(
        FileTransactionRecoveryAuthority recoveryAuthority,
        string builderStagingRoot)
        : this(
            new RmkWorkspaceDiscoveryLimits(),
            recoveryAuthority: recoveryAuthority
                ?? throw new ArgumentNullException(nameof(recoveryAuthority)),
            builderStagingRoot: builderStagingRoot)
    {
    }

    internal RmkWorkspaceService(
        RmkWorkspaceDiscoveryLimits discoveryLimits,
        Func<string, IEnumerable<string>>? enumerateWorkspaceCandidates = null,
        FileTransactionRecoveryAuthority? recoveryAuthority = null,
        string? builderStagingRoot = null)
    {
        ArgumentNullException.ThrowIfNull(discoveryLimits);
        discoveryLimits.Validate();
        this.discoveryLimits = discoveryLimits;
        this.enumerateWorkspaceCandidates = enumerateWorkspaceCandidates
            ?? (container => Directory.EnumerateDirectories(container, "*", SearchOption.TopDirectoryOnly));
        this.recoveryAuthority = recoveryAuthority;
        this.builderStagingRoot = string.IsNullOrWhiteSpace(builderStagingRoot)
            ? null
            : PathSafety.Normalize(builderStagingRoot);
    }

    private static string GetDefaultBuilderStagingRoot(
        FileTransactionRecoveryAuthority? recoveryAuthority)
    {
        ArgumentNullException.ThrowIfNull(recoveryAuthority);
        var parent = Path.GetDirectoryName(recoveryAuthority.Root)
            ?? throw new InvalidDataException(
                "The recovery authority has no parent for RMK Builder staging.");
        return Path.Combine(parent, "rmk-builder-staging");
    }

    public bool IsWorkspaceRoot(string root, bool requireGit = true)
    {
        if (PathSafety.IsNetworkPath(root)) return false;
        if (!Directory.Exists(root)) return false;
        var full = PathSafety.Normalize(root);
        return Directory.Exists(Path.Combine(full, "Data"))
            && File.Exists(Path.Combine(full, "ModList.tsv"))
            && (!requireGit || Directory.Exists(Path.Combine(full, ".git")) || File.Exists(Path.Combine(full, ".git")));
    }

    public string RequireWritableWorkspace(string root)
    {
        if (PathSafety.IsNetworkPath(root))
            throw new InvalidDataException("RMK work clones must use a local path.");
        var full = PathSafety.Normalize(root);
        if (PathSafety.IsWorkshopContentPath(full))
            throw new InvalidOperationException("Steam Workshop subscription/content roots are read-only and cannot be used as RMK work clones.");
        PathSafety.EnsureNoReparsePointsToVolumeRoot(full);
        var canonical = PathSafety.GetCanonicalExistingDirectory(full);
        if (PathSafety.IsWorkshopContentPath(canonical))
            throw new InvalidOperationException("Steam Workshop subscription/content roots are read-only and cannot be used through an alias.");
        if (!canonical.Equals(full, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RMK work clones must be opened through their canonical physical path.");
        if (!IsWorkspaceRoot(full, true))
            throw new InvalidDataException("RMK writes require a Git work clone containing Data and ModList.tsv.");
        foreach (var path in new[]
                 {
                     Path.Combine(full, "Data"),
                     Path.Combine(full, "ModList.tsv"),
                     Path.Combine(full, ".git")
                 })
        {
            PathSafety.EnsureNoReparsePoints(path, full);
        }
        var branch = ReadGitBranch(full);
        if (!branch.Equals("bus", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"RMK writes require the bus branch. Current branch: {branch}");
        return full;
    }

    public bool IsWritableWorkspace(string root)
    {
        try
        {
            RequireWritableWorkspace(root);
            return true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"RMK writable-workspace check failed closed ({exception.GetType().Name}).");
            return false;
        }
    }

    public IReadOnlyList<RmkModListEntry> ReadModList(string root)
    {
        if (!IsWorkspaceRoot(root, false)) return [];
        var path = Path.Combine(PathSafety.Normalize(root), "ModList.tsv");
        var entries = new List<RmkModListEntry>();
        var text = ReadText(path, MaximumMetadataBytes, "RMK ModList");
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (entries.Count >= MaximumModListEntries)
                throw new InvalidDataException($"RMK ModList contains more than {MaximumModListEntries:N0} entries.");
            var columns = line.Split('\t');
            if (columns.Length < 4) continue;
            entries.Add(new RmkModListEntry(columns[0].Trim(), columns[1].Trim(), columns[2].Trim(), columns[3].Trim()));
        }
        return entries;
    }

    public string FindSubscriptionRoot(IEnumerable<string> steamLibraryRoots)
    {
        foreach (var steamRoot in steamLibraryRoots)
        {
            var candidate = Path.Combine(steamRoot, "steamapps", "workshop", "content", "294100", WorkshopId);
            if (IsWorkspaceRoot(candidate, false)) return PathSafety.Normalize(candidate);
        }
        return string.Empty;
    }

    public string FindWritableWorkspace(
        IEnumerable<string> localModContainers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localModContainers);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerator<string> containers;
        try
        {
            containers = localModContainers.GetEnumerator();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"RMK container enumeration failed closed ({exception.GetType().Name}).");
            return string.Empty;
        }

        var inspectedContainers = 0;
        var inspectedCandidates = 0;
        using (containers)
        {
            while (TryMoveNext(containers, "container", cancellationToken, out var container))
            {
                if (++inspectedContainers > discoveryLimits.MaximumContainers)
                {
                    throw new InvalidDataException(
                        $"RMK workspace discovery inspected more than {discoveryLimits.MaximumContainers:N0} mod containers.");
                }

                if (string.IsNullOrWhiteSpace(container)
                    || PathSafety.IsNetworkPath(container)
                    || !Directory.Exists(container))
                {
                    continue;
                }

                IEnumerator<string> candidates;
                try
                {
                    candidates = enumerateWorkspaceCandidates(container).GetEnumerator();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"RMK candidate enumeration skipped ({exception.GetType().Name}).");
                    continue;
                }

                using (candidates)
                {
                    while (TryMoveNext(candidates, "candidate", cancellationToken, out var child))
                    {
                        if (++inspectedCandidates > discoveryLimits.MaximumCandidateDirectories)
                        {
                            throw new InvalidDataException(
                                $"RMK workspace discovery inspected more than {discoveryLimits.MaximumCandidateDirectories:N0} candidate directories.");
                        }

                        if (IsWritableWorkspace(child))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return PathSafety.Normalize(child);
                        }
                    }
                }
            }
        }

        return string.Empty;

        static bool TryMoveNext(
            IEnumerator<string> enumerator,
            string scope,
            CancellationToken cancellationToken,
            out string current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!enumerator.MoveNext())
                {
                    current = string.Empty;
                    return false;
                }

                current = enumerator.Current;
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"RMK {scope} iterator stopped safely ({exception.GetType().Name}).");
                current = string.Empty;
                return false;
            }
        }
    }

    public IReadOnlyList<RmkTarget> FindTargets(string workspaceRoot, TranslationProject project, string sourceKind = "작업 클론")
    {
        if (!IsWorkspaceRoot(workspaceRoot, false)) return [];
        var root = PathSafety.Normalize(workspaceRoot);
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(root);
        if (!canonicalRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RMK target discovery requires a canonical physical workspace path.");
        var traversal = new RmkTargetTraversalBudget();
        var rows = ReadModList(root);
        var hasIndex = rows.Count > 0;
        var matches = !string.IsNullOrWhiteSpace(project.WorkshopId)
            ? rows.Where(row => row.WorkshopId.Equals(project.WorkshopId, StringComparison.Ordinal)).ToArray()
            : [];
        if (matches.Length == 0 && !string.IsNullOrWhiteSpace(project.PackageId))
            matches = rows.Where(row => row.PackageId.Equals(project.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hasIndex && matches.Length == 0) return [];

        var yamlPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in matches)
        {
            var relative = row.RelativeLocation.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string location;
            try { location = PathSafety.ResolveInside(root, relative); }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException)
            {
                System.Diagnostics.Debug.WriteLine($"Unsafe RMK index location skipped ({exception.GetType().Name}).");
                continue;
            }
            if (!Directory.Exists(location)) continue;
            if (!scannedLocations.Add(location)) continue;
            var expected = Path.Combine(location, $"{row.ModName} - {project.WorkshopId}");
            var candidates = EnumerateImmediateRmkDirectories(root, location, traversal)
                .Where(path => string.IsNullOrWhiteSpace(project.WorkshopId)
                    || Path.GetFileName(path).EndsWith(" - " + project.WorkshopId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => !path.Equals(expected, StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                foreach (var yaml in EnumerateRmkMetadataFiles(root, candidate, "LoadFolders.Build.yaml", traversal))
                    yamlPaths.Add(yaml);
            }
        }

        if (yamlPaths.Count == 0 && !hasIndex)
        {
            var dataRoot = Path.Combine(root, "Data");
            foreach (var yaml in EnumerateRmkMetadataFiles(root, dataRoot, "LoadFolders.Build.yaml", traversal))
            {
                if (!string.IsNullOrWhiteSpace(project.WorkshopId)
                    && yaml.Contains(" - " + project.WorkshopId + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    yamlPaths.Add(yaml);
                else if (string.IsNullOrWhiteSpace(project.WorkshopId) && !string.IsNullOrWhiteSpace(project.PackageId))
                {
                    try { if (ReadText(yaml, MaximumMetadataBytes, "RMK metadata").Contains(project.PackageId, StringComparison.OrdinalIgnoreCase)) yamlPaths.Add(yaml); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Unreadable RMK metadata skipped ({exception.GetType().Name}).");
                    }
                }
            }
        }

        var result = new List<RmkTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var yaml in yamlPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var target = ReadTarget(yaml, root, sourceKind, traversal);
            if (target is null) continue;
            var idMatch = !string.IsNullOrWhiteSpace(project.WorkshopId) && target.WorkshopId.Equals(project.WorkshopId, StringComparison.Ordinal);
            var packageMatch = !string.IsNullOrWhiteSpace(project.PackageId)
                               && ReadText(yaml, MaximumMetadataBytes, "RMK metadata").Contains(project.PackageId, StringComparison.OrdinalIgnoreCase);
            if ((idMatch || packageMatch) && seen.Add(target.Root)) result.Add(target);
        }
        return result;
    }

    public RmkTarget? SelectTarget(IEnumerable<RmkTarget> targets, string version)
    {
        var rows = targets.ToArray();
        return rows.FirstOrDefault(row => row.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(row => string.IsNullOrWhiteSpace(row.Version))
            ?? rows.OrderByDescending(row => ParseVersion(row.Version)).FirstOrDefault();
    }

    public RmkTarget CreateTarget(
        string workspaceRoot,
        TranslationProject project,
        string version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = RequireWritableWorkspace(workspaceRoot);
        using var recoverySession = FileTransaction.AcquireRecoveryLease(
            recoveryAuthority,
            root,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(project.WorkshopId) || string.IsNullOrWhiteSpace(project.PackageId))
            throw new InvalidDataException("A Workshop ID and Package ID are required to create a new RMK target.");
        var dataRoot = Path.Combine(root, "Data");
        var safeName = SafeFolderName(project.Name);
        var targetRoot = PathSafety.ResolveInside(dataRoot, Path.Combine($"{safeName} - {project.WorkshopId}", version));
        PathSafety.EnsureNoReparsePoints(targetRoot, root);
        var languageRoot = Path.Combine(targetRoot, "Languages", "Korean (한국어)");
        var yamlPath = Path.Combine(targetRoot, "LoadFolders.Build.yaml");
        var targetDirectories = EnumerateTargetDirectories(dataRoot, languageRoot);
        var absentDirectories = targetDirectories
            .Where(path => !Directory.Exists(path) && !File.Exists(path))
            .ToArray();
        var absentDirectorySet = absentDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var createdDirectoryIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var directory in targetDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (absentDirectorySet.Contains(directory))
                {
                    createdDirectoryIdentities[directory] = CreateOwnedTargetDirectory(
                        directory,
                        cancellationToken);
                    continue;
                }

                if (!Directory.Exists(directory) || File.Exists(directory))
                {
                    throw new ConcurrentLeafChangeException(
                        "A pre-existing RMK target directory changed while target creation was starting.",
                        directory);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.EnsureNoReparsePoints(targetRoot, root);

            if (File.Exists(yamlPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                BeforeCreateTargetReadbackTestHook?.Invoke(yamlPath);
                cancellationToken.ThrowIfCancellationRequested();
                return ReadTarget(yamlPath, root, "작업 클론")
                    ?? throw new InvalidDataException("Existing RMK target could not be read.");
            }

            var yaml = $"""
                BuildRule:
                  Binding:
                    PackageID: ["{Yaml(project.PackageId)}"]
                    Mode: "None"
                    Dependency: "Independent"
                  Order:
                    After:
                    Before:
                  Version:
                    Default: "{Yaml(version)}"
                    LeftBoundary:
                    RightBoundary:
                    Designate:
                    Ban:
                Metadata:
                  WorkshopID: "{Yaml(project.WorkshopId)}"
                  ModName: "{Yaml(project.Name)}"
                """;

            using var writeBoundary = PathSafety.AcquireTrustedWriteBoundary(
                root,
                [yamlPath],
                cancellationToken);
            VerifyOwnedTargetDirectories(createdDirectoryIdentities);
            if (writeBoundary.TargetExisted(yamlPath))
            {
                throw new ConcurrentLeafChangeException(
                    "An RMK target metadata path appeared while target creation was being prepared.",
                    yamlPath);
            }

            RmkTarget? created = null;
            FileTransaction.Execute(
                [yamlPath],
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AtomicFile.WriteBytesValidated(
                        yamlPath,
                        Utf8Strict.GetBytes(yaml),
                        temporaryPath =>
                        {
                            if (ReadTarget(temporaryPath, root, "작업 클론") is null)
                                throw new InvalidDataException("Prepared RMK target metadata could not be read.");
                        },
                        writeBoundary,
                        keepBackup: false);
                    cancellationToken.ThrowIfCancellationRequested();
                    BeforeCreateTargetReadbackTestHook?.Invoke(yamlPath);
                    cancellationToken.ThrowIfCancellationRequested();
                    var committedYaml = writeBoundary.ReadCurrentWriteBytes(
                        yamlPath,
                        MaximumMetadataBytes,
                        cancellationToken);
                    created = ReadTarget(
                                  yamlPath,
                                  root,
                                  "작업 클론",
                                  trustedMetadataBytes: committedYaml)
                        ?? throw new InvalidDataException("Created RMK target could not be read.");
                },
                "RMK target creation",
                writeBoundary.ReleaseWriteLeafLocksForRollback,
                writeBoundary,
                recoverySession,
                cancellationToken);
            return created ?? throw new InvalidDataException("Created RMK target could not be read.");
        }
        catch (Exception operationError)
        {
            var cleanupFailures = CleanupCreatedTargetDirectories(
                absentDirectories,
                createdDirectoryIdentities);
            if (cleanupFailures.Count > 0)
            {
                throw new IOException(
                    $"RMK target creation failed and run-owned directory cleanup was incomplete ({operationError.GetType().Name}). "
                    + $"Directories with concurrent content or changed identity were preserved: {string.Join(" | ", cleanupFailures)}",
                    operationError);
            }
            throw;
        }
    }

    private string CreateOwnedTargetDirectory(
        string directory,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(directory)
            ?? throw new InvalidDataException("RMK target directory has no parent.");
        if (!Directory.Exists(parent) || File.Exists(parent))
            throw new DirectoryNotFoundException("The RMK target directory parent changed before creation.");

        var stagingDirectory = CreateOwnedDirectoryStaging(parent, Path.GetFileName(directory));
        string stagingIdentity;
        try
        {
            stagingIdentity = PathSafety.GetExistingDirectoryIdentity(stagingDirectory);
        }
        catch (Exception identityError)
        {
            throw new IOException(
                $"RMK target staging directory identity could not be captured; the directory was preserved: {stagingDirectory}",
                identityError);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeCreateTargetDirectoryCommitTestHook?.Invoke(directory);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(stagingDirectory, directory);
            }
            catch (IOException exception) when (Directory.Exists(directory) || File.Exists(directory))
            {
                throw new ConcurrentLeafChangeException(
                    "An RMK target directory appeared during atomic creation and was preserved.",
                    exception,
                    directory);
            }

            var committedIdentity = PathSafety.GetExistingDirectoryIdentity(directory);
            if (!committedIdentity.Equals(stagingIdentity, StringComparison.Ordinal))
            {
                throw new ConcurrentLeafChangeException(
                    "An RMK target directory changed before its ownership identity could be pinned.",
                    directory);
            }
            return committedIdentity;
        }
        catch (Exception operationError)
        {
            var cleanupFailure = CleanupOwnedEmptyDirectory(stagingDirectory, stagingIdentity);
            if (cleanupFailure is not null)
            {
                throw new IOException(
                    $"RMK target directory creation failed and staging cleanup was incomplete: {cleanupFailure}",
                    operationError);
            }
            throw;
        }
    }

    private static string CreateOwnedDirectoryStaging(string parent, string targetName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Atomic RMK target directory creation requires Windows.");

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var stagingDirectory = Path.Combine(
                parent,
                $".{targetName}.{Guid.NewGuid():N}.rmk-create.tmp");
            if (CreateDirectoryNative(AddExtendedPathPrefix(stagingDirectory), IntPtr.Zero))
                return stagingDirectory;

            var error = Marshal.GetLastWin32Error();
            if (error is 80 or 183) continue;
            throw new IOException(
                "The RMK target staging directory could not be created.",
                new Win32Exception(error));
        }

        throw new IOException("A unique RMK target staging directory could not be allocated.");
    }

    private static string AddExtendedPathPrefix(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + fullPath[2..];
        return @"\\?\" + fullPath;
    }

    private static void VerifyOwnedTargetDirectories(
        IReadOnlyDictionary<string, string> createdDirectoryIdentities)
    {
        foreach (var (directory, expectedIdentity) in createdDirectoryIdentities)
        {
            if (!Directory.Exists(directory) || File.Exists(directory))
            {
                throw new ConcurrentLeafChangeException(
                    "A run-owned RMK target directory disappeared before the write boundary was locked.",
                    directory);
            }
            var currentIdentity = PathSafety.GetExistingDirectoryIdentity(directory);
            if (!currentIdentity.Equals(expectedIdentity, StringComparison.Ordinal))
            {
                throw new ConcurrentLeafChangeException(
                    "A run-owned RMK target directory changed before the write boundary was locked.",
                    directory);
            }
        }
    }

    private static string[] EnumerateTargetDirectories(string dataRoot, string languageRoot)
    {
        var root = PathSafety.Normalize(dataRoot);
        var current = PathSafety.Normalize(languageRoot);
        var directories = new List<string>();
        while (!current.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (!PathSafety.IsStrictlyInside(current, root))
                throw new InvalidDataException("RMK target directory escaped the workspace Data root.");
            directories.Add(current);
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException("RMK target directory has no parent.");
        }
        directories.Reverse();
        return directories.ToArray();
    }

    private static string? CleanupOwnedEmptyDirectory(string directory, string expectedIdentity)
    {
        if (!Directory.Exists(directory))
            return File.Exists(directory) ? $"{directory} (replaced by a file)" : null;

        try
        {
            var currentIdentity = PathSafety.GetExistingDirectoryIdentity(directory);
            if (!currentIdentity.Equals(expectedIdentity, StringComparison.Ordinal))
                return $"{directory} (directory identity changed)";
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                return $"{directory} (not empty)";
            Directory.Delete(directory, recursive: false);
            return null;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
        {
            return $"{directory} ({exception.GetType().Name})";
        }
    }

    private static IReadOnlyList<string> CleanupCreatedTargetDirectories(
        IReadOnlyList<string> absentDirectories,
        IReadOnlyDictionary<string, string> createdDirectoryIdentities)
    {
        var failures = new List<string>();
        for (var index = absentDirectories.Count - 1; index >= 0; index--)
        {
            var directory = absentDirectories[index];
            if (!Directory.Exists(directory))
            {
                if (File.Exists(directory)) failures.Add($"{directory} (replaced by a file)");
                continue;
            }

            if (!createdDirectoryIdentities.TryGetValue(directory, out var expectedIdentity))
            {
                failures.Add($"{directory} (ownership identity unavailable)");
                continue;
            }

            try
            {
                var currentIdentity = PathSafety.GetExistingDirectoryIdentity(directory);
                if (!currentIdentity.Equals(expectedIdentity, StringComparison.Ordinal))
                {
                    failures.Add($"{directory} (directory identity changed)");
                    continue;
                }
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    failures.Add($"{directory} (not empty)");
                    continue;
                }
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                failures.Add($"{directory} ({exception.GetType().Name})");
            }
        }
        return failures;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string path, IntPtr securityAttributes);

    public RmkBuilderExecutionPlan CreateBuildPlan(string workspaceRoot)
    {
        var root = RequireWritableWorkspace(workspaceRoot);
        var builder = PathSafety.ResolveInside(root, "LoadFoldersBuilder.exe");
        if (!File.Exists(builder)) throw new FileNotFoundException("LoadFoldersBuilder.exe를 찾을 수 없습니다.", builder);
        PathSafety.EnsureNoReparsePoints(builder, root);
        var canonicalBuilder = PathSafety.GetCanonicalExistingFile(builder);
        if (!canonicalBuilder.Equals(builder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RMK Builder must be opened through its canonical physical path.");
        using var stream = OpenBuilderReadLock(builder);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new RmkBuilderExecutionPlan(
            root,
            PathSafety.GetExistingDirectoryIdentity(root),
            builder,
            hash,
            stream.Length);
    }

    public async Task<RmkBuildResult> BuildAsync(
        RmkBuilderExecutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var root = RequireWritableWorkspace(plan.WorkspaceRoot);
        if (!PathSafety.GetExistingDirectoryIdentity(root).Equals(plan.WorkspaceIdentity, StringComparison.Ordinal)
            || !root.Equals(plan.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The confirmed RMK workspace identity changed before execution.");
        }
        if (recoveryAuthority is null)
            throw new InvalidOperationException(
                "RMK Builder execution requires a durable recovery authority.");
        if (builderStagingRoot is null)
            throw new InvalidOperationException(
                "RMK Builder execution requires an application-owned staging root.");
        using var stagingRootLease = PrepareBuilderStagingRoot(
            builderStagingRoot,
            recoveryAuthority.Root,
            root,
            cancellationToken);
        using var stagingLease = AcquireBuilderStagingLease(
            stagingRootLease.Root,
            cancellationToken);
        stagingRootLease.VerifyUnchanged();
        var stagingCleanup = CleanupAbandonedBuilderStages(
            stagingRootLease,
            cancellationToken);
        using var recoverySession = FileTransaction.AcquireRecoveryLease(
            recoveryAuthority,
            root,
            cancellationToken);
        var builder = PathSafety.ResolveInside(root, "LoadFoldersBuilder.exe");
        if (!builder.Equals(plan.BuilderPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The confirmed RMK Builder path changed before execution.");
        var loadFolders = PathSafety.ResolveInside(root, "LoadFolders.xml");
        var modList = PathSafety.ResolveInside(root, "ModList.tsv");
        foreach (var path in new[] { builder, loadFolders, modList })
            PathSafety.EnsureNoReparsePoints(path, root);
        var initialOutputs = new Dictionary<string, SnapshotLeafFingerprint>(
            StringComparer.OrdinalIgnoreCase)
        {
            [loadFolders] = CaptureInitialBuilderOutput(
                loadFolders,
                MaximumLoadFoldersBytes,
                "RMK LoadFolders output",
                cancellationToken),
            [modList] = CaptureInitialBuilderOutput(
                modList,
                MaximumMetadataBytes,
                "RMK ModList output",
                cancellationToken)
        };

        async Task RunBuilderProcessAsync(BuilderStage stage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executionBoundary = stage.ExecutionBoundary
                ?? throw new InvalidOperationException(
                    "The isolated RMK Builder stage has no execution boundary.");
            executionBoundary.VerifyBeforeProcess(cancellationToken);
            var stagedPlan = stage.Plan;
            using var launchBoundary = PathSafety.AcquireTrustedReadBoundary(
                stagedPlan.WorkspaceRoot,
                [stagedPlan.BuilderPath],
                cancellationToken);
            launchBoundary.VerifyUnchanged();
            using var builderLock = OpenVerifiedBuilder(stagedPlan, stagedPlan.WorkspaceRoot);
            launchBoundary.VerifyUnchanged();
            using var process = WindowsContainedProcess.Start(
                stagedPlan.BuilderPath,
                stagedPlan.WorkspaceRoot,
                IsSensitiveEnvironmentName,
                BeforeBuilderJobAssignmentTestHook,
                BuilderEnvironmentTestHook?.Invoke());
            var stdout = ReadBoundedAsync(process.StandardOutput, 16 * 1024, CancellationToken.None);
            var stderr = ReadBoundedAsync(process.StandardError, 16 * 1024, CancellationToken.None);
            try
            {
                await process.StandardInput.WriteLineAsync("-build".AsMemory(), cancellationToken);
                process.StandardInput.Close();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    await TerminateProcessAsync(process, stdout, stderr);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("RMK Builder가 120초 안에 끝나지 않아 중지했습니다.");
                }

                process.CloseContainment();
                try
                {
                    await Task.WhenAll(stdout, stderr).WaitAsync(
                        TimeSpan.FromSeconds(5),
                        CancellationToken.None);
                }
                catch (TimeoutException exception)
                {
                    throw new InvalidDataException("RMK Builder output streams did not close after the process exited.", exception);
                }
                executionBoundary.VerifyAfterProcess(CancellationToken.None);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"RMK Builder가 종료 코드 {process.ExitCode}로 실패했습니다. 작업 클론과 Builder 로그를 확인하세요.");
            }
            finally
            {
                if (!process.HasExited) await TerminateProcessAsync(process, stdout, stderr);
            }
        }

        var stage = CreateBuilderStage(
            plan,
            root,
            stagingRootLease,
            cancellationToken);
        byte[] stagedLoadFolders;
        byte[] stagedModList;
        try
        {
            await RunBuilderProcessAsync(stage).ConfigureAwait(false);
            var stagedLoadFoldersPath = Path.Combine(stage.Root, "LoadFolders.xml");
            var stagedModListPath = Path.Combine(stage.Root, "ModList.tsv");
            using var validated = PinBuilderOutputs(
                stagedLoadFoldersPath,
                stagedModListPath,
                stage.Root,
                requireSemanticallyValidFiles: true,
                cancellationToken);
            AfterBuilderOutputsValidatedBeforeEvidenceTestHook?.Invoke(
                stagedLoadFoldersPath,
                stagedModListPath);
            cancellationToken.ThrowIfCancellationRequested();
            stagedLoadFolders = validated.LoadFoldersBytes.ToArray();
            stagedModList = validated.ModListBytes.ToArray();
        }
        catch (Exception operationError)
        {
            CleanupBuilderStageOrThrow(stage, operationError);
            throw;
        }

        CleanupBuilderStage(stage);
        cancellationToken.ThrowIfCancellationRequested();
        var currentRoot = RequireWritableWorkspace(root);
        if (!PathSafety.GetExistingDirectoryIdentity(currentRoot)
                .Equals(plan.WorkspaceIdentity, StringComparison.Ordinal)
            || !currentRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConcurrentLeafChangeException(
                "The confirmed RMK workspace identity changed before Builder outputs could be published.",
                root);
        }
        using var sourceBoundary = AcquireBuilderSourcePublicationBoundary(
            plan,
            root,
            stage.SourceSeal,
            cancellationToken);

        BeforeBuilderOutputsPublishedTestHook?.Invoke(loadFolders, modList);
        cancellationToken.ThrowIfCancellationRequested();
        sourceBoundary.VerifyUnchanged(cancellationToken);
        var transactionPaths = new[] { loadFolders, modList };
        using var writeBoundary = PathSafety.AcquireTrustedWriteBoundary(
            root,
            transactionPaths,
            cancellationToken);
        VerifyBuilderOutputCas(writeBoundary, initialOutputs, cancellationToken);
        sourceBoundary.VerifyUnchanged(cancellationToken);

        // From the first prepared write onward, publication and durable recovery
        // resolution are deliberately non-cancellable so a validated pair cannot be
        // stranded in a mixed state.
        FileTransaction.Execute(
            transactionPaths,
            () =>
            {
                sourceBoundary.VerifyUnchanged(CancellationToken.None);
                AtomicFile.WriteBytesValidated(
                    loadFolders,
                    stagedLoadFolders,
                    temporaryPath => ValidateLoadFolders(
                        BoundedFileReader.ReadAllBytes(
                            temporaryPath,
                            MaximumLoadFoldersBytes,
                            "prepared RMK LoadFolders output")),
                    writeBoundary,
                    keepBackup: false);
                AtomicFile.WriteBytesValidated(
                    modList,
                    stagedModList,
                    temporaryPath => ValidateModList(
                        BoundedFileReader.ReadAllBytes(
                            temporaryPath,
                            MaximumMetadataBytes,
                            "prepared RMK ModList output"),
                        root),
                    writeBoundary,
                    keepBackup: false);
                sourceBoundary.VerifyUnchanged(CancellationToken.None);
            },
            "RMK Builder publication",
            writeBoundary.ReleaseWriteLeafLocksForRollback,
            writeBoundary,
            recoverySession,
            CancellationToken.None);
        var cleanupNotice = stagingCleanup.PreservedCount == 0
            ? string.Empty
            : $"{Environment.NewLine}경고: 소유권을 증명할 수 없는 이전 RMK Builder 스테이징 항목 {stagingCleanup.PreservedCount}개를 삭제하지 않고 보존했습니다: {string.Join(", ", stagingCleanup.SampleNames)}";
        return new RmkBuildResult(
            "RMK Builder가 검증된 인덱스 파일을 생성했습니다." + cleanupNotice,
            loadFolders,
            modList);
    }

    private static BuilderStagingRootLease PrepareBuilderStagingRoot(
        string requestedStagingRoot,
        string recoveryRoot,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stagingRoot = PathSafety.Normalize(requestedStagingRoot);
        EnsureLocalBuilderStagingPath(stagingRoot);
        if (PathsOverlap(stagingRoot, recoveryRoot)
            || PathsOverlap(stagingRoot, workspaceRoot))
        {
            throw new InvalidDataException(
                "RMK Builder staging must be separate from both the recovery authority and the selected workspace.");
        }

        SafeFileHandle? rootLease = null;
        try
        {
            using (PathSafety.AcquireTrustedDirectoryCreationBoundary(
                       stagingRoot,
                       cancellationToken))
            {
                PathSafety.EnsureNoReparsePointsToVolumeRoot(stagingRoot);
                rootLease = PathSafety.WindowsPathHandle
                    .OpenDirectoryWithoutDeleteSharing(stagingRoot);
            }
            var snapshot = PathSafety.GetExistingDirectorySnapshot(stagingRoot);
            if (!snapshot.CanonicalPath.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The RMK Builder staging root is redirected outside its exact application-owned path.");
            EnsureLocalBuilderStagingPath(snapshot.CanonicalPath);
            var leasedRoot = PathSafety.Normalize(
                PathSafety.WindowsPathHandle.GetFinalPath(rootLease));
            var leasedIdentity = PathSafety.WindowsPathHandle.GetIdentity(rootLease);
            var identity =
                $"{leasedIdentity.VolumeSerialNumber:x8}:{leasedIdentity.FileIndex:x16}";
            if (!leasedRoot.Equals(snapshot.CanonicalPath, StringComparison.OrdinalIgnoreCase)
                || !identity.Equals(snapshot.Identity, StringComparison.Ordinal)
                || (leasedIdentity.FileAttributes
                    & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
                   != FileAttributes.Directory)
            {
                throw new InvalidDataException(
                    "The RMK Builder staging root changed before its identity lease was established.");
            }
            var result = new BuilderStagingRootLease(
                snapshot.CanonicalPath,
                snapshot.Identity,
                rootLease);
            rootLease = null;
            return result;
        }
        finally
        {
            rootLease?.Dispose();
        }
    }

    private static void EnsureLocalBuilderStagingPath(string path)
    {
        if (PathSafety.IsNetworkPath(path))
            throw new InvalidDataException("RMK Builder staging must use a local filesystem path.");
        var volumeRoot = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || new DriveInfo(volumeRoot).DriveType == DriveType.Network)
        {
            throw new InvalidDataException(
                "RMK Builder staging must not use a mapped network drive.");
        }
    }

    private static IDisposable AcquireBuilderStagingLease(
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(
            stagingRoot,
            ".rmk-builder-staging.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4096,
                    FileOptions.None);
                return CreateBuilderStagingFileLease(lockPath, stream);
            }
            catch (IOException exception) when (IsTransientBuilderLockError(exception))
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private static BuilderStagingFileLease CreateBuilderStagingFileLease(
        string lockPath,
        FileStream stream)
    {
        try
        {
            if (!PathSafety.Normalize(
                        PathSafety.WindowsPathHandle.GetFinalPath(stream.SafeFileHandle))
                    .Equals(lockPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The RMK Builder staging lock is redirected or ambiguous.");
            }
            return new BuilderStagingFileLease(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool IsTransientBuilderLockError(IOException exception)
    {
        var nativeCode = exception.HResult & 0xFFFF;
        return nativeCode is 2 or 3 or 32 or 33 or 80 or 183;
    }

    private static bool PathsOverlap(string left, string right)
    {
        var normalizedLeft = PathSafety.Normalize(left);
        var normalizedRight = PathSafety.Normalize(right);
        return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase)
               || PathSafety.IsStrictlyInside(normalizedLeft, normalizedRight)
               || PathSafety.IsStrictlyInside(normalizedRight, normalizedLeft);
    }

    private static BuilderStagingCleanupReport CleanupAbandonedBuilderStages(
        BuilderStagingRootLease stagingRootLease,
        CancellationToken cancellationToken)
    {
        stagingRootLease.VerifyUnchanged();
        var stagingRoot = stagingRootLease.Root;
        var inspected = 0;
        var registrations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var quarantines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Preserve(string name, Exception? exception = null)
        {
            preserved.Add(name);
            if (exception is not null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"RMK Builder staging residue was preserved ({exception.GetType().Name}): {name}");
            }
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     stagingRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspected > MaximumBuilderStagingRecoveryEntries)
                throw new InvalidDataException(
                    "The RMK Builder staging root contains too many abandoned entries to clean safely.");
            var name = Path.GetFileName(entry);
            if (name.Equals(
                    ".rmk-builder-staging.lock",
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(entry)
                && !Directory.Exists(entry))
            {
                continue;
            }
            if (BuilderStageDirectoryNameRegex().IsMatch(name)
                && Directory.Exists(entry)
                && !File.Exists(entry))
            {
                if (!stages.TryAdd(name, entry))
                    throw new InvalidDataException(
                        "The RMK Builder staging root contains ambiguous stage names.");
                continue;
            }
            if (BuilderStageQuarantineDirectoryNameRegex().IsMatch(name)
                && Directory.Exists(entry)
                && !File.Exists(entry))
            {
                if (!quarantines.TryAdd(name, entry))
                    throw new InvalidDataException(
                        "The RMK Builder staging root contains ambiguous quarantine names.");
                continue;
            }
            var match = BuilderStageOwnershipFileNameRegex().Match(name);
            if (match.Success && File.Exists(entry) && !Directory.Exists(entry))
            {
                if (!registrations.TryAdd(match.Groups[1].Value, entry))
                    throw new InvalidDataException(
                        "The RMK Builder staging root contains ambiguous ownership records.");
                continue;
            }
            throw new InvalidDataException(
                $"The RMK Builder staging root contains an unrecognized entry that was preserved: {name}");
        }

        var ownerships = new Dictionary<string, BuilderStageOwnership>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (stageName, registrationPath) in registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ownerships.Add(
                    stageName,
                    ReadBuilderStageOwnership(
                        registrationPath,
                        stageName,
                        cancellationToken));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or ArgumentException
                                               or NotSupportedException)
            {
                Preserve(Path.GetFileName(registrationPath), exception);
            }
        }

        var candidatesByIdentity = new Dictionary<string, List<BuilderStageCandidate>>(
            StringComparer.Ordinal);
        foreach (var (name, path) in stages.Concat(quarantines))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetExactBuilderStageDirectoryIdentity(
                    stagingRoot,
                    path,
                    out var identity,
                    out var failure))
            {
                Preserve(name, failure);
                continue;
            }
            if (!candidatesByIdentity.TryGetValue(identity, out var candidates))
            {
                candidates = [];
                candidatesByIdentity.Add(identity, candidates);
            }
            candidates.Add(new BuilderStageCandidate(name, path));
        }

        var duplicateOwnershipIdentities = ownerships.Values
            .GroupBy(value => value.StageIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var removedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stageName, ownership) in ownerships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var registrationPath = registrations[stageName];
            if (duplicateOwnershipIdentities.Contains(ownership.StageIdentity))
            {
                Preserve(Path.GetFileName(registrationPath));
                continue;
            }

            candidatesByIdentity.TryGetValue(
                ownership.StageIdentity,
                out var candidates);
            if (candidates is { Count: 1 })
            {
                try
                {
                    CleanupRegisteredBuilderStage(
                        candidates[0].Path,
                        ownership,
                        registrationPath);
                    removedCandidates.Add(candidates[0].Name);
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or InvalidDataException
                                                   or ArgumentException
                                                   or NotSupportedException)
                {
                    Preserve(candidates[0].Name, exception);
                    Preserve(Path.GetFileName(registrationPath));
                }
            }
            else if (candidates is { Count: > 1 })
            {
                Preserve(Path.GetFileName(registrationPath));
                foreach (var candidate in candidates) Preserve(candidate.Name);
            }
            else
            {
                try
                {
                    DeleteBuilderStageOwnership(registrationPath, ownership);
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or InvalidDataException)
                {
                    Preserve(Path.GetFileName(registrationPath), exception);
                }
            }
            stagingRootLease.VerifyUnchanged();
        }

        foreach (var name in stages.Keys.Concat(quarantines.Keys))
        {
            if (!removedCandidates.Contains(name)) Preserve(name);
        }

        var samples = preserved
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return new BuilderStagingCleanupReport(preserved.Count, samples);
    }

    private static bool TryGetExactBuilderStageDirectoryIdentity(
        string stagingRoot,
        string path,
        out string identity,
        out Exception? failure)
    {
        identity = string.Empty;
        failure = null;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    "A recognized RMK Builder staging directory is redirected or not a directory.");
            }
            var canonical = PathSafety.GetCanonicalExistingDirectory(path);
            var normalized = PathSafety.Normalize(path);
            if (!canonical.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || !Path.GetDirectoryName(canonical)!.Equals(
                    stagingRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A recognized RMK Builder staging directory escaped its exact root.");
            }
            identity = PathSafety.GetExistingDirectoryIdentity(canonical);
            if (!BuilderStageIdentityRegex().IsMatch(identity))
                throw new InvalidDataException(
                    "A recognized RMK Builder staging directory has an invalid identity.");
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            failure = exception;
            return false;
        }
    }

    private static BuilderStageOwnership WriteBuilderStageOwnership(
        string stagingRoot,
        string stageName,
        string stageIdentity)
    {
        if (!BuilderStageDirectoryNameRegex().IsMatch(stageName)
            || !BuilderStageIdentityRegex().IsMatch(stageIdentity))
        {
            throw new InvalidDataException(
                "An RMK Builder stage cannot be registered with an invalid name or identity.");
        }
        var registrationPath = PathSafety.ResolveInside(
            stagingRoot,
            stageName + ".owner");
        var bytes = Utf8Strict.GetBytes(
            $"RimWorldAiTranslator.RmkBuilderStage/1\n{stageName}\n{stageIdentity}\n");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                registrationPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);
            if (!PathSafety.Normalize(
                        PathSafety.WindowsPathHandle.GetFinalPath(stream.SafeFileHandle))
                    .Equals(registrationPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "The RMK Builder stage ownership record is redirected.");
            }
            stream.Write(bytes);
            stream.Flush(true);
            var ownership = new BuilderStageOwnership(
                stageName,
                stageIdentity,
                registrationPath,
                bytes.LongLength,
                SHA256.HashData(bytes));
            return ownership;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static BuilderStageOwnership ReadBuilderStageOwnership(
        string registrationPath,
        string expectedStageName,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            registrationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        if (!PathSafety.Normalize(PathSafety.WindowsPathHandle.GetFinalPath(stream.SafeFileHandle))
                .Equals(PathSafety.Normalize(registrationPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"An RMK Builder stage ownership record is redirected or ambiguous and was preserved: {registrationPath}");
        }
        var bytes = BoundedFileReader.ReadAllBytes(
            stream,
            512,
            "RMK Builder stage ownership record",
            cancellationToken: cancellationToken);
        var lines = Utf8Strict.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        if (lines.Length != 4
            || !lines[0].Equals(
                "RimWorldAiTranslator.RmkBuilderStage/1",
                StringComparison.Ordinal)
            || !lines[1].Equals(expectedStageName, StringComparison.Ordinal)
            || !BuilderStageDirectoryNameRegex().IsMatch(lines[1])
            || !BuilderStageIdentityRegex().IsMatch(lines[2])
            || lines[3].Length != 0)
        {
            throw new InvalidDataException(
                $"An RMK Builder stage ownership record is malformed and was preserved: {registrationPath}");
        }
        return new BuilderStageOwnership(
            lines[1],
            lines[2],
            PathSafety.Normalize(registrationPath),
            bytes.LongLength,
            SHA256.HashData(bytes));
    }

    private static void CleanupRegisteredBuilderStage(
        string stagePath,
        BuilderStageOwnership ownership,
        string registrationPath)
    {
        var attributes = File.GetAttributes(stagePath);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException(
                $"A registered RMK Builder stage is redirected and was preserved: {stagePath}");
        var canonical = PathSafety.GetCanonicalExistingDirectory(stagePath);
        if (!canonical.Equals(PathSafety.Normalize(stagePath), StringComparison.OrdinalIgnoreCase)
            || !PathSafety.GetExistingDirectoryIdentity(canonical)
                .Equals(ownership.StageIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"A registered RMK Builder stage no longer matches its durable identity and was preserved: {stagePath}");
        }
        var cleanup = ExactDirectoryCleanup.QuarantineAndDelete(
            canonical,
            ownership.StageIdentity);
        if (!cleanup.Removed)
        {
            throw new IOException(
                $"A registered RMK Builder stage could not be cleaned safely and was preserved: {cleanup.PreservedPath ?? canonical}. {cleanup.Failure}");
        }
        DeleteBuilderStageOwnership(registrationPath, ownership);
    }

    private static void DeleteBuilderStageOwnership(
        string registrationPath,
        BuilderStageOwnership ownership)
    {
        if (!AtomicTemporaryFiles.TryDeleteRegularFile(
                registrationPath,
                ownership.RegistrationLength,
                ownership.RegistrationSha256))
        {
            throw new IOException(
                $"An exact RMK Builder stage ownership record could not be removed and was preserved: {registrationPath}");
        }
    }

    private static SnapshotLeafFingerprint CaptureInitialBuilderOutput(
        string path,
        long maximumBytes,
        string description,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
            throw new InvalidDataException($"{description} is occupied by a directory.");
        var fingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(
            path,
            cancellationToken);
        if (fingerprint.Kind == SnapshotLeafKind.File
            && fingerprint.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"{description} exceeds the {maximumBytes:N0}-byte limit.");
        }
        return fingerprint;
    }

    private static void VerifyBuilderOutputCas(
        PathSafety.TrustedWriteBoundary writeBoundary,
        IReadOnlyDictionary<string, SnapshotLeafFingerprint> initialOutputs,
        CancellationToken cancellationToken)
    {
        foreach (var (path, initial) in initialOutputs)
        {
            var current = writeBoundary.CaptureCurrentWriteFingerprint(
                path,
                cancellationToken);
            if (BuilderOutputFingerprintMatches(initial, current)) continue;
            throw new ConcurrentLeafChangeException(
                "An RMK index changed while Builder ran; the concurrent file was preserved and staged outputs were not published.",
                path);
        }
    }

    private static bool BuilderOutputFingerprintMatches(
        SnapshotLeafFingerprint expected,
        SnapshotLeafFingerprint current)
    {
        if (expected.Kind != current.Kind) return false;
        if (expected.Kind == SnapshotLeafKind.Missing) return true;
        return expected.Kind == SnapshotLeafKind.File
               && expected.Length == current.Length
               && expected.LastWriteTimeUtcTicks == current.LastWriteTimeUtcTicks
               && expected.Sha256 is { Length: 32 }
               && current.Sha256 is { Length: 32 }
               && CryptographicOperations.FixedTimeEquals(expected.Sha256, current.Sha256);
    }

    private BuilderSourcePublicationBoundary AcquireBuilderSourcePublicationBoundary(
        RmkBuilderExecutionPlan plan,
        string workspaceRoot,
        string expectedSeal,
        CancellationToken cancellationToken)
    {
        var layout = CollectBuilderStageLayout(
            workspaceRoot,
            plan.BuilderPath,
            cancellationToken);
        var inputBoundary = PathSafety.AcquireTrustedReadBoundary(
            workspaceRoot,
            layout.Inputs.Select(input => input.SourcePath),
            cancellationToken);
        try
        {
            var result = new BuilderSourcePublicationBoundary(
                this,
                plan,
                workspaceRoot,
                expectedSeal,
                layout,
                inputBoundary);
            result.VerifyUnchanged(cancellationToken);
            return result;
        }
        catch
        {
            inputBoundary.Dispose();
            throw;
        }
    }

    private static string CaptureBuilderSourceSeal(
        BuilderStageLayout layout,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendBuilderSourceSeal(hash, "RimWorldAiTranslator.RmkBuilderSource/1");
        foreach (var directory in layout.RelativeDirectories
                     .Select(NormalizeBuilderRelativePath)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendBuilderSourceSeal(hash, "D");
            AppendBuilderSourceSeal(hash, directory);
        }
        foreach (var input in layout.Inputs
                     .OrderBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(
                input.SourcePath,
                cancellationToken);
            if (fingerprint.Kind != SnapshotLeafKind.File
                || fingerprint.Sha256 is not { Length: 32 })
            {
                throw new ConcurrentLeafChangeException(
                    "An RMK Builder source input changed while its seal was captured.",
                    input.SourcePath);
            }
            AppendBuilderSourceSeal(hash, "F");
            AppendBuilderSourceSeal(
                hash,
                NormalizeBuilderRelativePath(input.RelativePath));
            AppendBuilderSourceSeal(hash, fingerprint.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendBuilderSourceSeal(hash, fingerprint.LastWriteTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendBuilderSourceSeal(hash, Convert.ToHexString(fingerprint.Sha256));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendBuilderSourceSeal(
        IncrementalHash hash,
        string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string CaptureBuilderContentSeal(
        BuilderStageLayout layout,
        Func<BuilderStageInput, string> selectPath,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendBuilderSourceSeal(hash, "RimWorldAiTranslator.RmkBuilderContent/1");
        var manifest = BuildBuilderStageManifestPaths(layout);
        foreach (var directory in manifest.RelativeDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendBuilderSourceSeal(hash, "D");
            AppendBuilderSourceSeal(hash, directory);
        }
        foreach (var input in layout.Inputs
                     .OrderBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(
                selectPath(input),
                cancellationToken);
            if (fingerprint.Kind != SnapshotLeafKind.File
                || fingerprint.Sha256 is not { Length: 32 }
                || fingerprint.Length < 0
                || fingerprint.Length > input.MaximumBytes)
            {
                throw new ConcurrentLeafChangeException(
                    "An RMK Builder input changed while its content seal was captured.",
                    selectPath(input));
            }
            AppendBuilderSourceSeal(hash, "F");
            AppendBuilderSourceSeal(
                hash,
                NormalizeBuilderRelativePath(input.RelativePath));
            AppendBuilderSourceSeal(
                hash,
                fingerprint.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendBuilderSourceSeal(hash, Convert.ToHexString(fingerprint.Sha256));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static BuilderStageManifestPaths ValidateBuilderStageLayoutBudget(
        BuilderStageLayout layout,
        CancellationToken cancellationToken)
    {
        var manifest = BuildBuilderStageManifestPaths(layout);
        if (manifest.RelativeDirectories.Count + manifest.RelativeFiles.Count
            > MaximumBuilderStageEntries)
        {
            throw new InvalidDataException(
                $"RMK Builder staging exceeds {MaximumBuilderStageEntries:N0} exact manifest entries.");
        }
        if (manifest.RelativeDirectories.Any(directory =>
                GetRelativePathDepth(directory) >= MaximumTargetTraversalDepth))
        {
            throw new InvalidDataException(
                $"RMK Builder staging exceeds the cleanup-safe relative directory depth of {MaximumTargetTraversalDepth - 1:N0}.");
        }

        var aggregateBytes = 0L;
        foreach (var input in layout.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(
                input.SourcePath,
                cancellationToken);
            if (fingerprint.Kind != SnapshotLeafKind.File
                || fingerprint.Sha256 is not { Length: 32 })
            {
                throw new ConcurrentLeafChangeException(
                    "An RMK Builder source input changed during staging preflight.",
                    input.SourcePath);
            }
            if (fingerprint.Length < 0 || fingerprint.Length > input.MaximumBytes)
                throw new InvalidDataException(
                    $"An RMK Builder source input exceeds its {input.MaximumBytes:N0}-byte limit.");
            aggregateBytes = checked(aggregateBytes + fingerprint.Length);
            if (aggregateBytes > MaximumBuilderStageInputBytes)
                throw new InvalidDataException(
                    $"RMK Builder inputs exceed the {MaximumBuilderStageInputBytes:N0}-byte preflight limit.");
        }
        return manifest;
    }

    private static BuilderStageManifestPaths BuildBuilderStageManifestPaths(
        BuilderStageLayout layout)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDirectoryAndAncestors(string relativePath)
        {
            var current = NormalizeAndValidateBuilderRelativePath(relativePath);
            while (!string.IsNullOrEmpty(current))
            {
                directories.Add(current);
                var separator = current.LastIndexOf('/');
                current = separator < 0 ? string.Empty : current[..separator];
            }
        }

        foreach (var directory in layout.RelativeDirectories)
            AddDirectoryAndAncestors(directory);
        foreach (var input in layout.Inputs)
        {
            var relative = NormalizeAndValidateBuilderRelativePath(input.RelativePath);
            if (!files.Add(relative))
                throw new InvalidDataException(
                    "RMK Builder staging contains case-insensitive duplicate input paths.");
            var separator = relative.LastIndexOf('/');
            if (separator >= 0) AddDirectoryAndAncestors(relative[..separator]);
        }
        if (files.Any(directories.Contains))
            throw new InvalidDataException(
                "RMK Builder staging contains a file/directory manifest collision.");
        return new BuilderStageManifestPaths(
            directories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            files.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string NormalizeAndValidateBuilderRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("An RMK Builder staging path is not relative.");
        var normalized = NormalizeBuilderRelativePath(relativePath);
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                                       || segment is "." or ".."
                                       || !PathSafety.IsSafeFileNameSegment(segment)))
        {
            throw new InvalidDataException(
                "An RMK Builder staging path contains an unsafe segment.");
        }
        return string.Join('/', segments);
    }

    private static bool BuilderStageLayoutsMatch(
        BuilderStageLayout left,
        BuilderStageLayout right)
    {
        var leftDirectories = left.RelativeDirectories
            .Select(NormalizeBuilderRelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rightDirectories = right.RelativeDirectories
            .Select(NormalizeBuilderRelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!leftDirectories.SequenceEqual(
                rightDirectories,
                StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        var leftInputs = left.Inputs
            .Select(input => NormalizeBuilderRelativePath(input.RelativePath))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rightInputs = right.Inputs
            .Select(input => NormalizeBuilderRelativePath(input.RelativePath))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return leftInputs.SequenceEqual(
            rightInputs,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeBuilderRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static bool TryCompareHexSha256(string expected, string current)
    {
        try
        {
            var expectedBytes = Convert.FromHexString(expected);
            var currentBytes = Convert.FromHexString(current);
            return expectedBytes.Length == 32
                   && currentBytes.Length == 32
                   && CryptographicOperations.FixedTimeEquals(
                       expectedBytes,
                       currentBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private BuilderStage CreateBuilderStage(
        RmkBuilderExecutionPlan plan,
        string workspaceRoot,
        BuilderStagingRootLease stagingRootLease,
        CancellationToken cancellationToken)
    {
        stagingRootLease.VerifyUnchanged();
        var stagingRoot = stagingRootLease.Root;
        var layout = CollectBuilderStageLayout(
            workspaceRoot,
            plan.BuilderPath,
            cancellationToken);
        using var inputBoundary = PathSafety.AcquireTrustedReadBoundary(
            workspaceRoot,
            layout.Inputs.Select(input => input.SourcePath),
            cancellationToken);
        if (!PathSafety.GetExistingDirectoryIdentity(workspaceRoot)
                .Equals(plan.WorkspaceIdentity, StringComparison.Ordinal))
        {
            throw new ConcurrentLeafChangeException(
                "The confirmed RMK workspace changed while the Builder stage was being prepared.",
                workspaceRoot);
        }
        inputBoundary.VerifyUnchanged();
        var confirmedLayout = CollectBuilderStageLayout(
            workspaceRoot,
            plan.BuilderPath,
            cancellationToken);
        if (!BuilderStageLayoutsMatch(layout, confirmedLayout))
            throw new ConcurrentLeafChangeException(
                "RMK Builder inputs changed before isolated staging began.",
                workspaceRoot);
        var manifestPaths = ValidateBuilderStageLayoutBudget(
            layout,
            cancellationToken);
        var sourceSeal = CaptureBuilderSourceSeal(
            layout,
            cancellationToken);
        var sourceContentSeal = CaptureBuilderContentSeal(
            layout,
            input => input.SourcePath,
            cancellationToken);
        inputBoundary.VerifyUnchanged();
        stagingRootLease.VerifyUnchanged();

        var stageRoot = CreateUniqueBuilderStageDirectory(stagingRoot);
        var stageRootLease = PathSafety.WindowsPathHandle.OpenDirectoryWithoutDeleteSharing(stageRoot);
        var leasedStageRoot = PathSafety.Normalize(
            PathSafety.WindowsPathHandle.GetFinalPath(stageRootLease));
        var leasedStageIdentity = PathSafety.WindowsPathHandle.GetIdentity(stageRootLease);
        var stageIdentity =
            $"{leasedStageIdentity.VolumeSerialNumber:x8}:{leasedStageIdentity.FileIndex:x16}";
        if (!leasedStageRoot.Equals(stageRoot, StringComparison.OrdinalIgnoreCase)
            || !PathSafety.GetExistingDirectoryIdentity(stageRoot)
                .Equals(stageIdentity, StringComparison.Ordinal))
        {
            stageRootLease.Dispose();
            throw new InvalidDataException(
                "The newly created RMK Builder stage changed before its identity could be leased.");
        }
        BuilderStageOwnership ownership;
        try
        {
            ownership = WriteBuilderStageOwnership(
                stagingRoot,
                Path.GetFileName(stageRoot),
                stageIdentity);
        }
        catch (Exception registrationError)
        {
            stageRootLease.Dispose();
            var cleanup = ExactDirectoryCleanup.QuarantineAndDelete(
                stageRoot,
                stageIdentity);
            if (!cleanup.Removed)
            {
                throw new IOException(
                    $"RMK Builder stage registration failed and its unregistered directory was preserved: {cleanup.PreservedPath ?? stageRoot}. {cleanup.Failure}",
                    registrationError);
            }
            throw;
        }
        var stage = new BuilderStage(
            stageRoot,
            stageIdentity,
            new RmkBuilderExecutionPlan(stageRoot, stageIdentity, string.Empty, string.Empty, 0),
            sourceSeal,
            stageRootLease,
            ownership,
            ExecutionBoundary: null);
        try
        {
            foreach (var directory in manifestPaths.RelativeDirectories
                         .OrderBy(GetRelativePathDepth)
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = PathSafety.ResolveInside(stageRoot, directory);
                Directory.CreateDirectory(destination);
                PathSafety.EnsureNoReparsePoints(destination, stageRoot);
            }
            foreach (var input in layout.Inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = PathSafety.ResolveInside(
                    stageRoot,
                    input.RelativePath);
                var parent = Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException(
                        "An RMK Builder staged input has no parent directory.");
                Directory.CreateDirectory(parent);
                PathSafety.EnsureNoReparsePoints(parent, stageRoot);
                AtomicFile.CopyFlushedBounded(
                    input.SourcePath,
                    destination,
                    input.MaximumBytes,
                    cancellationToken);
            }
            inputBoundary.VerifyUnchanged();
            confirmedLayout = CollectBuilderStageLayout(
                workspaceRoot,
                plan.BuilderPath,
                cancellationToken);
            if (!BuilderStageLayoutsMatch(layout, confirmedLayout)
                || !TryCompareHexSha256(
                    sourceSeal,
                    CaptureBuilderSourceSeal(layout, cancellationToken)))
            {
                throw new ConcurrentLeafChangeException(
                    "RMK Builder inputs changed while the isolated stage was being copied.",
                    workspaceRoot);
            }

            PopulateBuilderStageTestHook?.Invoke(workspaceRoot, stageRoot);
            var stagedBuilder = PathSafety.ResolveInside(
                stageRoot,
                "LoadFoldersBuilder.exe");
            var stagedSemanticLayout = CollectBuilderStageLayout(
                stageRoot,
                stagedBuilder,
                cancellationToken);
            if (!BuilderStageLayoutsMatch(layout, stagedSemanticLayout))
                throw new ConcurrentLeafChangeException(
                    "The isolated RMK Builder semantic layout no longer matches its confirmed source layout.",
                    stageRoot);
            ValidateBuilderStageInventory(stageRoot, stageIdentity, cancellationToken);
            var stagedContentSeal = CaptureBuilderContentSeal(
                layout,
                input => PathSafety.ResolveInside(stageRoot, input.RelativePath),
                cancellationToken);
            if (!TryCompareHexSha256(sourceContentSeal, stagedContentSeal))
                throw new ConcurrentLeafChangeException(
                    "An isolated RMK Builder input no longer matches its confirmed source copy.",
                    stageRoot);

            var stageSnapshot = CaptureBuilderStageSnapshot(
                stageRoot,
                stageIdentity,
                cancellationToken);
            var stageBoundary = PathSafety.AcquireTrustedReadBoundary(
                stageRoot,
                stageSnapshot.Files.Keys.Select(
                    relative => PathSafety.ResolveInside(stageRoot, relative)),
                cancellationToken);
            stage = stage with
            {
                ExecutionBoundary = new BuilderStageExecutionBoundary(
                    stageRoot,
                    stageIdentity,
                    stageSnapshot,
                    layout,
                    stagedBuilder,
                    stageBoundary)
            };
            stage.ExecutionBoundary.VerifyBeforeProcess(cancellationToken);
            var pinnedContentSeal = CaptureBuilderContentSeal(
                layout,
                input => PathSafety.ResolveInside(stageRoot, input.RelativePath),
                cancellationToken);
            if (!TryCompareHexSha256(sourceContentSeal, pinnedContentSeal))
                throw new ConcurrentLeafChangeException(
                    "An isolated RMK Builder input changed before its execution boundary was pinned.",
                    stageRoot);
            inputBoundary.VerifyUnchanged();
            using var stagedBuilderLock = OpenBuilderReadLock(stagedBuilder);
            var stagedHash = Convert.ToHexString(SHA256.HashData(stagedBuilderLock));
            if (stagedBuilderLock.Length != plan.BuilderLength
                || !stagedHash.Equals(plan.BuilderSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The isolated RMK Builder copy does not match the confirmed executable.");
            }
            stage = stage with
            {
                Plan = new RmkBuilderExecutionPlan(
                    stageRoot,
                    stageIdentity,
                    stagedBuilder,
                    stagedHash,
                    stagedBuilderLock.Length),
                SourceSeal = sourceSeal
            };
            return stage;
        }
        catch (Exception operationError)
        {
            CleanupBuilderStageOrThrow(stage, operationError);
            throw;
        }
    }

    private static string CreateUniqueBuilderStageDirectory(string stagingRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Isolated RMK Builder staging requires Windows directory identities.");
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var stageRoot = Path.Combine(stagingRoot, $"run-{Guid.NewGuid():N}");
            if (CreateDirectoryNative(AddExtendedPathPrefix(stageRoot), IntPtr.Zero))
                return stageRoot;
            var error = Marshal.GetLastWin32Error();
            if (error is 80 or 183) continue;
            throw new IOException(
                "A unique RMK Builder stage could not be created.",
                new Win32Exception(error));
        }
        throw new IOException("A unique RMK Builder stage could not be allocated.");
    }

    private static BuilderStageLayout CollectBuilderStageLayout(
        string workspaceRoot,
        string builderPath,
        CancellationToken cancellationToken)
    {
        var inputs = new List<BuilderStageInput>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "About",
            "Data"
        };
        AddBuilderStageInput(
            inputs,
            builderPath,
            "LoadFoldersBuilder.exe",
            MaximumBuilderBytes);

        foreach (var candidate in Directory.EnumerateFiles(
                     workspaceRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Equals(builderPath, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(candidate);
            var supportedCompanion = Path.GetExtension(name)
                                         .Equals(".dll", StringComparison.OrdinalIgnoreCase)
                                     || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                                     || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                                     || name.Equals(
                                         "LoadFoldersBuilder.exe.config",
                                         StringComparison.OrdinalIgnoreCase);
            if (!supportedCompanion) continue;
            AddBuilderStageInput(inputs, candidate, name, MaximumBuilderBytes);
        }

        var about = Path.Combine(workspaceRoot, "About", "About.xml");
        if (!File.Exists(about))
            throw new FileNotFoundException(
                "RMK Builder staging requires About/About.xml in the selected work clone.",
                about);
        AddBuilderStageInput(
            inputs,
            about,
            Path.Combine("About", "About.xml"),
            MaximumMetadataBytes);

        var dataRoot = Path.Combine(workspaceRoot, "Data");
        var budget = new RmkTargetTraversalBudget();
        budget.RegisterDirectory(dataRoot);
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((dataRoot, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (!TryValidateRmkTraversalDirectory(
                    workspaceRoot,
                    directory,
                    out var canonicalDirectory))
            {
                continue;
            }
            if (!budget.MarkScanned(canonicalDirectory)) continue;
            var directoryName = Path.GetFileName(canonicalDirectory);
            if (directoryName.Equals("Languages", StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals("Textures", StringComparison.OrdinalIgnoreCase))
            {
                directories.Add(Path.GetRelativePath(workspaceRoot, canonicalDirectory));
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         canonicalDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    budget.RegisterDirectory(entry);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        continue;
                    if (depth >= MaximumTargetTraversalDepth)
                        budget.ThrowDepthLimitExceeded();
                    pending.Push((entry, depth + 1));
                    continue;
                }

                budget.RegisterFile();
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                    || !Path.GetFileName(entry).Equals(
                        "LoadFolders.Build.yaml",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var canonicalFile = PathSafety.GetCanonicalExistingFile(entry);
                if (!canonicalFile.Equals(
                        PathSafety.Normalize(entry),
                        StringComparison.OrdinalIgnoreCase)
                    || !PathSafety.IsStrictlyInside(canonicalFile, workspaceRoot))
                {
                    throw new InvalidDataException(
                        "An RMK Builder YAML input resolved outside the selected work clone.");
                }
                var relative = Path.GetRelativePath(workspaceRoot, canonicalFile);
                directories.Add(Path.GetDirectoryName(relative) ?? "Data");
                AddBuilderStageInput(
                    inputs,
                    canonicalFile,
                    relative,
                    MaximumMetadataBytes);
            }
        }

        if (inputs.Count + directories.Count > MaximumBuilderStageEntries)
            throw new InvalidDataException(
                $"RMK Builder staging exceeds {MaximumBuilderStageEntries:N0} bounded entries.");
        return new BuilderStageLayout(
            directories.ToArray(),
            inputs.ToArray());
    }

    private static void AddBuilderStageInput(
        ICollection<BuilderStageInput> inputs,
        string sourcePath,
        string relativePath,
        long maximumBytes)
    {
        var normalizedSource = PathSafety.Normalize(sourcePath);
        var canonicalSource = PathSafety.GetCanonicalExistingFile(normalizedSource);
        if (!canonicalSource.Equals(normalizedSource, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "An RMK Builder staged input is not an exact canonical file.");
        inputs.Add(new BuilderStageInput(
            canonicalSource,
            relativePath,
            maximumBytes));
    }

    private static int GetRelativePathDepth(string relativePath) =>
        relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Length;

    private static void ValidateBuilderStageInventory(
        string stageRoot,
        string expectedIdentity,
        CancellationToken cancellationToken) =>
        _ = CaptureBuilderStageSnapshot(
            stageRoot,
            expectedIdentity,
            cancellationToken);

    private static BuilderStageSnapshot CaptureBuilderStageSnapshot(
        string stageRoot,
        string expectedIdentity,
        CancellationToken cancellationToken)
    {
        if (!PathSafety.GetExistingDirectoryIdentity(stageRoot)
                .Equals(expectedIdentity, StringComparison.Ordinal))
        {
            throw new ConcurrentLeafChangeException(
                "The RMK Builder stage identity changed while it was being prepared.",
                stageRoot);
        }

        var entries = 0;
        var aggregateBytes = 0L;
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, SnapshotLeafFingerprint>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((stageRoot, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaximumBuilderStageEntries)
                    throw new InvalidDataException(
                        $"RMK Builder staging exceeds {MaximumBuilderStageEntries:N0} entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new InvalidDataException(
                        "RMK Builder staging contains a redirected or device entry.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (depth >= MaximumTargetTraversalDepth - 1)
                        throw new InvalidDataException(
                            "RMK Builder staging exceeds the supported directory depth.");
                    var canonicalDirectory = PathSafety.GetCanonicalExistingDirectory(entry);
                    if (!canonicalDirectory.Equals(
                            PathSafety.Normalize(entry),
                            StringComparison.OrdinalIgnoreCase)
                        || !PathSafety.IsStrictlyInside(canonicalDirectory, stageRoot))
                    {
                        throw new InvalidDataException(
                            "An RMK Builder staging directory escaped its exact stage root.");
                    }
                    var directoryRelative = NormalizeBuilderRelativePath(
                        Path.GetRelativePath(stageRoot, canonicalDirectory));
                    if (!directories.TryAdd(
                            directoryRelative,
                            PathSafety.GetExistingDirectoryIdentity(canonicalDirectory)))
                    {
                        throw new InvalidDataException(
                            "RMK Builder staging contains ambiguous directory paths.");
                    }
                    pending.Push((canonicalDirectory, depth + 1));
                    continue;
                }

                var canonicalFile = PathSafety.GetCanonicalExistingFile(entry);
                if (!canonicalFile.Equals(
                        PathSafety.Normalize(entry),
                        StringComparison.OrdinalIgnoreCase)
                    || !PathSafety.IsStrictlyInside(canonicalFile, stageRoot))
                {
                    throw new InvalidDataException(
                        "An RMK Builder staging file escaped its exact stage root.");
                }
                var fingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(
                    canonicalFile,
                    cancellationToken);
                if (fingerprint.Kind != SnapshotLeafKind.File
                    || fingerprint.Sha256 is not { Length: 32 }
                    || fingerprint.Length < 0
                    || fingerprint.Length > MaximumBuilderBytes)
                    throw new InvalidDataException(
                        "An RMK Builder staging file exceeds the bounded per-file limit.");
                var fileRelative = NormalizeBuilderRelativePath(
                    Path.GetRelativePath(stageRoot, canonicalFile));
                if (!files.TryAdd(fileRelative, fingerprint))
                    throw new InvalidDataException(
                        "RMK Builder staging contains ambiguous file paths.");
                aggregateBytes = checked(aggregateBytes + fingerprint.Length);
                if (aggregateBytes > MaximumBuilderStageBytes)
                    throw new InvalidDataException(
                        "RMK Builder staging exceeds the bounded aggregate byte limit.");
            }
        }
        return new BuilderStageSnapshot(directories, files, aggregateBytes);
    }

    private static void VerifyBuilderStageSnapshot(
        BuilderStageSnapshot expected,
        BuilderStageSnapshot current,
        bool allowBuilderOutputs,
        string stageRoot)
    {
        if (expected.Directories.Count != current.Directories.Count
            || expected.Directories.Any(pair =>
                !current.Directories.TryGetValue(pair.Key, out var identity)
                || !identity.Equals(pair.Value, StringComparison.Ordinal)))
        {
            throw new ConcurrentLeafChangeException(
                "The isolated RMK Builder directory manifest changed during execution.",
                stageRoot);
        }
        foreach (var (relative, fingerprint) in expected.Files)
        {
            if (!current.Files.TryGetValue(relative, out var currentFingerprint)
                || !BuilderOutputFingerprintMatches(fingerprint, currentFingerprint))
            {
                throw new ConcurrentLeafChangeException(
                    "An isolated RMK Builder input changed during execution.",
                    Path.Combine(stageRoot, relative));
            }
        }

        var extras = current.Files.Keys
            .Where(relative => !expected.Files.ContainsKey(relative))
            .ToArray();
        if (!allowBuilderOutputs && extras.Length != 0
            || allowBuilderOutputs && extras.Any(relative =>
                !relative.Equals("LoadFolders.xml", StringComparison.OrdinalIgnoreCase)
                && !relative.Equals("ModList.tsv", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The isolated RMK Builder created an unexpected staging entry; live indexes were not modified.");
        }
    }

    private static void CleanupBuilderStage(BuilderStage stage)
    {
        stage.ExecutionBoundary?.Dispose();
        stage.RootLease.Dispose();
        var cleanup = ExactDirectoryCleanup.QuarantineAndDelete(
            stage.Root,
            stage.Identity);
        if (!cleanup.Removed)
        {
            throw new IOException(
                $"The isolated RMK Builder stage could not be cleaned safely and was preserved: {cleanup.PreservedPath ?? stage.Root}. {cleanup.Failure}");
        }
        DeleteBuilderStageOwnership(
            stage.Ownership.RegistrationPath,
            stage.Ownership);
    }

    private static void CleanupBuilderStageOrThrow(
        BuilderStage stage,
        Exception operationError)
    {
        try
        {
            CleanupBuilderStage(stage);
        }
        catch (Exception cleanupError) when (cleanupError is IOException
                                             or UnauthorizedAccessException
                                             or InvalidDataException)
        {
            if (operationError is OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Cancelled RMK Builder stage cleanup was deferred ({cleanupError.GetType().Name}).");
                throw operationError;
            }
            throw new IOException(
                "RMK Builder failed and its isolated stage could not be cleaned safely. The live indexes were not modified.",
                new AggregateException(operationError, cleanupError));
        }
    }

    private static FileStream OpenVerifiedBuilder(RmkBuilderExecutionPlan plan, string root)
    {
        var stream = OpenBuilderReadLock(plan.BuilderPath);
        try
        {
            PathSafety.EnsureNoReparsePoints(plan.BuilderPath, root);
            var canonical = PathSafety.GetCanonicalExistingFile(plan.BuilderPath);
            if (!canonical.Equals(plan.BuilderPath, StringComparison.OrdinalIgnoreCase)
                || stream.Length != plan.BuilderLength)
            {
                throw new InvalidDataException("The confirmed RMK Builder changed before execution.");
            }
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(plan.BuilderSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The confirmed RMK Builder hash changed before execution.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static FileStream OpenBuilderReadLock(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumBuilderBytes)
        {
            stream.Dispose();
            throw new InvalidDataException($"RMK Builder exceeds the {MaximumBuilderBytes:N0}-byte limit.");
        }
        return stream;
    }

    public string GetRimWorldVersion(string modRoot, IEnumerable<string> steamLibraryRoots)
    {
        var fullMod = PathSafety.Normalize(modRoot);
        foreach (var steam in steamLibraryRoots)
        {
            var containers = new[]
            {
                Path.Combine(steam, "steamapps", "workshop", "content", "294100"),
                Path.Combine(steam, "steamapps", "common", "RimWorld", "Mods")
            };
            if (!containers.Any(container => Directory.Exists(container) && PathSafety.IsStrictlyInside(fullMod, container))) continue;
            var versionFile = Path.Combine(steam, "steamapps", "common", "RimWorld", "Version.txt");
            if (!File.Exists(versionFile)) continue;
            var match = VersionRegex().Match(ReadText(versionFile, 64 * 1024, "RimWorld version file"));
            if (match.Success) return match.Groups[1].Value;
        }
        return "1.6";
    }

    private static IReadOnlyList<string> EnumerateImmediateRmkDirectories(
        string trustedRoot,
        string startDirectory,
        RmkTargetTraversalBudget budget)
    {
        budget.RegisterDirectory(startDirectory);
        if (!TryValidateRmkTraversalDirectory(trustedRoot, startDirectory, out var canonicalStart)) return [];
        if (!budget.MarkScanned(canonicalStart)) return [];
        var result = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(canonicalStart, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                budget.RegisterFile();
                continue;
            }

            budget.RegisterDirectory(entry);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
            if (TryValidateRmkTraversalDirectory(trustedRoot, entry, out var canonicalChild))
                result.Add(canonicalChild);
        }
        return result;
    }

    private static IEnumerable<string> EnumerateRmkMetadataFiles(
        string trustedRoot,
        string startDirectory,
        string expectedFileName,
        RmkTargetTraversalBudget budget)
    {
        budget.RegisterDirectory(startDirectory);
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((startDirectory, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if (!TryValidateRmkTraversalDirectory(trustedRoot, directory, out var canonicalDirectory)) continue;
            if (!budget.MarkScanned(canonicalDirectory)) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(canonicalDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    budget.RegisterDirectory(entry);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                    if (depth >= MaximumTargetTraversalDepth)
                        budget.ThrowDepthLimitExceeded();
                    pending.Push((entry, depth + 1));
                    continue;
                }

                budget.RegisterFile();
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                    || !Path.GetFileName(entry).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var normalizedFile = PathSafety.Normalize(entry);
                var canonicalFile = PathSafety.GetCanonicalExistingFile(normalizedFile);
                if (!canonicalFile.Equals(normalizedFile, StringComparison.OrdinalIgnoreCase)
                    || !PathSafety.IsStrictlyInside(canonicalFile, trustedRoot))
                {
                    throw new InvalidDataException("RMK target metadata resolved outside its trusted workspace path.");
                }
                yield return canonicalFile;
            }
        }
    }

    private static IEnumerable<string> EnumerateImmediateRmkFiles(
        string trustedRoot,
        string startDirectory,
        string expectedExtension,
        RmkTargetTraversalBudget budget)
    {
        budget.RegisterDirectory(startDirectory);
        if (!TryValidateRmkTraversalDirectory(trustedRoot, startDirectory, out var canonicalStart)) yield break;
        foreach (var entry in Directory.EnumerateFileSystemEntries(canonicalStart, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                budget.RegisterDirectory(entry);
                continue;
            }
            budget.RegisterFile();
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || !Path.GetExtension(entry).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var normalizedFile = PathSafety.Normalize(entry);
            var canonicalFile = PathSafety.GetCanonicalExistingFile(normalizedFile);
            if (!canonicalFile.Equals(normalizedFile, StringComparison.OrdinalIgnoreCase)
                || !PathSafety.IsStrictlyInside(canonicalFile, trustedRoot))
            {
                throw new InvalidDataException("RMK target workbook resolved outside its trusted workspace path.");
            }
            yield return canonicalFile;
        }
    }

    private static bool TryValidateRmkTraversalDirectory(
        string trustedRoot,
        string path,
        out string canonicalDirectory)
    {
        var normalized = PathSafety.Normalize(path);
        if (!normalized.Equals(trustedRoot, StringComparison.OrdinalIgnoreCase)
            && !PathSafety.IsStrictlyInside(normalized, trustedRoot))
        {
            throw new InvalidDataException("RMK target traversal attempted to leave its trusted workspace path.");
        }
        var attributes = File.GetAttributes(normalized);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            canonicalDirectory = string.Empty;
            return false;
        }
        canonicalDirectory = PathSafety.GetCanonicalExistingDirectory(normalized);
        if (!canonicalDirectory.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || !canonicalDirectory.Equals(trustedRoot, StringComparison.OrdinalIgnoreCase)
               && !PathSafety.IsStrictlyInside(canonicalDirectory, trustedRoot))
        {
            throw new InvalidDataException("RMK target directory resolved outside its trusted workspace path.");
        }
        return true;
    }

    private sealed class RmkTargetTraversalBudget
    {
        private readonly HashSet<string> registeredDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> scannedDirectories = new(StringComparer.OrdinalIgnoreCase);
        private int fileCount;

        public bool LimitExceeded { get; private set; }

        public void RegisterDirectory(string path)
        {
            if (!registeredDirectories.Add(PathSafety.Normalize(path))) return;
            if (registeredDirectories.Count > MaximumTargetTraversalDirectories)
            {
                LimitExceeded = true;
                throw new InvalidDataException($"RMK target traversal exceeds {MaximumTargetTraversalDirectories:N0} directories.");
            }
        }

        public void RegisterFile()
        {
            if (++fileCount > MaximumTargetTraversalFiles)
            {
                LimitExceeded = true;
                throw new InvalidDataException($"RMK target traversal exceeds {MaximumTargetTraversalFiles:N0} files.");
            }
        }

        public bool MarkScanned(string canonicalDirectory) => scannedDirectories.Add(canonicalDirectory);

        public void ThrowDepthLimitExceeded()
        {
            LimitExceeded = true;
            throw new InvalidDataException($"RMK target traversal exceeds the supported depth of {MaximumTargetTraversalDepth:N0}.");
        }
    }

    private static RmkTarget? ReadTarget(
        string yamlPath,
        string sourceRoot,
        string sourceKind,
        RmkTargetTraversalBudget? traversal = null,
        byte[]? trustedMetadataBytes = null)
    {
        try
        {
            traversal ??= new RmkTargetTraversalBudget();
            var text = trustedMetadataBytes is null
                ? ReadText(yamlPath, MaximumMetadataBytes, "RMK metadata")
                : trustedMetadataBytes.Length <= MaximumMetadataBytes
                    ? Utf8Strict.GetString(trustedMetadataBytes)
                    : throw new InvalidDataException("RMK metadata exceeds its size limit.");
            var workshop = ValueRegex("WorkshopID").Match(text).Groups[1].Value.Trim();
            var name = ValueRegex("ModName").Match(text).Groups[1].Value.Trim().Trim('"', '\'');
            var defaultVersion = ValueRegex("Default").Match(text).Groups[1].Value.Trim();
            var root = PathSafety.Normalize(Path.GetDirectoryName(yamlPath)!);
            var leaf = Path.GetFileName(root);
            var version = VersionOnlyRegex().IsMatch(leaf) ? leaf : defaultVersion;
            var workbook = string.Empty;
            foreach (var candidate in EnumerateImmediateRmkFiles(sourceRoot, root, ".xlsx", traversal))
            {
                if (string.IsNullOrEmpty(workbook)
                    || CompareRmkWorkbookCandidate(candidate, workbook, workshop) < 0)
                {
                    workbook = candidate;
                }
            }
            return new RmkTarget(root, Path.GetFullPath(yamlPath), Path.Combine(root, "Languages", "Korean (한국어)"), workbook, workshop, name, version, sourceRoot, sourceKind);
        }
        catch (InvalidDataException) when (traversal?.LimitExceeded == true)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Invalid RMK target skipped ({exception.GetType().Name}).");
            return null;
        }
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    private static string ReadGitBranch(string root)
    {
        var gitDirectory = ResolveValidatedGitDirectory(root);
        var head = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(head)) return "알 수 없음";
        var text = ReadText(head, 64 * 1024, "Git HEAD").Trim();
        const string prefix = "ref: refs/heads/";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : "분리된 HEAD";
    }

    private static string ResolveValidatedGitDirectory(string root)
    {
        var git = Path.Combine(root, ".git");
        if (Directory.Exists(git))
        {
            PathSafety.EnsureNoReparsePoints(git, root);
            var canonicalDirectory = PathSafety.GetCanonicalExistingDirectory(git);
            if (!canonicalDirectory.Equals(PathSafety.Normalize(git), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The RMK Git directory must be opened through its canonical physical path.");
            return canonicalDirectory;
        }

        if (!File.Exists(git)) throw new InvalidDataException("The RMK workspace does not contain Git metadata.");
        PathSafety.EnsureNoReparsePoints(git, root);
        var canonicalGitFile = PathSafety.GetCanonicalExistingFile(git);
        if (!canonicalGitFile.Equals(PathSafety.Normalize(git), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RMK worktree .git file must be opened through its canonical physical path.");
        var pointer = ReadText(git, 64 * 1024, "Git worktree metadata").Trim();
        if (!pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RMK worktree .git file is invalid.");
        var adminDirectory = ResolveGitMetadataPath(root, pointer[7..], expectDirectory: true);

        var backlinkFile = Path.Combine(adminDirectory, "gitdir");
        var commonPointerFile = Path.Combine(adminDirectory, "commondir");
        if (!File.Exists(backlinkFile) || !File.Exists(commonPointerFile))
            throw new InvalidDataException("The RMK worktree Git administration directory is incomplete.");
        var backlink = ResolveGitMetadataPath(
            adminDirectory,
            ReadText(backlinkFile, 64 * 1024, "Git worktree backlink"),
            expectDirectory: false);
        if (!backlink.Equals(canonicalGitFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RMK worktree Git backlink does not match the selected workspace.");
        var commonDirectory = ResolveGitMetadataPath(
            adminDirectory,
            ReadText(commonPointerFile, 64 * 1024, "Git worktree common directory"),
            expectDirectory: true);
        var expectedWorktreesDirectory = Path.Combine(commonDirectory, "worktrees");
        if (!Path.GetDirectoryName(adminDirectory)!.Equals(expectedWorktreesDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RMK worktree Git administration directory is outside its validated common directory.");
        if (!File.Exists(Path.Combine(adminDirectory, "HEAD")))
            throw new InvalidDataException("The RMK worktree Git administration directory has no HEAD.");
        return adminDirectory;
    }

    private static string ResolveGitMetadataPath(string baseDirectory, string rawValue, bool expectDirectory)
    {
        var value = rawValue.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            throw new InvalidDataException("Git worktree metadata contains an invalid path.");
        var path = PathSafety.Normalize(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));
        if (expectDirectory)
        {
            if (!Directory.Exists(path)) throw new InvalidDataException("Git worktree metadata references a missing directory.");
            var canonical = PathSafety.GetCanonicalExistingDirectory(path);
            if (!canonical.Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Git worktree metadata must use a canonical physical directory.");
            return canonical;
        }

        if (!File.Exists(path)) throw new InvalidDataException("Git worktree metadata references a missing file.");
        var canonicalFile = PathSafety.GetCanonicalExistingFile(path);
        if (!canonicalFile.Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Git worktree metadata must use a canonical physical file.");
        return canonicalFile;
    }

    private static PinnedBuilderOutputs PinBuilderOutputs(
        string loadFoldersPath,
        string modListPath,
        string workspaceRoot,
        bool requireSemanticallyValidFiles,
        CancellationToken cancellationToken)
    {
        PinnedBuilderOutput? loadFolders = null;
        PinnedBuilderOutput? modList = null;
        try
        {
            loadFolders = PinnedBuilderOutput.Open(
                loadFoldersPath,
                workspaceRoot,
                MaximumLoadFoldersBytes,
                "RMK LoadFolders output",
                allowMissing: !requireSemanticallyValidFiles,
                cancellationToken);
            modList = PinnedBuilderOutput.Open(
                modListPath,
                workspaceRoot,
                MaximumMetadataBytes,
                "RMK ModList output",
                allowMissing: !requireSemanticallyValidFiles,
                cancellationToken);
            if (requireSemanticallyValidFiles)
            {
                ValidateLoadFolders(loadFolders.Bytes);
                ValidateModList(modList.Bytes, workspaceRoot);
            }
            return new PinnedBuilderOutputs(loadFolders, modList);
        }
        catch
        {
            modList?.Dispose();
            loadFolders?.Dispose();
            throw;
        }
    }

    private static void ValidateLoadFolders(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var document = SafeXml.LoadBounded(stream, MaximumLoadFoldersBytes);
        if (document.Root is null
            || !document.Root.Name.LocalName.Equals("loadFolders", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "RMK Builder produced a LoadFolders.xml file with an invalid root element.");
        }
    }

    private static void ValidateModList(byte[] bytes, string workspaceRoot)
    {
        var offset = bytes.Length >= 3
                     && bytes[0] == 0xEF
                     && bytes[1] == 0xBB
                     && bytes[2] == 0xBF
            ? 3
            : 0;
        var lines = Utf8Strict.GetString(bytes, offset, bytes.Length - offset)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
        {
            throw new InvalidDataException("RMK Builder produced an empty ModList.tsv file.");
        }
        if (lines.Length > MaximumModListEntries)
            throw new InvalidDataException(
                $"RMK Builder produced more than {MaximumModListEntries:N0} ModList entries.");
        foreach (var line in lines)
        {
            if (line.Any(character => char.IsControl(character) && character != '\t'))
            {
                throw new InvalidDataException(
                    "RMK Builder produced a ModList.tsv row containing invalid control characters.");
            }
            var columns = line.Split('\t');
            if (columns.Length != 4 || columns.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException(
                    "RMK Builder produced a malformed ModList.tsv row; exactly four non-empty columns are required.");
            var workshopId = columns[0].Trim();
            if (!workshopId.Equals("No ID", StringComparison.Ordinal)
                && (workshopId.Length is < 1 or > 20 || !workshopId.All(char.IsAsciiDigit)))
            {
                throw new InvalidDataException(
                    "RMK Builder produced a ModList.tsv row with an invalid Workshop ID.");
            }
            ValidateModListRelativeLocation(columns[2].Trim(), workspaceRoot);
        }
    }

    private static void ValidateModListRelativeLocation(
        string relativeLocation,
        string workspaceRoot)
    {
        if (relativeLocation.Contains('\\')
            || relativeLocation.Contains(':')
            || Path.IsPathRooted(relativeLocation))
        {
            throw new InvalidDataException(
                "RMK Builder produced an unsafe ModList.tsv relative location.");
        }
        var segments = relativeLocation.Split('/');
        if (segments.Length == 0
            || !segments[0].Equals("Data", StringComparison.OrdinalIgnoreCase)
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                                       || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "RMK Builder produced a ModList.tsv location outside Data.");
        }
        var resolved = PathSafety.ResolveInside(
            workspaceRoot,
            Path.Combine(segments));
        var dataRoot = Path.Combine(workspaceRoot, "Data");
        if (!resolved.Equals(dataRoot, StringComparison.OrdinalIgnoreCase)
            && !PathSafety.IsStrictlyInside(resolved, dataRoot))
        {
            throw new InvalidDataException(
                "RMK Builder produced a ModList.tsv location outside Data.");
        }
    }

    private sealed class PinnedBuilderOutputs : IDisposable
    {
        private readonly PinnedBuilderOutput loadFolders;
        private readonly PinnedBuilderOutput modList;

        public PinnedBuilderOutputs(
            PinnedBuilderOutput loadFolders,
            PinnedBuilderOutput modList)
        {
            this.loadFolders = loadFolders;
            this.modList = modList;
            Fingerprints = new Dictionary<string, SnapshotLeafFingerprint>(
                StringComparer.OrdinalIgnoreCase)
            {
                [loadFolders.Path] = loadFolders.Fingerprint,
                [modList.Path] = modList.Fingerprint
            };
        }

        public IReadOnlyDictionary<string, SnapshotLeafFingerprint> Fingerprints { get; }

        public ReadOnlyMemory<byte> LoadFoldersBytes => loadFolders.Bytes;

        public ReadOnlyMemory<byte> ModListBytes => modList.Bytes;

        public void Dispose()
        {
            modList.Dispose();
            loadFolders.Dispose();
        }
    }

    private sealed class PinnedBuilderOutput : IDisposable
    {
        private readonly FileStream? stream;

        private PinnedBuilderOutput(
            string path,
            SnapshotLeafFingerprint fingerprint,
            byte[] bytes,
            FileStream? stream)
        {
            Path = path;
            Fingerprint = fingerprint;
            Bytes = bytes;
            this.stream = stream;
        }

        public string Path { get; }

        public SnapshotLeafFingerprint Fingerprint { get; }

        public byte[] Bytes { get; }

        public static PinnedBuilderOutput Open(
            string path,
            string workspaceRoot,
            long maximumBytes,
            string description,
            bool allowMissing,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = PathSafety.Normalize(path);
            PathSafety.EnsureNoReparsePoints(fullPath, workspaceRoot);
            if (Directory.Exists(fullPath))
                throw new InvalidDataException($"{description} is occupied by a directory.");
            if (!File.Exists(fullPath))
            {
                if (!allowMissing)
                    throw new InvalidDataException($"{description} is missing.");
                return new PinnedBuilderOutput(
                    fullPath,
                    new SnapshotLeafFingerprint(SnapshotLeafKind.Missing),
                    [],
                    null);
            }
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "RMK Builder output pinning requires Windows file identities.");

            var handle = PathSafety.WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(fullPath);
            FileStream? stream = null;
            var streamOwnsHandle = false;
            try
            {
                var identity = PathSafety.WindowsPathHandle.GetIdentity(handle);
                if (identity.FileIndex == 0
                    || identity.NumberOfLinks != 1
                    || (identity.FileAttributes
                        & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        $"{description} is not a unique regular file.");
                }
                var finalPath = PathSafety.WindowsPathHandle.GetFinalPath(handle);
                if (!PathSafety.Normalize(finalPath).Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"{description} changed physical path while it was being pinned.");

                stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
                streamOwnsHandle = true;
                var bytes = BoundedFileReader.ReadAllBytes(
                    stream,
                    maximumBytes,
                    description,
                    cancellationToken: cancellationToken);
                var identityAfterRead = PathSafety.WindowsPathHandle.GetIdentity(stream.SafeFileHandle);
                if (!identityAfterRead.Equals(identity)
                    || stream.Length != bytes.LongLength
                    || !PathSafety.Normalize(
                            PathSafety.WindowsPathHandle.GetFinalPath(stream.SafeFileHandle))
                        .Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConcurrentLeafChangeException(
                        $"{description} changed while it was being pinned.",
                        fullPath);
                }

                var fingerprint = new SnapshotLeafFingerprint(
                    SnapshotLeafKind.File,
                    bytes.LongLength,
                    SHA256.HashData(bytes),
                    File.GetLastWriteTimeUtc(fullPath).Ticks);
                var result = new PinnedBuilderOutput(fullPath, fingerprint, bytes, stream);
                stream = null;
                return result;
            }
            finally
            {
                stream?.Dispose();
                if (!streamOwnsHandle) handle.Dispose();
            }
        }

        public void Dispose() => stream?.Dispose();
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(limit, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return output.ToString();
            var remaining = limit - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(read, remaining));
        }
    }

    private static int CompareRmkWorkbookCandidate(string left, string right, string workshopId)
    {
        var leftPreferred = !string.IsNullOrWhiteSpace(workshopId)
                            && Path.GetFileNameWithoutExtension(left).Contains(workshopId, StringComparison.Ordinal);
        var rightPreferred = !string.IsNullOrWhiteSpace(workshopId)
                             && Path.GetFileNameWithoutExtension(right).Contains(workshopId, StringComparison.Ordinal);
        if (leftPreferred != rightPreferred) return leftPreferred ? -1 : 1;
        var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(left), Path.GetFileName(right));
        return nameComparison != 0 ? nameComparison : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static string ReadText(string path, long maximumBytes, string label)
    {
        var bytes = BoundedFileReader.ReadAllBytes(path, maximumBytes, label);
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Utf8Strict.GetString(bytes, offset, bytes.Length - offset);
    }

    private static void CopyRmkSnapshotBounded(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(sourcePath);
        var maximumBytes = name.StartsWith("LoadFolders.xml", StringComparison.OrdinalIgnoreCase)
            ? MaximumLoadFoldersBytes
            : name.StartsWith("ModList.tsv", StringComparison.OrdinalIgnoreCase)
                ? MaximumMetadataBytes
                : throw new InvalidDataException($"Unexpected RMK snapshot target: {name}");
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (source.Length > maximumBytes)
            throw new InvalidDataException($"{name} exceeds the {maximumBytes:N0}-byte RMK snapshot limit.");
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        var remaining = maximumBytes;
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining + 1));
            if (read == 0) break;
            if (read > remaining)
                throw new InvalidDataException($"{name} grew beyond the {maximumBytes:N0}-byte RMK snapshot limit.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
        destination.Flush(true);
    }

    private static async Task TerminateProcessAsync(
        WindowsContainedProcess process,
        Task<string> stdout,
        Task<string> stderr)
    {
        Exception? terminationError = null;
        if (!process.HasExited)
        {
            try
            {
                process.TerminateTree();
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
            {
                terminationError = exception;
            }
        }

        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            terminationError ??= exception;
        }

        if (!process.HasExited)
            throw new IOException($"RMK Builder 프로세스를 종료하지 못했습니다 ({terminationError?.GetType().Name ?? "Unknown"}).", terminationError);

        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException)
        {
            System.Diagnostics.Debug.WriteLine($"RMK Builder output drain ended after termination: {exception.GetType().Name}");
        }
    }

    internal static bool IsSensitiveEnvironmentName(string name) =>
        name.Contains("KEY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Contains("AUTH", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase)
        || name.Contains("COOKIE", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PROXY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PROFILER", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("PATH", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PATH", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("COMSPEC", StringComparison.OrdinalIgnoreCase)
        || name.Equals("__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase)
        || name.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("JAVA_TOOL_OPTIONS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("_JAVA_OPTIONS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("JDK_JAVA_OPTIONS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PYTHONPATH", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PYTHONHOME", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PYTHONSTARTUP", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PSMODULEPATH", StringComparison.OrdinalIgnoreCase)
        || name.Equals("RUBYOPT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PERL5OPT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PERL5LIB", StringComparison.OrdinalIgnoreCase)
        || name.Equals("BASH_ENV", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("HOSTFXR_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("HOSTPOLICY_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("APPDOMAIN_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("NUGET", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("VSTEST", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("RIMWORLD_TRANSLATOR_", StringComparison.OrdinalIgnoreCase);

    private static string SafeFolderName(string value)
    {
        var result = string.Concat((value ?? string.Empty).Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(result)) result = "RimWorld Mod";
        result = result.Length > 100 ? result[..100].TrimEnd('.', ' ') : result;
        return PathSafety.IsSafeFileNameSegment(result) ? result : "_" + result;
    }

    private static string Yaml(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    private static Regex ValueRegex(string key) => new($"(?mi)^\\s*{Regex.Escape(key)}:\\s*[\"']?([^\"'\\s#]+|.+?)[\"']?\\s*$", RegexOptions.CultureInvariant);

    private sealed record BuilderStage(
        string Root,
        string Identity,
        RmkBuilderExecutionPlan Plan,
        string SourceSeal,
        SafeFileHandle RootLease,
        BuilderStageOwnership Ownership,
        BuilderStageExecutionBoundary? ExecutionBoundary);

    private sealed class BuilderStageOwnership(
        string stageName,
        string stageIdentity,
        string registrationPath,
        long registrationLength,
        byte[] registrationSha256)
    {
        public string StageName { get; } = stageName;
        public string StageIdentity { get; } = stageIdentity;
        public string RegistrationPath { get; } = registrationPath;
        public long RegistrationLength { get; } = registrationLength;
        public byte[] RegistrationSha256 { get; } = registrationSha256;
    }

    private sealed record BuilderStageInput(
        string SourcePath,
        string RelativePath,
        long MaximumBytes);

    private sealed record BuilderStageLayout(
        IReadOnlyList<string> RelativeDirectories,
        IReadOnlyList<BuilderStageInput> Inputs);

    private sealed record BuilderStageManifestPaths(
        IReadOnlyList<string> RelativeDirectories,
        IReadOnlyList<string> RelativeFiles);

    private sealed record BuilderStageSnapshot(
        IReadOnlyDictionary<string, string> Directories,
        IReadOnlyDictionary<string, SnapshotLeafFingerprint> Files,
        long AggregateBytes);

    private sealed record BuilderStageCandidate(string Name, string Path);

    private sealed record BuilderStagingCleanupReport(
        int PreservedCount,
        IReadOnlyList<string> SampleNames);

    private sealed class BuilderStageExecutionBoundary(
        string stageRoot,
        string stageIdentity,
        BuilderStageSnapshot expected,
        BuilderStageLayout expectedSemanticLayout,
        string stagedBuilderPath,
        PathSafety.TrustedWriteBoundary inputBoundary) : IDisposable
    {
        private PathSafety.TrustedWriteBoundary? boundary = inputBoundary;

        public void VerifyBeforeProcess(CancellationToken cancellationToken) =>
            Verify(allowBuilderOutputs: false, cancellationToken);

        public void VerifyAfterProcess(CancellationToken cancellationToken) =>
            Verify(allowBuilderOutputs: true, cancellationToken);

        private void Verify(
            bool allowBuilderOutputs,
            CancellationToken cancellationToken)
        {
            var currentBoundary = boundary
                ?? throw new ObjectDisposedException(
                    nameof(BuilderStageExecutionBoundary));
            cancellationToken.ThrowIfCancellationRequested();
            currentBoundary.VerifyUnchanged();
            var current = CaptureBuilderStageSnapshot(
                stageRoot,
                stageIdentity,
                cancellationToken);
            VerifyBuilderStageSnapshot(
                expected,
                current,
                allowBuilderOutputs,
                stageRoot);
            var currentSemanticLayout = CollectBuilderStageLayout(
                stageRoot,
                stagedBuilderPath,
                cancellationToken);
            if (!BuilderStageLayoutsMatch(
                    expectedSemanticLayout,
                    currentSemanticLayout))
            {
                throw new ConcurrentLeafChangeException(
                    "The isolated RMK Builder semantic layout changed across its execution boundary.",
                    stageRoot);
            }
            currentBoundary.VerifyUnchanged();
        }

        public void Dispose() =>
            Interlocked.Exchange(ref boundary, null)?.Dispose();
    }

    private sealed class BuilderSourcePublicationBoundary(
        RmkWorkspaceService owner,
        RmkBuilderExecutionPlan plan,
        string workspaceRoot,
        string expectedSeal,
        BuilderStageLayout layout,
        PathSafety.TrustedWriteBoundary inputBoundary) : IDisposable
    {
        private PathSafety.TrustedWriteBoundary? boundary = inputBoundary;

        public void VerifyUnchanged(CancellationToken cancellationToken)
        {
            var currentBoundary = boundary
                ?? throw new ObjectDisposedException(
                    nameof(BuilderSourcePublicationBoundary));
            cancellationToken.ThrowIfCancellationRequested();
            var currentRoot = owner.RequireWritableWorkspace(workspaceRoot);
            if (!currentRoot.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                || !PathSafety.GetExistingDirectoryIdentity(currentRoot)
                    .Equals(plan.WorkspaceIdentity, StringComparison.Ordinal))
            {
                throw new ConcurrentLeafChangeException(
                    "The confirmed RMK workspace changed before source publication completed.",
                    workspaceRoot);
            }
            currentBoundary.VerifyUnchanged();
            var confirmedLayout = CollectBuilderStageLayout(
                workspaceRoot,
                plan.BuilderPath,
                cancellationToken);
            if (!BuilderStageLayoutsMatch(layout, confirmedLayout))
                throw new ConcurrentLeafChangeException(
                    "RMK Builder inputs changed before staged indexes could be published.",
                    workspaceRoot);
            var currentSeal = CaptureBuilderSourceSeal(
                layout,
                cancellationToken);
            currentBoundary.VerifyUnchanged();
            if (!TryCompareHexSha256(expectedSeal, currentSeal))
                throw new ConcurrentLeafChangeException(
                    "RMK Builder inputs changed while the isolated Builder was running; staged indexes were discarded.",
                    workspaceRoot);
        }

        public void Dispose() =>
            Interlocked.Exchange(ref boundary, null)?.Dispose();
    }

    private sealed class BuilderStagingFileLease(FileStream stream) : IDisposable
    {
        private FileStream? lockStream = stream;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref lockStream, null);
            if (current is null) return;
            current.Dispose();
            // The content-independent lock file deliberately persists. A forced exit at
            // any point leaves a reusable exact regular file instead of a partial record.
        }
    }

    private sealed class BuilderStagingRootLease(
        string root,
        string identity,
        SafeFileHandle handle) : IDisposable
    {
        private SafeFileHandle? rootHandle = handle;

        public string Root { get; } = root;
        public string Identity { get; } = identity;

        public void VerifyUnchanged()
        {
            var current = rootHandle
                ?? throw new ObjectDisposedException(nameof(BuilderStagingRootLease));
            var information = PathSafety.WindowsPathHandle.GetIdentity(current);
            var formatted =
                $"{information.VolumeSerialNumber:x8}:{information.FileIndex:x16}";
            var finalPath = PathSafety.Normalize(
                PathSafety.WindowsPathHandle.GetFinalPath(current));
            if (!finalPath.Equals(Root, StringComparison.OrdinalIgnoreCase)
                || !formatted.Equals(Identity, StringComparison.Ordinal)
                || (information.FileAttributes
                    & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
                   != FileAttributes.Directory)
            {
                throw new ConcurrentLeafChangeException(
                    "The application-owned RMK Builder staging root changed while it was leased.",
                    Root);
            }
        }

        public void Dispose() =>
            Interlocked.Exchange(ref rootHandle, null)?.Dispose();
    }

    [GeneratedRegex("(\\d+\\.\\d+)", RegexOptions.CultureInvariant)] private static partial Regex VersionRegex();
    [GeneratedRegex("^\\d+\\.\\d+$", RegexOptions.CultureInvariant)] private static partial Regex VersionOnlyRegex();
    [GeneratedRegex("^run-[0-9a-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex BuilderStageDirectoryNameRegex();
    [GeneratedRegex("^(run-[0-9a-f]{32})\\.owner$", RegexOptions.CultureInvariant)] private static partial Regex BuilderStageOwnershipFileNameRegex();
    [GeneratedRegex("^\\.rimworld-ai-cleanup-[0-9a-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex BuilderStageQuarantineDirectoryNameRegex();
    [GeneratedRegex("^[0-9a-f]{8}:[0-9a-f]{16}$", RegexOptions.CultureInvariant)] private static partial Regex BuilderStageIdentityRegex();
}
