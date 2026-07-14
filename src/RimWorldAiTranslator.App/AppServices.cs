using System.Collections.Concurrent;
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

internal sealed class AppServices : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonRecoveryNotice> recoveryNotices = new();
    private readonly ConcurrentQueue<PendingFileTransaction> pendingFileTransactions = new();
    private int disposalStarted;

    public AppServices(string? dataRoot = null, RimWorldModDiscoveryService? discovery = null)
        : this(dataRoot, discovery, loggerFactory: null, CancellationToken.None)
    {
    }

    internal AppServices(
        string? dataRoot,
        RimWorldModDiscoveryService? discovery,
        Func<string, AppLogger>? loggerFactory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContentRoot = FindContentRoot();
        Paths = new AppDataPaths(dataRoot ?? Environment.GetEnvironmentVariable("RIMWORLD_TRANSLATOR_DATA_ROOT"));
        Paths.EnsureExists();
        cancellationToken.ThrowIfCancellationRequested();
        Store = new AtomicJsonStore();
        Store.RecoveredFromBackup += (_, notice) => recoveryNotices.Enqueue(notice);
        ProjectRepository = new ProjectRepository(Store, Paths);
        ProjectStats = new ProjectStatsCacheRepository(Store, Paths);
        SettingsRepository = new SettingsRepository(Store, Paths);
        Discovery = discovery ?? new RimWorldModDiscoveryService();
        Extractor = new SourceExtractor(Path.Combine(ContentRoot, "rimworld-def-field-rules.txt"));
        LanguageFiles = new LanguageFileService();
        Projects = new ProjectService(ProjectRepository, Discovery, new ProjectCleanupService());
        Activity = new ActivityService(Store, Paths.Reviews);
        Reviews = new ReviewWorkspaceService(Store, Extractor, Paths.Reviews);
        ReviewSaves = new ReviewSaveCoordinator(Reviews);
        TranslationArtifacts = new TranslationRunArtifactService(Paths.Temp, Paths.Reviews);
        IsolationAcknowledgements = new IsolatedDiscoveryAcknowledgementService();
        RecoveryAuthority = new FileTransactionRecoveryAuthority(Paths.RecoveryAuthority);
        foreach (var pending in RecoveryAuthority.FindPending(cancellationToken: cancellationToken))
            pendingFileTransactions.Enqueue(pending);
        RmkWorkspace = new RmkWorkspaceService(RecoveryAuthority, Paths.RmkBuilderStaging);
        Apply = new ReviewApplyService(
            Store,
            LanguageFiles,
            Extractor,
            Paths.Reviews,
            RecoveryAuthority);
        Rmk = new RmkExportService(
            Store,
            LanguageFiles,
            Extractor,
            RmkWorkspace,
            Paths.Reviews,
            RecoveryAuthority);
        Diagnostics = new DiagnosticBundleService();
        Settings = LoadSettings(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ProjectStore = Projects.Load(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Open the persistent log only after recoverable state has loaded. If state
        // construction fails, no logger handle can escape from a failed constructor.
        Logger = (loggerFactory ?? (static path => new AppLogger(path)))(Paths.Logs)
            ?? throw new InvalidOperationException("The logger factory returned no logger.");
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
    public ActivityService Activity { get; }
    public ReviewWorkspaceService Reviews { get; }
    public ReviewSaveCoordinator ReviewSaves { get; }
    public TranslationRunArtifactService TranslationArtifacts { get; }
    public IsolatedDiscoveryAcknowledgementService IsolationAcknowledgements { get; }
    public FileTransactionRecoveryAuthority RecoveryAuthority { get; }
    public ReviewApplyService Apply { get; }
    public RmkExportService Rmk { get; }
    public RmkWorkspaceService RmkWorkspace { get; }
    public DiagnosticBundleService Diagnostics { get; }
    public AppLogger Logger { get; }
    public AppSettingsDocument Settings { get; private set; }
    public bool StoredSettingsCredentialCorrectionRequired { get; private set; }
    public ProjectStoreDocument ProjectStore { get; }
    public Dictionary<string, string> ApiKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<JsonRecoveryNotice> DrainRecoveryNotices()
    {
        var notices = new List<JsonRecoveryNotice>();
        while (recoveryNotices.TryDequeue(out var notice)) notices.Add(notice);
        return notices;
    }

    public IReadOnlyList<PendingFileTransaction> DrainPendingFileTransactions()
    {
        var pending = new List<PendingFileTransaction>();
        while (pendingFileTransactions.TryDequeue(out var transaction)) pending.Add(transaction);
        return pending;
    }

    public TranslationEngine CreateTranslationEngine() =>
        new(ContentRoot, Extractor, LanguageFiles, recoveryAuthority: RecoveryAuthority);

    public async Task SaveSettingsAsync(
        AppSettingsDocument settings,
        CancellationToken cancellationToken = default)
    {
        await SettingsRepository.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        Settings = settings;
        StoredSettingsCredentialCorrectionRequired =
            SettingsRepository.StoredCredentialCorrectionRequired();
    }

    public Task FlushSettingsAsync(CancellationToken cancellationToken = default) =>
        SettingsRepository.FlushAsync(cancellationToken);

    private AppSettingsDocument LoadSettings(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = SettingsRepository.Load(cancellationToken);
        StoredSettingsCredentialCorrectionRequired =
            SettingsRepository.UnsafeExtensionDataExcludedOnLastLoad
            || SettingsRepository.StoredCredentialCorrectionRequired();
        var defaults = ApiProviderCatalog.CreateDefaultSettings();
        foreach (var pair in defaults)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposalStarted, 1) != 0) return;
        DisposeSupportingServices();
        try { Logger.Dispose(); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Logger disposal failed ({exception.GetType().Name}).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposalStarted, 1) != 0) return;
        await Task.Run(DisposeSupportingServices).ConfigureAwait(false);
        try { await Logger.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Logger async disposal failed ({exception.GetType().Name}).");
        }
    }

    private void DisposeSupportingServices()
    {
        try { ReviewSaves.Dispose(); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Review save service disposal failed ({exception.GetType().Name}).");
        }
        try { SettingsRepository.Dispose(); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Settings service disposal failed ({exception.GetType().Name}).");
        }
    }
}
