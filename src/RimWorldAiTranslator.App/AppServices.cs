using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Diagnostics;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Logging;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.App;

internal sealed class AppServices : IDisposable
{
    public AppServices(string? dataRoot = null)
    {
        ContentRoot = FindContentRoot();
        Paths = new AppDataPaths(dataRoot ?? Environment.GetEnvironmentVariable("RIMWORLD_TRANSLATOR_DATA_ROOT"));
        Paths.EnsureExists();
        Store = new AtomicJsonStore();
        ProjectRepository = new ProjectRepository(Store, Paths);
        ProjectStats = new ProjectStatsCacheRepository(Store, Paths);
        SettingsRepository = new SettingsRepository(Store, Paths);
        Discovery = new RimWorldModDiscoveryService();
        Extractor = new SourceExtractor(Path.Combine(ContentRoot, "rimworld-def-field-rules.txt"));
        LanguageFiles = new LanguageFileService();
        Projects = new ProjectService(ProjectRepository, Discovery, new ProjectCleanupService());
        Reviews = new ReviewWorkspaceService(Store, Extractor);
        Apply = new ReviewApplyService(Store, LanguageFiles, Extractor);
        Rmk = new RmkExportService(Store, LanguageFiles, Extractor);
        RmkWorkspace = new RmkWorkspaceService();
        Diagnostics = new DiagnosticBundleService();
        Logger = new AppLogger(Paths.Logs);
        Settings = LoadSettings();
        ProjectStore = Projects.Load();
    }

    public string ContentRoot { get; }
    public AppDataPaths Paths { get; }
    public AtomicJsonStore Store { get; }
    public ProjectRepository ProjectRepository { get; }
    public ProjectStatsCacheRepository ProjectStats { get; }
    public SettingsRepository SettingsRepository { get; }
    public RimWorldModDiscoveryService Discovery { get; }
    public SourceExtractor Extractor { get; }
    public LanguageFileService LanguageFiles { get; }
    public ProjectService Projects { get; }
    public ReviewWorkspaceService Reviews { get; }
    public ReviewApplyService Apply { get; }
    public RmkExportService Rmk { get; }
    public RmkWorkspaceService RmkWorkspace { get; }
    public DiagnosticBundleService Diagnostics { get; }
    public AppLogger Logger { get; }
    public AppSettingsDocument Settings { get; private set; }
    public ProjectStoreDocument ProjectStore { get; }
    public Dictionary<string, string> ApiKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TranslationEngine CreateTranslationEngine() => new(ContentRoot, Extractor, LanguageFiles);

    public void SaveSettings(AppSettingsDocument settings)
    {
        Settings = settings;
        SettingsRepository.Save(settings);
    }

    private AppSettingsDocument LoadSettings()
    {
        var settings = SettingsRepository.Load();
        var defaults = ApiProviderCatalog.CreateDefaultSettings();
        foreach (var pair in defaults)
        {
            if (!settings.ApiProviders.ContainsKey(pair.Key)) settings.ApiProviders[pair.Key] = pair.Value;
        }
        if (!ApiProviderCatalog.All.Any(profile => profile.Id.Equals(settings.ApiProviderId, StringComparison.OrdinalIgnoreCase)))
            settings.ApiProviderId = "Cerebras";
        settings.TextSize = Math.Clamp(settings.TextSize, 9, 16);
        return settings;
    }

    private static string FindContentRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var cursor = new DirectoryInfo(start);
            for (var depth = 0; depth < 8 && cursor is not null; depth++, cursor = cursor.Parent)
            {
                if (File.Exists(Path.Combine(cursor.FullName, "rimworld-def-field-rules.txt"))) return cursor.FullName;
            }
        }
        return AppContext.BaseDirectory;
    }

    public void Dispose() => Logger.Dispose();
}
