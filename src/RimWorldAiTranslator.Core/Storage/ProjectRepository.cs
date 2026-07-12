using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Storage;

public sealed class ProjectRepository
{
    private readonly AtomicJsonStore store;
    private readonly AppDataPaths paths;

    public ProjectRepository(AtomicJsonStore store, AppDataPaths paths)
    {
        this.store = store;
        this.paths = paths;
    }

    public ProjectStoreDocument Load()
    {
        if (!store.Exists(paths.Projects))
        {
            return new ProjectStoreDocument();
        }

        var document = store.Read<ProjectStoreDocument>(paths.Projects)
            ?? throw new InvalidDataException("Project store is empty.");
        if (document.Version is not (1 or 2))
        {
            store.Block(paths.Projects);
            throw new InvalidDataException($"Unsupported project store version: {document.Version}");
        }

        if (document.Projects is null)
        {
            store.Block(paths.Projects);
            throw new InvalidDataException("Project store is missing the projects collection.");
        }

        store.Unblock(paths.Projects);
        return document;
    }

    public void Save(ProjectStoreDocument document)
    {
        document.Version = 2;
        document.UpdatedAt = DateTimeOffset.Now.ToString("O");
        store.Write(paths.Projects, document);
    }
}

public sealed class SettingsRepository
{
    private readonly AtomicJsonStore store;
    private readonly AppDataPaths paths;

    public SettingsRepository(AtomicJsonStore store, AppDataPaths paths)
    {
        this.store = store;
        this.paths = paths;
    }

    public AppSettingsDocument Load() =>
        store.Exists(paths.Settings)
            ? store.Read<AppSettingsDocument>(paths.Settings) ?? new AppSettingsDocument()
            : new AppSettingsDocument();

    public void Save(AppSettingsDocument settings)
    {
        settings.Version = 3;
        store.Write(paths.Settings, settings);
    }
}

public sealed class ReviewRepository
{
    private readonly AtomicJsonStore store;

    public ReviewRepository(AtomicJsonStore store)
    {
        this.store = store;
    }

    public ReviewDecisionDocument Load(string reviewRoot)
    {
        var path = GetDecisionPath(reviewRoot);
        return store.Exists(path)
            ? store.Read<ReviewDecisionDocument>(path) ?? new ReviewDecisionDocument { ReviewRoot = Path.GetFullPath(reviewRoot) }
            : new ReviewDecisionDocument { ReviewRoot = Path.GetFullPath(reviewRoot) };
    }

    public void Save(string reviewRoot, ReviewDecisionDocument decisions)
    {
        decisions.Version = 5;
        decisions.Sparse = true;
        decisions.ReviewRoot = Path.GetFullPath(reviewRoot);
        decisions.UpdatedAt = DateTimeOffset.Now.ToString("O");
        store.Write(GetDecisionPath(reviewRoot), decisions);
    }

    public static string GetDecisionPath(string reviewRoot) => Path.Combine(Path.GetFullPath(reviewRoot), "review-decisions.json");
}
