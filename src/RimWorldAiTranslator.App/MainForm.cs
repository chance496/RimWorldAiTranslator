using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.App.Dialogs;
using System.Text.Json;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Diagnostics;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Quality;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Validation;

namespace RimWorldAiTranslator.App;

internal sealed class MainForm : Form
{
    private readonly AppServices services;
    private readonly Panel header;
    private readonly Panel contentHost;
    private readonly ProjectDashboardControl dashboard;
    private readonly ActivityControl activity;
    private readonly SettingsControl settings;
    private readonly ReviewWorkspaceControl workspace;
    private readonly LoadingOverlay loading;
    private readonly WorkspaceLoadCover workspaceLoading;
    private readonly Button projectsNav;
    private readonly Button activityNav;
    private readonly Button settingsNav;
    private readonly Button commandNav;
    private readonly System.Windows.Forms.Timer autoSaveTimer;
    private ThemePalette theme;
    private IReadOnlyList<RimWorldModInfo> mods = [];
    private readonly Dictionary<string, ProjectCardStats> projectStats = new(StringComparer.Ordinal);
    private readonly ProjectStatsCacheDocument statsCache;
    private readonly SemaphoreSlim statsRefreshGate = new(1, 1);
    private readonly SemaphoreSlim projectStateGate = new(1, 1);
    private readonly object workflowSync = new();
    private readonly HashSet<Task> activeWorkflows = [];
    private readonly HashSet<Task> workflowObservers = [];
    private CancellationTokenSource formLifetimeCancellation = new();
    private bool acceptingWorkflows = true;
    private CancellationTokenSource? operationCancellation;
    private bool operationRunning;
    private bool startupComplete;
    private bool closing;
    private bool autoSaveRunning;
    private bool closeSaveInProgress;
    private bool closeSaveCompleted;
    private bool closePromptInProgress;
    private bool closeBarrierTimedOut;
    private bool forceCloseRequested;
    private bool servicesDisposed;
    private Task? closeCoordinatorTask;
    private Task? closeCoordinatorFaultObserver;
    private Task? closeBarrierTask;
    private Task? closeBarrierFaultObserver;
    private Task? serviceDisposalTask;
    private ReviewWorkspace? closeReviewWorkspace;
    private Exception? closePreparationError;
    private Func<CancellationToken, Task>? lastRetryOperation;
    private readonly string? isolationAcknowledgementPath;
    private readonly TimeSpan closeBarrierTimeout;
    private readonly Func<bool>? forceCloseConfirmation;
    private readonly Func<DialogResult>? activeOperationCloseConfirmation;
    private readonly Func<DialogResult>? unsavedCloseConfirmation;
    private readonly UiIoHooks ioHooks;
    private Exception? startupFailure;
    private bool startupPreparationInProgress;
    private bool preparedForFirstShow;
    private bool firstFrameRevealed;

    internal event EventHandler<MainFormStartupFailureEventArgs>? StartupFailed;

    public MainForm(
        string? dataRoot = null,
        RimWorldModDiscoveryService? discovery = null,
        string? isolationAcknowledgementPath = null,
        TimeSpan? closeBarrierTimeout = null,
        Func<bool>? forceCloseConfirmation = null,
        UiIoHooks? ioHooks = null,
        Func<DialogResult>? activeOperationCloseConfirmation = null,
        Func<DialogResult>? unsavedCloseConfirmation = null)
        : this(
            CreateCompatibilityServices(dataRoot, discovery, closeBarrierTimeout),
            projectStats: null,
            isolationAcknowledgementPath,
            closeBarrierTimeout,
            forceCloseConfirmation,
            ioHooks,
            activeOperationCloseConfirmation,
            unsavedCloseConfirmation,
            usePreloadedProjectStats: false)
    {
    }

    internal MainForm(
        AppServices services,
        ProjectStatsCacheDocument projectStats,
        string? isolationAcknowledgementPath = null,
        TimeSpan? closeBarrierTimeout = null,
        Func<bool>? forceCloseConfirmation = null,
        UiIoHooks? ioHooks = null,
        Func<DialogResult>? activeOperationCloseConfirmation = null,
        Func<DialogResult>? unsavedCloseConfirmation = null)
        : this(
            services,
            projectStats,
            isolationAcknowledgementPath,
            closeBarrierTimeout,
            forceCloseConfirmation,
            ioHooks,
            activeOperationCloseConfirmation,
            unsavedCloseConfirmation,
            usePreloadedProjectStats: true)
    {
    }

    private MainForm(
        AppServices services,
        ProjectStatsCacheDocument? projectStats,
        string? isolationAcknowledgementPath,
        TimeSpan? closeBarrierTimeout,
        Func<bool>? forceCloseConfirmation,
        UiIoHooks? ioHooks,
        Func<DialogResult>? activeOperationCloseConfirmation,
        Func<DialogResult>? unsavedCloseConfirmation,
        bool usePreloadedProjectStats)
    {
        var configuredCloseBarrierTimeout = closeBarrierTimeout ?? TimeSpan.FromSeconds(30);
        if (configuredCloseBarrierTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(closeBarrierTimeout));
        this.isolationAcknowledgementPath = isolationAcknowledgementPath;
        this.closeBarrierTimeout = configuredCloseBarrierTimeout;
        this.forceCloseConfirmation = forceCloseConfirmation;
        this.activeOperationCloseConfirmation = activeOperationCloseConfirmation;
        this.unsavedCloseConfirmation = unsavedCloseConfirmation;
        this.ioHooks = ioHooks ?? new UiIoHooks();
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        statsCache = usePreloadedProjectStats
            ? projectStats ?? throw new ArgumentNullException(nameof(projectStats))
            : services.ProjectStats.Load();
        foreach (var entry in statsCache.Entries)
            this.projectStats[entry.ProjectId] = CreateProjectCardStats(entry.Stats.ToStats());
        theme = ThemeManager.Create(services.Settings);
        Text = "RimWorld AI Translator";
        AccessibleName = "RimWorld AI Translator";
        AccessibleDescription = "RimWorld 모드의 원문, 번역문, 검토 상태를 관리하는 로컬 번역 도구";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(900, 600);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        Opacity = 0;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.ResizeRedraw,
            true);
        SuspendLayout();

        header = new BufferedPanel
        {
            Dock = DockStyle.Top,
            Height = 70,
            Tag = "header"
        };
        header.SuspendLayout();
        var title = Ui.Label("RimWorld AI Translator", 13f, FontStyle.Bold);
        title.ForeColor = theme.HeaderText;
        title.Tag = "header-text";
        title.SetBounds(28, 15, 300, 32);
        title.AutoSize = false;
        projectsNav = Ui.Button("프로젝트", "primary", 92);
        activityNav = Ui.Button("활동", null, 82);
        settingsNav = Ui.Button("설정", null, 82);
        projectsNav.TabIndex = 0;
        activityNav.TabIndex = 1;
        settingsNav.TabIndex = 2;
        projectsNav.SetBounds(350, 16, 92, 36);
        activityNav.SetBounds(450, 16, 82, 36);
        settingsNav.SetBounds(540, 16, 82, 36);
        projectsNav.Click += (_, _) => ShowDashboard();
        activityNav.Click += (_, _) => TryStartUiWorkflow("활동 기록 열기", ShowActivityAsync);
        settingsNav.Click += (_, _) => ShowSettings();
        commandNav = Ui.Button("명령  Ctrl+Shift+P", null, 156);
        commandNav.TabIndex = 3;
        commandNav.SetBounds(996, 16, 156, 36);
        commandNav.Click += (_, _) => ShowCommandPalette();
        var headerAccent = new Panel { Tag = "accent" };
        headerAccent.SetBounds(0, 67, Width, 3);
        headerAccent.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        header.Controls.AddRange([title, projectsNav, activityNav, settingsNav, commandNav, headerAccent]);
        header.Resize += (_, _) => commandNav.SetBounds(Math.Max(676, header.ClientSize.Width - 184), 16, 156, 36);

        contentHost = new BufferedPanel { Dock = DockStyle.Fill };
        contentHost.SuspendLayout();
        dashboard = new ProjectDashboardControl();
        activity = new ActivityControl();
        settings = new SettingsControl(services, TryStartUiWorkflow, TryStartDurableUiWorkflow, this.ioHooks);
        workspace = new ReviewWorkspaceControl(services, TryStartUiWorkflow, this.ioHooks);
        loading = new LoadingOverlay();
        workspaceLoading = new WorkspaceLoadCover();
        loading.CancelRequested += (_, _) => operationCancellation?.Cancel();
        loading.RetryRequested += (_, _) =>
        {
            var retry = lastRetryOperation;
            loading.HideOverlay();
            if (retry is not null) TryStartUiWorkflow("작업 다시 시도", retry);
        };
        loading.ReviewRequested += (_, _) => loading.HideOverlay();
        contentHost.Controls.AddRange([dashboard, activity, settings, workspace, loading, workspaceLoading]);
        Controls.AddRange([contentHost, header]);

        dashboard.CreateProjectRequested += (_, mod) => TryStartUiWorkflow("프로젝트 만들기", token => CreateProjectAsync(mod, token));
        dashboard.ChooseFolderRequested += (_, _) => TryStartUiWorkflow("모드 폴더 선택", ChooseModFolderAsync);
        dashboard.RefreshRequested += (_, _) => TryStartUiWorkflow("모드 목록 새로고침", token => RefreshModsAsync(true, token));
        dashboard.OpenProjectRequested += (_, project) => TryStartUiWorkflow("프로젝트 열기", token => OpenProjectAsync(project, token));
        dashboard.DeleteProjectRequested += (_, project) => TryStartUiWorkflow("프로젝트 삭제", token => DeleteProjectAsync(project, token));
        settings.SettingsSaved += (_, _) => TryStartUiWorkflow("설정 반영", SettingsSavedAsync);
        settings.AppearanceChanged += (_, _) => ApplyTheme();
        settings.DiagnosticsRequested += (_, _) => TryStartUiWorkflow("진단 번들 저장", ExportDiagnosticsAsync);
        workspace.BackRequested += (_, _) => ShowDashboard();
        workspace.OpenFolderRequested += (_, _) => TryStartUiWorkflow("모드 폴더 열기", OpenCurrentModFolderAsync);
        workspace.SourceRefreshRequested += (_, _) => TryStartUiWorkflow("원문 다시 분석", RefreshSourceAsync);
        workspace.AiTranslateRequested += (_, _) => TryStartUiWorkflow("AI 번역", TranslateAsync);
        workspace.StopRequested += (_, _) => operationCancellation?.Cancel();
        workspace.ApplyRequested += (_, request) => TryStartUiWorkflow("번역 적용", token => ApplyAsync(request, token));
        workspace.SaveRequested += (_, _) => TryStartUiWorkflow("검수 저장", token => SaveReviewAsync(true, token));
        workspace.QualityExportRequested += (_, _) => TryStartUiWorkflow("품질 보고서 저장", ExportQualityReportAsync);
        workspace.RmkBuildRequested += (_, _) => TryStartUiWorkflow("RMK 인덱스 빌드", BuildRmkWorkspaceAsync);
        services.Logger.MessageWritten += (_, line) => workspace.AppendLog(line);

        autoSaveTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        autoSaveTimer.Tick += (_, _) =>
        {
            if (!autoSaveRunning
                && !closing
                && !operationRunning
                && services.Settings.AutoSave
                && (workspace.HasPendingEditorChanges || workspace.Workspace?.Dirty == true))
            {
                autoSaveRunning = true;
                if (!TryStartUiWorkflow(
                        "자동 저장",
                        token => SaveReviewAsync(false, token, automatic: true),
                        () => autoSaveRunning = false))
                {
                    autoSaveRunning = false;
                }
            }
        };
        Shown += MainFormShown;
        FormClosing += MainFormClosing;
        ApplyTheme();
        ShowControl(dashboard);
        contentHost.ResumeLayout(false);
        header.ResumeLayout(false);
        ResumeLayout(false);
    }

    private static AppServices CreateCompatibilityServices(
        string? dataRoot,
        RimWorldModDiscoveryService? discovery,
        TimeSpan? closeBarrierTimeout)
    {
        if (closeBarrierTimeout is { } configuredTimeout && configuredTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(closeBarrierTimeout));
        return new AppServices(dataRoot, discovery);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if (modifiers == (Keys.Control | Keys.Shift) && key == Keys.P && !operationRunning) { ShowCommandPalette(); return true; }
        if (modifiers == Keys.Control && key == Keys.Home) { ShowDashboard(); return true; }
        if (modifiers == Keys.Alt && key == Keys.C && workspace.Visible && workspace.Workspace is not null)
        {
            workspace.SelectSideTab("비교");
            return true;
        }
        if (modifiers == Keys.Alt && key == Keys.Q && workspace.Visible && workspace.Workspace is not null)
        {
            workspace.SelectSideTab("품질");
            return true;
        }
        if (modifiers == Keys.Control && key == Keys.F)
        {
            if (workspace.Visible) workspace.FocusSearch();
            else if (dashboard.Visible) dashboard.FocusSearch();
            else return base.ProcessCmdKey(ref msg, keyData);
            return true;
        }
        if (workspace.Visible && workspace.HandleShortcut(keyData)) return true;
        if (modifiers == Keys.None && key == Keys.F5)
        {
            if (dashboard.Visible && !operationRunning) TryStartUiWorkflow("모드 목록 새로고침", token => RefreshModsAsync(true, token));
            else if (workspace.Visible && workspace.Project is not null && !operationRunning) TryStartUiWorkflow("원문 다시 분석", RefreshSourceAsync);
            return true;
        }
        if (modifiers == Keys.Shift && key == Keys.F9)
        {
            operationCancellation?.Cancel();
            return true;
        }
        if (modifiers == Keys.None && key == Keys.F9)
        {
            if (workspace.Visible && workspace.Project is not null && !operationRunning) TryStartUiWorkflow("AI 번역", TranslateAsync);
            return true;
        }
        if ((modifiers == Keys.None || modifiers == Keys.Shift) && key == Keys.F6)
        {
            FocusNextWorkRegion(modifiers == Keys.Shift ? -1 : 1);
            return true;
        }
        if (modifiers == Keys.None && key == Keys.Escape && dashboard.Visible)
        {
            if (dashboard.ClearSearchAndFocus()) return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void FocusNextWorkRegion(int direction)
    {
        if (workspace.Visible)
        {
            workspace.FocusNextWorkRegion(direction);
            return;
        }
        if (dashboard.Visible)
        {
            dashboard.FocusNextWorkRegion(direction);
            return;
        }

        SelectNextControl(
            ActiveControl,
            forward: direction >= 0,
            tabStopOnly: true,
            nested: true,
            wrap: true);
    }

    private void MainFormShown(object? sender, EventArgs e)
    {
        if (preparedForFirstShow) return;
        TryStartUiWorkflow("프로그램 시작", StartAsync);
    }

    internal async Task PrepareForFirstShowAsync(CancellationToken cancellationToken)
    {
        if (Visible)
            throw new InvalidOperationException("The main form must be prepared before it is shown.");

        try
        {
            await InitializeStartupAsync(cancellationToken);
            PrepareInitialLayout();
            preparedForFirstShow = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordStartupFailure(exception);
            throw;
        }
    }

    internal Task RevealPreparedFirstFrameAsync()
    {
        if (!preparedForFirstShow || !startupComplete)
            throw new InvalidOperationException("The main form is not ready to be revealed.");
        if (!Visible)
            throw new InvalidOperationException("The main form must be shown before its first frame is revealed.");
        if (firstFrameRevealed) return Task.CompletedTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? idleHandler = null;
        idleHandler = (_, _) =>
        {
            Application.Idle -= idleHandler;
            if (closing || IsDisposed)
            {
                completion.TrySetCanceled();
                return;
            }

            Enabled = true;
            Opacity = 1;
            firstFrameRevealed = true;
            completion.TrySetResult();
        };
        Application.Idle += idleHandler;
        return completion.Task;
    }

    internal void CompleteFirstShow()
    {
        if (!firstFrameRevealed || closing || IsDisposed) return;
        ShowRecoveryNotices();
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (startupComplete) return;
        try
        {
            await InitializeStartupAsync(cancellationToken);
            PrepareInitialLayout();
            preparedForFirstShow = true;
            if (Visible) await RevealPreparedFirstFrameAsync();
            CompleteFirstShow();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordStartupFailure(exception);
            if (Opacity < 1) Opacity = 1;
            ReportStartupFailure(exception);
        }
    }

    private async Task InitializeStartupAsync(CancellationToken cancellationToken)
    {
        if (startupComplete) return;
        if (startupPreparationInProgress)
            throw new InvalidOperationException("Startup preparation is already in progress.");

        startupPreparationInProgress = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ioHooks.MainFormStartup?.Invoke();
            mods = await services.Discovery.DiscoverAsync(cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshProjectStatsAsync(cancellationToken, updateDashboard: false);
            if (closing) return;
            UpdateDashboardProviderStatus();
            dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
            await LoadGlossaryAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(isolationAcknowledgementPath))
            {
                var acknowledgementPath = isolationAcknowledgementPath;
                var dataRoot = services.Paths.Root;
                await Task.Run(
                    () => services.IsolationAcknowledgements.Write(acknowledgementPath, dataRoot),
                    cancellationToken);
            }
            startupComplete = true;
            if (!closing)
            {
                autoSaveTimer.Start();
                services.Logger.Info($"프로그램 시작 · 프로젝트 {services.ProjectStore.Projects.Count:N0}개 · 모드 {mods.Count:N0}개");
            }
        }
        finally
        {
            startupPreparationInProgress = false;
        }
    }

    private void PrepareInitialLayout()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        _ = Handle;
        EnsureHandlesCreated(header);
        EnsureHandlesCreated(dashboard);
        PerformLayout();
    }

    private static void EnsureHandlesCreated(Control root)
    {
        _ = root.Handle;
        foreach (Control child in root.Controls)
            EnsureHandlesCreated(child);
    }

    private void RecordStartupFailure(Exception exception)
    {
        Interlocked.CompareExchange(ref startupFailure, exception, null);
        if (!servicesDisposed)
            services.Logger.Error(OperationErrorPresentation.CreateLogMessage("프로그램 시작", exception));
    }

    private async Task SettingsSavedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        services.Logger.Info("설정을 저장했습니다.");
        UpdateDashboardProviderStatus();
        await LoadGlossaryAsync(cancellationToken);
    }

    private async Task LoadGlossaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var generatedGlossaryPath = Path.Combine(services.ContentRoot, "glossary.generated.ko.json");
            var customGlossaryPath = services.Settings.CustomGlossaryPath;
            var terms = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var glossary = new GlossaryService();
                return glossary.Load(
                    generatedGlossaryPath,
                    customGlossaryPath,
                    !string.IsNullOrWhiteSpace(customGlossaryPath));
            }, cancellationToken);
            if (!closing) workspace.SetGlossary(terms);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!closing) workspace.SetGlossary([]);
            services.Logger.Warning(OperationErrorPresentation.CreateLogMessage("용어집 로드", ex));
        }
    }

    private async Task RefreshModsAsync(bool showOverlay, CancellationToken cancellationToken)
    {
        if (operationRunning) return;
        dashboard.SetBusy(true);
        if (showOverlay) loading.Show("모드 목록 새로고침", "Steam 라이브러리와 로컬 Mods 폴더를 확인하고 있습니다.", theme);
        try
        {
            mods = await services.Discovery.DiscoverAsync(cancellationToken: cancellationToken);
            if (closing) return;
            UpdateDashboardProviderStatus();
            dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
            await RefreshProjectStatsAsync(cancellationToken);
        }
        finally
        {
            if (CanUpdateUi())
            {
                dashboard.SetBusy(false);
                if (showOverlay) loading.HideOverlay();
            }
        }
    }

    private async Task RefreshProjectStatsAsync(
        CancellationToken cancellationToken,
        bool updateDashboard = true)
    {
        if (!await statsRefreshGate.WaitAsync(0, cancellationToken)) return;
        var cacheChanged = false;
        var projectGateAcquired = false;
        try
        {
            await projectStateGate.WaitAsync(cancellationToken);
            projectGateAcquired = true;
            var projectSnapshots = services.ProjectStore.Projects
                .Select(project => new ProjectStatsInput(project.Id, project.LatestReviewRoot))
                .ToArray();
            var activeIds = projectSnapshots.Select(project => project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            cacheChanged |= statsCache.Entries.RemoveAll(entry => !activeIds.Contains(entry.ProjectId)) > 0;
            foreach (var project in projectSnapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (closing || string.IsNullOrWhiteSpace(project.LatestReviewRoot)) continue;
                try
                {
                    var inspection = await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return IsLocalDirectory(project.LatestReviewRoot)
                            ? (Exists: true, Stamp: ProjectStatsCacheRepository.CreateStamp(project.LatestReviewRoot))
                            : (Exists: false, Stamp: string.Empty);
                    }, cancellationToken);
                    if (!inspection.Exists) continue;
                    var stamp = inspection.Stamp;
                    var cached = statsCache.Entries.FirstOrDefault(entry => entry.ProjectId.Equals(project.Id, StringComparison.OrdinalIgnoreCase));
                    if (cached is not null && cached.Stamp.Equals(stamp, StringComparison.Ordinal))
                    {
                        projectStats[project.Id] = CreateProjectCardStats(cached.Stats.ToStats());
                        continue;
                    }
                    var stats = await Task.Run(
                        () => services.Reviews.GetStats(services.Reviews.Load(project.LatestReviewRoot, cancellationToken: cancellationToken)),
                        cancellationToken);
                    projectStats[project.Id] = CreateProjectCardStats(stats);
                    if (cached is null)
                    {
                        cached = new ProjectStatsCacheEntry { ProjectId = project.Id };
                        statsCache.Entries.Add(cached);
                    }
                    cached.Stamp = stamp;
                    cached.Stats = ProjectStatsCacheValue.FromStats(stats);
                    cacheChanged = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"Project statistics refresh failed ({ex.GetType().Name}).");
                    projectStats[project.Id] = new ProjectCardStats(new ReviewWorkspaceStats(0, 0, 0, 0, 0, 0), "검수 통계 읽기 실패");
                }
            }
            if (cacheChanged && !closing)
            {
                var cacheSnapshot = CloneProjectStatsCache(statsCache);
                await Task.Run(() => services.ProjectStats.Save(cacheSnapshot), cancellationToken);
            }
            if (updateDashboard && !closing && dashboard.Visible)
                dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
        }
        finally
        {
            if (projectGateAcquired) projectStateGate.Release();
            statsRefreshGate.Release();
        }
    }

    private static ProjectCardStats CreateProjectCardStats(ReviewWorkspaceStats stats) => new(stats,
        $"전체 {stats.Total:N0} / 미번역 {stats.Pending:N0} / 번역됨 {stats.Translated:N0} / 검토됨 {stats.Approved:N0} / 변경 {stats.Updated:N0}");

    private void InvalidateProjectStats(string projectId)
    {
        projectStats.Remove(projectId);
        statsCache.Entries.RemoveAll(entry => entry.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<TranslationProject> UpsertProjectAsync(
        RimWorldModInfo mod,
        string sourceLanguageFolder,
        CancellationToken cancellationToken)
    {
        var projectId = await MutateProjectStoreAsync(
            document => services.Projects.Upsert(document, mod, sourceLanguageFolder).Id,
            cancellationToken);
        return services.ProjectStore.Projects.Single(project => project.Id.Equals(projectId, StringComparison.Ordinal));
    }

    private async Task<TranslationProject> RegisterProjectRunAsync(
        string projectId,
        string reviewRoot,
        string provider,
        CancellationToken cancellationToken)
    {
        await MutateProjectStoreAsync(
            document =>
            {
                var project = document.Projects.Single(value => value.Id.Equals(projectId, StringComparison.Ordinal));
                services.Projects.RegisterRun(document, project, reviewRoot, provider);
                return project.Id;
            },
            cancellationToken);
        return services.ProjectStore.Projects.Single(project => project.Id.Equals(projectId, StringComparison.Ordinal));
    }

    private async Task<TranslationProject> MarkProjectAppliedAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await MutateProjectStoreAsync(
            document =>
            {
                var project = document.Projects.Single(value => value.Id.Equals(projectId, StringComparison.Ordinal));
                services.Projects.MarkApplied(document, project);
                return project.Id;
            },
            cancellationToken);
        return services.ProjectStore.Projects.Single(project => project.Id.Equals(projectId, StringComparison.Ordinal));
    }

    private Task<ProjectRemovalResult> RemoveProjectAsync(
        string projectId,
        ProjectCleanupPlan confirmedPlan,
        CancellationToken cancellationToken) =>
        MutateProjectStoreAsync(
            document =>
            {
                var project = document.Projects.Single(value => value.Id.Equals(projectId, StringComparison.Ordinal));
                return services.Projects.Remove(document, project, confirmedPlan);
            },
            cancellationToken);

    private async Task<TResult> MutateProjectStoreAsync<TResult>(
        Func<ProjectStoreDocument, TResult> mutation,
        CancellationToken cancellationToken)
    {
        await projectStateGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = CloneProjectStore(services.ProjectStore);
            var result = await Task.Run(() => mutation(snapshot), cancellationToken);
            MergeProjectStore(services.ProjectStore, snapshot);
            return result;
        }
        finally
        {
            projectStateGate.Release();
        }
    }

    private async Task<ProjectStoreDocument> CaptureProjectStoreAsync(CancellationToken cancellationToken)
    {
        await projectStateGate.WaitAsync(cancellationToken);
        try { return CloneProjectStore(services.ProjectStore); }
        finally { projectStateGate.Release(); }
    }

    private static ProjectStoreDocument CloneProjectStore(ProjectStoreDocument source) => new()
    {
        Version = source.Version,
        UpdatedAt = source.UpdatedAt,
        Revision = source.Revision,
        ObservedContentSha256 = source.ObservedContentSha256,
        ObservedMissing = source.ObservedMissing,
        Projects = source.Projects.Select(CloneTranslationProject).ToList(),
        ExtensionData = CloneExtensionData(source.ExtensionData)
    };

    private static TranslationProject CloneTranslationProject(TranslationProject source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        ModRoot = source.ModRoot,
        SourceKind = source.SourceKind,
        PackageId = source.PackageId,
        WorkshopId = source.WorkshopId,
        SourceLanguageFolder = source.SourceLanguageFolder,
        LatestReviewRoot = source.LatestReviewRoot,
        LatestReviewAt = source.LatestReviewAt,
        LastAppliedAt = source.LastAppliedAt,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        Runs = source.Runs.Select(run => new ProjectRun
        {
            ReviewRoot = run.ReviewRoot,
            CreatedAt = run.CreatedAt,
            Provider = run.Provider,
            ExtensionData = CloneExtensionData(run.ExtensionData)
        }).ToList(),
        ExtensionData = CloneExtensionData(source.ExtensionData)
    };

    private static void MergeProjectStore(ProjectStoreDocument destination, ProjectStoreDocument source)
    {
        var existing = destination.Projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var merged = new List<TranslationProject>(source.Projects.Count);
        foreach (var snapshot in source.Projects)
        {
            if (!existing.TryGetValue(snapshot.Id, out var project))
            {
                merged.Add(CloneTranslationProject(snapshot));
                continue;
            }
            CopyTranslationProject(project, snapshot);
            merged.Add(project);
        }
        destination.Version = source.Version;
        destination.UpdatedAt = source.UpdatedAt;
        destination.Revision = source.Revision;
        destination.ObservedContentSha256 = source.ObservedContentSha256;
        destination.ObservedMissing = source.ObservedMissing;
        destination.Projects = merged;
        destination.ExtensionData = CloneExtensionData(source.ExtensionData);
    }

    private static void CopyTranslationProject(TranslationProject destination, TranslationProject source)
    {
        destination.Name = source.Name;
        destination.ModRoot = source.ModRoot;
        destination.SourceKind = source.SourceKind;
        destination.PackageId = source.PackageId;
        destination.WorkshopId = source.WorkshopId;
        destination.SourceLanguageFolder = source.SourceLanguageFolder;
        destination.LatestReviewRoot = source.LatestReviewRoot;
        destination.LatestReviewAt = source.LatestReviewAt;
        destination.LastAppliedAt = source.LastAppliedAt;
        destination.CreatedAt = source.CreatedAt;
        destination.UpdatedAt = source.UpdatedAt;
        destination.Runs = source.Runs.Select(run => new ProjectRun
        {
            ReviewRoot = run.ReviewRoot,
            CreatedAt = run.CreatedAt,
            Provider = run.Provider,
            ExtensionData = CloneExtensionData(run.ExtensionData)
        }).ToList();
        destination.ExtensionData = CloneExtensionData(source.ExtensionData);
    }

    private static Dictionary<string, JsonElement>? CloneExtensionData(Dictionary<string, JsonElement>? source)
    {
        if (source is null) return null;
        return source.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
    }

    private static ProjectStatsCacheDocument CloneProjectStatsCache(ProjectStatsCacheDocument source) => new()
    {
        Version = source.Version,
        Entries = source.Entries.Select(entry => new ProjectStatsCacheEntry
        {
            ProjectId = entry.ProjectId,
            Stamp = entry.Stamp,
            Stats = new ProjectStatsCacheValue
            {
                Total = entry.Stats.Total,
                Pending = entry.Stats.Pending,
                Translated = entry.Stats.Translated,
                Approved = entry.Stats.Approved,
                Updated = entry.Stats.Updated,
                Warnings = entry.Stats.Warnings
            }
        }).ToList()
    };

    private static AppSettingsDocument CloneSettings(AppSettingsDocument source) => new()
    {
        Version = source.Version,
        ThemeMode = source.ThemeMode,
        DesignPreset = source.DesignPreset,
        TextSize = source.TextSize,
        HighContrast = source.HighContrast,
        AutoSave = source.AutoSave,
        RmkWorkspaceRoot = source.RmkWorkspaceRoot,
        RmkUseExisting = source.RmkUseExisting,
        CustomGlossaryPath = source.CustomGlossaryPath,
        ApiProviderId = source.ApiProviderId,
        ApiProviders = source.ApiProviders.ToDictionary(
            pair => pair.Key,
            pair => new ApiProviderSettings
            {
                Name = pair.Value.Name,
                Url = pair.Value.Url,
                Model = pair.Value.Model,
                Temperature = pair.Value.Temperature,
                ExtensionData = CloneExtensionData(pair.Value.ExtensionData)
            },
            StringComparer.OrdinalIgnoreCase),
        ExtensionData = CloneExtensionData(source.ExtensionData)
    };

    private async Task CreateProjectAsync(RimWorldModInfo mod, CancellationToken cancellationToken)
    {
        var language = await SelectSourceLanguageAsync(mod, cancellationToken);
        if (language is null) return;
        var project = await UpsertProjectAsync(mod, language, cancellationToken);
        dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
        await GenerateSourceReviewAsync(project, "source", cancellationToken);
    }

    private async Task ChooseModFolderAsync(CancellationToken cancellationToken)
    {
        using var dialog = new FolderBrowserDialog { Description = "RimWorld 모드 폴더를 선택하세요." };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var selectedPath = dialog.SelectedPath;
        var info = await Task.Run(
            () => services.Discovery.GetModInfo(selectedPath, "Manual"),
            cancellationToken);
        if (info is null)
        {
            MessageBox.Show(this, "About.xml, Defs 또는 Languages가 있는 RimWorld 모드 폴더가 아닙니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!mods.Any(mod => mod.Path.Equals(info.Path, StringComparison.OrdinalIgnoreCase))) mods = mods.Append(info).OrderBy(mod => mod.Name).ToArray();
        await CreateProjectAsync(info, cancellationToken);
    }

    private async Task<string?> SelectSourceLanguageAsync(RimWorldModInfo mod, CancellationToken cancellationToken)
    {
        var modPath = mod.Path;
        var choices = await Task.Run(
            () => services.Discovery.GetSourceLanguageOptions(modPath),
            cancellationToken);
        if (choices.Count == 0) return "Auto";
        if (choices.Count == 1) return choices[0].Folder;
        using var dialog = new SourceLanguageDialog(mod.Name, choices);
        ThemeManager.Apply(dialog, theme, services.Settings.TextSize);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedFolder : null;
    }

    private async Task OpenProjectAsync(TranslationProject project, CancellationToken cancellationToken)
    {
        var latestReviewRoot = project.LatestReviewRoot;
        var modRoot = project.ModRoot;
        var paths = await Task.Run(
            () => (
                ReviewExists: !string.IsNullOrWhiteSpace(latestReviewRoot) && IsLocalDirectory(latestReviewRoot),
                ModExists: IsLocalDirectory(modRoot)),
            cancellationToken);
        if (paths.ReviewExists)
        {
            await LoadReviewAsync(project, latestReviewRoot, cancellationToken: cancellationToken);
            return;
        }
        if (!paths.ModExists)
        {
            MessageBox.Show(this, "저장된 모드 폴더를 찾을 수 없습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await GenerateSourceReviewAsync(project, "source", cancellationToken);
    }

    private static bool IsLocalDirectory(string path) =>
        !PathSafety.IsNetworkPath(path) && Directory.Exists(path);

    private bool IsLocalActionDirectory(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !PathSafety.IsNetworkPath(path)
        && (ioHooks.DirectoryExists?.Invoke(path) ?? Directory.Exists(path));

    private async Task LoadReviewAsync(
        TranslationProject project,
        string reviewRoot,
        bool showOverlay = true,
        CancellationToken cancellationToken = default)
    {
        if (showOverlay) workspaceLoading.ShowCover("프로젝트 구성 중", "문자열과 검수 상태를 한 번에 준비하고 있습니다.", theme);
        try
        {
            var loaded = await services.Reviews.LoadAsync(reviewRoot, project, cancellationToken);
            if (closing) return;
            ShowRecoveryNotices();
            workspace.SetWorkspace(project, loaded);
            ShowWorkspace();
            ApplyTheme();
            services.Logger.Info($"검수 작업 로드 · {loaded.Items.Count:N0}개 문자열 · 변경 {loaded.ChangedSources:N0}개");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var userDetail = OperationErrorPresentation.CreateUserDetail(ex);
            if (!servicesDisposed) services.Logger.Error(OperationErrorPresentation.CreateLogMessage("검수 작업 로드", ex));
            if (CanUpdateUi())
                MessageBox.Show(this, "검수 작업을 읽지 못했습니다. 원본과 백업은 보존됩니다.\n\n" + userDetail, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { if (showOverlay && CanUpdateUi()) workspaceLoading.HideCover(); }
    }

    private void ShowRecoveryNotices()
    {
        var notices = services.DrainRecoveryNotices();
        if (notices.Count == 0) return;

        var presentation = RecoveryNoticePresentation.Create(notices);
        services.Logger.Warning(presentation.LogMessage);
        MessageBox.Show(
            this,
            presentation.UserMessage,
            Text,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private async Task RefreshSourceAsync(CancellationToken cancellationToken)
    {
        if (workspace.Project is not { } project) return;
        await GenerateSourceReviewAsync(project, "source-refresh", cancellationToken);
    }

    private async Task GenerateSourceReviewAsync(
        TranslationProject project,
        string provider,
        CancellationToken cancellationToken)
    {
        var modRoot = project.ModRoot;
        if (!await Task.Run(() => IsLocalActionDirectory(modRoot), cancellationToken)) return;
        await SaveReviewAsync(false, cancellationToken, persistLoadMigration: true);
        await RunOperationAsync(
            "원문 분석 중",
            "모드 XML과 기존 번역을 읽고 검수 프로젝트를 준비합니다.",
            async token =>
            {
                var projectSnapshot = CloneTranslationProject(project);
                var useRmkReference = services.Settings.RmkUseExisting;
                var configuredRmkRoot = services.Settings.RmkWorkspaceRoot;
                var references = await Task.Run(
                    () => ResolveRmkReference(projectSnapshot, useRmkReference, configuredRmkRoot, token),
                    token);
                var options = new TranslationEngineOptions
                {
                    ModRoot = project.ModRoot,
                    SourceOnly = true,
                    ReviewOnly = true,
                    ReviewRoot = services.Paths.Reviews,
                    SourceLanguageFolder = project.SourceLanguageFolder,
                    ExistingLanguageRoot = Path.Combine(project.ModRoot, "Languages", "Korean"),
                    ReferenceLanguageRoots = references.Target is null ? [] : [references.Target.LanguageRoot],
                    ReferenceSourceWorkbook = references.Target?.WorkbookPath ?? string.Empty
                };
                var progress = new Progress<TranslationProgress>(UpdateOperationProgress);
                var result = await Task.Run(() => services.CreateTranslationEngine().RunAsync(options, progress, token), token);
                if (!await Task.Run(() => services.TranslationArtifacts.HasCompleteReview(result), token))
                {
                    services.Logger.Info("분석 가능한 번역 원문이 없어 기존 검수 실행을 유지합니다.");
                    return;
                }
                project = await RegisterProjectRunAsync(project.Id, result.ReviewRoot!, provider, token);
                InvalidateProjectStats(project.Id);
                await LoadReviewAsync(project, result.ReviewRoot!, false, token);
            }, cancellationToken, showOverlay: true, retry: retryToken => GenerateSourceReviewAsync(project, provider, retryToken));
    }

    private async Task TranslateAsync(CancellationToken cancellationToken)
    {
        if (workspace.Project is not { } project || workspace.Workspace is not { } currentWorkspace) return;
        workspace.SaveCurrentEditor(false);
        await SaveReviewAsync(false, cancellationToken, persistLoadMigration: true);
        var selection = settings.GetSelection();
        var validation = ProviderValidator.Validate(selection.Profile, selection.Settings, selection.Keys.Count);
        if (!validation.Valid)
        {
            MessageBox.Show(this, "API 설정을 확인하세요.\n\n" + string.Join("\n", validation.ErrorCodes), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowSettings();
            return;
        }
        var existingCount = currentWorkspace.Items.Count(item => !string.IsNullOrWhiteSpace(item.Decision.Text));
        const int batchSize = 40;
        var batchCount = Math.Max(1, (int)Math.Ceiling(currentWorkspace.Items.Count / (double)batchSize));
        var usesGoogle = selection.Keys.Count == 0 || selection.Profile.Id.Equals("Google", StringComparison.OrdinalIgnoreCase);
        var estimatedTokens = currentWorkspace.Items.Sum(item => (int)Math.Ceiling((item.Row.Source.Length + item.Row.Key.Length) / 3d) + 40) + batchCount * 1800;
        var providerText = selection.Keys.Count == 0 && !selection.Profile.Id.Equals("Google", StringComparison.OrdinalIgnoreCase)
            ? "Google 번역 (키 없음 대체)" : selection.Profile.Name;
        var modelText = usesGoogle ? "Google Translate" : selection.Settings.Model;
        var usage = usesGoogle ? "Google 번역 · API 토큰 추정 해당 없음" : $"약 {estimatedTokens:N0} ~ {(int)Math.Ceiling(estimatedTokens * 1.35):N0} 입력 토큰";
        var sourceLanguage = string.IsNullOrWhiteSpace(project.SourceLanguageFolder) ? "Auto" : project.SourceLanguageFolder;
        var effectiveEndpoint = usesGoogle
            ? selection.Profile.Id.Equals("Google", StringComparison.OrdinalIgnoreCase)
              && !string.IsNullOrWhiteSpace(selection.Settings.Url)
                ? selection.Settings.Url
                : ApiProviderCatalog.Get("Google").Url
            : selection.Settings.Url;
        var endpointUri = new Uri(effectiveEndpoint, UriKind.Absolute);
        var endpointHost = endpointUri.IsDefaultPort ? endpointUri.IdnHost : $"{endpointUri.IdnHost}:{endpointUri.Port}";
        using var dialog = new TranslationModeDialog(new TranslationPreflightInfo(
            project.Name,
            sourceLanguage,
            providerText,
            modelText,
            currentWorkspace.Items.Count,
            batchCount,
            usage,
            existingCount > 0,
            endpointHost));
        ThemeManager.Apply(dialog, theme, services.Settings.TextSize);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var catalogEndpoint = usesGoogle ? ApiProviderCatalog.Get("Google").Url : selection.Profile.Url;
        var customOrigin = selection.Profile.Id.Equals("Custom", StringComparison.OrdinalIgnoreCase)
                           || !HasSameOrigin(effectiveEndpoint, catalogEndpoint);
        if (customOrigin
            && MessageBox.Show(
                this,
                $"사용자 지정 전송 대상 {endpointHost}로 원문, 요청 문맥과 선택한 용어집이 전송됩니다. API 키가 있으면 Authorization 헤더도 이 대상에 전송됩니다.\n\n이 대상의 개인정보·보안·비용 정책을 확인했고 계속할까요?",
                "사용자 지정 API 대상 확인",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }
        var mode = dialog.Mode;
        string preservePath = string.Empty;
        if (mode == TranslationStartMode.MissingOnly)
            preservePath = await services.TranslationArtifacts.CreatePreservedTranslationsAsync(currentWorkspace, cancellationToken);
        try
        {
            await RunOperationAsync(
                "초벌 번역 실행",
                $"{providerText} · {sourceLanguage} 원문 · 검수 프로젝트에만 저장",
                async token =>
                {
                    var projectSnapshot = CloneTranslationProject(project);
                    var useRmkReference = services.Settings.RmkUseExisting;
                    var configuredRmkRoot = services.Settings.RmkWorkspaceRoot;
                    var references = await Task.Run(
                        () => ResolveRmkReference(projectSnapshot, useRmkReference, configuredRmkRoot, token),
                        token);
                    var options = BuildTranslationOptions(project, selection, mode, preservePath, references.Target);
                    var progress = new Progress<TranslationProgress>(value =>
                    {
                        var safeProgress = OperationErrorPresentation.CreateSafeProgress(value);
                        UpdateOperationProgress(safeProgress);
                        if (safeProgress.IsWarning) services.Logger.Warning(safeProgress.Message);
                    });
                    TranslationRunResult result;
                    try
                    {
                        result = await Task.Run(() => services.CreateTranslationEngine().RunAsync(options, progress, token), token);
                    }
                    catch (TranslationRunCanceledException ex)
                    {
                        var hasPartialReview = ex.CheckpointPersisted
                            && await Task.Run(
                                () => services.TranslationArtifacts.HasCompleteReview(ex.PartialResult),
                                CancellationToken.None);
                        if (hasPartialReview)
                        {
                            var partialProvider = selection.Keys.Count == 0 ? "Google" : selection.Profile.Id;
                            project = await RegisterProjectRunAsync(
                                project.Id,
                                ex.PartialResult.ReviewRoot!,
                                partialProvider,
                                CancellationToken.None);
                            InvalidateProjectStats(project.Id);
                            await LoadReviewAsync(project, ex.PartialResult.ReviewRoot!, false, CancellationToken.None);
                            services.Logger.Warning($"취소된 번역의 부분 검수 결과를 보존했습니다 · 번역 {ex.PartialResult.TranslatedEntries:N0}개");
                        }
                        throw;
                    }
                    if (selection.DryRun)
                    {
                        services.Logger.Info($"Dry-run 완료 · 원문 {result.SourceEntries:N0}개 · 검토 대상 {result.ReviewEntries:N0}개 · 기존 검수 실행 유지");
                        return;
                    }
                    if (!await Task.Run(() => services.TranslationArtifacts.HasCompleteReview(result), token))
                    {
                        services.Logger.Info("번역할 원문이 없어 기존 검수 실행을 유지합니다.");
                        return;
                    }
                    var usedProvider = selection.Keys.Count == 0 ? "Google" : selection.Profile.Id;
                    project = await RegisterProjectRunAsync(project.Id, result.ReviewRoot!, usedProvider, token);
                    InvalidateProjectStats(project.Id);
                    await LoadReviewAsync(project, result.ReviewRoot!, false, token);
                    services.Logger.Info($"번역 완료 · {result.TranslatedEntries:N0}개 · 주의 {result.TokenWarnings + result.SkippedUnsafe:N0}개");
                }, cancellationToken, showOverlay: true, retry: TranslateAsync);
        }
        finally
        {
            try { await services.TranslationArtifacts.DeletePreservedTranslationsAsync(preservePath, CancellationToken.None); }
            catch (Exception ex) { services.Logger.Warning("임시 번역 보존 파일을 정리하지 못했습니다: " + ex.GetType().Name); }
        }
    }

    private static bool HasSameOrigin(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
            || !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
        {
            return false;
        }
        return leftUri.Scheme.Equals(rightUri.Scheme, StringComparison.OrdinalIgnoreCase)
               && leftUri.IdnHost.Equals(rightUri.IdnHost, StringComparison.OrdinalIgnoreCase)
               && leftUri.Port == rightUri.Port;
    }

    private TranslationEngineOptions BuildTranslationOptions(
        TranslationProject project,
        TranslationProviderSelection selection,
        TranslationStartMode mode,
        string preservePath,
        RmkTarget? reference)
    {
        var profile = selection.Profile;
        return new TranslationEngineOptions
        {
            ModRoot = project.ModRoot,
            ApiKeys = selection.Keys,
            Provider = profile,
            ProviderSettings = selection.Settings,
            SourceLanguageFolder = project.SourceLanguageFolder,
            ReviewOnly = true,
            ReviewRoot = services.Paths.Reviews,
            ExistingLanguageRoot = Path.Combine(project.ModRoot, "Languages", "Korean"),
            ReferenceLanguageRoots = reference is null ? [] : [reference.LanguageRoot],
            ReferenceSourceWorkbook = reference?.WorkbookPath ?? string.Empty,
            PreserveTranslationFile = preservePath,
            TranslateMissingOnly = mode == TranslationStartMode.MissingOnly,
            Overwrite = mode == TranslationStartMode.Overwrite,
            DryRun = selection.DryRun,
            BatchSize = 40,
            MaxGeneratedGlossaryTermsPerBatch = 40,
            RequestsPerMinutePerKey = profile.RequestsPerMinute,
            InputTokensPerMinutePerKey = profile.InputTokensPerMinute,
            DailyTokenBudgetPerKey = profile.DailyTokens,
            MaxCompletionTokens = profile.MaxOutputTokens > 0 ? profile.MaxOutputTokens : 16_000,
            CompletionTokenParameter = profile.TokenParameter,
            ResponseFormatMode = profile.ResponseFormat,
            ReasoningEffort = profile.ReasoningEffort,
            UseCuratedGlossary = !string.IsNullOrWhiteSpace(services.Settings.CustomGlossaryPath),
            CuratedGlossaryPath = services.Settings.CustomGlossaryPath
        };
    }

    private async Task ApplyAsync(WorkspaceApplyRequest request, CancellationToken cancellationToken)
    {
        if (workspace.Project is not { } project || workspace.Workspace is not { } review) return;
        workspace.SaveCurrentEditor(false);
        await SaveReviewAsync(false, cancellationToken, persistLoadMigration: true);
        var label = request.UseRmk ? "RMK에 적용" : "모드에 적용";
        var appliedAny = false;
        var openSettingsAfterOperation = false;

        await RunOperationAsync(label + " 미리보기", "대상, 상태, 원문 변경, 토큰과 경로를 쓰기 전에 검사합니다.", async token =>
        {
            if (request.UseRmk)
            {
                var resolution = await ResolveWritableRmkTargetAsync(project, token);
                var target = resolution.Target;
                openSettingsAfterOperation = resolution.SettingsNeeded;
                if (target is null) return;
                RmkExportOptions Options(bool dryRun, string? expectedPlanFingerprint = null) => new()
                {
                    RmkWorkspaceRoot = target.SourceRoot,
                    RmkEntryRoot = target.Root,
                    ReviewRoot = review.ReviewRoot,
                    ModRoot = project.ModRoot,
                    RmkLanguageFolderName = "Korean (한국어)",
                    WorkbookPath = target.WorkbookPath,
                    SourceLanguage = project.SourceLanguageFolder == "Auto" ? "English" : project.SourceLanguageFolder,
                    ApplyStatus = request.Status,
                    Overwrite = true,
                    DryRun = dryRun,
                    ExpectedPlanFingerprint = expectedPlanFingerprint
                };
                var preview = await Task.Run(() => services.Rmk.Export(Options(true), token), token);
                if (CanUpdateUi()) ShowRecoveryNotices();
                if (preview.EligibleEntries == 0)
                {
                    MessageBox.Show(this,
                        $"적용 가능한 번역이 없습니다.\n\n원문 변경 {preview.SkippedSourceChanged:N0}개 · 안전 검사 제외 {preview.SkippedUnsafe:N0}개 · 상태 제외 {preview.SkippedStatus:N0}개 · 매핑 제외 {preview.SkippedUnmapped:N0}개 · 중복 키 제외 {preview.SkippedAmbiguous:N0}개",
                        label, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var answer = MessageBox.Show(this,
                    $"다음 RMK 작업 클론 대상에 적용할까요?\n\n대상: {target.LanguageRoot}\nWorkbook: {target.WorkbookPath}\n번역: {preview.EligibleEntries:N0}개\nXML 파일: {preview.WrittenFiles:N0}개\n원문 변경 제외: {preview.SkippedSourceChanged:N0}개\n안전 검사 제외: {preview.SkippedUnsafe:N0}개\n상태 제외: {preview.SkippedStatus:N0}개\n매핑 제외: {preview.SkippedUnmapped:N0}개\n중복 키 제외: {preview.SkippedAmbiguous:N0}개",
                    label, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
                var result = await Task.Run(
                    () => services.Rmk.Export(Options(false, preview.PlanFingerprint), token),
                    token);
                appliedAny = result.EligibleEntries > 0;
                services.Logger.Info($"RMK 적용 완료 · XML {result.WrittenFiles:N0}개 · 번역 {result.EligibleEntries:N0}개");
            }
            else
            {
                ReviewApplyOptions Options(bool dryRun, string? expectedPlanFingerprint = null) => new()
                {
                    ModRoot = project.ModRoot,
                    ReviewRoot = review.ReviewRoot,
                    SourceKind = project.SourceKind,
                    SourceLanguageFolder = project.SourceLanguageFolder,
                    ApplyStatus = request.Status,
                    Overwrite = true,
                    DryRun = dryRun,
                    ExpectedPlanFingerprint = expectedPlanFingerprint
                };
                var preview = await Task.Run(() => services.Apply.Apply(Options(true), token), token);
                if (CanUpdateUi()) ShowRecoveryNotices();
                if (preview.AppliedEntries == 0)
                {
                    MessageBox.Show(this,
                        $"적용 가능한 번역이 없습니다.\n\n원문 변경 {preview.SkippedSourceChanged:N0}개 · 안전 검사 제외 {preview.SkippedUnsafe:N0}개 · 상태 제외 {preview.SkippedNotApproved:N0}개 · 빈 번역 제외 {preview.SkippedBlank:N0}개 · 매핑 제외 {preview.SkippedUnmapped:N0}개 · 기존 항목 제외 {preview.SkippedExistingEntries:N0}개",
                        label, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var target = Path.Combine(project.ModRoot, "Languages", "Korean");
                var answer = MessageBox.Show(this,
                    $"다음 로컬 모드 대상에 적용할까요?\n\n대상: {target}\n번역: {preview.AppliedEntries:N0}개\n파일: {preview.WrittenFiles:N0}개\n원문 변경 제외: {preview.SkippedSourceChanged:N0}개\n안전 검사 제외: {preview.SkippedUnsafe:N0}개\n상태 제외: {preview.SkippedNotApproved:N0}개\n빈 번역 제외: {preview.SkippedBlank:N0}개\n매핑 제외: {preview.SkippedUnmapped:N0}개\n기존 항목 제외: {preview.SkippedExistingEntries:N0}개",
                    label, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
                var result = await Task.Run(
                    () => services.Apply.Apply(Options(false, preview.PlanFingerprint), token),
                    token);
                appliedAny = result.AppliedEntries > 0;
                services.Logger.Info($"모드 적용 완료 · 파일 {result.WrittenFiles:N0}개 · 번역 {result.AppliedEntries:N0}개");
            }
            if (!appliedAny) return;
            project = await FinalizeCommittedOperationAsync(
                label,
                finalizationToken => MarkProjectAppliedAsync(project.Id, finalizationToken),
                token);
            projectStats.Remove(project.Id);
            if (CanUpdateUi())
                MessageBox.Show(this, label + "이 완료되었습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, cancellationToken, showOverlay: true, retry: retryToken => ApplyAsync(request, retryToken), completionPredicate: () => appliedAny);
        if (openSettingsAfterOperation) ShowSettings();
    }

    private (RmkTarget? Target, string Version) ResolveRmkReference(
        TranslationProject project,
        bool useExisting,
        string configuredRoot,
        CancellationToken cancellationToken)
    {
        if (!useExisting) return (null, "1.6");
        cancellationToken.ThrowIfCancellationRequested();
        var steamRoots = services.Discovery.GetSteamLibraryRoots();
        var version = services.RmkWorkspace.GetRimWorldVersion(project.ModRoot, steamRoots);
        var roots = new List<(string Path, string Kind)>();
        var workspaceRoot = ResolveConfiguredOrDetectedRmkWorkspace(
            steamRoots,
            configuredRoot,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(workspaceRoot)) roots.Add((workspaceRoot, "작업 클론"));
        var subscription = services.RmkWorkspace.FindSubscriptionRoot(steamRoots);
        if (!string.IsNullOrWhiteSpace(subscription)
            && !subscription.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase)) roots.Add((subscription, "구독본"));
        foreach (var root in roots)
        {
            var targets = services.RmkWorkspace.FindTargets(root.Path, project, root.Kind);
            var target = services.RmkWorkspace.SelectTarget(targets, version);
            if (target is not null && Directory.Exists(target.LanguageRoot)) return (target, version);
        }
        return (null, version);
    }

    private async Task<(RmkTarget? Target, bool SettingsNeeded)> ResolveWritableRmkTargetAsync(
        TranslationProject project,
        CancellationToken cancellationToken)
    {
        var configuredRoot = services.Settings.RmkWorkspaceRoot;
        var projectSnapshot = CloneTranslationProject(project);
        var resolved = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamRoots = services.Discovery.GetSteamLibraryRoots();
            var root = ResolveConfiguredOrDetectedRmkWorkspace(
                steamRoots,
                configuredRoot,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(root)) return (Root: string.Empty, Version: string.Empty, Target: (RmkTarget?)null, AutoDetected: false);
            var version = services.RmkWorkspace.GetRimWorldVersion(projectSnapshot.ModRoot, steamRoots);
            var target = services.RmkWorkspace.SelectTarget(services.RmkWorkspace.FindTargets(root, projectSnapshot), version);
            var autoDetected = !root.Equals(configuredRoot, StringComparison.OrdinalIgnoreCase);
            return (Root: root, Version: version, Target: target, AutoDetected: autoDetected);
        }, cancellationToken);

        var root = resolved.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            MessageBox.Show(this, "설정에서 Data, ModList.tsv와 .git이 있는 RMK Git 작업 클론을 선택하세요.", "RMK 작업 폴더", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return (null, true);
        }
        if (resolved.AutoDetected)
        {
            services.Settings.RmkWorkspaceRoot = root;
            await services.SaveSettingsAsync(services.Settings, CancellationToken.None);
        }
        if (resolved.Target is not null) return (resolved.Target, false);
        var answer = MessageBox.Show(this, "RMK 작업 클론에 이 모드의 항목이 없습니다. 새 항목을 만들까요?", "RMK 항목 만들기", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return (null, false);
        var created = await Task.Run(
            () => services.RmkWorkspace.CreateTarget(root, projectSnapshot, resolved.Version, cancellationToken),
            cancellationToken);
        return (created, false);
    }

    private string ResolveConfiguredOrDetectedRmkWorkspace(
        IReadOnlyList<string> steamRoots,
        string configured,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(configured) && services.RmkWorkspace.IsWritableWorkspace(configured))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Path.GetFullPath(configured);
        }
        var localContainers = services.Discovery.GetModContainers(steamRoots)
            .Where(container => container.Source.Equals("Local", StringComparison.OrdinalIgnoreCase))
            .Select(container => container.Path);
        var detected = services.RmkWorkspace.FindWritableWorkspace(localContainers, cancellationToken);
        return string.IsNullOrWhiteSpace(detected) ? string.Empty : detected;
    }

    private async Task SaveReviewAsync(
        bool announce,
        CancellationToken cancellationToken,
        bool persistLoadMigration = false,
        bool automatic = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workspace.SaveCurrentEditor(false);
        if (workspace.Workspace is not { } review) return;
        if (!review.Dirty && !((announce || persistLoadMigration) && review.MigrationPending))
        {
            if (announce) workspace.SetSaveSucceeded(false);
            return;
        }
        var project = workspace.Project;
        workspace.SetSaveInProgress(automatic);
        try
        {
            if (!review.Dirty && review.MigrationPending && (announce || persistLoadMigration))
            {
                await services.Reviews.SaveAsync(review, cancellationToken);
            }
            do
            {
                await services.ReviewSaves.SaveAsync(review, cancellationToken);
                if (!ReferenceEquals(workspace.Workspace, review)) break;
                workspace.SaveCurrentEditor(false);
            }
            while (review.Dirty);
            if (ReferenceEquals(workspace.Workspace, review)) workspace.SetSaveSucceeded(automatic);
            if (announce) services.Logger.Info("검수 상태를 저장했습니다.");
            if (project is not null) InvalidateProjectStats(project.Id);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(workspace.Workspace, review)) workspace.SetSavePending();
            throw;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(workspace.Workspace, review)) workspace.SetSaveFailed(automatic);
            services.Logger.Error("검수 저장 실패 (" + ex.GetType().Name + ")");
            throw;
        }
    }

    private static async Task<TResult> FinalizeCommittedOperationAsync<TResult>(
        string operationLabel,
        Func<CancellationToken, Task<TResult>> finalizer,
        CancellationToken postCommitCancellation)
    {
        ArgumentNullException.ThrowIfNull(finalizer);
        _ = postCommitCancellation;
        try
        {
            // The target-file transaction has already committed. Finalizing local
            // project metadata is therefore durable cleanup and must not be revoked
            // by a cancellation that arrived after the commit point.
            return await finalizer(CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new CommittedOperationFinalizationException(operationLabel, exception);
        }
    }

    private async Task BuildRmkWorkspaceAsync(CancellationToken cancellationToken)
    {
        await BuildRmkWorkspaceAsync(services.Settings.RmkWorkspaceRoot, cancellationToken);
    }

    private async Task BuildRmkWorkspaceAsync(string root, CancellationToken cancellationToken)
    {
        var workspaceProbe = ioHooks.IsWritableRmkWorkspace ?? services.RmkWorkspace.IsWritableWorkspace;
        if (!await Task.Run(() => workspaceProbe(root), cancellationToken))
        {
            MessageBox.Show(
                this,
                "설정에서 Data, ModList.tsv와 .git이 있고 bus 브랜치인 RMK 작업 클론을 선택하세요.",
                "RMK Builder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            ShowSettings();
            return;
        }

        RmkBuilderExecutionPlan? executionPlan = null;
        if (ioHooks.BuildRmkWorkspace is null)
        {
            executionPlan = await Task.Run(() => services.RmkWorkspace.CreateBuildPlan(root), cancellationToken);
            var confirmation = MessageBox.Show(
                this,
                $"이 작업은 선택한 RMK 작업 클론의 실행 파일을 시작합니다.\n\n실행 파일(정규 물리 경로): {executionPlan.BuilderPath}\nSHA-256: {executionPlan.BuilderSha256}\n크기: {executionPlan.BuilderLength:N0} bytes\n\n앱은 확인한 EXE를 정지 상태로 만든 뒤 프로세스 트리 종료 경계에 편입하고, 편입에 성공한 경우에만 실행합니다. 관리자 권한을 요청하거나 셸을 사용하지 않습니다.\n\n이 보호는 샌드박스가 아닙니다. Builder는 현재 사용자 권한으로 파일과 네트워크에 접근할 수 있습니다. SHA-256은 위 EXE 하나만 고정하며, 인접 DLL/config와 작업 클론 내용은 고정하지 않습니다. 이 작업 클론 전체를 신뢰할 때만 실행하세요.\n\n계속할까요?",
                "RMK Builder 실행 확인",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes) return;
        }

        RmkBuildResult? result = null;
        await RunOperationAsync(
            "RMK 인덱스 빌드 중",
            "LoadFolders.xml과 ModList.tsv를 백업한 뒤 검증된 작업 클론에서 Builder를 실행합니다.",
            async token =>
            {
                result = ioHooks.BuildRmkWorkspace is { } build
                    ? await Task.Run(() => build(root, token), token)
                    : await Task.Run(() => services.RmkWorkspace.BuildAsync(executionPlan!, token), token);
                token.ThrowIfCancellationRequested();
                if (!CanUpdateUi()) return;
                workspace.SetRmkBuildResult(result);
                services.Logger.Info("RMK Builder 완료 · LoadFolders.xml · ModList.tsv");
            },
            cancellationToken,
            showOverlay: true,
            retry: BuildRmkWorkspaceAsync,
            completionPredicate: () => result is not null);
    }

    internal Task BuildRmkWorkspaceForTesting(string root, CancellationToken cancellationToken) =>
        BuildRmkWorkspaceAsync(root, cancellationToken);

    internal Task DeleteProjectForTestingAsync(
        TranslationProject project,
        CancellationToken cancellationToken) =>
        DeleteProjectAsync(project, cancellationToken);

    private async Task DeleteProjectAsync(TranslationProject project, CancellationToken cancellationToken)
    {
        var projectSnapshot = CloneTranslationProject(project);
        var reviewRoots = new[] { services.Paths.Reviews };
        var plan = await Task.Run(
            () => services.Projects.GetRemovalPlan(projectSnapshot, reviewRoots),
            cancellationToken);
        var message = $"'{project.Name}' 프로젝트를 삭제할까요?\n\n앱이 만든 검수 폴더 {plan.SafePaths.Count:N0}개가 함께 삭제됩니다.\n원본 모드와 모드 안의 Korean 폴더는 삭제하지 않습니다.";
        if (plan.UnsafePaths.Count > 0) message += $"\n\n안전 경계 밖의 기록 {plan.UnsafePaths.Count:N0}개는 건드리지 않습니다.";
        var deleteAnswer = ioHooks.ProjectDeleteConfirmation is { } confirmDelete
            ? confirmDelete(message)
            : MessageBox.Show(this, message, "프로젝트 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (deleteAnswer != DialogResult.Yes) return;
        loading.Show("프로젝트 삭제 중", "앱이 소유한 검수 파일만 안전 경계 안에서 정리합니다.", theme);
        try
        {
            var removal = await RemoveProjectAsync(project.Id, plan, cancellationToken);
            if (!removal.ProjectRecordRemoved)
            {
                MessageBox.Show(
                    this,
                    "일부 검수 데이터를 안전하게 확인하지 못해 프로젝트를 유지했습니다. "
                    + "원본 데이터는 변경하지 않았습니다. 자세한 진단 기록을 확인하세요.",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (workspace.Project?.Id.Equals(project.Id, StringComparison.Ordinal) == true)
                workspace.ClearWorkspace();
            InvalidateProjectStats(project.Id);
            var cacheSnapshot = CloneProjectStatsCache(statsCache);
            Exception? cacheFailure = null;
            try
            {
                var saveCache = ioHooks.SaveProjectStatsCache ?? services.ProjectStats.Save;
                await Task.Run(() => saveCache(cacheSnapshot), CancellationToken.None);
            }
            catch (Exception exception)
            {
                cacheFailure = exception;
                services.Logger.Warning(
                    OperationErrorPresentation.CreateLogMessage("프로젝트 삭제 후 통계 캐시 갱신", exception));
            }
            dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
            services.Logger.Info($"프로젝트 삭제 · {project.Name}");
            if (removal.CleanupFailures.Count > 0 && CanUpdateUi())
            {
                var cleanupWarning =
                    $"프로젝트 기록은 삭제되었지만 검수 폴더 {removal.CleanupFailures.Count:N0}개를 정리하지 못했습니다. "
                    + "해당 데이터는 그대로 보존했습니다. 자세한 진단 기록을 확인하세요.";
                if (ioHooks.WarningPresenter is not null) ioHooks.WarningPresenter(cleanupWarning);
                else
                    MessageBox.Show(
                        this,
                        cleanupWarning,
                        Text,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
            }
            if (cacheFailure is not null && CanUpdateUi())
            {
                var warning =
                    "프로젝트 삭제는 완료되었습니다. 통계 캐시를 갱신하지 못했지만 다음 통계 새로고침에서 다시 생성됩니다.\n\n"
                    + OperationErrorPresentation.CreateUserDetail(cacheFailure);
                if (ioHooks.WarningPresenter is not null) ioHooks.WarningPresenter(warning);
                else
                    MessageBox.Show(
                        this,
                        warning,
                        Text,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
            }
        }
        finally { loading.HideOverlay(); }
    }

    private async Task RunOperationAsync(
        string title,
        string detail,
        Func<CancellationToken, Task> body,
        CancellationToken lifetimeCancellation,
        bool showOverlay = true,
        Func<CancellationToken, Task>? retry = null,
        Func<bool>? completionPredicate = null)
    {
        if (operationRunning) return;
        operationRunning = true;
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation);
        lastRetryOperation = retry;
        workspace.SetRunning(true, title);
        if (showOverlay) loading.Show(title, detail, theme, cancellable: true);
        var presentation = GetOperationPresentation(title);
        var finalStatus = "준비됨";
        try
        {
            await body(operationCancellation.Token);
            if (!CanUpdateUi())
            {
                finalStatus = "종료 대기 중";
            }
            else if (completionPredicate?.Invoke() == false)
            {
                if (showOverlay) loading.HideOverlay();
            }
            else if (showOverlay)
            {
                loading.Complete("completed", presentation.CompletedHeading, presentation.CompletedDetail);
            }
        }
        catch (OperationCanceledException)
        {
            if (!servicesDisposed)
                services.Logger.Warning("작업 취소 · 완료되지 않은 결과는 성공으로 기록하지 않았습니다.");
            finalStatus = "취소됨 · 안전 지점에서 중단";
            if (showOverlay && CanUpdateUi())
                loading.Complete("cancelled", "작업을 중지했습니다", presentation.CancelledDetail);
        }
        catch (Exception ex)
        {
            var committedFailure = ex as CommittedOperationFinalizationException;
            var presentedException = committedFailure?.InnerException ?? ex;
            var userDetail = OperationErrorPresentation.CreateUserDetail(presentedException);
            if (!servicesDisposed)
            {
                var logTitle = committedFailure is null ? title : title + " 대상 커밋 후 상태 기록";
                services.Logger.Error(OperationErrorPresentation.CreateLogMessage(logTitle, presentedException));
            }
            finalStatus = committedFailure is null
                ? "실패 · 로그에서 원인 확인"
                : "대상 적용 완료 · 프로젝트 상태 기록 실패";
            if (CanUpdateUi())
            {
                if (committedFailure is not null)
                {
                    var committedDetail =
                        "대상 파일 저장은 완료되었지만 로컬 프로젝트 상태를 기록하지 못했습니다. "
                        + "대상 파일을 다시 적용하기 전에 프로젝트를 다시 열어 결과를 확인하세요.\n\n"
                        + userDetail;
                    if (showOverlay)
                        loading.Complete("error", committedFailure.OperationLabel + " 대상 저장 완료 · 상태 기록 실패", committedDetail);
                    else
                        MessageBox.Show(this, committedDetail, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else if (showOverlay) loading.Complete("error", presentation.FailedHeading, userDetail, retry is not null);
                else MessageBox.Show(this, title + "에 실패했습니다.\n\n" + userDetail, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (CanUpdateUi()) workspace.SetRunning(false, finalStatus);
            operationCancellation?.Dispose();
            operationCancellation = null;
            operationRunning = false;
        }
    }

    internal Task RunOperationForTesting(
        Func<CancellationToken, Task> body,
        CancellationToken lifetimeCancellation) =>
        RunOperationAsync(
            "synthetic close operation",
            "synthetic close operation",
            body,
            lifetimeCancellation,
            showOverlay: false);

    private bool CanUpdateUi() => !closing && !servicesDisposed && !IsDisposed;

    private void UpdateOperationProgress(TranslationProgress value)
    {
        if (!CanUpdateUi()) return;
        var safeProgress = OperationErrorPresentation.CreateSafeProgress(value);
        workspace.UpdateProgress(safeProgress);
        if (loading.Visible)
            loading.UpdateProgress(
                safeProgress.Stage,
                safeProgress.Message,
                safeProgress.Current,
                safeProgress.Total);
    }

    private static (string CompletedHeading, string CompletedDetail, string CancelledDetail, string FailedHeading) GetOperationPresentation(string title)
    {
        if (title.Equals("초벌 번역 실행", StringComparison.Ordinal))
            return ("초벌 번역이 완료되었습니다", "문제 항목을 확인하고 검토를 시작할 수 있습니다.",
                "완료된 배치는 가능한 경우 검수 프로젝트에 남아 있습니다.", "번역 작업에 실패했습니다");
        if (title.Equals("원문 분석 중", StringComparison.Ordinal))
            return ("원문 분석이 완료되었습니다", "변경된 문자열과 검수 상태를 확인할 수 있습니다.",
                "기존 검수 프로젝트와 완료된 분석 결과는 보존했습니다.", "원문 분석에 실패했습니다");
        if (title.Contains("적용", StringComparison.Ordinal))
            return ("번역 적용이 완료되었습니다", "선택한 번역을 대상 폴더에 안전하게 저장했습니다.",
                "적용 전 파일과 완료된 변경은 가능한 경우 보존했습니다.", "번역 적용에 실패했습니다");
        if (title.StartsWith("진단 번들", StringComparison.Ordinal))
            return ("진단 번들을 저장했습니다", "개인정보를 제외한 진단 파일을 만들었습니다.",
                "완료된 진단 파일은 보존했습니다.", "진단 번들 저장에 실패했습니다");
        if (title.StartsWith("품질 보고서", StringComparison.Ordinal))
            return ("품질 보고서를 저장했습니다", "원문과 번역문을 제외한 품질 보고서를 만들었습니다.",
                "완료된 보고서 파일은 보존했습니다.", "품질 보고서 저장에 실패했습니다");
        return ("작업이 완료되었습니다", "요청한 작업을 안전하게 마쳤습니다.",
            "완료된 내용은 가능한 경우 보존했습니다.", "작업에 실패했습니다");
    }

    private async Task OpenCurrentModFolderAsync(CancellationToken cancellationToken)
    {
        var path = workspace.Project?.ModRoot;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsLocalActionDirectory(path)) SafeDirectoryLauncher.Open(path);
            }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var userDetail = OperationErrorPresentation.CreateUserDetail(ex);
            if (!servicesDisposed) services.Logger.Warning(OperationErrorPresentation.CreateLogMessage("프로젝트 폴더 열기", ex));
            if (CanUpdateUi())
                MessageBox.Show(this, "폴더를 열지 못했습니다.\n\n" + userDetail, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ExportDiagnosticsAsync(CancellationToken cancellationToken)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "진단 번들 저장",
            Filter = "ZIP 파일 (*.zip)|*.zip",
            DefaultExt = "zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"RimWorldAiTranslator-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunOperationAsync("진단 번들 저장 중", "원문, 번역문, 키, API 키와 전체 경로를 제외한 집계 정보만 만듭니다.", async token =>
        {
            var outputPath = dialog.FileName;
            var settingsSnapshot = CloneSettings(services.Settings);
            var projectSnapshot = await CaptureProjectStoreAsync(token);
            var logSnapshot = workspace.GetRecentLogLines().ToArray();
            var options = new DiagnosticBundleOptions(
                outputPath,
                services.Paths,
                settingsSnapshot,
                projectSnapshot,
                AppContext.BaseDirectory,
                logSnapshot,
                Force: true);
            var result = await Task.Run(() => services.Diagnostics.Create(options, token), token);
            services.Logger.Info($"진단 번들 저장 · 항목 {result.Entries:N0}개 · {result.Bytes:N0} bytes");
            MessageBox.Show(this,
                $"진단 번들을 저장했습니다.\n\n{result.Path}\n\n원문, 번역문, 키, API 키, 전체 경로와 원시 로그는 포함하지 않습니다.",
                "진단 번들", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, cancellationToken, retry: ExportDiagnosticsAsync);
    }

    private async Task ExportQualityReportAsync(CancellationToken cancellationToken)
    {
        if (workspace.Workspace is null) return;
        using var dialog = new SaveFileDialog
        {
            Title = "품질 보고서 저장",
            Filter = "HTML 파일 (*.html)|*.html",
            DefaultExt = "html",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"RimWorldAiTranslator-quality-{DateTime.Now:yyyyMMdd-HHmmss}.html"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunOperationAsync("품질 보고서 저장 중", "원문과 번역문 없이 문제 유형과 개수만 정리합니다.", async token =>
        {
            var entries = workspace.GetQualityEntries();
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return QualityService.ExportHtml(dialog.FileName, entries, QualityService.FindIssues(entries));
            }, token);
            services.Logger.Info($"품질 보고서 저장 · 문자열 {result.Model.EntryCount:N0}개 · 문제 {result.Model.IssueCount:N0}개");
            MessageBox.Show(this, "품질 보고서를 저장했습니다.\n\n" + result.Path, "품질 보고서", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, cancellationToken, showOverlay: true, retry: ExportQualityReportAsync);
    }

    private void ShowDashboard()
    {
        if (operationRunning) return;
        header.Visible = true;
        TryStartUiWorkflow("검수 저장", token => SaveReviewAsync(false, token));
        UpdateDashboardProviderStatus();
        dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
        ShowControl(dashboard);
        SetActiveNav(projectsNav);
        TryStartUiWorkflow("프로젝트 통계 새로고침", token => RefreshProjectStatsAsync(token));
    }

    private async Task ShowActivityAsync(CancellationToken cancellationToken)
    {
        if (operationRunning) return;
        header.Visible = true;
        await SaveReviewAsync(false, cancellationToken);
        TranslationProject[] projectSnapshot;
        await projectStateGate.WaitAsync(cancellationToken);
        try { projectSnapshot = services.ProjectStore.Projects.Select(CloneTranslationProject).ToArray(); }
        finally { projectStateGate.Release(); }
        var entries = await services.Activity.LoadAsync(projectSnapshot, cancellationToken);
        if (CanUpdateUi()) ShowRecoveryNotices();
        if (closing) return;
        activity.SetEntries(entries);
        ShowControl(activity);
        SetActiveNav(activityNav);
    }

    private void UpdateDashboardProviderStatus()
    {
        var selection = settings.GetSelection();
        dashboard.SetProviderStatus(
            selection.Profile.Name,
            selection.Settings.Model,
            selection.Keys.Count,
            selection.Profile.NeedsKey);
    }

    private void ShowSettings()
    {
        if (operationRunning) return;
        header.Visible = true;
        TryStartUiWorkflow("검수 저장", token => SaveReviewAsync(false, token));
        settings.Reload();
        ShowControl(settings);
        SetActiveNav(settingsNav);
    }

    private void ShowWorkspace()
    {
        header.Visible = false;
        ShowControl(workspace);
        SetActiveNav(null);
    }

    private void ShowControl(Control control)
    {
        dashboard.Visible = false;
        activity.Visible = false;
        settings.Visible = false;
        workspace.Visible = false;
        control.Visible = true;
        control.BringToFront();
        loading.BringToFront();
    }

    private void SetActiveNav(Button? active)
    {
        foreach (var button in new[] { projectsNav, activityNav, settingsNav }) button.Tag = ReferenceEquals(button, active) ? "primary" : null;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        theme = ThemeManager.Create(services.Settings);
        ThemeManager.Apply(this, theme, services.Settings.TextSize);
        foreach (Control control in Controls)
        {
            if (control.Tag as string == "header")
            {
                control.BackColor = theme.Header;
                control.ForeColor = theme.HeaderText;
                StyleHeader(control);
            }
        }
        BackColor = theme.Background;
        workspace.RefreshThemeState();
        Invalidate(true);

        void StyleHeader(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                child.ForeColor = theme.HeaderText;
                if (child is Button button)
                {
                    var selected = button.Tag as string == "primary";
                    button.BackColor = selected ? theme.Accent : theme.Header;
                    button.ForeColor = ThemeManager.ReadableForeground(
                        button.BackColor,
                        selected ? theme.AccentText : theme.HeaderText,
                        theme.Text);
                    button.FlatAppearance.BorderColor = ThemeManager.ReadableForeground(
                        button.BackColor,
                        theme.Border,
                        button.ForeColor);
                }
                else if (child.Tag as string == "accent") child.BackColor = theme.Accent;
                StyleHeader(child);
            }
        }
    }

    private void ShowCommandPalette()
    {
        if (operationRunning) return;
        var reviewVisible = workspace.Visible && workspace.Workspace is not null;
        var hasProject = reviewVisible && workspace.Project is not null;
        var actions = new List<CommandPaletteAction>
        {
            new("프로젝트 목록 열기", "이동", "Ctrl+Home", true, ShowDashboard),
            new("현재 화면 검색", "이동", "Ctrl+F", true, () => { if (workspace.Visible) workspace.FocusSearch(); else dashboard.FocusSearch(); }),
            new("선택 문자열 비교 열기", "검수", "Alt+C", reviewVisible, () => workspace.SelectSideTab("비교")),
            new("프로젝트 품질 센터 열기", "검수", "Alt+Q", reviewVisible, () => workspace.SelectSideTab("품질")),
            new("이전 문자열", "검수", "Shift+F3", reviewVisible, () => workspace.HandleShortcut(Keys.Alt | Keys.Left)),
            new("다음 문자열", "검수", "F3", reviewVisible, () => workspace.HandleShortcut(Keys.Alt | Keys.Right)),
            new("검토 완료 후 다음", "검수", "Ctrl+Enter", reviewVisible, () => workspace.HandleShortcut(Keys.Control | Keys.Enter)),
            new("검수 내용 저장", "프로젝트", "Ctrl+S", reviewVisible, () => TryStartUiWorkflow("검수 저장", token => SaveReviewAsync(true, token))),
            new("모드 원문 다시 분석", "프로젝트", "F5", reviewVisible, () => TryStartUiWorkflow("원문 다시 분석", RefreshSourceAsync)),
            new("AI 초벌 번역 준비", "프로젝트", "F9", reviewVisible, () => TryStartUiWorkflow("AI 번역", TranslateAsync)),
            new("개인정보 보호 품질 보고서", "도구", string.Empty, reviewVisible, () => TryStartUiWorkflow("품질 보고서 저장", ExportQualityReportAsync)),
            new("현재 모드 폴더 열기", "도구", string.Empty, hasProject, () => TryStartUiWorkflow("모드 폴더 열기", OpenCurrentModFolderAsync)),
            new("API 및 화면 설정 열기", "설정", string.Empty, true, ShowSettings),
            new("개인정보 보호 진단 번들 저장", "도구", string.Empty, true, () => TryStartUiWorkflow("진단 번들 저장", ExportDiagnosticsAsync))
        };
        using var dialog = new CommandPaletteDialog(actions);
        ThemeManager.Apply(dialog, theme, services.Settings.TextSize);
        if (dialog.ShowDialog(this) == DialogResult.OK) dialog.SelectedAction?.Execute();
    }

    private bool TryStartUiWorkflow(
        string label,
        Func<CancellationToken, Task> action,
        Action? completed = null)
    {
        Task workflow;
        lock (workflowSync)
        {
            if (!acceptingWorkflows || closing || formLifetimeCancellation.IsCancellationRequested) return false;
            workflow = RunTrackedUiWorkflowAsync(label, action, formLifetimeCancellation.Token);
            activeWorkflows.Add(workflow);
        }
        TrackWorkflowObserver(workflow, completed);
        return true;
    }

    private bool TryStartDurableUiWorkflow(
        string label,
        Func<Task> accept,
        Func<Task, CancellationToken, Task> action,
        Action? completed = null)
    {
        Task workflow;
        lock (workflowSync)
        {
            if (!acceptingWorkflows || closing || formLifetimeCancellation.IsCancellationRequested) return false;
            Task acceptedTask;
            try
            {
                acceptedTask = accept()
                    ?? Task.FromException(new InvalidOperationException("The durable workflow did not return a task."));
            }
            catch (Exception exception)
            {
                acceptedTask = Task.FromException(exception);
            }
            workflow = RunAcceptedUiWorkflowAsync(
                label,
                acceptedTask,
                action,
                formLifetimeCancellation.Token);
            activeWorkflows.Add(workflow);
        }
        TrackWorkflowObserver(workflow, completed);
        return true;
    }

    private void TrackWorkflowObserver(Task workflow, Action? completed)
    {
        var observer = ObserveTrackedUiWorkflowAsync(workflow, completed);
        lock (workflowSync)
        {
            workflowObservers.RemoveWhere(static task => task.IsCompleted);
            workflowObservers.Add(observer);
        }
    }

    internal bool StartUiWorkflowForTesting(
        string label,
        Func<CancellationToken, Task> action,
        Action? completed = null) =>
        TryStartUiWorkflow(label, action, completed);

    internal bool StartDurableUiWorkflowForTesting(
        string label,
        Func<Task> accept,
        Func<Task, CancellationToken, Task> action,
        Action? completed = null) =>
        TryStartDurableUiWorkflow(label, accept, action, completed);

    internal bool IsAcceptingUiWorkflowsForTesting
    {
        get
        {
            lock (workflowSync)
                return acceptingWorkflows && !closing && !formLifetimeCancellation.IsCancellationRequested;
        }
    }

    internal bool IsCloseBarrierTimedOutForTesting =>
        closeBarrierTimedOut && closeBarrierTask is { IsCompleted: false };

    internal bool IsServiceDisposalPendingForTesting =>
        serviceDisposalTask is { IsCompleted: false };

    internal bool HasIncompleteUiWorkflowsForTesting
    {
        get
        {
            lock (workflowSync) return activeWorkflows.Any(static workflow => !workflow.IsCompleted);
        }
    }

    internal ReviewWorkspaceControl WorkspaceControlForTesting => workspace;

    internal ProjectDashboardControl DashboardControlForTesting => dashboard;

    internal bool IsLocalActionDirectoryForTesting(string path) => IsLocalActionDirectory(path);

    internal void SuppressStartupForTesting()
    {
        startupComplete = true;
        preparedForFirstShow = true;
        firstFrameRevealed = true;
        Opacity = 1;
    }

    internal void ShowWorkspaceForTesting()
    {
        SuppressStartupForTesting();
        ShowWorkspace();
    }

    internal bool StartupFailedForTesting => Volatile.Read(ref startupFailure) is not null;

    internal Task SaveReviewForTestingAsync(CancellationToken cancellationToken) =>
        SaveReviewAsync(false, cancellationToken);

    internal Task RunUiActionForTestingAsync(string label, Func<Task> action) =>
        RunUiActionAsync(label, action);

    internal static Task<TResult> FinalizeCommittedOperationForTestingAsync<TResult>(
        string operationLabel,
        Func<CancellationToken, Task<TResult>> finalizer,
        CancellationToken postCommitCancellation) =>
        FinalizeCommittedOperationAsync(operationLabel, finalizer, postCommitCancellation);

    internal void LoadWorkspaceForTesting(TranslationProject project, ReviewWorkspace review)
    {
        SuppressStartupForTesting();
        workspace.SetWorkspace(project, review);
        ShowWorkspace();
    }

    internal bool DispatchShortcutForTesting(Keys keyData)
    {
        var message = default(Message);
        return ProcessCmdKey(ref message, keyData);
    }

    private async Task RunTrackedUiWorkflowAsync(
        string label,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        await RunUiActionAsync(label, () => action(cancellationToken));
    }

    private async Task RunAcceptedUiWorkflowAsync(
        string label,
        Task acceptedTask,
        Func<Task, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        await RunUiActionAsync(label, () => action(acceptedTask, cancellationToken));
    }

    private async Task ObserveTrackedUiWorkflowAsync(Task workflow, Action? completed)
    {
        try
        {
            await workflow.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext
                | ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            lock (workflowSync) activeWorkflows.Remove(workflow);
            try { completed?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        }
    }

    private async Task CancelAndDrainUiWorkflowsAsync()
    {
        Task[] workflows;
        CancellationTokenSource lifetime;
        CancellationTokenSource? operation;
        lock (workflowSync)
        {
            acceptingWorkflows = false;
            lifetime = formLifetimeCancellation;
            workflows = activeWorkflows.ToArray();
            operation = operationCancellation;
        }

        var errors = new List<Exception>();
        try { lifetime.Cancel(); }
        catch (Exception exception) { AddException(errors, exception); }
        try { operation?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // RunOperationAsync only disposes this source after its body has completed;
            // a close-time race with that disposal therefore needs no further cancellation.
        }
        catch (Exception exception) { AddException(errors, exception); }
        if (workflows.Length > 0)
        {
            await Task.WhenAll(workflows).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (var workflow in workflows)
                if (workflow.IsFaulted && workflow.Exception is not null)
                    AddException(errors, workflow.Exception);
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private void StopAcceptingUiWorkflows()
    {
        lock (workflowSync) acceptingWorkflows = false;
    }

    private static void AddException(ICollection<Exception> errors, Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions) errors.Add(inner);
        }
        else
        {
            errors.Add(exception);
        }
    }

    private void ResetUiWorkflowLifetime()
    {
        CancellationTokenSource previous;
        lock (workflowSync)
        {
            previous = formLifetimeCancellation;
            formLifetimeCancellation = new CancellationTokenSource();
            acceptingWorkflows = true;
        }
        previous.Dispose();
    }

    private async Task RunUiActionAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            if (!closing && !servicesDisposed) services.Logger.Warning(label + " 취소");
        }
        catch (Exception exception)
        {
            var userDetail = OperationErrorPresentation.CreateUserDetail(exception);
            if (!servicesDisposed)
                services.Logger.Error(OperationErrorPresentation.CreateLogMessage(label, exception));
            if (!closing && !servicesDisposed && !IsDisposed)
            {
                var message = $"{label} 작업을 완료하지 못했습니다.\n\n{userDetail}";
                if (ioHooks.ErrorPresenter is not null) ioHooks.ErrorPresenter(message);
                else
                    MessageBox.Show(
                        this,
                        message,
                        Text,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (CanUpdateUi()) ShowRecoveryNotices();
        }
    }

    private void ReportStartupFailure(Exception exception)
    {
        var existing = Interlocked.CompareExchange(ref startupFailure, exception, null);
        if (existing is not null && !ReferenceEquals(existing, exception)) return;
        var handler = StartupFailed;
        if (handler is not null)
        {
            try
            {
                handler(this, new MainFormStartupFailureEventArgs(exception));
                return;
            }
            catch (Exception observerException)
            {
                if (!servicesDisposed)
                    services.Logger.Error(OperationErrorPresentation.CreateLogMessage("시작 실패 전달", observerException));
            }
        }

        if (!CanUpdateUi()) return;
        var detail = OperationErrorPresentation.CreateUserDetail(exception);
        var message = "프로그램을 준비하지 못했습니다.\n\n" + detail;
        if (ioHooks.ErrorPresenter is not null) ioHooks.ErrorPresenter(message);
        else MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        if (!IsDisposed && IsHandleCreated) BeginInvoke(Close);
    }

    private void MainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (closeSaveCompleted)
        {
            closing = true;
            autoSaveTimer.Stop();
            return;
        }

        e.Cancel = true;
        if (closePromptInProgress) return;
        if (closeSaveInProgress)
        {
            if (closeBarrierTimedOut
                && closeBarrierTask is { IsCompleted: false }
                && ConfirmForceCloseAfterTimeout())
            {
                RequestForceClose();
            }
            return;
        }

        closePromptInProgress = true;
        autoSaveTimer.Stop();
        var closeAccepted = false;
        try
        {
            if (operationRunning && !ConfirmCloseDuringActiveOperation()) return;

            Exception? preparationError = null;
            try { workspace.SaveCurrentEditor(true); }
            catch (Exception exception) { preparationError = exception; }

            var review = workspace.Workspace;
            var hasUnsavedReview = preparationError is not null
                || workspace.HasPendingEditorChanges
                || review?.Dirty == true;
            var saveReview = false;
            if (hasUnsavedReview)
            {
                var answer = ConfirmUnsavedReviewClose();
                if (answer == DialogResult.Cancel) return;
                saveReview = answer == DialogResult.Yes;
            }

            StopAcceptingUiWorkflows();
            closing = true;
            closeSaveInProgress = true;
            closeBarrierTimedOut = false;
            forceCloseRequested = false;
            closePreparationError = saveReview ? preparationError : null;
            closeReviewWorkspace = saveReview ? review : null;
            closeAccepted = true;
            Enabled = false;
            workspace.SuspendBackgroundUiWorkForClose();
            if (saveReview) workspace.SetCloseSaveInProgress();
            workspace.SetRunning(
                true,
                operationRunning
                    ? "종료 전 작업 취소 중"
                    : saveReview ? "종료 전 검수 저장 중" : "종료 준비 중");
            var coordinator = CompleteFormClosingAsync();
            closeCoordinatorTask = coordinator;
            closeCoordinatorFaultObserver = ObserveTaskFaultAsync(coordinator, "close coordinator");
        }
        finally
        {
            closePromptInProgress = false;
            if (!closeAccepted && startupComplete && !closing) autoSaveTimer.Start();
        }
    }

    private bool ConfirmCloseDuringActiveOperation()
    {
        if (activeOperationCloseConfirmation is not null)
        {
            try { return activeOperationCloseConfirmation() == DialogResult.OK; }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Active-operation close confirmation failed ({exception.GetType().Name}).");
                return false;
            }
        }

        if (IsDisposed || !IsHandleCreated) return false;
        return MessageBox.Show(
            this,
            "작업이 실행 중입니다. 완료된 배치를 보존하고 작업을 중지한 뒤 종료할까요?",
            Text,
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private DialogResult ConfirmUnsavedReviewClose()
    {
        if (unsavedCloseConfirmation is not null)
        {
            try
            {
                var result = unsavedCloseConfirmation();
                return result is DialogResult.Yes or DialogResult.No or DialogResult.Cancel
                    ? result
                    : DialogResult.Cancel;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Unsaved-review close confirmation failed ({exception.GetType().Name}).");
                return DialogResult.Cancel;
            }
        }

        if (IsDisposed || !IsHandleCreated) return DialogResult.Cancel;
        return MessageBox.Show(
            this,
            "저장하지 않은 검수 내용이 있습니다. 저장할까요?",
            Text,
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
    }

    private async Task CompleteFormClosingAsync()
    {
        try
        {
            var review = closeReviewWorkspace;
            var preparationError = closePreparationError;
            var barrier = Task.Run(() => ExecuteCloseBarrierAsync(review, preparationError));
            closeBarrierTask = barrier;
            closeBarrierFaultObserver = ObserveTaskFaultAsync(barrier, "close barrier");
            await Task.WhenAny(barrier, Task.Delay(closeBarrierTimeout));

            if (barrier.IsCompleted)
            {
                await barrier.ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext
                    | ConfigureAwaitOptions.SuppressThrowing);
                if (barrier.IsCompletedSuccessfully)
                {
                    await CompleteCommittedCloseAsync();
                    return;
                }

                var failure = barrier.Exception?.GetBaseException()
                    ?? new InvalidOperationException("The close barrier did not complete successfully.");
                LogCloseSaveFailure(failure);
                if (closeReviewWorkspace is not null) workspace.SetSaveFailed(false);
                var answer = MessageBox.Show(
                    this,
                    "설정 또는 검수 내용을 저장하지 못했습니다. 프로그램을 닫을까요?\n\n"
                    + OperationErrorPresentation.CreateUserDetail(failure),
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.Yes) await CompleteCommittedCloseAsync();
                else RestoreAfterCancelledClose("저장 실패 · 종료 취소");
                return;
            }

            closeBarrierTimedOut = true;
            if (!IsDisposed)
                workspace.SetRunning(true, "종료 대기 중 · 저장 작업이 끝날 때까지 새 작업을 시작할 수 없습니다");
            if (ConfirmForceCloseAfterTimeout())
            {
                RequestForceClose();
                return;
            }

            await barrier.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext
                | ConfigureAwaitOptions.SuppressThrowing);
            if (forceCloseRequested || servicesDisposed || IsDisposed) return;
            if (barrier.IsFaulted && barrier.Exception is not null)
            {
                var failure = barrier.Exception.GetBaseException();
                LogCloseSaveFailure(failure);
                if (closeReviewWorkspace is not null) workspace.SetSaveFailed(false);
                RestoreAfterCancelledClose("저장 실패 · 종료 취소");
            }
            else
            {
                RestoreAfterCancelledClose("종료 취소");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.GetType().Name);
            var pendingBarrier = closeBarrierTask;
            if (pendingBarrier is { IsCompleted: false } && !forceCloseRequested)
            {
                await pendingBarrier.ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext
                    | ConfigureAwaitOptions.SuppressThrowing);
            }
            if (!forceCloseRequested && !servicesDisposed && !IsDisposed)
            {
                try
                {
                    if (pendingBarrier?.IsFaulted == true && closeReviewWorkspace is not null)
                        workspace.SetSaveFailed(false);
                    var status = pendingBarrier?.IsFaulted == true ? "저장 실패 · 종료 취소" : "종료 취소";
                    RestoreAfterCancelledClose(status);
                }
                catch (Exception restoreError)
                {
                    System.Diagnostics.Debug.WriteLine(restoreError.GetType().Name);
                }
            }
        }
        finally
        {
            closeSaveInProgress = false;
        }
    }

    private async Task ExecuteCloseBarrierAsync(ReviewWorkspace? review, Exception? preparationError)
    {
        var errors = new List<Exception>();
        if (preparationError is not null) AddException(errors, preparationError);

        try { await CancelAndDrainUiWorkflowsAsync().ConfigureAwait(false); }
        catch (Exception exception) { AddException(errors, exception); }
        try { await services.FlushSettingsAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { AddException(errors, exception); }
        if (review is not null)
        {
            try { await services.ReviewSaves.SaveAsync(review, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { AddException(errors, exception); }
        }

        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private static async Task ObserveTaskFaultAsync(Task task, string label)
    {
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (task.IsFaulted && task.Exception is not null)
            System.Diagnostics.Debug.WriteLine($"{label} failed ({task.Exception.GetBaseException().GetType().Name}).");
    }

    private bool ConfirmForceCloseAfterTimeout()
    {
        if (forceCloseConfirmation is not null)
        {
            try { return forceCloseConfirmation(); }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Force-close confirmation failed ({exception.GetType().Name}).");
                return false;
            }
        }
        if (IsDisposed || !IsHandleCreated) return false;
        var seconds = Math.Max(1, (int)Math.Ceiling(closeBarrierTimeout.TotalSeconds));
        return MessageBox.Show(
            this,
            $"종료 전 저장 작업이 {seconds:N0}초 안에 끝나지 않았습니다. 저장 완료를 기다리지 않고 강제로 닫을까요?\n\n아니요를 선택하면 작업이 끝날 때까지 안전한 대기 상태를 유지한 뒤 종료를 취소합니다.",
            Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void RequestForceClose()
    {
        if (forceCloseRequested) return;
        forceCloseRequested = true;
        closeSaveCompleted = true;
        if (!IsDisposed && IsHandleCreated) BeginInvoke(Close);
    }

    private void ScheduleCloseAfterCompletedBarrier()
    {
        forceCloseRequested = false;
        closeSaveCompleted = true;
        if (!IsDisposed && IsHandleCreated) BeginInvoke(Close);
    }

    private async Task CompleteCommittedCloseAsync()
    {
        try
        {
            await DisposeFormServicesAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Committed close disposal failed ({exception.GetType().Name}).");
        }
        ScheduleCloseAfterCompletedBarrier();
    }

    private void LogCloseSaveFailure(Exception exception)
    {
        if (servicesDisposed) return;
        try { services.Logger.Error("종료 전 상태 저장 실패 (" + exception.GetType().Name + ")"); }
        catch (Exception loggingException)
        {
            System.Diagnostics.Debug.WriteLine($"Close failure logging was unavailable ({loggingException.GetType().Name}).");
        }
    }

    private void RestoreAfterCancelledClose(string status)
    {
        if (closeBarrierTask is { IsCompleted: false })
            throw new InvalidOperationException("The UI workflow lifetime cannot be reset before the close barrier completes.");
        closeSaveCompleted = false;
        forceCloseRequested = false;
        closeBarrierTimedOut = false;
        closeReviewWorkspace = null;
        closePreparationError = null;
        ResetUiWorkflowLifetime();
        closing = false;
        Enabled = true;
        loading.HideOverlay();
        workspaceLoading.HideCover();
        workspace.SetRunning(false, status);
        settings.ResumeAfterCloseCancellation();
        workspace.ResumeAfterCloseCancellation();
        if (startupComplete) autoSaveTimer.Start();
        else TryStartUiWorkflow("프로그램 시작", StartAsync);
    }

    private async Task DisposeFormServicesAsync()
    {
        if (servicesDisposed)
        {
            if (serviceDisposalTask is { } existing)
                await existing.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            return;
        }
        servicesDisposed = true;
        try { formLifetimeCancellation.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        try { projectStateGate.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        try { statsRefreshGate.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        var disposal = services.DisposeAsync().AsTask();
        serviceDisposalTask = disposal;
        try
        {
            await disposal.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
    }

    internal async Task DisposeAfterFailedBootstrapAsync()
    {
        closing = true;
        StopAcceptingUiWorkflows();
        autoSaveTimer.Stop();
        try
        {
            // The settings controls queue read-only probes while the form is built. A
            // transition failure can occur before those continuations run, so they must
            // observe cancellation and finish before their shared services are released.
            await Task.Run(CancelAndDrainUiWorkflowsAsync)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Failed bootstrap workflow drain skipped ({exception.GetType().Name}).");
        }
        await DisposeFormServicesAsync();
        try { Dispose(); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Failed bootstrap form disposal skipped ({exception.GetType().Name}).");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            autoSaveTimer?.Stop();
            autoSaveTimer?.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record ProjectStatsInput(string Id, string LatestReviewRoot);
}

internal sealed class MainFormStartupFailureEventArgs(Exception exception) : EventArgs
{
    internal Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));
}

internal sealed class CommittedOperationFinalizationException(string operationLabel, Exception innerException)
    : Exception("Target files committed, but local project metadata finalization failed.", innerException)
{
    internal string OperationLabel { get; } = string.IsNullOrWhiteSpace(operationLabel)
        ? "작업"
        : operationLabel.Trim();
}
