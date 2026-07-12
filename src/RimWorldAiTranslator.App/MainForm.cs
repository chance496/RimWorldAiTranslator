using System.Diagnostics;
using System.Text.Json;
using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.App.Dialogs;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Diagnostics;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Quality;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Validation;

namespace RimWorldAiTranslator.App;

internal sealed class MainForm : Form
{
    private readonly AppServices services;
    private readonly Panel contentHost;
    private readonly ProjectDashboardControl dashboard;
    private readonly ActivityControl activity;
    private readonly SettingsControl settings;
    private readonly ReviewWorkspaceControl workspace;
    private readonly LoadingOverlay loading;
    private readonly Button projectsNav;
    private readonly Button activityNav;
    private readonly Button settingsNav;
    private readonly Button commandNav;
    private readonly System.Windows.Forms.Timer autoSaveTimer;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private ThemePalette theme;
    private IReadOnlyList<RimWorldModInfo> mods = [];
    private readonly Dictionary<string, ProjectCardStats> projectStats = new(StringComparer.Ordinal);
    private readonly ProjectStatsCacheDocument statsCache;
    private readonly SemaphoreSlim statsRefreshGate = new(1, 1);
    private CancellationTokenSource? operationCancellation;
    private bool operationRunning;
    private bool startupComplete;
    private bool closing;
    private bool closeAfterOperation;

    public MainForm(string? dataRoot = null)
    {
        services = new AppServices(dataRoot);
        statsCache = services.ProjectStats.Load();
        foreach (var entry in statsCache.Entries)
            projectStats[entry.ProjectId] = CreateProjectCardStats(entry.Stats.ToStats());
        theme = ThemeManager.Create(services.Settings);
        Text = "RimWorld AI Translator";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1100, 700);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        Opacity = 0;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 84,
            ColumnCount = 3,
            Padding = new Padding(34, 14, 28, 10),
            Tag = "header"
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = Ui.Label("RimWorld AI Translator", 13.5f, FontStyle.Bold);
        title.ForeColor = theme.HeaderText;
        title.Margin = new Padding(0, 13, 0, 0);
        var nav = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        projectsNav = Ui.Button("프로젝트", "primary", 112);
        activityNav = Ui.Button("활동", null, 94);
        settingsNav = Ui.Button("설정", null, 94);
        projectsNav.Click += (_, _) => ShowDashboard();
        activityNav.Click += (_, _) => ShowActivity();
        settingsNav.Click += (_, _) => ShowSettings();
        nav.Controls.Add(projectsNav); nav.Controls.Add(activityNav); nav.Controls.Add(settingsNav);
        commandNav = Ui.Button("명령  Ctrl+Shift+P", null, 178);
        commandNav.Click += (_, _) => ShowCommandPalette();
        commandNav.Margin = new Padding(36, 5, 0, 5);
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(nav, 1, 0);
        header.Controls.Add(commandNav, 2, 0);

        contentHost = new BufferedPanel { Dock = DockStyle.Fill };
        dashboard = new ProjectDashboardControl();
        activity = new ActivityControl();
        settings = new SettingsControl(services);
        workspace = new ReviewWorkspaceControl(services);
        loading = new LoadingOverlay();
        loading.CancelRequested += (_, _) => operationCancellation?.Cancel();
        contentHost.Controls.Add(dashboard);
        contentHost.Controls.Add(activity);
        contentHost.Controls.Add(settings);
        contentHost.Controls.Add(workspace);
        contentHost.Controls.Add(loading);
        Controls.Add(contentHost);
        Controls.Add(header);

        dashboard.CreateProjectRequested += async (_, mod) => await CreateProjectAsync(mod);
        dashboard.ChooseFolderRequested += async (_, _) => await ChooseModFolderAsync();
        dashboard.RefreshRequested += async (_, _) => await RefreshModsAsync(true);
        dashboard.OpenProjectRequested += async (_, project) => await OpenProjectAsync(project);
        dashboard.DeleteProjectRequested += async (_, project) => await DeleteProjectAsync(project);
        settings.SettingsSaved += async (_, _) =>
        {
            services.Logger.Info("설정을 저장했습니다.");
            await LoadGlossaryAsync();
        };
        settings.AppearanceChanged += (_, _) => ApplyTheme();
        settings.DiagnosticsRequested += async (_, _) => await ExportDiagnosticsAsync();
        workspace.BackRequested += (_, _) => ShowDashboard();
        workspace.OpenFolderRequested += (_, _) => OpenCurrentModFolder();
        workspace.SourceRefreshRequested += async (_, _) => await RefreshSourceAsync();
        workspace.AiTranslateRequested += async (_, _) => await TranslateAsync();
        workspace.StopRequested += (_, _) => operationCancellation?.Cancel();
        workspace.ApplyRequested += async (_, request) => await ApplyAsync(request);
        workspace.SaveRequested += async (_, _) => await SaveReviewAsync(true);
        workspace.QualityExportRequested += async (_, _) => await ExportQualityReportAsync();
        services.Logger.MessageWritten += (_, line) => workspace.AppendLog(line);

        autoSaveTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        autoSaveTimer.Tick += async (_, _) =>
        {
            if (!operationRunning && services.Settings.AutoSave && workspace.Workspace?.Dirty == true) await SaveReviewAsync(false);
        };
        Shown += async (_, _) => await StartAsync();
        FormClosing += MainFormClosing;
        ApplyTheme();
        ShowControl(dashboard);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.P)) { ShowCommandPalette(); return true; }
        if (workspace.Visible && workspace.HandleShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task StartAsync()
    {
        if (startupComplete) return;
        loading.Show("작업공간 준비 중", "저장된 프로젝트와 RimWorld 모드 위치를 확인하고 있습니다.", theme);
        Opacity = 1;
        try
        {
            await RefreshModsAsync(false);
            _ = LoadGlossaryAsync();
            autoSaveTimer.Start();
            startupComplete = true;
            services.Logger.Info($"프로그램 시작 · 프로젝트 {services.ProjectStore.Projects.Count:N0}개 · 모드 {mods.Count:N0}개");
        }
        catch (Exception ex)
        {
            services.Logger.Error("시작 실패: " + ex.Message);
            MessageBox.Show(this, "프로그램을 준비하지 못했습니다. 기존 파일은 변경하지 않았습니다.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            loading.HideOverlay();
            BeginInvoke(() => { PerformLayout(); Invalidate(true); });
        }
    }

    private async Task LoadGlossaryAsync()
    {
        try
        {
            var terms = await Task.Run(() =>
            {
                var glossary = new GlossaryService();
                var custom = services.Settings.CustomGlossaryPath;
                return glossary.Load(
                    Path.Combine(services.ContentRoot, "glossary.generated.ko.json"),
                    custom,
                    !string.IsNullOrWhiteSpace(custom));
            });
            if (!closing) workspace.SetGlossary(terms);
        }
        catch (Exception ex) { services.Logger.Warning("용어집 로드 실패: " + ex.Message); }
    }

    private async Task RefreshModsAsync(bool showOverlay)
    {
        if (operationRunning) return;
        dashboard.SetBusy(true);
        if (showOverlay) loading.Show("모드 목록 새로고침", "Steam 라이브러리와 로컬 Mods 폴더를 확인하고 있습니다.", theme);
        try
        {
            mods = await services.Discovery.DiscoverAsync();
            dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
            _ = RefreshProjectStatsAsync();
        }
        finally
        {
            dashboard.SetBusy(false);
            if (showOverlay) loading.HideOverlay();
        }
    }

    private async Task RefreshProjectStatsAsync()
    {
        if (!await statsRefreshGate.WaitAsync(0)) return;
        var cacheChanged = false;
        try
        {
            var activeIds = services.ProjectStore.Projects.Select(project => project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            cacheChanged |= statsCache.Entries.RemoveAll(entry => !activeIds.Contains(entry.ProjectId)) > 0;
            foreach (var project in services.ProjectStore.Projects.ToArray())
            {
                if (closing || string.IsNullOrWhiteSpace(project.LatestReviewRoot) || !Directory.Exists(project.LatestReviewRoot)) continue;
                try
                {
                    var stamp = ProjectStatsCacheRepository.CreateStamp(project.LatestReviewRoot);
                    var cached = statsCache.Entries.FirstOrDefault(entry => entry.ProjectId.Equals(project.Id, StringComparison.OrdinalIgnoreCase));
                    if (cached is not null && cached.Stamp.Equals(stamp, StringComparison.Ordinal))
                    {
                        projectStats[project.Id] = CreateProjectCardStats(cached.Stats.ToStats());
                        continue;
                    }
                    var stats = await Task.Run(() => services.Reviews.GetStats(services.Reviews.Load(project.LatestReviewRoot)));
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
                catch
                {
                    projectStats[project.Id] = new ProjectCardStats(new ReviewWorkspaceStats(0, 0, 0, 0, 0, 0), "검수 통계 읽기 실패");
                }
            }
            if (cacheChanged) await Task.Run(() => services.ProjectStats.Save(statsCache));
            if (!closing && dashboard.Visible) dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
        }
        finally { statsRefreshGate.Release(); }
    }

    private static ProjectCardStats CreateProjectCardStats(ReviewWorkspaceStats stats) => new(stats,
        $"전체 {stats.Total:N0} / 미번역 {stats.Pending:N0} / 번역됨 {stats.Translated:N0} / 검토됨 {stats.Approved:N0} / 변경 {stats.Updated:N0}");

    private void InvalidateProjectStats(string projectId)
    {
        projectStats.Remove(projectId);
        statsCache.Entries.RemoveAll(entry => entry.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task CreateProjectAsync(RimWorldModInfo mod)
    {
        var language = SelectSourceLanguage(mod);
        if (language is null) return;
        var project = services.Projects.Upsert(services.ProjectStore, mod, language);
        dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
        await GenerateSourceReviewAsync(project, "source");
    }

    private async Task ChooseModFolderAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "RimWorld 모드 폴더를 선택하세요." };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var info = services.Discovery.GetModInfo(dialog.SelectedPath, "Manual");
        if (info is null)
        {
            MessageBox.Show(this, "About.xml, Defs 또는 Languages가 있는 RimWorld 모드 폴더가 아닙니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!mods.Any(mod => mod.Path.Equals(info.Path, StringComparison.OrdinalIgnoreCase))) mods = mods.Append(info).OrderBy(mod => mod.Name).ToArray();
        await CreateProjectAsync(info);
    }

    private string? SelectSourceLanguage(RimWorldModInfo mod)
    {
        var choices = services.Discovery.GetSourceLanguageOptions(mod.Path);
        if (choices.Count == 0) return "Auto";
        if (choices.Count == 1) return choices[0].Folder;
        using var dialog = new SourceLanguageDialog(mod.Name, choices);
        ThemeManager.Apply(dialog, theme, services.Settings.TextSize);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedFolder : null;
    }

    private async Task OpenProjectAsync(TranslationProject project)
    {
        if (!string.IsNullOrWhiteSpace(project.LatestReviewRoot) && Directory.Exists(project.LatestReviewRoot))
        {
            await LoadReviewAsync(project, project.LatestReviewRoot);
            return;
        }
        if (!Directory.Exists(project.ModRoot))
        {
            MessageBox.Show(this, "저장된 모드 폴더를 찾을 수 없습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await GenerateSourceReviewAsync(project, "source");
    }

    private async Task LoadReviewAsync(TranslationProject project, string reviewRoot, bool showOverlay = true)
    {
        if (showOverlay) loading.Show("검수 작업 불러오는 중", "문자열, 검수 상태와 업데이트 변경 사항을 한 번에 준비하고 있습니다.", theme);
        try
        {
            var loaded = await services.Reviews.LoadAsync(reviewRoot, project);
            workspace.SetWorkspace(project, loaded);
            ShowWorkspace();
            ApplyTheme();
            if (loaded.Dirty) await SaveReviewAsync(false);
            services.Logger.Info($"검수 작업 로드 · {loaded.Items.Count:N0}개 문자열 · 변경 {loaded.ChangedSources:N0}개");
        }
        catch (Exception ex)
        {
            services.Logger.Error("검수 작업 로드 실패: " + ex.Message);
            MessageBox.Show(this, "검수 작업을 읽지 못했습니다. 원본과 백업은 보존됩니다.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { if (showOverlay) loading.HideOverlay(); }
    }

    private async Task RefreshSourceAsync()
    {
        if (workspace.Project is not { } project) return;
        await GenerateSourceReviewAsync(project, "source-refresh");
    }

    private async Task GenerateSourceReviewAsync(TranslationProject project, string provider)
    {
        if (!Directory.Exists(project.ModRoot)) return;
        await SaveReviewAsync(false);
        await RunOperationAsync(
            "원문 분석 중",
            "모드 XML과 기존 번역을 읽고 검수 프로젝트를 준비합니다.",
            async token =>
            {
                var references = ResolveRmkReference(project);
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
                var progress = new Progress<TranslationProgress>(workspace.UpdateProgress);
                var result = await Task.Run(() => services.CreateTranslationEngine().RunAsync(options, progress, token).GetAwaiter().GetResult(), token);
                if (result.ReviewRoot is null) throw new InvalidOperationException("검수 프로젝트 경로가 생성되지 않았습니다.");
                services.Projects.RegisterRun(services.ProjectStore, project, result.ReviewRoot, provider);
                InvalidateProjectStats(project.Id);
                await LoadReviewAsync(project, result.ReviewRoot, false);
            }, showOverlay: !workspace.Visible);
    }

    private async Task TranslateAsync()
    {
        if (workspace.Project is not { } project || workspace.Workspace is not { } currentWorkspace) return;
        workspace.SaveCurrentEditor(false);
        await SaveReviewAsync(false);
        var existingCount = currentWorkspace.Items.Count(item => !string.IsNullOrWhiteSpace(item.Decision.Text));
        var mode = TranslationStartMode.Overwrite;
        if (existingCount > 0)
        {
            using var dialog = new TranslationModeDialog(existingCount);
            ThemeManager.Apply(dialog, theme, services.Settings.TextSize);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            mode = dialog.Mode;
        }
        var selection = settings.GetSelection();
        var validation = ProviderValidator.Validate(selection.Profile, selection.Settings, selection.Keys.Count);
        if (!validation.Valid)
        {
            MessageBox.Show(this, "API 설정을 확인하세요.\n\n" + string.Join("\n", validation.ErrorCodes), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowSettings();
            return;
        }
        string preservePath = string.Empty;
        if (mode == TranslationStartMode.MissingOnly) preservePath = WritePreservedTranslations(currentWorkspace);
        try
        {
            await RunOperationAsync(
                "AI 번역 중",
                selection.Keys.Count == 0 ? "API 키가 없어 Google 번역으로 빈 항목을 채웁니다." : $"{selection.Profile.Name} · {selection.Settings.Model}",
                async token =>
                {
                    var references = ResolveRmkReference(project);
                    var options = BuildTranslationOptions(project, selection, mode, preservePath, references.Target);
                    var progress = new Progress<TranslationProgress>(value =>
                    {
                        workspace.UpdateProgress(value);
                        if (value.IsWarning) services.Logger.Warning(value.Message);
                    });
                    var result = await Task.Run(() => services.CreateTranslationEngine().RunAsync(options, progress, token).GetAwaiter().GetResult(), token);
                    if (result.ReviewRoot is null) throw new InvalidOperationException("번역 검수 프로젝트가 생성되지 않았습니다.");
                    var usedProvider = selection.Keys.Count == 0 ? "Google" : selection.Profile.Id;
                    services.Projects.RegisterRun(services.ProjectStore, project, result.ReviewRoot, usedProvider);
                    InvalidateProjectStats(project.Id);
                    await LoadReviewAsync(project, result.ReviewRoot, false);
                    services.Logger.Info($"번역 완료 · {result.TranslatedEntries:N0}개 · 주의 {result.TokenWarnings + result.SkippedUnsafe:N0}개");
                }, showOverlay: false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(preservePath) && File.Exists(preservePath)) File.Delete(preservePath);
        }
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
            BatchSize = 40,
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

    private async Task ApplyAsync(WorkspaceApplyRequest request)
    {
        if (workspace.Project is not { } project || workspace.Workspace is not { } review) return;
        workspace.SaveCurrentEditor(false);
        await SaveReviewAsync(false);
        var label = request.UseRmk ? "RMK에 적용" : "모드에 적용";
        var answer = MessageBox.Show(this,
            request.Status == ReviewApplyStatus.ApprovedOnly
                ? $"검토 완료된 안전한 번역만 {label}할까요?"
                : $"번역됨과 검토 완료 상태의 안전한 번역을 모두 {label}할까요?",
            label, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        await RunOperationAsync(label + " 중", "상태, 원문 변경, 토큰과 경로를 다시 검사합니다.", async token =>
        {
            if (request.UseRmk)
            {
                var target = ResolveWritableRmkTarget(project);
                if (target is null) return;
                var result = await Task.Run(() => services.Rmk.Export(new RmkExportOptions
                {
                    RmkEntryRoot = target.Root,
                    ReviewRoot = review.ReviewRoot,
                    RmkLanguageFolderName = "Korean (한국어)",
                    WorkbookPath = target.WorkbookPath,
                    SourceLanguage = project.SourceLanguageFolder == "Auto" ? "English" : project.SourceLanguageFolder,
                    ApplyStatus = request.Status,
                    Overwrite = true
                }), token);
                services.Logger.Info($"RMK 적용 완료 · XML {result.WrittenFiles:N0}개 · 번역 {result.EligibleEntries:N0}개");
            }
            else
            {
                var result = await Task.Run(() => services.Apply.Apply(new ReviewApplyOptions
                {
                    ModRoot = project.ModRoot,
                    ReviewRoot = review.ReviewRoot,
                    ApplyStatus = request.Status,
                    Overwrite = true
                }), token);
                services.Logger.Info($"모드 적용 완료 · 파일 {result.WrittenFiles:N0}개 · 번역 {result.AppliedEntries:N0}개");
            }
            services.Projects.MarkApplied(services.ProjectStore, project);
            projectStats.Remove(project.Id);
            MessageBox.Show(this, label + "이 완료되었습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, showOverlay: false);
    }

    private (RmkTarget? Target, string Version) ResolveRmkReference(TranslationProject project)
    {
        if (!services.Settings.RmkUseExisting) return (null, "1.6");
        var steamRoots = services.Discovery.GetSteamLibraryRoots();
        var version = services.RmkWorkspace.GetRimWorldVersion(project.ModRoot, steamRoots);
        var roots = new List<(string Path, string Kind)>();
        var workspaceRoot = ResolveConfiguredOrDetectedRmkWorkspace(steamRoots, persist: false);
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

    private RmkTarget? ResolveWritableRmkTarget(TranslationProject project)
    {
        var steamRoots = services.Discovery.GetSteamLibraryRoots();
        var root = ResolveConfiguredOrDetectedRmkWorkspace(steamRoots, persist: true);
        if (string.IsNullOrWhiteSpace(root))
        {
            MessageBox.Show(this, "설정에서 Data, ModList.tsv와 .git이 있는 RMK Git 작업 클론을 선택하세요.", "RMK 작업 폴더", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowSettings();
            return null;
        }
        var version = services.RmkWorkspace.GetRimWorldVersion(project.ModRoot, steamRoots);
        var target = services.RmkWorkspace.SelectTarget(services.RmkWorkspace.FindTargets(root, project), version);
        if (target is not null) return target;
        var answer = MessageBox.Show(this, "RMK 작업 클론에 이 모드의 항목이 없습니다. 새 항목을 만들까요?", "RMK 항목 만들기", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes ? services.RmkWorkspace.CreateTarget(root, project, version) : null;
    }

    private string ResolveConfiguredOrDetectedRmkWorkspace(IReadOnlyList<string> steamRoots, bool persist)
    {
        var configured = services.Settings.RmkWorkspaceRoot;
        if (!string.IsNullOrWhiteSpace(configured) && services.RmkWorkspace.IsWorkspaceRoot(configured, true))
            return Path.GetFullPath(configured);
        var localContainers = services.Discovery.GetModContainers(steamRoots)
            .Where(container => container.Source.Equals("Local", StringComparison.OrdinalIgnoreCase))
            .Select(container => container.Path);
        var detected = services.RmkWorkspace.FindWritableWorkspace(localContainers);
        if (string.IsNullOrWhiteSpace(detected)) return string.Empty;
        services.Settings.RmkWorkspaceRoot = detected;
        if (persist) services.SaveSettings(services.Settings);
        return detected;
    }

    private async Task SaveReviewAsync(bool announce)
    {
        if (workspace.Workspace is not { } review || !review.Dirty) return;
        if (!await saveGate.WaitAsync(0)) return;
        try
        {
            workspace.SaveCurrentEditor(false);
            await services.Reviews.SaveAsync(review);
            if (announce) services.Logger.Info("검수 상태를 저장했습니다.");
            if (workspace.Project is { } project) InvalidateProjectStats(project.Id);
        }
        catch (Exception ex)
        {
            services.Logger.Error("검수 저장 실패: " + ex.Message);
            if (announce) MessageBox.Show(this, "검수 상태를 저장하지 못했습니다. 원본과 백업은 보존됩니다.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { saveGate.Release(); }
    }

    private async Task DeleteProjectAsync(TranslationProject project)
    {
        var plan = services.Projects.GetRemovalPlan(project, [services.Paths.Reviews]);
        var message = $"'{project.Name}' 프로젝트를 삭제할까요?\n\n앱이 만든 검수 폴더 {plan.SafePaths.Count:N0}개가 함께 삭제됩니다.\n원본 모드와 모드 안의 Korean 폴더는 삭제하지 않습니다.";
        if (plan.UnsafePaths.Count > 0) message += $"\n\n안전 경계 밖의 기록 {plan.UnsafePaths.Count:N0}개는 건드리지 않습니다.";
        if (MessageBox.Show(this, message, "프로젝트 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        loading.Show("프로젝트 삭제 중", "앱이 소유한 검수 파일만 안전 경계 안에서 정리합니다.", theme);
        try
        {
            var failures = await Task.Run(() => services.Projects.Remove(services.ProjectStore, project, [services.Paths.Reviews]));
            if (failures.Count > 0) MessageBox.Show(this, "일부 파일을 안전하게 삭제하지 못해 프로젝트를 유지했습니다.\n\n" + string.Join("\n", failures), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
            {
                InvalidateProjectStats(project.Id);
                await Task.Run(() => services.ProjectStats.Save(statsCache));
                dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
                services.Logger.Info($"프로젝트 삭제 · {project.Name}");
            }
        }
        finally { loading.HideOverlay(); }
    }

    private async Task RunOperationAsync(string title, string detail, Func<CancellationToken, Task> body, bool showOverlay = true)
    {
        if (operationRunning) return;
        operationRunning = true;
        operationCancellation = new CancellationTokenSource();
        workspace.SetRunning(true, title);
        if (showOverlay) loading.Show(title, detail, theme, cancellable: true);
        var finalStatus = "준비됨";
        try
        {
            await body(operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            services.Logger.Warning("작업을 취소했습니다. 완료된 배치의 체크포인트는 보존됩니다.");
            finalStatus = "취소됨 · 완료된 체크포인트 보존";
        }
        catch (Exception ex)
        {
            services.Logger.Error(title + " 실패: " + ex.Message);
            finalStatus = "실패 · 로그에서 원인 확인";
            MessageBox.Show(this, title + "에 실패했습니다. 기존 번역 파일은 롤백되거나 변경되지 않았습니다.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (showOverlay) loading.HideOverlay();
            workspace.SetRunning(false, finalStatus);
            operationCancellation.Dispose();
            operationCancellation = null;
            operationRunning = false;
            if (closeAfterOperation && !IsDisposed) BeginInvoke(Close);
        }
    }

    private string WritePreservedTranslations(ReviewWorkspace review)
    {
        Directory.CreateDirectory(services.Paths.Temp);
        var path = Path.Combine(services.Paths.Temp, $"preserve-{Guid.NewGuid():N}.json");
        var languageRoot = Path.Combine(review.ReviewRoot, "Languages", "Korean");
        var items = review.Items.Where(item => !string.IsNullOrWhiteSpace(item.Decision.Text)).Select(item => new
        {
            key = item.Row.Key,
            kind = item.Row.Kind,
            defClass = item.Row.DefClass,
            @namespace = item.Row.Kind == "Keyed" ? "Keyed" : item.Row.DefClass,
            target = item.RelativeTarget,
            text = item.Decision.Text,
            origin = item.Decision.TranslationOrigin,
            translationUpdatedAt = item.Decision.TranslationUpdatedAt
        }).ToArray();
        AtomicFile.WriteUtf8(path, JsonSerializer.Serialize(new { version = 1, languageRoot, items }));
        return path;
    }

    private void OpenCurrentModFolder()
    {
        var path = workspace.Project?.ModRoot;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private async Task ExportDiagnosticsAsync()
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
            var options = new DiagnosticBundleOptions(
                dialog.FileName,
                services.Paths,
                services.Settings,
                services.ProjectStore,
                AppContext.BaseDirectory,
                workspace.GetRecentLogLines(),
                Force: true);
            var result = await Task.Run(() => services.Diagnostics.Create(options, token), token);
            services.Logger.Info($"진단 번들 저장 · 항목 {result.Entries:N0}개 · {result.Bytes:N0} bytes");
            MessageBox.Show(this,
                $"진단 번들을 저장했습니다.\n\n{result.Path}\n\n원문, 번역문, 키, API 키, 전체 경로와 원시 로그는 포함하지 않습니다.",
                "진단 번들", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private async Task ExportQualityReportAsync()
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
        }, showOverlay: false);
    }

    private void ShowDashboard()
    {
        if (operationRunning) return;
        _ = SaveReviewAsync(false);
        dashboard.SetData(mods, services.ProjectStore.Projects, projectStats);
        ShowControl(dashboard);
        SetActiveNav(projectsNav);
        _ = RefreshProjectStatsAsync();
    }

    private void ShowActivity()
    {
        if (operationRunning) return;
        activity.SetProjects(services.ProjectStore.Projects, services.Store);
        ShowControl(activity);
        SetActiveNav(activityNav);
    }

    private void ShowSettings()
    {
        if (operationRunning) return;
        settings.Reload();
        ShowControl(settings);
        SetActiveNav(settingsNav);
    }

    private void ShowWorkspace()
    {
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
        Invalidate(true);

        void StyleHeader(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                child.ForeColor = theme.HeaderText;
                if (child is Button button)
                {
                    button.BackColor = button.Tag as string == "primary" ? theme.Accent : theme.Header;
                    button.ForeColor = theme.HeaderText;
                    button.FlatAppearance.BorderColor = button.Tag as string == "primary" ? theme.Accent : theme.Border;
                }
                StyleHeader(child);
            }
        }
    }

    private void ShowCommandPalette()
    {
        using var menu = new ContextMenuStrip();
        Add("프로젝트 보기", ShowDashboard);
        Add("활동 보기", ShowActivity);
        Add("설정 보기", ShowSettings);
        Add("개인정보 보호 진단 번들 저장", () => _ = ExportDiagnosticsAsync());
        if (workspace.Visible)
        {
            Add("현재 검수 저장  Ctrl+S", () => _ = SaveReviewAsync(true));
            Add("AI 번역 시작", () => _ = TranslateAsync());
            Add("원문 갱신", () => _ = RefreshSourceAsync());
            Add("현재 모드 폴더 열기", OpenCurrentModFolder);
            Add("품질 보고서 저장", () => _ = ExportQualityReportAsync());
        }
        menu.Show(commandNav, new Point(0, commandNav.Height));
        return;

        void Add(string text, Action action)
        {
            var item = menu.Items.Add(text);
            item.Click += (_, _) => action();
        }
    }

    private void MainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (operationRunning)
        {
            e.Cancel = true;
            closeAfterOperation = true;
            operationCancellation?.Cancel();
            workspace.SetRunning(true, "종료 전 작업 취소 중");
            return;
        }
        closing = true;
        autoSaveTimer.Stop();
        workspace.SaveCurrentEditor(false);
        if (workspace.Workspace?.Dirty == true)
        {
            try { services.Reviews.Save(workspace.Workspace); }
            catch (Exception ex)
            {
                var answer = MessageBox.Show(this, "검수 내용을 저장하지 못했습니다. 프로그램을 닫을까요?\n\n" + ex.Message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) { e.Cancel = true; closing = false; autoSaveTimer.Start(); return; }
            }
        }
        services.Dispose();
    }
}
