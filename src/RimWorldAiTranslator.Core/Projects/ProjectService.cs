using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Utilities;

namespace RimWorldAiTranslator.Core.Projects;

public sealed class ProjectService
{
    private readonly ProjectRepository repository;
    private readonly RimWorldModDiscoveryService discovery;
    private readonly ProjectCleanupService cleanup;

    public ProjectService(ProjectRepository repository, RimWorldModDiscoveryService discovery, ProjectCleanupService cleanup)
    {
        this.repository = repository;
        this.discovery = discovery;
        this.cleanup = cleanup;
    }

    public ProjectStoreDocument Load()
    {
        var document = repository.Load();
        var changed = false;
        foreach (var project in document.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.SourceLanguageFolder))
            {
                project.SourceLanguageFolder = "Auto";
                changed = true;
            }
            if (!Directory.Exists(project.ModRoot)) continue;
            var resolved = discovery.ResolveContentRoot(project.ModRoot);
            if (resolved.Equals(project.ModRoot, StringComparison.OrdinalIgnoreCase)) continue;
            project.ModRoot = resolved;
            project.UpdatedAt = DateTimeOffset.Now.ToString("O");
            changed = true;
        }
        if (changed) repository.Save(document);
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
        project.PackageId = mod.PackageId;
        project.WorkshopId = mod.WorkshopId;
        project.SourceLanguageFolder = string.IsNullOrWhiteSpace(sourceLanguageFolder) ? "Auto" : sourceLanguageFolder;
        project.UpdatedAt = now;
        repository.Save(document);
        return project;
    }

    public void RegisterRun(ProjectStoreDocument document, TranslationProject project, string reviewRoot, string provider)
    {
        var full = Path.GetFullPath(reviewRoot);
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

    public void MarkApplied(ProjectStoreDocument document, TranslationProject project)
    {
        project.LastAppliedAt = DateTimeOffset.Now.ToString("O");
        project.UpdatedAt = project.LastAppliedAt;
        repository.Save(document);
    }

    public ProjectCleanupPlan GetRemovalPlan(TranslationProject project, IEnumerable<string> reviewRoots) =>
        cleanup.BuildPlan(project, reviewRoots);

    public IReadOnlyList<string> Remove(ProjectStoreDocument document, TranslationProject project, IEnumerable<string> reviewRoots)
    {
        var plan = cleanup.BuildPlan(project, reviewRoots);
        var failures = cleanup.Remove(project, reviewRoots, plan.SafePaths).ToList();
        failures.AddRange(plan.MarkerErrors);
        if (failures.Count > 0) return failures;
        document.Projects.Remove(project);
        repository.Save(document);
        return [];
    }
}
