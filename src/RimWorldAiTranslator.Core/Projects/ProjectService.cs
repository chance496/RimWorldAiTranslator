using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Utilities;

namespace RimWorldAiTranslator.Core.Projects;

public sealed record ProjectRemovalResult(
    bool ProjectRecordRemoved,
    IReadOnlyList<string> CleanupFailures);

public sealed class ProjectService
{
    private readonly ProjectRepository repository;
    private readonly RimWorldModDiscoveryService discovery;
    private readonly ProjectCleanupService cleanup;
    internal Action<string>? BeforeModRootProbeTestHook { get; set; }

    public ProjectService(ProjectRepository repository, RimWorldModDiscoveryService discovery, ProjectCleanupService cleanup)
    {
        this.repository = repository;
        this.discovery = discovery;
        this.cleanup = cleanup;
    }

    public ProjectStoreDocument Load(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = repository.Load(cancellationToken);
        foreach (var project in document.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(project.SourceKind))
            {
                project.SourceKind = PathSafety.IsWorkshopContentPath(project.ModRoot) ? "Workshop" : "Folder";
            }
            if (string.IsNullOrWhiteSpace(project.SourceLanguageFolder))
            {
                project.SourceLanguageFolder = "Auto";
            }
            if (PathSafety.IsNetworkPath(project.ModRoot)) continue;
            BeforeModRootProbeTestHook?.Invoke(project.ModRoot);
            cancellationToken.ThrowIfCancellationRequested();
            if (!discovery.IsIsolated && !Directory.Exists(project.ModRoot)) continue;
            var resolved = discovery.ResolveContentRoot(project.ModRoot);
            if (discovery.IsIsolated && !Directory.Exists(resolved)) continue;
            if (resolved.Equals(project.ModRoot, StringComparison.OrdinalIgnoreCase)) continue;
            project.ModRoot = resolved;
            project.UpdatedAt = DateTimeOffset.Now.ToString("O");
        }
        return document;
    }

    public TranslationProject Upsert(ProjectStoreDocument document, RimWorldModInfo mod, string sourceLanguageFolder)
    {
        var root = discovery.ResolveContentRoot(mod.Path);
        var id = StableIdentity.ProjectId(root, mod.PackageId, mod.WorkshopId);
        var now = DateTimeOffset.Now.ToString("O");
        var project = document.Projects.FirstOrDefault(value => value.Id.Equals(id, StringComparison.Ordinal));
        if (project is null)
        {
            project = new TranslationProject
            {
                Id = id,
                CreatedAt = now,
                Runs = []
            };
            document.Projects.Add(project);
        }
        project.Name = mod.Name;
        project.ModRoot = root;
        project.SourceKind = mod.Source;
        project.PackageId = mod.PackageId;
        project.WorkshopId = mod.WorkshopId;
        project.SourceLanguageFolder = string.IsNullOrWhiteSpace(sourceLanguageFolder) ? "Auto" : sourceLanguageFolder;
        project.UpdatedAt = now;
        repository.Save(document);
        return project;
    }

    public void RegisterRun(ProjectStoreDocument document, TranslationProject project, string reviewRoot, string provider)
    {
        if (PathSafety.IsNetworkPath(reviewRoot))
            throw new InvalidDataException("Review runs must use a local path.");
        var full = Path.GetFullPath(reviewRoot);
        if (!PathSafety.IsStrictlyInside(full, repository.ReviewsRoot))
            throw new InvalidDataException("Review runs must remain inside the application review root.");
        PathSafety.EnsureNoReparsePoints(full, repository.ReviewsRoot);

        var auditRoot = Path.Combine(full, "_TranslationAudit");
        var languageRoot = Path.Combine(full, "Languages", "Korean");
        var comparisonFile = Directory.Exists(auditRoot)
            ? ReviewComparisonDocument.EnumerateCandidateFiles(auditRoot)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (!Directory.Exists(full)
            || !Directory.Exists(auditRoot)
            || !Directory.Exists(languageRoot)
            || string.IsNullOrWhiteSpace(comparisonFile)
            || !ContainsReviewRows(full, comparisonFile))
        {
            throw new InvalidDataException("Only a complete review run with comparison evidence can be registered.");
        }

        var now = DateTimeOffset.Now.ToString("O");
        project.LatestReviewRoot = full;
        project.LatestReviewAt = now;
        project.UpdatedAt = now;
        var existing = project.Runs.FirstOrDefault(run => run.ReviewRoot.Equals(full, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            project.Runs.Add(new ProjectRun { ReviewRoot = full, CreatedAt = now, Provider = provider });
        }
        ProjectCleanupService.WriteOwnershipMarker(project, full);
        repository.Save(document);
    }

    private static bool ContainsReviewRows(string reviewRoot, string comparisonFile)
    {
        try
        {
            return ReviewComparisonDocument.LoadExact(reviewRoot, comparisonFile).Rows.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    public void MarkApplied(ProjectStoreDocument document, TranslationProject project)
    {
        project.LastAppliedAt = DateTimeOffset.Now.ToString("O");
        project.UpdatedAt = project.LastAppliedAt;
        repository.Save(document);
    }

    public ProjectCleanupPlan GetRemovalPlan(TranslationProject project, IEnumerable<string> reviewRoots) =>
        cleanup.BuildPlan(project, reviewRoots);

    public ProjectRemovalResult Remove(
        ProjectStoreDocument document,
        TranslationProject project,
        ProjectCleanupPlan confirmedPlan)
    {
        var failures = cleanup.ValidateRemovalPlan(project, confirmedPlan).ToList();
        failures.AddRange(confirmedPlan.MarkerErrors);
        if (failures.Count > 0) return new ProjectRemovalResult(false, failures);

        var projectIndex = document.Projects.IndexOf(project);
        if (projectIndex < 0)
            throw new InvalidOperationException("The project selected for removal is no longer in the project store.");
        document.Projects.RemoveAt(projectIndex);
        try
        {
            repository.Save(document);
        }
        catch
        {
            document.Projects.Insert(projectIndex, project);
            throw;
        }

        var cleanupFailures = cleanup.Remove(project, confirmedPlan).ToList();
        return new ProjectRemovalResult(true, cleanupFailures);
    }
}
