using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Quality;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Validation;

namespace RimWorldAiTranslator.App.Controls;

internal sealed record WorkspaceApplyRequest(ReviewApplyStatus Status, bool UseRmk);

internal sealed record UiIoHooks(
    Func<string, bool>? GlossaryFileExists = null,
    Func<string, bool>? DirectoryExists = null,
    Func<string, bool>? IsRmkWorkspaceRoot = null,
    Func<string, bool>? IsWritableRmkWorkspace = null,
    Action<string>? OpenDirectory = null,
    Func<string, CancellationToken, Task<RmkBuildResult>>? BuildRmkWorkspace = null,
    Action? MainFormStartup = null,
    Action<string>? ErrorPresenter = null,
    Func<string, DialogResult>? ProjectDeleteConfirmation = null,
    Action<ProjectStatsCacheDocument>? SaveProjectStatsCache = null,
    Action<string>? WarningPresenter = null,
    Func<CancellationToken, IReadOnlyList<GlossaryTerm>>? LoadGlossary = null);

internal sealed class ReviewWorkspaceControl : UserControl
{
    private readonly AppServices services;
    private readonly UiWorkflowStarter startWorkflow;
    private readonly Func<string, bool> directoryExists;
    private readonly Func<string, bool> isRmkWorkspaceRoot;
    private readonly Func<string, bool> isWritableRmkWorkspace;
    private readonly Action<string> openDirectory;
    private readonly Label projectName;
    private readonly Label projectPath;
    private readonly Label operationStatus;
    private readonly Label saveStatus;
    private readonly ProgressBar operationProgress;
    private readonly Button aiTranslate;
    private readonly Button stop;
    private readonly Button sourceRefresh;
    private readonly Button applyApproved;
    private readonly Button applyTranslated;
    private readonly Button projectListButton;
    private readonly Button folderButton;
    private readonly Button saveButton;
    private readonly CheckBox useRmk;
    private readonly CommandToolTipService commandToolTips;
    private TextBox search = null!;
    private ComboBox searchField = null!;
    private ComboBox statusFilter = null!;
    private ComboBox fileFilter = null!;
    private ComboBox sortMode = null!;
    private Label crumb = null!;
    private Label summary = null!;
    private ListBox list = null!;
    private Label itemStatus = null!;
    private Label updateBadge = null!;
    private Label translationLabel = null!;
    private Label existingLabel = null!;
    private RichTextBox source = null!;
    private RichTextBox translation = null!;
    private RichTextBox metadata = null!;
    private RichTextBox existing = null!;
    private RichTextBox candidate = null!;
    private RichTextBox diffSource = null!;
    private Label diffBeforeLabel = null!;
    private RichTextBox diffExisting = null!;
    private RichTextBox diffCurrent = null!;
    private Label diffSummary = null!;
    private RichTextBox history = null!;
    private RichTextBox terms = null!;
    private TextBox memo = null!;
    private RichTextBox rmkInfo = null!;
    private Label rmkStatus = null!;
    private Button rmkOpen = null!;
    private Button rmkBuild = null!;
    private Button rmkRefresh = null!;
    private ListView issues = null!;
    private Label qualitySummary = null!;
    private Label selectedQuality = null!;
    private ComboBox qualityCategory = null!;
    private Button qualityJump = null!;
    private Button qualityRefresh = null!;
    private Button exportQuality = null!;
    private RichTextBox warnings = null!;
    private RichTextBox log = null!;
    private ListBox memory = null!;
    private TabControl sideTabs = null!;
    private readonly System.Windows.Forms.Timer searchTimer;
    private readonly TableLayoutPanel root;
    private readonly SplitContainer outerSplit;
    private readonly SplitContainer innerSplit;
    private IReadOnlyList<GlossaryTerm> glossary = [];
    private IReadOnlyList<ReviewItem> filtered = [];
    private IReadOnlyList<QualityIssue> qualityIssues = [];
    private IReadOnlyList<QualityIssue> visibleQualityIssues = [];
    private IReadOnlyDictionary<string, ReviewItem[]> translationMemorySourceIndex =
        new Dictionary<string, ReviewItem[]>(StringComparer.Ordinal);
    private TranslationProject? project;
    private ReviewWorkspace? workspace;
    private ReviewItem? current;
    private bool suppressEditor;
    private bool editorTouched;
    private bool translationTouched;
    private bool operationRunning;
    private bool stopRequested;
    private bool disposed;
    private bool windowResizeInProgress;
    private bool windowResizePending;
    private long rmkStatusRevision;
    private long filterRevision;
    private long qualityRevision;
    private CancellationTokenSource? filterCancellation;
    private CancellationTokenSource? qualityCancellation;
    private bool filterBusy;
    private bool qualityBusy;
    private bool lastFilterUsedWorkerThread;
    private bool lastQualityUsedWorkerThread;
    private readonly int uiThreadId = Environment.CurrentManagedThreadId;
    private TimeSpan lastQualityBuildDuration;
    private string editBaseline = string.Empty;
    private string editBaselineOrigin = string.Empty;
    private string pendingEditOrigin = "local";
    private readonly Dictionary<Button, ReviewStatusFilter> statusButtons = [];
    private Button[] utilityButtons = [];
    private Button[] reviewStatusButtons = [];

    public ReviewWorkspaceControl(
        AppServices services,
        UiWorkflowStarter startWorkflow,
        UiIoHooks? ioHooks = null)
    {
        this.services = services;
        this.startWorkflow = startWorkflow;
        directoryExists = ioHooks?.DirectoryExists ?? Directory.Exists;
        isRmkWorkspaceRoot = ioHooks?.IsRmkWorkspaceRoot
            ?? (path => services.RmkWorkspace.IsWorkspaceRoot(path, false));
        isWritableRmkWorkspace = ioHooks?.IsWritableRmkWorkspace
            ?? services.RmkWorkspace.IsWritableWorkspace;
        openDirectory = ioHooks?.OpenDirectory ?? SafeDirectoryLauncher.Open;
        commandToolTips = new CommandToolTipService();
        Dock = DockStyle.Fill;
        TabStop = false;

        root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new BufferedPanel { Dock = DockStyle.Fill, Tag = "header" };
        projectName = Ui.Label("프로젝트", 11.5f, FontStyle.Bold);
        projectName.AutoSize = false;
        projectName.AutoEllipsis = true;
        projectName.Tag = "header-text";
        projectName.SetBounds(24, 12, 286, 25);
        projectPath = Ui.Label(string.Empty, 8f);
        projectPath.AutoSize = false;
        projectPath.SetBounds(24, 42, 286, 18);
        projectPath.AutoEllipsis = true;
        projectPath.Tag = "header-muted";
        projectListButton = Ui.Button("프로젝트", "header-button", 74);
        projectListButton.SetBounds(330, 21, 74, 36);
        projectListButton.Margin = Padding.Empty;
        projectListButton.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        projectListButton.AccessibleDescription = "프로젝트 목록으로 돌아갑니다. 단축키 Ctrl+Home.";
        projectListButton.TabIndex = 0;
        folderButton = Ui.Button("폴더", "header-button", 60);
        folderButton.SetBounds(412, 21, 60, 36);
        folderButton.Margin = Padding.Empty;
        folderButton.Click += (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty);
        folderButton.AccessibleDescription = "현재 모드 폴더를 엽니다.";
        folderButton.TabIndex = 1;
        saveButton = Ui.Button("저장", "header-button", 58);
        saveButton.SetBounds(480, 21, 58, 36);
        saveButton.Margin = Padding.Empty;
        saveButton.Click += (_, _) => CommitWithDuplicatePrompt();
        saveButton.AccessibleDescription = "현재 편집 내용을 저장합니다. 단축키 Ctrl+S.";
        saveButton.TabIndex = 2;
        operationStatus = Ui.Label("준비됨", 8.5f, FontStyle.Bold);
        operationStatus.AutoSize = false;
        operationStatus.AutoEllipsis = true;
        operationStatus.Tag = "header-muted";
        operationStatus.AccessibleName = "검수 작업 상태";
        operationStatus.AccessibleDescription = "현재 검수 작업의 상태를 표시합니다.";
        operationStatus.SetBounds(550, 17, 260, 18);
        saveStatus = Ui.Label(string.Empty, 8.5f);
        saveStatus.AutoSize = false;
        saveStatus.AutoEllipsis = true;
        saveStatus.Tag = "header-muted";
        saveStatus.AccessibleName = "검수 저장 상태";
        saveStatus.AccessibleDescription = "현재 검수 내용의 저장 상태를 표시합니다.";
        saveStatus.SetBounds(550, 40, 260, 18);
        sourceRefresh = Ui.Button("원문 갱신", "header-button", 96);
        sourceRefresh.Margin = Padding.Empty;
        sourceRefresh.Click += (_, _) => SourceRefreshRequested?.Invoke(this, EventArgs.Empty);
        sourceRefresh.AccessibleDescription = "현재 원문을 다시 분석합니다. 단축키 F5.";
        sourceRefresh.TabIndex = 4;
        aiTranslate = Ui.Button("AI 번역", "primary", 88);
        aiTranslate.Margin = Padding.Empty;
        aiTranslate.Click += (_, _) => AiTranslateRequested?.Invoke(this, EventArgs.Empty);
        aiTranslate.AccessibleDescription = "AI 초벌 번역을 준비합니다. 단축키 F9.";
        aiTranslate.TabIndex = 5;
        stop = Ui.Button("중지", "danger", 54);
        stop.Margin = Padding.Empty;
        stop.Enabled = false;
        stop.Click += (_, _) =>
        {
            if (!operationRunning || stopRequested || !stop.Enabled) return;
            stopRequested = true;
            stop.Enabled = false;
            stop.Text = "요청됨";
            stop.AccessibleName = "취소 요청됨";
            operationStatus.Text = "취소 요청됨 · 안전한 중지 대기";
            StopRequested?.Invoke(this, EventArgs.Empty);
        };
        stop.AccessibleDescription = "실행 중인 작업을 중지합니다. 단축키 Shift+F9.";
        stop.TabIndex = 6;
        useRmk = new CheckBox { Text = "RMK에 적용", AutoSize = false, BackColor = Color.Transparent, Tag = "header-check" };
        useRmk.AccessibleName = "RMK 작업 클론에 적용";
        useRmk.AccessibleDescription = "선택하면 로컬 모드 대신 검증된 bus 브랜치 RMK 작업 클론을 대상으로 사용합니다.";
        useRmk.TabIndex = 3;
        applyApproved = Ui.Button("검토 적용", "success", 92);
        applyApproved.Margin = Padding.Empty;
        applyApproved.Click += (_, _) => ApplyRequested?.Invoke(this, new WorkspaceApplyRequest(ReviewApplyStatus.ApprovedOnly, useRmk.Checked));
        applyApproved.AccessibleDescription = "검토 완료된 안전한 번역만 미리보기 후 적용합니다.";
        applyApproved.TabIndex = 7;
        applyTranslated = Ui.Button("전체 적용", "primary", 100);
        applyTranslated.Margin = Padding.Empty;
        applyTranslated.Click += (_, _) => ApplyRequested?.Invoke(this, new WorkspaceApplyRequest(ReviewApplyStatus.TranslatedAndApproved, useRmk.Checked));
        applyTranslated.AccessibleDescription = "번역됨과 검토 완료 상태의 안전한 번역을 미리보기 후 적용합니다.";
        applyTranslated.TabIndex = 8;
        operationProgress = new ProgressBar
        {
            Visible = false,
            Style = ProgressBarStyle.Blocks,
            Minimum = 0,
            Maximum = 100,
            AccessibleName = "검수 작업 진행 상태",
            AccessibleDescription = "원문 갱신, 번역 또는 적용 작업의 진행률을 표시합니다."
        };
        var accent = new Panel { Dock = DockStyle.Bottom, Height = 3, Tag = "accent" };
        header.Controls.AddRange([projectName, projectPath, projectListButton, folderButton, saveButton, operationStatus, saveStatus, sourceRefresh, aiTranslate, stop, useRmk, applyApproved, applyTranslated, operationProgress, accent]);
        header.Resize += (_, _) => ResizeHeader(header, projectListButton, folderButton, saveButton);

        outerSplit = new LayoutSplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.None, SplitterWidth = 2 };
        innerSplit = new LayoutSplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.None, SplitterWidth = 2 };
        outerSplit.Panel2.Controls.Add(innerSplit);
        outerSplit.Panel1.Controls.Add(BuildListPane());
        innerSplit.Panel1.Controls.Add(BuildEditorPane());
        innerSplit.Panel2.Controls.Add(BuildSidePane());

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(outerSplit, 0, 1);
        Controls.Add(root);
        RegisterCommandToolTips();

        searchTimer = new System.Windows.Forms.Timer { Interval = 75 };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshFilter(true); };
        void RefreshResponsiveLayout()
        {
            if (disposed || IsDisposed) return;
            ResizeHeader(header, projectListButton, folderButton, saveButton);
            if (windowResizeInProgress) windowResizePending = true;
            else InitializeSplitters();
        }
        Resize += (_, _) => RefreshResponsiveLayout();
        HandleCreated += (_, _) => BeginInvoke((Action)RefreshResponsiveLayout);
        VisibleChanged += (_, _) =>
        {
            if (Visible && IsHandleCreated) BeginInvoke((Action)RefreshResponsiveLayout);
        };
        ResizeHeader(header, projectListButton, folderButton, saveButton);
    }

    private void RegisterCommandToolTips()
    {
        string? BusyOrUnavailable(bool available, string unavailableReason) => operationRunning
            ? "현재 작업이 실행 중이라 사용할 수 없습니다."
            : available ? null : unavailableReason;

        commandToolTips.Register(projectListButton, UiCommand.Projects,
            () => operationRunning ? "현재 작업이 실행 중이라 사용할 수 없습니다." : null);
        commandToolTips.Register(folderButton, UiCommand.OpenModFolder,
            () => BusyOrUnavailable(project is not null, "먼저 프로젝트를 여세요."));
        commandToolTips.Register(saveButton, UiCommand.SaveReview,
            () => BusyOrUnavailable(workspace is not null, "저장할 검수 작업이 없습니다."));
        commandToolTips.Register(sourceRefresh, UiCommand.RefreshSource,
            () => BusyOrUnavailable(project is not null, "먼저 프로젝트를 여세요."));
        commandToolTips.Register(aiTranslate, UiCommand.AiTranslate,
            () => BusyOrUnavailable(workspace is not null, "먼저 원문 분석을 완료하세요."));
        commandToolTips.Register(stop, UiCommand.StopOperation,
            () => stopRequested ? "중지 요청을 처리하고 있습니다." : "실행 중인 작업이 없습니다.");
        commandToolTips.Register(useRmk, UiCommand.UseRmk,
            () => operationRunning ? "현재 작업이 실행 중이라 변경할 수 없습니다." : null);
        commandToolTips.Register(applyApproved, UiCommand.ApplyApproved,
            () => BusyOrUnavailable(workspace is not null, "적용할 검수 작업이 없습니다."));
        commandToolTips.Register(applyTranslated, UiCommand.ApplyAll,
            () => BusyOrUnavailable(workspace is not null, "적용할 검수 작업이 없습니다."));

        commandToolTips.Register(search, UiCommand.ReviewSearch);
        commandToolTips.Register(searchField, UiCommand.SearchField);
        commandToolTips.Register(statusFilter, UiCommand.StatusFilter);
        commandToolTips.Register(sortMode, UiCommand.SortReview);
        foreach (var (button, _) in statusButtons)
            commandToolTips.Register(button, UiCommand.StatusFilter,
                description: () => $"{button.Text} 상태의 문자열만 표시합니다.");

        UiCommand[] utilityCommands =
        [
            UiCommand.PreviousReview,
            UiCommand.NextReview,
            UiCommand.UseCandidate,
            UiCommand.UseExisting,
            UiCommand.CopyTranslation,
            UiCommand.UndoTranslation
        ];
        for (var index = 0; index < utilityButtons.Length; index++)
            commandToolTips.Register(utilityButtons[index], utilityCommands[index]);

        UiCommand[] reviewCommands =
        [
            UiCommand.MarkPending,
            UiCommand.MarkTranslated,
            UiCommand.MarkApproved,
            UiCommand.MarkApprovedAndNext,
            UiCommand.ApproveAll
        ];
        for (var index = 0; index < reviewStatusButtons.Length; index++)
            commandToolTips.Register(reviewStatusButtons[index], reviewCommands[index]);

        commandToolTips.Register(rmkRefresh, UiCommand.RefreshRmk);
        commandToolTips.Register(rmkOpen, UiCommand.OpenRmk,
            () => workspace is null ? "먼저 프로젝트를 여세요." : "사용 가능한 RMK 작업 클론이 없습니다.");
        commandToolTips.Register(rmkBuild, UiCommand.BuildRmk,
            () => workspace is null ? "먼저 프로젝트를 여세요." : "검증된 쓰기 가능한 RMK 작업 클론이 없습니다.");
        commandToolTips.Register(qualityCategory, UiCommand.QualityFilter);
        commandToolTips.Register(qualityRefresh, UiCommand.RefreshQuality);
        commandToolTips.Register(exportQuality, UiCommand.ExportQuality,
            () => workspace is null ? "먼저 프로젝트를 여세요." : null);
    }

    private void ResizeHeader(Control header, Control projects, Control folder, Control save)
    {
        var width = Math.Max(1, ToLogical(header.ClientSize.Width));
        var wrapped = width < 1220;
        var desiredHeight = ToDevice(wrapped ? 128 : 78);
        if (Math.Abs(root.RowStyles[0].Height - desiredHeight) > 0.1f)
        {
            root.RowStyles[0].Height = desiredHeight;
            root.PerformLayout();
        }

        if (wrapped)
        {
            var wrappedWidths = new[] { 104, 92, 58, 98, 108 };
            const int wrappedGap = 8;
            var wrappedTotal = wrappedWidths.Sum() + wrappedGap * (wrappedWidths.Length - 1);
            var wrappedX = Math.Max(384, width - 24 - wrappedTotal);

            SetLogicalBounds(projectName, 24, 7, 200, 25);
            SetLogicalBounds(projectPath, 24, 34, 200, 20);
            SetLogicalBounds(projects, 236, 12, 74, 36);
            SetLogicalBounds(folder, 318, 12, 60, 36);
            SetLogicalBounds(save, 386, 12, 58, 36);
            folder.Visible = true;
            save.Visible = true;

            SetLogicalBounds(useRmk, 24, 78, 112, 24);
            operationStatus.Visible = true;
            saveStatus.Visible = true;
            SetLogicalBounds(operationStatus, 150, 66, Math.Max(100, wrappedX - 162), 20);
            SetLogicalBounds(saveStatus, 150, 92, Math.Max(100, wrappedX - 162), 20);

            SetLogicalBounds(sourceRefresh, wrappedX, 76, wrappedWidths[0], 36);
            SetLogicalBounds(aiTranslate, wrappedX + wrappedWidths[0] + wrappedGap, 76, wrappedWidths[1], 36);
            SetLogicalBounds(stop, wrappedX + wrappedWidths[0] + wrappedGap + wrappedWidths[1] + wrappedGap, 76, wrappedWidths[2], 36);
            SetLogicalBounds(applyApproved, wrappedX + wrappedWidths[0] + wrappedGap + wrappedWidths[1] + wrappedGap + wrappedWidths[2] + wrappedGap, 76, wrappedWidths[3], 36);
            SetLogicalBounds(applyTranslated, wrappedX + wrappedWidths[0] + wrappedGap + wrappedWidths[1] + wrappedGap + wrappedWidths[2] + wrappedGap + wrappedWidths[3] + wrappedGap, 76, wrappedWidths[4], 36);
            return;
        }

        var actionWidths = new[] { 96, 88, 54, 92, 100 };
        const int gap = 8;
        var actionTotal = actionWidths.Sum() + gap * (actionWidths.Length - 1);
        var actionX = Math.Max(434, width - 24 - actionTotal);
        var rmkX = Math.Max(330, actionX - 120);
        var showUtilities = actionX >= 646;
        var compact = actionX < 516;
        SetLogicalBounds(projectName, 24, 12, compact ? 200 : 286, 25);
        SetLogicalBounds(projectPath, 24, 42, compact ? 200 : 286, 18);
        SetLogicalBounds(projects, compact ? 236 : 330, 21, 74, 36);
        SetLogicalBounds(folder, 412, 21, 60, 36);
        SetLogicalBounds(save, 480, 21, 58, 36);
        folder.Visible = showUtilities;
        save.Visible = showUtilities;
        var showStatus = showUtilities && rmkX - 550 >= 100;
        operationStatus.Visible = showStatus;
        saveStatus.Visible = showStatus;
        var statusWidth = Math.Max(96, rmkX - 562);
        SetLogicalBounds(operationStatus, 550, 15, statusWidth, 20);
        SetLogicalBounds(saveStatus, 550, 39, statusWidth, 20);
        SetLogicalBounds(useRmk, rmkX, 27, 112, 24);
        SetLogicalBounds(sourceRefresh, actionX, 21, actionWidths[0], 36);
        SetLogicalBounds(aiTranslate, actionX + actionWidths[0] + gap, 21, actionWidths[1], 36);
        SetLogicalBounds(stop, actionX + actionWidths[0] + gap + actionWidths[1] + gap, 21, actionWidths[2], 36);
        SetLogicalBounds(applyApproved, actionX + actionWidths[0] + gap + actionWidths[1] + gap + actionWidths[2] + gap, 21, actionWidths[3], 36);
        SetLogicalBounds(applyTranslated, actionX + actionWidths[0] + gap + actionWidths[1] + gap + actionWidths[2] + gap + actionWidths[3] + gap, 21, actionWidths[4], 36);
    }

    public event EventHandler? BackRequested;
    public event EventHandler? OpenFolderRequested;
    public event EventHandler? SourceRefreshRequested;
    public event EventHandler? AiTranslateRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<WorkspaceApplyRequest>? ApplyRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? QualityExportRequested;
    public event EventHandler? RmkBuildRequested;

    public ReviewWorkspace? Workspace => workspace;
    public TranslationProject? Project => project;
    public bool HasPendingEditorChanges => editorTouched;
    internal int WorkspaceHeaderHeight => (int)Math.Round(root.RowStyles[0].Height);
    internal bool QualityListUsesVirtualizationForTesting => issues.VirtualMode;
    internal int VisibleQualityIssueCountForTesting => visibleQualityIssues.Count;
    internal TimeSpan LastQualityBuildDurationForTesting => lastQualityBuildDuration;
    internal int FilteredReviewCountForTesting => filtered.Count;
    internal int CurrentOriginalIndexForTesting => current?.OriginalIndex ?? -1;
    internal string CurrentKeyForTesting => current?.Row.Key ?? string.Empty;
    internal string SelectedSideTabForTesting => sideTabs.SelectedTab?.Text ?? string.Empty;
    internal bool UiDataWorkPendingForTesting => filterBusy || qualityBusy;
    internal bool FilterWorkPendingForTesting => filterBusy;
    internal bool QualityWorkPendingForTesting => qualityBusy;

    private float DpiScaleFactor => Math.Max(1f, DeviceDpi / 96f);
    private int ToDevice(int logical) => (int)Math.Round(logical * DpiScaleFactor);
    private int ToLogical(int device) => Math.Max(0, (int)Math.Floor(device / DpiScaleFactor));
    private void SetLogicalBounds(Control control, int x, int y, int width, int height)
    {
        var bounds = new Rectangle(ToDevice(x), ToDevice(y), ToDevice(width), ToDevice(height));
        if (control.Bounds != bounds) control.Bounds = bounds;
    }
    internal void SetWindowResizeInProgress(bool inProgress)
    {
        windowResizeInProgress = inProgress;
        if (inProgress)
        {
            windowResizePending = false;
            return;
        }
        if (windowResizePending && !disposed && !IsDisposed) InitializeSplitters();
        windowResizePending = false;
    }
    internal bool LastFilterUsedWorkerThreadForTesting => lastFilterUsedWorkerThread;
    internal bool LastQualityUsedWorkerThreadForTesting => lastQualityUsedWorkerThread;
    internal bool TranslationTouchedForTesting => translationTouched;
    internal string PendingEditOriginForTesting => pendingEditOrigin;
    internal string CommandToolTipTextForTesting(Control control) => commandToolTips.GetText(control);
    internal int CommandToolTipRegistrationCountForTesting(Control control) => commandToolTips.RegistrationCount(control);
    internal bool CommandToolTipFitsForTesting(Control control, int dpi, Size workingArea) =>
        commandToolTips.FitsWorkingAreaForTesting(control, dpi, workingArea);

    public void SetGlossary(IReadOnlyList<GlossaryTerm> termsList) => glossary = termsList;

    public void SetWorkspace(TranslationProject selectedProject, ReviewWorkspace loaded)
    {
        if (!IsHandleCreated) _ = Handle;
        SaveCurrentEditor(false);
        project = selectedProject;
        workspace = loaded;
        current = null;
        filtered = [];
        list.Items.Clear();
        translationMemorySourceIndex = loaded.Items
            .GroupBy(item => item.Row.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        projectName.Text = selectedProject.Name;
        projectPath.Text = selectedProject.ModRoot;
        crumb.Text = $"{selectedProject.Name}\r\n전체 문자열  ·  모든 상태";
        fileFilter.BeginUpdate();
        fileFilter.Items.Clear();
        fileFilter.Items.Add(new Choice<string>("전체 파일", string.Empty));
        foreach (var file in loaded.Items.Select(item => item.RelativeTarget).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            fileFilter.Items.Add(new Choice<string>(file, file));
        fileFilter.SelectedIndex = 0;
        fileFilter.EndUpdate();
        BuildQualityIssues();
        RefreshRmkPanel();
        RefreshFilter(false);
        operationStatus.Text = $"불러옴 · {loaded.Items.Count:N0}개 문자열";
        if (loaded.Dirty) SetSavePending();
        else SetSaveSucceeded(false);
        commandToolTips.RefreshAll();
    }

    public void ClearWorkspace()
    {
        filterRevision++;
        qualityRevision++;
        rmkStatusRevision++;
        filterCancellation?.Cancel();
        qualityCancellation?.Cancel();
        filterCancellation = null;
        qualityCancellation = null;
        filterBusy = false;
        qualityBusy = false;
        searchTimer.Stop();

        project = null;
        workspace = null;
        current = null;
        filtered = [];
        qualityIssues = [];
        visibleQualityIssues = [];
        translationMemorySourceIndex = new Dictionary<string, ReviewItem[]>(StringComparer.Ordinal);
        list.Items.Clear();
        issues.VirtualListSize = 0;
        ClearEditor();
        projectName.Text = "프로젝트 없음";
        projectPath.Text = string.Empty;
        crumb.Text = "프로젝트를 선택하세요";
        summary.Text = "표시할 검수 작업이 없습니다";
        qualitySummary.Text = "검수 작업을 열면 품질 검사를 시작합니다.";
        selectedQuality.Text = "문제를 선택하면 상세 내용을 표시합니다.";
        rmkStatus.Text = "검수 작업을 열면 RMK 상태를 확인합니다.";
        SetRunning(false, "준비됨");
        SetSaveSucceeded(false);
        commandToolTips.RefreshAll();
    }

    public void SetRunning(bool running, string message = "")
    {
        if (running && !operationRunning) SaveEditor(false);
        operationRunning = running;
        stopRequested = false;
        outerSplit.Enabled = !running;
        projectListButton.Enabled = !running;
        folderButton.Enabled = !running && project is not null;
        saveButton.Enabled = !running && workspace is not null;
        useRmk.Enabled = !running;
        aiTranslate.Enabled = !running && workspace is not null;
        sourceRefresh.Enabled = !running && project is not null;
        applyApproved.Enabled = !running && workspace is not null;
        applyTranslated.Enabled = !running && workspace is not null;
        stop.Enabled = running;
        stop.Text = "중지";
        stop.AccessibleName = "중지";
        operationProgress.Style = running ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        operationProgress.MarqueeAnimationSpeed = running ? 24 : 0;
        if (!string.IsNullOrWhiteSpace(message)) operationStatus.Text = message;
        commandToolTips.RefreshAll();
    }

    public void UpdateProgress(TranslationProgress progress)
    {
        var percent = progress.Total > 0
            ? Math.Clamp((int)Math.Round(progress.Current * 100d / progress.Total), 0, 100)
            : -1;
        if (!stopRequested)
        {
            operationStatus.Text = percent >= 0
                ? $"{progress.Message} · {percent}%"
                : progress.Message;
        }
        if (progress.Total > 0)
        {
            operationProgress.Style = ProgressBarStyle.Blocks;
            operationProgress.Value = percent;
        }
    }

    public void AppendLog(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(line)); return; }
        log.AppendText(line + Environment.NewLine);
        if (log.TextLength > 300_000) log.Text = log.Text[^200_000..];
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }

    public void SaveCurrentEditor(bool promptDuplicates) => SaveEditor(promptDuplicates);

    public void SetSavePending()
    {
        SetSaveStatus(
            services.Settings.AutoSave ? "자동 저장 대기" : "저장 필요",
            "warning");
    }

    public void SetSaveInProgress(bool automatic) => SetSaveStatus(
        automatic ? "자동 저장 중" : "저장 중",
        "header-muted");

    public void SetSaveSucceeded(bool automatic)
    {
        var label = automatic ? "자동 저장됨" : "저장됨";
        SetSaveStatus($"{label} {DateTime.Now:HH:mm:ss}", "header-muted");
    }

    public void SetSaveFailed(bool automatic) => SetSaveStatus(
        automatic ? "자동 저장 실패" : "저장 실패",
        "danger-text");

    public void SetCloseSaveInProgress() => SetSaveStatus("종료 전 저장 중", "warning");

    public IReadOnlyList<string> GetRecentLogLines() => log.Lines.TakeLast(20_000).ToArray();

    public void RefreshThemeState()
    {
        SaveEditor(false);
        list.Invalidate();
        if (current is not null) ShowCurrent();
        commandToolTips.RefreshAll();
    }

    public void FocusSearch()
    {
        search.Focus();
        search.SelectAll();
    }

    public void FocusNextWorkRegion(int direction)
    {
        Control[] targets = [search, translation, sideTabs];
        var available = targets.Where(control => control.Visible && control.Enabled).ToArray();
        if (available.Length == 0) return;
        var currentIndex = Array.FindIndex(available, control => control.Focused || control.ContainsFocus);
        var next = (currentIndex + (direction < 0 ? -1 : 1)) % available.Length;
        if (next < 0) next += available.Length;
        available[next].Focus();
    }

    public void SelectSideTab(string name)
    {
        var page = sideTabs.TabPages.Cast<TabPage>().FirstOrDefault(item => item.Text.Equals(name, StringComparison.Ordinal));
        if (page is not null) sideTabs.SelectedTab = page;
    }

    public IReadOnlyList<QualityEntry> GetQualityEntries() => workspace is null
        ? []
        : services.Reviews.CreateQualityEntries(workspace);

    public bool HandleShortcut(Keys keyData)
    {
        if (operationRunning) return false;
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if (UiCommandCatalog.Matches(UiCommand.SaveReview, keyData)) { CommitWithDuplicatePrompt(); return true; }
        if (UiCommandCatalog.Matches(UiCommand.ReviewSearch, keyData)) { FocusSearch(); return true; }
        if (UiCommandCatalog.Matches(UiCommand.MarkApprovedAndNext, keyData)) { SetCurrentStatus(ReviewStatuses.Approved, true); return true; }
        if (UiCommandCatalog.Matches(UiCommand.MarkApproved, keyData)) { SetCurrentStatus(ReviewStatuses.Approved, false); return true; }
        if (UiCommandCatalog.Matches(UiCommand.MarkPending, keyData)) { SetCurrentStatus(ReviewStatuses.Pending, false); return true; }
        if (UiCommandCatalog.Matches(UiCommand.MarkTranslated, keyData)) { SetCurrentStatus(ReviewStatuses.Translated, false); return true; }
        if (UiCommandCatalog.Matches(UiCommand.ApproveAll, keyData)) { ApproveAllSafe(); return true; }
        if (UiCommandCatalog.Matches(UiCommand.UseCandidate, keyData)) { UseCandidate(); return true; }
        if (UiCommandCatalog.Matches(UiCommand.UseExisting, keyData)) { UseExisting(); return true; }
        if (UiCommandCatalog.Matches(UiCommand.UndoTranslation, keyData)) { UndoEdit(); return true; }
        if (modifiers == Keys.None && key == Keys.F2)
        {
            if (translation.Enabled)
            {
                translation.Focus();
                translation.SelectionStart = translation.TextLength;
            }
            return true;
        }
        if (UiCommandCatalog.Matches(UiCommand.PreviousReview, keyData)) { MoveSelection(-1); return true; }
        if (UiCommandCatalog.Matches(UiCommand.NextReview, keyData)) { MoveSelection(1); return true; }
        if (modifiers == Keys.None && key == Keys.Escape)
        {
            if (search.TextLength == 0 && statusFilter.SelectedIndex <= 0) return false;
            search.Clear();
            if (statusFilter.Items.Count > 0) statusFilter.SelectedIndex = 0;
            FocusSearch();
            return true;
        }
        return false;
    }

    private Control BuildListPane()
    {
        var panel = new BufferedPanel { Dock = DockStyle.Fill, Tag = "surface" };
        crumb = Ui.Label("모드\r\n전체 문자열  ·  모든 상태", 10.5f, FontStyle.Bold);
        crumb.AutoSize = false;
        crumb.Padding = new Padding(14, 7, 12, 5);
        crumb.Tag = "selection";
        search = Ui.TextBox("검색어 입력");
        search.AccessibleName = "문자열 검색";
        search.AccessibleDescription = "원문, 번역문 또는 키를 검색합니다. 단축키 Ctrl+F.";
        search.Margin = Padding.Empty;
        search.TabIndex = 0;
        search.TextChanged += (_, _) => RestartSearchTimer();
        search.KeyDown += (_, e) =>
        {
            if (e.Modifiers != Keys.None || e.KeyCode is not (Keys.Up or Keys.Down or Keys.Enter)) return;
            searchTimer.Stop();
            RefreshFilter(true);
            if (e.KeyCode == Keys.Up) MoveSelection(-1);
            else if (e.KeyCode == Keys.Down) MoveSelection(1);
            else
            {
                if (current is null && filtered.Count > 0) SelectFilteredIndex(0);
                if (current is not null)
                {
                    translation.Focus();
                    translation.SelectionStart = translation.TextLength;
                }
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
        searchField = NewChoice([
            new Choice<ReviewSearchField>("텍스트/키", ReviewSearchField.All),
            new Choice<ReviewSearchField>("텍스트", ReviewSearchField.Text),
            new Choice<ReviewSearchField>("키", ReviewSearchField.Key),
            new Choice<ReviewSearchField>("Def Class", ReviewSearchField.DefClass),
            new Choice<ReviewSearchField>("Node", ReviewSearchField.Node)
        ]);
        searchField.Dock = DockStyle.None;
        searchField.AccessibleName = "검색 대상";
        searchField.DropDownWidth = 128;
        searchField.TabIndex = 1;
        searchField.SelectedIndexChanged += (_, _) => RestartSearchTimer();
        statusFilter = NewChoice([
            new Choice<ReviewStatusFilter>("전체", ReviewStatusFilter.All),
            new Choice<ReviewStatusFilter>("미번역", ReviewStatusFilter.Pending),
            new Choice<ReviewStatusFilter>("번역됨", ReviewStatusFilter.Translated),
            new Choice<ReviewStatusFilter>("검토됨", ReviewStatusFilter.Approved),
            new Choice<ReviewStatusFilter>("업데이트로 변경됨", ReviewStatusFilter.Updated),
            new Choice<ReviewStatusFilter>("RMK 가져옴", ReviewStatusFilter.Rmk),
            new Choice<ReviewStatusFilter>("내 번역", ReviewStatusFilter.Local),
            new Choice<ReviewStatusFilter>("주의", ReviewStatusFilter.Warning),
            new Choice<ReviewStatusFilter>("후보 있음", ReviewStatusFilter.HasCandidate),
            new Choice<ReviewStatusFilter>("기존 있음", ReviewStatusFilter.HasExisting)
        ]);
        statusFilter.Dock = DockStyle.None;
        statusFilter.AccessibleName = "검수 상태 필터";
        statusFilter.DropDownWidth = 168;
        statusFilter.TabIndex = 2;
        statusFilter.SelectedIndexChanged += (_, _) =>
        {
            SyncStatusButtons((statusFilter.SelectedItem as Choice<ReviewStatusFilter>)?.Value ?? ReviewStatusFilter.All);
            RefreshFilter(true);
        };
        fileFilter = new ComboBox { Visible = false, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(Choice<string>.Text) };
        fileFilter.SelectedIndexChanged += (_, _) => RefreshFilter(true);
        sortMode = NewChoice([
            new Choice<ReviewSortMode>("기본 순서", ReviewSortMode.Default),
            new Choice<ReviewSortMode>("내 번역 최신순", ReviewSortMode.TranslationNewest),
            new Choice<ReviewSortMode>("내 번역 오래된순", ReviewSortMode.TranslationOldest)
        ]);
        sortMode.Dock = DockStyle.None;
        sortMode.AccessibleName = "문자열 정렬 순서";
        sortMode.DropDownWidth = 190;
        sortMode.TabIndex = 4;
        sortMode.SelectedIndexChanged += (_, _) => RefreshFilter(true);

        var filterButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        filterButtons.TabIndex = 3;
        foreach (var spec in new[]
                 {
                     ("전체", ReviewStatusFilter.All, 48), ("미번역", ReviewStatusFilter.Pending, 60),
                     ("번역됨", ReviewStatusFilter.Translated, 60), ("검토됨", ReviewStatusFilter.Approved, 60),
                     ("변경됨", ReviewStatusFilter.Updated, 60)
                 })
        {
            var button = Ui.Button(spec.Item1, spec.Item2 == ReviewStatusFilter.All ? "primary" : null, spec.Item3);
            button.Size = new Size(spec.Item3, 30);
            button.Margin = new Padding(0, 0, 4, 0);
            button.Font = new Font("Malgun Gothic", 8f, FontStyle.Bold);
            statusButtons[button] = spec.Item2;
            button.Click += (_, _) => SelectStatusFilter(spec.Item2);
            filterButtons.Controls.Add(button);
        }
        summary = Ui.Label("전체 0", 8.5f, FontStyle.Bold);
        summary.AutoSize = false;
        list = new BufferedListBox
        {
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 94,
            IntegralHeight = false,
            BorderStyle = BorderStyle.None,
            SelectionMode = SelectionMode.One,
            HorizontalScrollbar = false,
            Font = new Font("Malgun Gothic", 9f),
            AccessibleName = "검색된 문자열 목록"
        };
        list.AccessibleDescription = "검색과 필터에 맞는 문자열을 선택해 번역을 검수합니다.";
        list.TabIndex = 5;
        list.DrawItem += DrawReviewItem;
        list.SelectedIndexChanged += (_, _) => SelectionChanged();
        list.DoubleClick += (_, _) => translation.Focus();
        panel.Controls.AddRange([crumb, searchField, search, statusFilter, filterButtons, sortMode, summary, list, fileFilter]);
        panel.Resize += (_, _) =>
        {
            var width = Math.Max(340, ToLogical(panel.ClientSize.Width));
            var height = ToLogical(panel.ClientSize.Height);
            var inner = Math.Max(268, width - 32);
            SetLogicalBounds(crumb, 16, 16, inner, 64);
            SetLogicalBounds(searchField, 16, 92, 104, 32);
            const int statusWidth = 122;
            var statusX = width - statusWidth - 16;
            SetLogicalBounds(search, 120, 92, Math.Max(72, statusX - 128), 32);
            SetLogicalBounds(statusFilter, statusX, 92, statusWidth, 32);
            SetLogicalBounds(filterButtons, 16, 136, inner, 30);
            int[] filterWidths = [48, 60, 60, 60, 60];
            for (var index = 0; index < filterButtons.Controls.Count && index < filterWidths.Length; index++)
            {
                filterButtons.Controls[index].Size = new Size(ToDevice(filterWidths[index]), ToDevice(30));
                filterButtons.Controls[index].Margin = new Padding(0, 0, ToDevice(4), 0);
            }
            SetLogicalBounds(sortMode, 16, 174, inner, 32);
            SetLogicalBounds(summary, 16, 214, inner, 42);
            list.ItemHeight = ToDevice(94);
            SetLogicalBounds(list, 16, 266, inner, Math.Max(1, height - 282));
        };
        return panel;
    }

    private void SelectStatusFilter(ReviewStatusFilter value)
    {
        for (var index = 0; index < statusFilter.Items.Count; index++)
        {
            if (statusFilter.Items[index] is not Choice<ReviewStatusFilter> choice || choice.Value != value) continue;
            if (statusFilter.SelectedIndex == index)
            {
                SyncStatusButtons(value);
                RefreshFilter(true);
            }
            else
            {
                statusFilter.SelectedIndex = index;
            }
            return;
        }
    }

    private void SyncStatusButtons(ReviewStatusFilter selected)
    {
        foreach (var (button, value) in statusButtons)
        {
            button.Tag = value == selected ? "primary" : null;
            ThemeManager.Apply(button, ThemeManager.Current, services.Settings.TextSize);
        }
    }

    private Control BuildEditorPane()
    {
        var panel = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "surface" };
        itemStatus = Ui.Label("문자열을 선택하세요", 11f, FontStyle.Bold);
        itemStatus.AutoSize = false;
        itemStatus.AutoEllipsis = true;
        itemStatus.AccessibleName = "문자열 상태";
        itemStatus.AccessibleDescription = "선택한 문자열의 순서, 검수 상태, 번역 출처와 주의 항목 수를 표시합니다.";
        updateBadge = Ui.Label("업데이트로 변경됨", 8.5f, FontStyle.Bold);
        updateBadge.AutoSize = false;
        updateBadge.TextAlign = ContentAlignment.MiddleRight;
        updateBadge.Tag = "warning";
        updateBadge.AccessibleName = "원문 업데이트 없음";
        updateBadge.AccessibleDescription = "선택한 문자열에서 원문 변경이 감지되지 않았습니다.";
        updateBadge.Visible = false;
        var sourceLabel = Ui.Label("원문", 9f, FontStyle.Bold);
        sourceLabel.AutoSize = false;
        var sourceFrame = new Panel { BorderStyle = BorderStyle.FixedSingle, Tag = "surface" };
        source = NewRichText(true);
        source.AccessibleName = "선택 문자열 원문";
        source.AccessibleDescription = "현재 문자열의 읽기 전용 원문입니다. 두 번 클릭하면 전체 원문이 복사됩니다.";
        source.Dock = DockStyle.None;
        source.BorderStyle = BorderStyle.None;
        source.Tag = "readonly";
        source.DoubleClick += (_, _) => CopyEntireReadOnlyText(source);
        sourceFrame.TabIndex = 0;
        source.TabIndex = 0;
        sourceFrame.Controls.Add(source);
        translationLabel = Ui.Label("번역문", 9f, FontStyle.Bold);
        translationLabel.AutoSize = false;
        translationLabel.Tag = "muted";
        var translationFrame = new Panel { BorderStyle = BorderStyle.FixedSingle, Tag = "surface" };
        translation = NewRichText(false);
        translation.AccessibleName = "번역문 편집";
        translation.AccessibleDescription = "선택된 문자열의 한국어 번역문을 편집합니다. 단축키 F2.";
        translation.Dock = DockStyle.None;
        translation.BorderStyle = BorderStyle.None;
        translationFrame.TabIndex = 1;
        translation.TabIndex = 0;
        translation.TextChanged += (_, _) =>
        {
            if (suppressEditor) return;
            editorTouched = true;
            translationTouched = true;
            pendingEditOrigin = "local";
            SetSavePending();
            if (current is not null) UpdateDiffView(current, translation.Text);
        };
        translation.Enter += (_, _) =>
        {
            translationLabel.Text = "번역문  ·  편집 중";
            translationLabel.ForeColor = ThemeManager.ReadableForeground(
                ThemeManager.Current.Surface,
                ThemeManager.Current.Accent,
                ThemeManager.Current.Text);
        };
        translation.Leave += (_, _) => UpdateTranslationTitle();
        var translationAccent = new Panel { Height = 3, Tag = "accent" };
        translationFrame.Controls.AddRange([translation, translationAccent]);
        metadata = NewRichText(true);
        metadata.Text = "Def Class : -\r\nNode : -\r\n파일  -\r\nID  -    ·    출처 없음    ·    단어 0    ·    안전 적용 아니오";
        metadata.AccessibleName = "문자열 정보";
        metadata.AccessibleDescription = "현재 문자열의 Def Class, Node, 파일, ID, 번역 출처, 단어 수와 안전 적용 여부입니다. Tab으로 이동한 뒤 Ctrl+A와 Ctrl+C로 복사하거나 두 번 클릭하면 전체 정보가 복사됩니다.";
        metadata.AccessibleRole = AccessibleRole.Text;
        metadata.Dock = DockStyle.None;
        metadata.BorderStyle = BorderStyle.None;
        metadata.ScrollBars = RichTextBoxScrollBars.Vertical;
        metadata.Tag = "readonly";
        metadata.TabIndex = 2;
        metadata.DoubleClick += (_, _) => CopyEntireReadOnlyText(metadata);
        var divider = new Panel { Height = 1, Tag = "divider" };

        Button Tool(string text, string? role, Action action)
        {
            var button = Ui.Button(text, role, 72);
            button.Margin = Padding.Empty;
            button.Font = new Font("Malgun Gothic", 8.3f, FontStyle.Regular);
            button.Click += (_, _) => action();
            panel.Controls.Add(button);
            return button;
        }
        utilityButtons =
        [
            Tool("‹", null, () => MoveSelection(-1)), Tool("›", null, () => MoveSelection(1)),
            Tool("AI 후보", "accent-soft", UseCandidate), Tool("기존", "accent-soft", UseExisting),
            Tool("복사", null, CopyTranslation), Tool("되돌리기", null, UndoEdit)
        ];
        utilityButtons[0].AccessibleName = "이전 문자열";
        utilityButtons[0].AccessibleDescription = "이전 검색 결과로 이동합니다. 단축키 Shift+F3 또는 Alt+위쪽 화살표.";
        utilityButtons[1].AccessibleName = "다음 문자열";
        utilityButtons[1].AccessibleDescription = "다음 검색 결과로 이동합니다. 단축키 F3 또는 Alt+아래쪽 화살표.";
        utilityButtons[2].AccessibleDescription = "AI 후보를 편집기에 넣습니다. 단축키 Alt+1.";
        utilityButtons[3].AccessibleDescription = "기존 번역을 편집기에 넣습니다. 단축키 Alt+2.";
        utilityButtons[4].AccessibleDescription = "현재 번역문을 클립보드에 복사합니다.";
        utilityButtons[5].AccessibleDescription = "저장된 번역문으로 되돌립니다. 단축키 Alt+0.";
        reviewStatusButtons =
        [
            Tool("미번역", null, () => SetCurrentStatus(ReviewStatuses.Pending, false)),
            Tool("번역됨", "accent-soft", () => SetCurrentStatus(ReviewStatuses.Translated, false)),
            Tool("검토 완료", "success", () => SetCurrentStatus(ReviewStatuses.Approved, false)),
            Tool("완료 후 다음", "success", () => SetCurrentStatus(ReviewStatuses.Approved, true)),
            Tool("전체 검토", "warm-soft", ApproveAllSafe)
        ];
        string[] statusDescriptions =
        [
            "현재 문자열을 미번역 상태로 표시합니다. 단축키 Ctrl+1.",
            "현재 문자열을 번역됨 상태로 표시합니다. 단축키 Ctrl+2.",
            "현재 문자열을 검토 완료 상태로 표시합니다. 단축키 Ctrl+3 또는 Ctrl+Shift+Enter.",
            "현재 문자열을 검토 완료 상태로 표시하고 다음 검색 결과로 이동합니다. 단축키 Ctrl+Enter.",
            "경고가 없고 안전한 번역을 모두 검토 완료 상태로 표시합니다. 단축키 Ctrl+Shift+3."
        ];
        for (var index = 0; index < utilityButtons.Length; index++) utilityButtons[index].TabIndex = index + 3;
        for (var index = 0; index < reviewStatusButtons.Length; index++)
        {
            reviewStatusButtons[index].AccessibleDescription = statusDescriptions[index];
            reviewStatusButtons[index].TabIndex = utilityButtons.Length + index + 3;
        }
        var referenceLabel = Ui.Label("참고 번역", 8.5f, FontStyle.Bold);
        referenceLabel.AutoSize = false;
        existingLabel = Ui.Label("기존 번역", 9f, FontStyle.Bold);
        existingLabel.AutoSize = false;
        var candidateLabel = Ui.Label("AI 번역 후보", 9f, FontStyle.Bold);
        candidateLabel.AutoSize = false;
        existing = NewRichText(true);
        existing.AccessibleName = "기존 번역";
        existing.AccessibleDescription = "모드 또는 RMK에서 가져온 기존 한국어 번역입니다. 두 번 클릭하면 전체 번역이 복사됩니다.";
        existing.Dock = DockStyle.None;
        existing.Tag = "readonly";
        existing.TabIndex = utilityButtons.Length + reviewStatusButtons.Length + 3;
        existing.DoubleClick += (_, _) => CopyEntireReadOnlyText(existing);
        candidate = NewRichText(true);
        candidate.AccessibleName = "AI 번역 후보";
        candidate.AccessibleDescription = "번역 제공자가 만든 읽기 전용 초벌 번역입니다. 두 번 클릭하면 전체 후보가 복사됩니다.";
        candidate.Dock = DockStyle.None;
        candidate.Tag = "readonly";
        candidate.TabIndex = existing.TabIndex + 1;
        candidate.DoubleClick += (_, _) => CopyEntireReadOnlyText(candidate);
        memory = new ListBox { Visible = false, DisplayMember = nameof(MemoryChoice.Text) };
        memory.DoubleClick += (_, _) => { if (memory.SelectedItem is MemoryChoice choice) SetEditorText(choice.Text); };
        panel.Controls.AddRange([itemStatus, updateBadge, sourceLabel, sourceFrame, translationLabel, translationFrame,
            metadata, divider, referenceLabel, existingLabel, candidateLabel, existing, candidate]);

        panel.Resize += (_, _) =>
        {
            var logicalWidth = ToLogical(panel.ClientSize.Width);
            var logicalHeight = ToLogical(panel.ClientSize.Height);
            var pad = logicalWidth < 520 ? 18 : 24;
            var contentWidth = Math.Max(300, logicalWidth - pad * 2);
            var contentHeight = Math.Max(420, logicalHeight);
            var ultraCompact = logicalHeight < 500;
            var veryCompact = contentHeight < 660;
            var compact = contentHeight < 760;
            var sourceHeight = ultraCompact ? 48 : veryCompact ? 72 : compact ? 92 : 118;
            var translationHeight = ultraCompact ? 60 : veryCompact ? 112 : compact ? 148 : 190;
            var metaHeight = ultraCompact ? 34 : veryCompact ? 82 : 92;
            var itemY = ultraCompact ? 8 : 18;
            var sourceLabelY = ultraCompact ? 36 : 56;
            var sourceBoxY = ultraCompact ? 58 : 80;
            SetLogicalBounds(itemStatus, pad, itemY, Math.Max(180, contentWidth - 178), ultraCompact ? 24 : 28);
            SetLogicalBounds(updateBadge, pad + contentWidth - 168, itemY, 168, ultraCompact ? 24 : 26);
            SetLogicalBounds(sourceLabel, pad, sourceLabelY, contentWidth, 20);
            SetLogicalBounds(sourceFrame, pad, sourceBoxY, contentWidth, sourceHeight);
            SetLogicalBounds(
                source,
                ultraCompact ? 6 : 11,
                ultraCompact ? 5 : 9,
                Math.Max(120, contentWidth - (ultraCompact ? 12 : 24)),
                Math.Max(34, sourceHeight - (ultraCompact ? 10 : 18)));
            var translationLabelY = ultraCompact ? 110 : 80 + sourceHeight + 18;
            var translationBoxY = translationLabelY + (ultraCompact ? 20 : 24);
            SetLogicalBounds(translationLabel, pad, translationLabelY, contentWidth, 20);
            SetLogicalBounds(translationFrame, pad, translationBoxY, contentWidth, translationHeight);
            SetLogicalBounds(
                translation,
                ultraCompact ? 6 : 11,
                ultraCompact ? 5 : 9,
                Math.Max(120, contentWidth - (ultraCompact ? 12 : 24)),
                Math.Max(42, translationHeight - (ultraCompact ? 12 : 20)));
            translationAccent.SetBounds(0, Math.Max(0, translationFrame.ClientSize.Height - ToDevice(3)), Math.Max(1, translationFrame.ClientSize.Width), ToDevice(3));
            var metaY = translationBoxY + translationHeight + (ultraCompact ? 6 : 14);
            SetLogicalBounds(metadata, pad + 4, metaY + (ultraCompact ? 0 : 3), contentWidth - 8, Math.Max(34, metaHeight - (ultraCompact ? 0 : 3)));
            var dividerY = metaY + metaHeight + (ultraCompact ? 4 : 8);
            SetLogicalBounds(divider, pad, dividerY, contentWidth, 1);
            var toolbarY = dividerY + (ultraCompact ? 6 : 12);
            var utilityWidths = ultraCompact ? new[] { 36, 36, 66, 54, 50, 62 } : new[] { 38, 38, 72, 62, 58, 72 };
            utilityButtons[5].Text = ultraCompact ? "↶" : "되돌리기";
            var stateWidths = ultraCompact ? new[] { 64, 68, 80, 108, 80 } : new[] { 72, 76, 88, 108, 88 };
            var gap = ultraCompact ? 5 : 7;
            var toolbarHeight = ultraCompact ? 30 : 36;
            var utilityTotal = utilityWidths.Sum() + gap * (utilityWidths.Length - 1);
            var statusTotal = stateWidths.Sum() + gap * (stateWidths.Length - 1);
            var x = pad;
            for (var index = 0; index < utilityButtons.Length; index++)
            {
                SetLogicalBounds(utilityButtons[index], x, toolbarY, utilityWidths[index], toolbarHeight);
                x += utilityWidths[index] + gap;
            }
            int statusY;
            int toolbarBottom;
            if (contentWidth >= utilityTotal + statusTotal + 22)
            {
                x = pad + contentWidth - statusTotal;
                statusY = toolbarY;
                toolbarBottom = toolbarY + toolbarHeight;
            }
            else
            {
                x = pad;
                statusY = toolbarY + (ultraCompact ? 34 : 44);
                toolbarBottom = statusY + toolbarHeight;
            }
            for (var index = 0; index < reviewStatusButtons.Length; index++)
            {
                SetLogicalBounds(reviewStatusButtons[index], x, statusY, stateWidths[index], toolbarHeight);
                x += stateWidths[index] + gap;
            }
            var naturalReferenceTitleY = toolbarBottom + (ultraCompact ? 6 : 17);
            var referenceTitleY = ultraCompact && logicalHeight - naturalReferenceTitleY < 20
                ? logicalHeight + 2
                : naturalReferenceTitleY;
            var suggestionLabelY = referenceTitleY + (ultraCompact ? 19 : 25);
            SetLogicalBounds(referenceLabel, pad, referenceTitleY, contentWidth, 20);
            var halfWidth = Math.Max(140, (contentWidth - 14) / 2);
            var bottomBoxY = suggestionLabelY + (ultraCompact ? 21 : 24);
            var minimumBottomHeight = ultraCompact ? 52 : 76;
            var bottomMargin = ultraCompact ? 8 : 18;
            var bottomHeight = Math.Max(minimumBottomHeight, logicalHeight - bottomBoxY - bottomMargin);
            SetLogicalBounds(existingLabel, pad, suggestionLabelY, halfWidth, 20);
            SetLogicalBounds(existing, pad, bottomBoxY, halfWidth, bottomHeight);
            var candidateX = pad + halfWidth + 14;
            SetLogicalBounds(candidateLabel, candidateX, suggestionLabelY, halfWidth, 20);
            SetLogicalBounds(candidate, candidateX, bottomBoxY, halfWidth, bottomHeight);
            panel.AutoScrollMinSize = new Size(0, ToDevice(bottomBoxY + Math.Max(minimumBottomHeight, bottomHeight) + bottomMargin));
        };
        return panel;
    }

    private Control BuildSidePane()
    {
        var panel = new BufferedPanel { Dock = DockStyle.Fill, Tag = "surface" };
        sideTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Multiline = false,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(44, 38),
            DrawMode = TabDrawMode.OwnerDrawFixed,
            Padding = new Point(2, 3),
            Font = new Font("Malgun Gothic", 8.5f, FontStyle.Bold)
        };
        sideTabs.AccessibleName = "검수 도구 탭";
        sideTabs.AccessibleDescription = "번역 비교, 용어, 메모, RMK, 품질 검사와 로그를 전환합니다.";
        sideTabs.DrawItem += (_, e) =>
        {
            var theme = ThemeManager.Current;
            var selected = e.Index == sideTabs.SelectedIndex;
            using var background = new SolidBrush(theme.Surface);
            e.Graphics.FillRectangle(background, e.Bounds);
            if (selected)
            {
                using var accent = new SolidBrush(theme.Accent);
                e.Graphics.FillRectangle(accent, e.Bounds.X + 8, e.Bounds.Bottom - 3, Math.Max(8, e.Bounds.Width - 16), 3);
            }
            var tabText = ThemeManager.ReadableForeground(
                theme.Surface,
                selected ? theme.Accent : theme.Muted,
                theme.Text);
            TextRenderer.DrawText(e.Graphics, sideTabs.TabPages[e.Index].Text, sideTabs.Font, e.Bounds,
                tabText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (selected && sideTabs.Focused)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -4, -4), theme.Text, theme.Surface);
        };
        sideTabs.Resize += (_, _) =>
        {
            if (sideTabs.TabPages.Count == 0) return;
            var available = Math.Max(220, ToLogical(sideTabs.ClientSize.Width) - 100);
            var desired = new Size(ToDevice(Math.Max(44, available / sideTabs.TabPages.Count)), ToDevice(38));
            if (sideTabs.ItemSize != desired) sideTabs.ItemSize = desired;
        };

        var compare = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "surface" };
        diffSummary = Ui.Label("비교할 문자열을 선택하세요", 9f, FontStyle.Bold);
        diffSummary.AutoSize = false;
        var diffSourceLabel = Ui.Label("원문", 8.2f, FontStyle.Bold);
        diffBeforeLabel = Ui.Label("기존 번역", 8.2f, FontStyle.Bold);
        var diffCurrentLabel = Ui.Label("현재 번역", 8.2f, FontStyle.Bold);
        var historyLabel = Ui.Label("번역 기록", 8.2f, FontStyle.Bold);
        foreach (var label in new[] { diffSourceLabel, diffBeforeLabel, diffCurrentLabel, historyLabel }) label.AutoSize = false;
        diffSource = NewRichText(true); diffSource.Dock = DockStyle.None; diffSource.Tag = "readonly";
        diffSource.AccessibleName = "비교 원문";
        diffSource.AccessibleDescription = "현재 문자열의 원문입니다.";
        diffExisting = NewRichText(true); diffExisting.Dock = DockStyle.None;
        diffExisting.AccessibleName = "비교 기준 번역";
        diffExisting.AccessibleDescription = "기존 번역 또는 AI 후보이며 현재 번역과 달라진 부분이 강조됩니다.";
        diffCurrent = NewRichText(true); diffCurrent.Dock = DockStyle.None;
        diffCurrent.AccessibleName = "현재 번역 비교";
        diffCurrent.AccessibleDescription = "현재 검수 번역이며 비교 기준과 달라진 부분이 강조됩니다.";
        history = NewRichText(true);
        history.AccessibleName = "번역 상세 기록";
        history.AccessibleDescription = "원문, 기존 번역, AI 후보와 현재 검수 번역의 전체 기록입니다.";
        history.Dock = DockStyle.None;
        history.Tag = "readonly";
        compare.Controls.AddRange([diffSummary, diffSourceLabel, diffSource, diffBeforeLabel, diffExisting,
            diffCurrentLabel, diffCurrent, historyLabel, history]);
        compare.Resize += (_, _) =>
        {
            const int pad = 12;
            const int gap = 10;
            var labelHeight = DeviceDpi > 96 ? 20 : 18;
            var labelGap = labelHeight + 4;
            var width = Math.Max(240, ToLogical(compare.ClientSize.Width) - pad * 2);
            var height = ToLogical(compare.ClientSize.Height);
            SetLogicalBounds(diffSummary, pad, 12, width, 24);
            if (width >= 680)
            {
                var column = Math.Max(190, (width - gap * 2) / 3);
                var labels = new[] { diffSourceLabel, diffBeforeLabel, diffCurrentLabel };
                var boxes = new[] { diffSource, diffExisting, diffCurrent };
                for (var index = 0; index < 3; index++)
                {
                    var x = pad + (column + gap) * index;
                    SetLogicalBounds(labels[index], x, 44, column, labelHeight);
                    SetLogicalBounds(boxes[index], x, 44 + labelGap, column, 116);
                }
                SetLogicalBounds(historyLabel, pad, 198, width, labelHeight);
                SetLogicalBounds(history, pad, 220, width, Math.Max(120, height - 234));
                compare.AutoScrollMinSize = new Size(0, ToDevice(380));
            }
            else
            {
                var y = 44;
                foreach (var pair in new[] { (diffSourceLabel, diffSource), (diffBeforeLabel, diffExisting), (diffCurrentLabel, diffCurrent) })
                {
                    SetLogicalBounds(pair.Item1, pad, y, width, labelHeight);
                    SetLogicalBounds(pair.Item2, pad, y + labelGap, width, 74);
                    y += 110 + (labelHeight - 18);
                }
                SetLogicalBounds(historyLabel, pad, y, width, labelHeight);
                SetLogicalBounds(history, pad, y + labelGap, width, 170);
                compare.AutoScrollMinSize = new Size(0, ToDevice(y + 214));
            }
        };

        terms = NewRichText(true);
        terms.AccessibleName = "관련 용어와 번역 메모리";
        terms.AccessibleDescription = "현재 문자열과 관련된 용어와 동일 원문의 안전한 기존 번역을 보여줍니다.";
        terms.Tag = "readonly";
        memo = Ui.TextBox("이 문자열에 대한 메모", true);
        memo.AccessibleName = "검수 메모";
        memo.Dock = DockStyle.Fill;
        memo.TextChanged += (_, _) =>
        {
            if (suppressEditor) return;
            editorTouched = true;
            SetSavePending();
        };

        var rmkPane = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "surface" };
        rmkStatus = Ui.Label("RMK 상태", 9f, FontStyle.Bold); rmkStatus.AutoSize = false;
        rmkStatus.AccessibleName = "RMK 작업공간 상태 요약";
        rmkStatus.AccessibleDescription = "현재 프로젝트의 RMK 구독본과 쓰기 가능한 작업 클론 상태를 표시합니다.";
        rmkRefresh = Ui.Button("상태 갱신", null, 120);
        rmkOpen = Ui.Button("폴더 열기", null, 120);
        rmkBuild = Ui.Button("LoadFolders 빌드", "warm-soft", 180);
        rmkInfo = NewRichText(true); rmkInfo.Dock = DockStyle.None; rmkInfo.Tag = "readonly";
        rmkInfo.AccessibleName = "RMK 작업공간 상태";
        rmkInfo.AccessibleDescription = "현재 문자열의 RMK 워크북, 작업 클론 및 빌드 결과를 읽기 전용으로 표시합니다.";
        rmkRefresh.AccessibleName = "RMK 상태 갱신";
        rmkRefresh.AccessibleDescription = "RMK 구독본과 작업 클론에서 현재 프로젝트를 다시 찾습니다.";
        rmkRefresh.TabIndex = 1;
        rmkOpen.AccessibleName = "RMK 폴더 열기";
        rmkOpen.AccessibleDescription = "현재 RMK 번역 항목 또는 작업 클론 폴더를 엽니다.";
        rmkOpen.TabIndex = 2;
        rmkBuild.AccessibleName = "RMK LoadFolders 빌드";
        rmkBuild.AccessibleDescription = "검증된 RMK 작업 클론의 LoadFoldersBuilder를 실행합니다.";
        rmkBuild.TabIndex = 3;
        rmkInfo.TabIndex = 4;
        rmkRefresh.Click += (_, _) => RefreshRmkPanel();
        rmkOpen.Click += (_, _) => OpenCurrentRmkFolder();
        rmkBuild.Click += (_, _) => RmkBuildRequested?.Invoke(this, EventArgs.Empty);
        rmkPane.Controls.AddRange([rmkStatus, rmkRefresh, rmkOpen, rmkBuild, rmkInfo]);
        rmkPane.Resize += (_, _) =>
        {
            const int pad = 12;
            const int gap = 8;
            var width = Math.Max(220, ToLogical(rmkPane.ClientSize.Width) - pad * 2);
            var height = ToLogical(rmkPane.ClientSize.Height);
            var half = Math.Max(96, (width - gap) / 2);
            SetLogicalBounds(rmkStatus, pad, 12, width, 84);
            SetLogicalBounds(rmkRefresh, pad, 104, half, 34);
            SetLogicalBounds(rmkOpen, pad + half + gap, 104, half, 34);
            SetLogicalBounds(rmkBuild, pad, 146, width, 36);
            SetLogicalBounds(rmkInfo, pad, 194, width, Math.Max(120, height - 206));
            rmkPane.AutoScrollMinSize = new Size(0, ToDevice(326));
        };

        var issuesPane = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "surface" };
        qualitySummary = Ui.Label("품질 검사를 준비하는 중", 8f, FontStyle.Bold); qualitySummary.AutoSize = false;
        qualityCategory = NewChoice(new[]
        {
            new Choice<string>("전체 문제", ""), new Choice<string>("오류", "severity:error"),
            new Choice<string>("경고", "severity:warning"), new Choice<string>("미번역", "Missing"),
            new Choice<string>("원문 변경", "SourceChanged"), new Choice<string>("토큰/태그", "TokenOrTag"),
            new Choice<string>("길이 이상", "length"), new Choice<string>("원문과 동일", "SameAsSource"),
            new Choice<string>("중복 식별자", "DuplicateIdentity")
        });
        qualityCategory.Dock = DockStyle.None;
        qualityCategory.AccessibleName = "품질 문제 필터";
        qualityCategory.AccessibleDescription = "표시할 품질 문제의 등급 또는 분류를 선택합니다.";
        qualityCategory.TabIndex = 0;
        qualityCategory.SelectedIndexChanged += (_, _) => RefreshQualityList();
        qualityRefresh = Ui.Button("다시 검사", null, 82);
        qualityRefresh.AccessibleDescription = "현재 프로젝트의 품질 문제를 다시 계산합니다.";
        qualityRefresh.TabIndex = 1;
        qualityRefresh.Click += (_, _) => BuildQualityIssues();
        exportQuality = Ui.Button("보고서", "warm-soft", 78);
        exportQuality.AccessibleDescription = "원문이나 번역문을 포함하지 않는 집계 품질 보고서를 저장합니다.";
        exportQuality.TabIndex = 2;
        exportQuality.Click += (_, _) => QualityExportRequested?.Invoke(this, EventArgs.Empty);
        issues = new BufferedListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            VirtualMode = true,
            VirtualListSize = 0,
            TabIndex = 3
        };
        issues.AccessibleName = "품질 문제 목록";
        issues.AccessibleDescription = "현재 프로젝트에서 발견된 오류와 경고를 선택합니다.";
        issues.Columns.Add("등급", 48); issues.Columns.Add("분류", 92); issues.Columns.Add("키", 200);
        issues.RetrieveVirtualItem += (_, e) =>
        {
            e.Item = e.ItemIndex >= 0 && e.ItemIndex < visibleQualityIssues.Count
                ? CreateQualityListItem(visibleQualityIssues[e.ItemIndex])
                : new ListViewItem(string.Empty);
        };
        issues.SelectedIndexChanged += (_, _) =>
        {
            var issue = SelectedQualityIssue();
            qualityJump.Enabled = issue is not null;
            selectedQuality.Text = issue is not null
                ? $"선택 문자열 검사 · {issue.Detail}" : "선택 문자열 검사";
            warnings.Text = issue is not null
                ? $"{CategoryText(issue.Category)}\r\n{issue.Detail}"
                : string.Empty;
        };
        issues.DoubleClick += (_, _) => SelectIssue();
        selectedQuality = Ui.Label("선택 문자열 검사", 8.2f, FontStyle.Bold); selectedQuality.AutoSize = false;
        qualityJump = Ui.Button("문자열로 이동", "success", 114); qualityJump.Enabled = false; qualityJump.TabIndex = 4; qualityJump.Click += (_, _) => SelectIssue();
        warnings = NewRichText(true);
        warnings.AccessibleName = "선택 문자열 품질 경고";
        warnings.Dock = DockStyle.None;
        warnings.Tag = "readonly";
        warnings.TabIndex = 5;
        issuesPane.Controls.AddRange([qualitySummary, qualityCategory, qualityRefresh, exportQuality, issues, selectedQuality, warnings, qualityJump]);
        issuesPane.Resize += (_, _) =>
        {
            const int pad = 12;
            var width = Math.Max(240, ToLogical(issuesPane.ClientSize.Width) - pad * 2);
            var height = ToLogical(issuesPane.ClientSize.Height);
            SetLogicalBounds(qualitySummary, pad, 12, width + pad, 42);
            var filterWidth = Math.Max(110, width - 184);
            SetLogicalBounds(qualityCategory, pad, 60, filterWidth, 30);
            SetLogicalBounds(qualityRefresh, pad + filterWidth + 6, 60, 82, 30);
            SetLogicalBounds(exportQuality, pad + filterWidth + 94, 60, 78, 30);
            var listHeight = Math.Max(150, (int)(height * 0.46));
            SetLogicalBounds(issues, pad, 100, width, listHeight);
            if (issues.Columns.Count >= 3) issues.Columns[2].Width = ToDevice(Math.Max(90, width - 146));
            var detailY = 112 + listHeight;
            SetLogicalBounds(selectedQuality, pad, detailY, Math.Max(120, width - 122), 18);
            SetLogicalBounds(qualityJump, pad + width - 114, detailY - 4, 114, 30);
            SetLogicalBounds(warnings, pad, detailY + 26, width, Math.Max(82, height - detailY - 38));
            issuesPane.AutoScrollMinSize = new Size(0, ToDevice(detailY + 126));
        };

        log = NewRichText(true);
        log.AccessibleName = "작업 로그";
        log.AccessibleDescription = "민감한 원문과 API 키를 제외한 최근 작업 상태입니다.";
        log.Font = new Font("Consolas", 8.5f);
        log.Tag = "log";
        AddTab("비교", compare);
        AddTab("용어", terms);
        AddTab("메모", memo);
        AddTab("RMK", rmkPane);
        AddTab("품질", issuesPane);
        AddTab("로그", log);
        panel.Controls.Add(sideTabs);
        RefreshRmkPanel();
        return panel;

        void AddTab(string name, Control control)
        {
            var page = new TabPage(name) { Padding = Padding.Empty, Tag = "surface" };
            page.Controls.Add(control);
            sideTabs.TabPages.Add(page);
        }
    }

    private void RefreshFilter(bool keepCurrent, ReviewItem? preferredSelection = null, int fallbackIndex = 0)
    {
        if (workspace is null)
        {
            filterCancellation?.Cancel();
            filterBusy = false;
            filtered = [];
            list.Items.Clear();
            return;
        }
        SaveEditor(false, false);
        var selected = keepCurrent ? preferredSelection ?? current : null;
        var query = new ReviewQuery(
            search.Text,
            (searchField.SelectedItem as Choice<ReviewSearchField>)?.Value ?? ReviewSearchField.All,
            (statusFilter.SelectedItem as Choice<ReviewStatusFilter>)?.Value ?? ReviewStatusFilter.All,
            (fileFilter.SelectedItem as Choice<string>)?.Value ?? string.Empty,
            (sortMode.SelectedItem as Choice<ReviewSortMode>)?.Value ?? ReviewSortMode.Default);
        var targetWorkspace = workspace;
        var revision = ++filterRevision;
        filterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        filterCancellation = cancellation;
        filterBusy = true;
        _ = RefreshFilterAsync(targetWorkspace, query, selected, fallbackIndex, revision, cancellation);
    }

    private async Task RefreshFilterAsync(
        ReviewWorkspace targetWorkspace,
        ReviewQuery query,
        ReviewItem? selected,
        int fallbackIndex,
        long revision,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var workerThread = Environment.CurrentManagedThreadId != uiThreadId;
                var items = services.Reviews.Query(targetWorkspace, query);
                var stats = services.Reviews.GetStats(targetWorkspace);
                cancellation.Token.ThrowIfCancellationRequested();
                return (Items: items, Stats: stats, WorkerThread: workerThread);
            }, cancellation.Token).ConfigureAwait(false);

            await InvokeOnUiThreadAsync(() =>
            {
                if (disposed || IsDisposed || cancellation.IsCancellationRequested
                    || revision != filterRevision || !ReferenceEquals(workspace, targetWorkspace)) return;

                filtered = result.Items;
                lastFilterUsedWorkerThread = result.WorkerThread;
                ApplyFilterResult(selected, fallbackIndex, result.Stats);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer filter request superseded this one.
        }
        catch (Exception exception)
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (!disposed && !IsDisposed && revision == filterRevision)
                    operationStatus.Text = "목록 필터를 적용하지 못했습니다.";
            }).ConfigureAwait(false);
            services.Logger.Write("ERROR", $"Review filter failed ({exception.GetType().Name}).");
        }
        finally
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (revision != filterRevision) return;
                filterBusy = false;
                if (ReferenceEquals(filterCancellation, cancellation)) filterCancellation = null;
            }).ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    private void ApplyFilterResult(ReviewItem? selected, int fallbackIndex, ReviewWorkspaceStats stats)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var item in filtered) list.Items.Add(item);
        }
        finally
        {
            list.EndUpdate();
        }
        list.Invalidate();
        summary.Text = $"전체 {stats.Total:N0}  ·  미번역 {stats.Pending:N0}  ·  번역 {stats.Translated:N0}\r\n검토 {stats.Approved:N0}  ·  업데이트 변경 {stats.Updated:N0}";
        if (selected is not null)
        {
            var index = IndexOfReference(filtered, selected);
            if (index >= 0) SelectFilteredIndex(index);
            else if (filtered.Count > 0) SelectFilteredIndex(Math.Clamp(fallbackIndex, 0, filtered.Count - 1));
            else ClearEditor();
        }
        else if (filtered.Count > 0) SelectFilteredIndex(0);
        else ClearEditor();
    }

    private void DrawReviewItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= list.Items.Count || list.Items[e.Index] is not ReviewItem item) return;
        var theme = ThemeManager.Current;
        var updated = item.Decision.SourceChanged;
        var status = updated ? "변경됨" : item.EffectiveStatus switch
        {
            ReviewStatuses.Approved => "검토됨",
            ReviewStatuses.Translated => "번역됨",
            _ => "미번역"
        };
        var statusColor = updated
            ? (theme.Dark ? Color.FromArgb(232, 177, 82) : Color.FromArgb(174, 105, 24))
            : item.EffectiveStatus switch
            {
                ReviewStatuses.Approved => theme.Dark ? Color.FromArgb(99, 191, 124) : Color.FromArgb(42, 116, 67),
                ReviewStatuses.Translated => theme.Dark ? Color.FromArgb(107, 177, 236) : Color.FromArgb(36, 105, 170),
                _ => theme.Dark ? Color.FromArgb(190, 190, 184) : Color.FromArgb(91, 91, 86)
            };
        var selected = e.Index == list.SelectedIndex;
        var card = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 1), Math.Max(1, e.Bounds.Height - 6));
        var cardBackground = selected ? theme.Selection : theme.Surface;
        using (var backgroundBrush = new SolidBrush(list.BackColor)) e.Graphics.FillRectangle(backgroundBrush, e.Bounds);
        using (var cardBrush = new SolidBrush(cardBackground)) e.Graphics.FillRectangle(cardBrush, card);
        using (var stripeBrush = new SolidBrush(statusColor)) e.Graphics.FillRectangle(stripeBrush, card.X, card.Y, 3, card.Height);

        var statusWidth = updated ? 70 : 60;
        var sourceRect = new Rectangle(card.X + 18, card.Y + 6, Math.Max(30, card.Width - statusWidth - 42), 24);
        var statusRect = new Rectangle(card.Right - statusWidth - 12, card.Y + 6, statusWidth, 24);
        var translationRect = new Rectangle(card.X + 18, card.Y + 31, Math.Max(30, card.Width - 34), 24);
        var keyRect = new Rectangle(card.X + 18, card.Y + 58, Math.Max(30, card.Width - 34), 20);
        const TextFormatFlags singleLine = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        var translationText = string.IsNullOrWhiteSpace(item.Decision.Text) ? "번역 대기" : Preview(item.Decision.Text, 100);
        var origin = OriginText(item.Decision.TranslationOrigin);
        if (!string.IsNullOrWhiteSpace(item.Decision.Text) && !string.IsNullOrWhiteSpace(origin)) translationText = $"{origin}  ·  {translationText}";
        var translationColor = theme.Muted;
        if (item.IsWarning)
        {
            translationText = $"주의 · {translationText}";
            translationColor = theme.Dark ? Color.FromArgb(238, 183, 92) : Color.FromArgb(145, 91, 16);
        }
        statusColor = ThemeManager.ReadableForeground(cardBackground, statusColor, theme.Text);
        translationColor = ThemeManager.ReadableForeground(cardBackground, translationColor, theme.Text);
        var keyColor = ThemeManager.ReadableForeground(cardBackground, theme.Muted, theme.Text);
        var context = DefContextService.Get(item.Row);
        var keyText = $"{context.DefClass}  ·  {context.Node}";
        using var sourceFont = new Font(list.Font.FontFamily, Math.Max(8.5f, list.Font.Size - 0.5f));
        using var statusFont = new Font(list.Font.FontFamily, 8f, FontStyle.Bold);
        using var translationFont = new Font(list.Font.FontFamily, Math.Max(8f, list.Font.Size - 1.5f));
        using var keyFont = new Font(list.Font.FontFamily, 7.8f);
        TextRenderer.DrawText(e.Graphics, Preview(item.Row.Source, 100), sourceFont, sourceRect, theme.Text, singleLine);
        TextRenderer.DrawText(e.Graphics, status, statusFont, statusRect, statusColor, singleLine | TextFormatFlags.Right);
        TextRenderer.DrawText(e.Graphics, translationText, translationFont, translationRect, translationColor, singleLine);
        TextRenderer.DrawText(e.Graphics, keyText, keyFont, keyRect, keyColor, singleLine);
        if ((e.State & DrawItemState.Focus) != 0)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(card, -3, -3), theme.Text, selected ? theme.Selection : theme.Surface);
    }

    private void SelectionChanged()
    {
        if (list.SelectedIndex < 0) return;
        SelectFilteredIndex(list.SelectedIndex);
    }

    private void SelectFilteredIndex(int index)
    {
        if (index < 0 || index >= filtered.Count) return;
        if (!ReferenceEquals(current, filtered[index])) SaveEditor(false);
        current = filtered[index];
        if (list.SelectedIndex != index) list.SelectedIndex = index;
        var visibleCount = Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight));
        if (index < list.TopIndex) list.TopIndex = index;
        else if (index >= list.TopIndex + visibleCount) list.TopIndex = Math.Max(0, index - visibleCount + 1);
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        if (current is null) { ClearEditor(); return; }
        suppressEditor = true;
        try
        {
            source.Text = current.Row.Source;
            translation.Text = current.Decision.Text;
            memo.Text = current.Decision.Note;
            existing.Text = current.Row.Existing;
            candidate.Text = current.Row.Candidate;
            var existingOrigin = OriginText(current.Row.ExistingOrigin);
            existingLabel.Text = string.IsNullOrWhiteSpace(current.Row.Existing)
                ? "기존 번역"
                : $"기존 번역  ·  {existingOrigin}";
            editBaseline = current.Decision.Text;
            editBaselineOrigin = current.Decision.TranslationOrigin;
            pendingEditOrigin = current.Decision.TranslationOrigin;
            editorTouched = false;
            translationTouched = false;
            UpdateTranslationTitle();
            var context = DefContextService.Get(current.Row);
            var wordCount = current.Row.Source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var safe = ReviewSafety.IsTranslationSafe(current.Row, current.Decision.Text, services.Extractor) ? "예" : "아니오";
            metadata.Text = $"{context.ClassLine}\r\n{context.NodeLine}\r\n파일  {current.RelativeTarget}\r\nID  {current.Row.Id}    ·    출처 {OriginText(current.Decision.TranslationOrigin)}    ·    단어 {wordCount:N0}    ·    안전 적용 {safe}";
            var warningMessages = GetWarningMessages(current);
            var warningCount = warningMessages.Count;
            var warningLabel = warningCount > 0 ? $"  ·  주의 {warningCount:N0}" : string.Empty;
            var displayIndex = IndexOfReference(filtered, current) + 1;
            var statusText = StatusText(current);
            var originText = OriginText(current.Decision.TranslationOrigin);
            itemStatus.Text = $"{displayIndex:N0} / {filtered.Count:N0}   {statusText}  ·  {originText}{warningLabel}";
            itemStatus.AccessibleName = $"문자열 상태: {statusText}";
            itemStatus.AccessibleDescription = $"검색 결과 {filtered.Count:N0}개 중 {displayIndex:N0}번째입니다. 번역 출처는 {originText}이며 주의 항목은 {warningCount:N0}개입니다.";
            itemStatus.Tag = current.Decision.SourceChanged
                ? "warning"
                : current.EffectiveStatus switch
                {
                    ReviewStatuses.Approved => "success-label",
                    ReviewStatuses.Translated => "accent-label",
                    _ => "muted"
                };
            var preferredStatusColor = current.Decision.SourceChanged
                ? ThemeManager.Current.Warning
                : current.EffectiveStatus switch
                {
                    ReviewStatuses.Approved => ThemeManager.Current.Success,
                    ReviewStatuses.Translated => ThemeManager.Current.Accent,
                    _ => ThemeManager.Current.Muted
                };
            itemStatus.ForeColor = ThemeManager.ReadableForeground(
                ThemeManager.Current.Surface,
                preferredStatusColor,
                ThemeManager.Current.Text);
            updateBadge.Visible = current.Decision.SourceChanged;
            updateBadge.AccessibleName = current.Decision.SourceChanged ? "원문 업데이트 감지" : "원문 업데이트 없음";
            updateBadge.AccessibleDescription = current.Decision.SourceChanged
                ? "원문이 업데이트되어 기존 번역을 다시 검토해야 합니다."
                : "선택한 문자열에서 원문 변경이 감지되지 않았습니다.";
            UpdateDiffView(current);
            history.Text = BuildHistory(current);
            warnings.Text = warningMessages.Count > 0 ? string.Join("\r\n", warningMessages) : "문제 없음";
            rmkInfo.Text = BuildRmkInfo(current);
            RefreshTerms(current);
            RefreshMemory(current);
        }
        finally { suppressEditor = false; }
    }

    private void SaveEditor(bool promptDuplicates, bool refreshFilter = true)
    {
        if (suppressEditor || current is null || workspace is null || !editorTouched) return;
        var shouldPromptDuplicates = promptDuplicates && translationTouched;
        services.Reviews.UpdateTranslation(
            workspace,
            current,
            translation.Text,
            memo.Text,
            true,
            pendingEditOrigin);
        editorTouched = false;
        translationTouched = false;
        editBaseline = current.Decision.Text;
        editBaselineOrigin = current.Decision.TranslationOrigin;
        pendingEditOrigin = current.Decision.TranslationOrigin;
        SetSavePending();
        if (shouldPromptDuplicates)
        {
            var matches = workspace.Items.Count(item => !ReferenceEquals(item, current)
                && item.Row.Source.Equals(current.Row.Source, StringComparison.Ordinal)
                && (!item.Decision.Text.Equals(current.Decision.Text, StringComparison.Ordinal) || item.Decision.SourceChanged));
            if (matches > 0)
            {
                var answer = MessageBox.Show(FindForm(), $"같은 원문을 사용하는 다른 항목이 {matches:N0}개 있습니다.\n\n이 번역으로 모두 통일할까요?", "동일 원문 일괄 번역", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.Yes) services.Reviews.ApplyToDuplicateSources(workspace, current);
            }
        }
        if (refreshFilter) RefreshFilter(true);
    }

    private void CommitWithDuplicatePrompt()
    {
        SaveEditor(true);
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetCurrentStatus(string status, bool moveNext)
    {
        if (current is null || workspace is null) return;
        var target = current;
        var targetIndex = Math.Max(0, IndexOfReference(filtered, target));
        var nextTarget = moveNext && targetIndex + 1 < filtered.Count
            ? filtered[targetIndex + 1]
            : null;
        SaveEditor(true, false);
        if (status != ReviewStatuses.Pending && string.IsNullOrWhiteSpace(target.Decision.Text))
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        if (status == ReviewStatuses.Approved && target.IsWarning)
        {
            var answer = MessageBox.Show(FindForm(), "토큰, 조사 또는 원문 동일 경고가 있습니다. 그래도 검토 완료로 표시할까요?", "번역 안전 경고", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        services.Reviews.SetStatus(workspace, target, status);
        SetSavePending();
        RefreshFilter(true, moveNext ? nextTarget ?? target : target, targetIndex);
        SaveRequested?.Invoke(this, EventArgs.Empty);
        var retainedIndex = IndexOfReference(filtered, target);
        var nextIndex = nextTarget is null ? -1 : IndexOfReference(filtered, nextTarget);
        if (moveNext && nextIndex >= 0)
            SelectFilteredIndex(nextIndex);
        else if (retainedIndex < 0 && filtered.Count > 0)
            SelectFilteredIndex(Math.Min(targetIndex, filtered.Count - 1));
    }

    private void ApproveAllSafe()
    {
        if (workspace is null) return;
        SaveEditor(true);
        var blank = workspace.Items.Count(item => string.IsNullOrWhiteSpace(item.Decision.Text));
        var changed = workspace.Items.Count(item => item.Decision.SourceChanged);
        var warning = workspace.Items.Count(item => !string.IsNullOrWhiteSpace(item.Decision.Text) && item.IsWarning);
        var eligible = workspace.Items.Count(item => !string.IsNullOrWhiteSpace(item.Decision.Text)
            && !item.Decision.SourceChanged && !item.IsWarning && item.EffectiveStatus != ReviewStatuses.Approved);
        if (eligible == 0)
        {
            MessageBox.Show(FindForm(), "새로 검토 완료 처리할 안전한 번역이 없습니다.", "전체 검토", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var answer = MessageBox.Show(FindForm(),
            $"안전한 번역 {eligible:N0}개를 검토 완료로 표시할까요?\n\n빈 번역 {blank:N0}개 · 원문 변경 {changed:N0}개 · 주의 {warning:N0}개는 건너뜁니다.",
            "전체 검토", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        var approved = services.Reviews.ApproveAllEligible(workspace);
        SetSavePending();
        RefreshFilter(true);
        SaveRequested?.Invoke(this, EventArgs.Empty);
        MessageBox.Show(FindForm(), $"{approved:N0}개를 검토 완료로 표시했습니다.", "전체 검토", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void MoveSelection(int delta)
    {
        if (filtered.Count == 0) return;
        if (current is null)
        {
            SelectFilteredIndex(delta < 0 ? filtered.Count - 1 : 0);
            return;
        }
        var index = IndexOfReference(filtered, current);
        SelectFilteredIndex(Math.Clamp(index + delta, 0, filtered.Count - 1));
    }

    private void UseCandidate() { if (current is not null && !string.IsNullOrWhiteSpace(current.Row.Candidate)) SetEditorText(current.Row.Candidate, "ai"); }
    private void UseExisting()
    {
        if (current is null || string.IsNullOrWhiteSpace(current.Row.Existing)) return;
        SetEditorText(
            current.Row.Existing,
            string.IsNullOrWhiteSpace(current.Row.ExistingOrigin) ? "existing" : current.Row.ExistingOrigin);
    }
    private void UndoEdit()
    {
        suppressEditor = true;
        translation.Text = editBaseline;
        suppressEditor = false;
        pendingEditOrigin = editBaselineOrigin;
        editorTouched = false;
        translationTouched = false;
        if (workspace?.Dirty == true) SetSavePending();
        else SetSaveStatus(string.Empty, "header-muted");
        if (current is not null) UpdateDiffView(current, translation.Text);
        translation.Focus();
        translation.SelectionStart = translation.TextLength;
    }
    private void CopyTranslation() { if (!string.IsNullOrEmpty(translation.Text)) Clipboard.SetText(translation.Text); }

    private static void CopyEntireReadOnlyText(RichTextBox control)
    {
        if (control.TextLength == 0) return;
        control.SelectAll();
        control.Copy();
    }

    private void UpdateTranslationTitle()
    {
        if (current is null)
        {
            translationLabel.Text = "번역문";
        }
        else
        {
            translationLabel.Text = $"번역문  ·  {OriginText(current.Decision.TranslationOrigin)}";
        }
        translationLabel.ForeColor = ThemeManager.ReadableForeground(
            ThemeManager.Current.Surface,
            ThemeManager.Current.Muted,
            ThemeManager.Current.Text);
    }

    private void SetEditorText(string value, string origin = "local")
    {
        suppressEditor = true;
        translation.Text = value;
        suppressEditor = false;
        pendingEditOrigin = string.IsNullOrWhiteSpace(origin) ? "local" : origin.Trim().ToLowerInvariant();
        editorTouched = true;
        translationTouched = true;
        SetSavePending();
        if (current is not null) UpdateDiffView(current, translation.Text);
        translation.Focus();
        translation.SelectionStart = translation.TextLength;
    }

    private void BuildQualityIssues()
    {
        if (workspace is null) return;
        var targetWorkspace = workspace;
        var revision = ++qualityRevision;
        qualityCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        qualityCancellation = cancellation;
        qualityBusy = true;
        qualitySummary.Text = $"전체 {targetWorkspace.Items.Count:N0}개 · 품질 검사 중...";
        _ = BuildQualityIssuesAsync(targetWorkspace, revision, cancellation);
    }

    private async Task BuildQualityIssuesAsync(
        ReviewWorkspace targetWorkspace,
        long revision,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var entries = services.Reviews.CreateQualityEntries(targetWorkspace);
                var found = QualityService.FindIssues(entries);
                timer.Stop();
                cancellation.Token.ThrowIfCancellationRequested();
                return (Issues: found, Duration: timer.Elapsed, WorkerThread: Environment.CurrentManagedThreadId != uiThreadId);
            }, cancellation.Token).ConfigureAwait(false);

            await InvokeOnUiThreadAsync(() =>
            {
                if (disposed || IsDisposed || cancellation.IsCancellationRequested
                    || revision != qualityRevision || !ReferenceEquals(workspace, targetWorkspace)) return;

                qualityIssues = result.Issues;
                lastQualityBuildDuration = result.Duration;
                lastQualityUsedWorkerThread = result.WorkerThread;
                qualitySummary.Text = $"전체 {targetWorkspace.Items.Count:N0}개 · 오류 {qualityIssues.Count(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)):N0} · 경고 {qualityIssues.Count(issue => issue.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase)):N0} · 표시 {qualityIssues.Count:N0} · 검사 {result.Duration.TotalSeconds:0.0}초";
                RefreshQualityList();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer quality request superseded this one.
        }
        catch (Exception exception)
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (!disposed && !IsDisposed && revision == qualityRevision)
                    qualitySummary.Text = "품질 검사를 완료하지 못했습니다.";
            }).ConfigureAwait(false);
            services.Logger.Write("ERROR", $"Quality analysis failed ({exception.GetType().Name}).");
        }
        finally
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (revision != qualityRevision) return;
                qualityBusy = false;
                if (ReferenceEquals(qualityCancellation, cancellation)) qualityCancellation = null;
            }).ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    private void UpdateDiffView(ReviewItem item, string? currentText = null)
    {
        var before = item.Row.Existing;
        diffBeforeLabel.Text = "기존 번역";
        if (string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(item.Row.Candidate))
        {
            before = item.Row.Candidate;
            diffBeforeLabel.Text = "AI 후보";
        }

        var after = currentText ?? item.Decision.Text;
        var difference = GetSimpleDiff(before, after);
        diffSource.Text = item.Row.Source;
        SetDiffText(diffExisting, before, difference.PrefixLength, difference.BeforeChangedLength);
        SetDiffText(diffCurrent, after, difference.PrefixLength, difference.AfterChangedLength);
        if (string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after))
            diffSummary.Text = "비교할 번역이 없습니다";
        else if (!difference.Changed)
            diffSummary.Text = item.Decision.SourceChanged ? "번역은 같지만 원문이 변경되었습니다" : "기존 번역과 현재 번역이 같습니다";
        else
        {
            var changedSize = difference.BeforeChangedLength + difference.AfterChangedLength;
            diffSummary.Text = item.Decision.SourceChanged ? $"원문 변경됨  ·  번역 차이 {changedSize:N0}자" : $"번역 차이 {changedSize:N0}자";
        }
        var preferred = item.Decision.SourceChanged
            ? ThemeManager.Current.Warning
            : difference.Changed ? ThemeManager.Current.Accent : ThemeManager.Current.Muted;
        diffSummary.ForeColor = ThemeManager.ReadableForeground(
            ThemeManager.Current.Surface,
            preferred,
            ThemeManager.Current.Text);
    }

    private static SimpleDifference GetSimpleDiff(string? beforeValue, string? afterValue)
    {
        var before = beforeValue ?? string.Empty;
        var after = afterValue ?? string.Empty;
        var prefix = 0;
        var maximumPrefix = Math.Min(before.Length, after.Length);
        while (prefix < maximumPrefix && before[prefix] == after[prefix]) prefix++;

        var suffix = 0;
        var maximumSuffix = Math.Min(before.Length - prefix, after.Length - prefix);
        while (suffix < maximumSuffix
               && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        return new SimpleDifference(
            !before.Equals(after, StringComparison.Ordinal),
            prefix,
            before.Length - prefix - suffix,
            after.Length - prefix - suffix);
    }

    private static void SetDiffText(RichTextBox box, string? value, int start, int length)
    {
        box.Text = value ?? string.Empty;
        box.SelectAll();
        box.SelectionBackColor = box.BackColor;
        box.SelectionColor = ThemeManager.Current.Text;
        if (length > 0 && start >= 0 && start + length <= box.TextLength)
        {
            box.Select(start, length);
            box.SelectionBackColor = ThemeManager.Current.Dark
                ? Color.FromArgb(91, 69, 37)
                : Color.FromArgb(255, 229, 168);
            box.SelectionColor = ThemeManager.Current.Text;
        }
        box.Select(0, 0);
    }

    private readonly record struct SimpleDifference(
        bool Changed,
        int PrefixLength,
        int BeforeChangedLength,
        int AfterChangedLength);

    private void RefreshQualityList()
    {
        if (issues is null) return;
        var filter = (qualityCategory?.SelectedItem as Choice<string>)?.Value ?? string.Empty;
        IEnumerable<QualityIssue> visible = qualityIssues;
        if (filter.StartsWith("severity:", StringComparison.OrdinalIgnoreCase))
            visible = visible.Where(issue => issue.Severity.Equals(filter[9..], StringComparison.OrdinalIgnoreCase));
        else if (filter == "length")
            visible = visible.Where(issue => issue.Category is "TooShort" or "TooLong");
        else if (filter == "TokenOrTag")
            visible = visible.Where(issue => issue.Category is "TokenOrTag" or "Unsafe");
        else if (!string.IsNullOrWhiteSpace(filter))
            visible = visible.Where(issue => issue.Category.Equals(filter, StringComparison.OrdinalIgnoreCase));

        visibleQualityIssues = visible.ToArray();
        issues.BeginUpdate();
        issues.VirtualListSize = 0;
        issues.VirtualListSize = visibleQualityIssues.Count;
        issues.EndUpdate();
        qualityJump.Enabled = false;
        selectedQuality.Text = "선택 문자열 검사";
        warnings.Text = string.Empty;
    }

    private void SelectIssue()
    {
        var issue = SelectedQualityIssue();
        if (workspace is null || issue is null) return;
        var target = workspace.Items.ElementAtOrDefault(issue.Index);
        if (target is null) return;
        searchTimer.Stop();
        search.Text = target.Row.Key;
        searchField.SelectedItem = searchField.Items.Cast<Choice<ReviewSearchField>>().First(choice => choice.Value == ReviewSearchField.Key);
        searchTimer.Stop();
        RefreshFilter(false);
    }

    private QualityIssue? SelectedQualityIssue()
    {
        if (issues.SelectedIndices.Count == 0) return null;
        var index = issues.SelectedIndices[0];
        return index >= 0 && index < visibleQualityIssues.Count ? visibleQualityIssues[index] : null;
    }

    private static ListViewItem CreateQualityListItem(QualityIssue issue)
    {
        var isError = issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase);
        return new ListViewItem([isError ? "오류" : "경고", CategoryText(issue.Category), issue.Key])
        {
            Tag = issue,
            ForeColor = ThemeManager.ReadableForeground(
                ThemeManager.Current.Surface,
                isError ? ThemeManager.Current.Danger : ThemeManager.Current.Warning,
                ThemeManager.Current.Text)
        };
    }

    private void RefreshTerms(ReviewItem item)
    {
        var lines = new List<string>();
        foreach (var term in glossary.Where(term =>
                     item.Row.Source.Contains(term.Source, StringComparison.OrdinalIgnoreCase)
                     || item.Row.Candidate.Contains(term.Source, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(term => term.Priority)
                 .ThenByDescending(term => term.Source.Length)
                 .Take(60))
            lines.Add(string.IsNullOrWhiteSpace(term.Note) ? $"{term.Source} => {term.Korean}" : $"{term.Source} => {term.Korean} ({term.Note})");
        terms.Text = lines.Count == 0 ? "관련 용어 또는 번역 메모리 없음" : string.Join(Environment.NewLine, lines);
    }

    private void RefreshMemory(ReviewItem item)
    {
        if (workspace is null) return;
        var matchingItems = translationMemorySourceIndex.TryGetValue(item.Row.Source, out var sourceMatches)
            ? sourceMatches
            : [];
        var entries = matchingItems.Select(value => new TranslationMemoryEntry(
            $"{value.RelativeTarget}|{value.Row.Key}", value.Row.Source, value.Decision.Text, value.Decision.TranslationOrigin,
            value.EffectiveStatus, value.RelativeTarget, value.Decision.TranslationUpdatedAt, value.Decision.SourceChanged,
            ReviewSafety.IsTranslationSafe(value.Row, value.Decision.Text, services.Extractor)));
        var suggestions = TranslationMemoryService.Select(entries, item.Row.Source, $"{item.RelativeTarget}|{item.Row.Key}");
        memory.BeginUpdate();
        memory.Items.Clear();
        foreach (var suggestion in suggestions) memory.Items.Add(new MemoryChoice($"{OriginText(suggestion.Origin)} · {Preview(suggestion.Text, 100)}", suggestion.Text));
        if (memory.Items.Count == 0) memory.Items.Add(new MemoryChoice("같은 원문의 다른 번역이 없습니다.", string.Empty));
        memory.EndUpdate();
        if (suggestions.Count > 0)
        {
            var lines = new List<string> { "로컬 번역 메모리 · 동일 원문" };
            foreach (var suggestion in suggestions)
            {
                var status = suggestion.Status.Equals(ReviewStatuses.Approved, StringComparison.OrdinalIgnoreCase)
                    ? "검토됨"
                    : "번역됨";
                lines.Add($"[{status} · {OriginText(suggestion.Origin)} · {Path.GetFileName(suggestion.Target)}]");
                lines.Add(suggestion.Text);
                lines.Add(string.Empty);
            }
            if (!terms.Text.Equals("관련 용어 또는 번역 메모리 없음", StringComparison.Ordinal))
            {
                lines.Add("용어집");
                lines.Add(terms.Text);
            }
            terms.Text = string.Join(Environment.NewLine, lines);
        }
    }

    private void OpenCurrentRmkFolder()
    {
        var configured = services.Settings.RmkWorkspaceRoot;
        var workbookDirectory = TryGetDirectoryName(current?.Row.RmkWorkbook);
        startWorkflow(
            "RMK 폴더 열기",
            token => OpenCurrentRmkFolderAsync(configured, workbookDirectory, token),
            null);
    }

    private async Task OpenCurrentRmkFolderAsync(
        string configured,
        string workbookDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = !string.IsNullOrWhiteSpace(configured) && isRmkWorkspaceRoot(configured)
                    ? configured
                    : workbookDirectory;
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(path) || !directoryExists(path)) return;
                cancellationToken.ThrowIfCancellationRequested();
                openDirectory(path);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (cancellationToken.IsCancellationRequested || disposed || IsDisposed) return;
            MessageBox.Show(
                FindForm(),
                "폴더를 열지 못했습니다. 폴더가 존재하고 접근 가능한지 확인하세요.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void RefreshRmkPanel(string? validStatus = null)
    {
        if (rmkStatus is null || rmkBuild is null || rmkOpen is null || disposed) return;
        var root = services.Settings.RmkWorkspaceRoot;
        var workbookDirectory = TryGetDirectoryName(current?.Row.RmkWorkbook);
        QueueRmkPanelRefresh(root, workbookDirectory, validStatus);
        if (current is not null) rmkInfo.Text = BuildRmkInfo(current);
    }

    private void QueueRmkPanelRefresh(
        string root,
        string workbookDirectory,
        string? validStatus = null)
    {
        var revision = ++rmkStatusRevision;
        rmkStatus.Text = "RMK 작업 클론 상태 확인 중...";
        rmkBuild.Enabled = false;
        rmkOpen.Enabled = false;
        if (!startWorkflow(
                "RMK 작업 클론 상태 확인",
                token => RefreshRmkPanelAsync(root, workbookDirectory, validStatus, revision, token),
                null)
            && !disposed
            && revision == rmkStatusRevision)
        {
            rmkStatus.Text = "RMK 작업 클론 상태 확인 대기";
        }
    }

    private async Task RefreshRmkPanelAsync(
        string root,
        string workbookDirectory,
        string? validStatus,
        long revision,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var valid = isWritableRmkWorkspace(root);
                cancellationToken.ThrowIfCancellationRequested();
                var fallbackExists = !string.IsNullOrWhiteSpace(workbookDirectory)
                    && directoryExists(workbookDirectory);
                cancellationToken.ThrowIfCancellationRequested();
                return (Valid: valid, FallbackExists: fallbackExists);
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || IsDisposed || revision != rmkStatusRevision) return;
            rmkStatus.Text = result.Valid
                ? $"{validStatus ?? "RMK 작업 클론 준비됨"}\r\n{root}"
                : "RMK Git 작업 클론이 설정되지 않았습니다.\r\n설정 화면에서 bus 브랜치 작업 폴더를 선택하세요.";
            rmkBuild.Enabled = result.Valid;
            rmkOpen.Enabled = result.Valid || result.FallbackExists;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (disposed || IsDisposed || revision != rmkStatusRevision) return;
            rmkStatus.Text = "RMK 작업 클론 상태를 확인하지 못했습니다.";
            rmkBuild.Enabled = false;
            rmkOpen.Enabled = false;
        }
    }

    public void SetRmkBuildResult(RmkBuildResult result)
    {
        RefreshRmkPanel("RMK Builder 완료");
        rmkStatus.Text = "RMK Builder 완료 · 작업 클론 상태 확인 중";
        rmkInfo.Text = result.Output;
    }

    internal void ResumeAfterCloseCancellation()
    {
        if (disposed || IsDisposed) return;
        if (workspace is not null)
        {
            RefreshFilter(true);
            BuildQualityIssues();
        }
        RefreshRmkPanel();
    }

    internal void SuspendBackgroundUiWorkForClose()
    {
        filterRevision++;
        qualityRevision++;
        filterCancellation?.Cancel();
        qualityCancellation?.Cancel();
        filterCancellation = null;
        qualityCancellation = null;
        filterBusy = false;
        qualityBusy = false;
        searchTimer.Stop();
    }

    internal void RefreshRmkPanelForTesting(string root, string workbookPath) =>
        QueueRmkPanelRefresh(root, TryGetDirectoryName(workbookPath));

    internal void OpenRmkFolderForTesting(string configured, string workbookPath) =>
        startWorkflow(
            "합성 RMK 폴더 열기",
            token => OpenCurrentRmkFolderAsync(configured, TryGetDirectoryName(workbookPath), token),
            null);

    internal string RmkStatusForTesting => rmkStatus.Text;

    private static string TryGetDirectoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetDirectoryName(path) ?? string.Empty; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private void ClearEditor()
    {
        current = null;
        suppressEditor = true;
        source.Clear(); translation.Clear(); memo.Clear(); existing.Clear(); candidate.Clear(); history.Clear(); warnings.Clear(); rmkInfo.Clear(); terms.Clear(); memory.Items.Clear();
        diffSource.Clear(); diffExisting.Clear(); diffCurrent.Clear();
        diffSummary.Text = "비교할 문자열을 선택하세요";
        itemStatus.Text = "표시할 문자열이 없습니다";
        itemStatus.AccessibleName = "문자열 상태: 표시 항목 없음";
        itemStatus.AccessibleDescription = "현재 검색 조건에 표시할 문자열이 없습니다.";
        itemStatus.Tag = "muted";
        itemStatus.ForeColor = ThemeManager.ReadableForeground(
            ThemeManager.Current.Surface,
            ThemeManager.Current.Muted,
            ThemeManager.Current.Text);
        updateBadge.Visible = false;
        updateBadge.AccessibleName = "원문 업데이트 없음";
        updateBadge.AccessibleDescription = "선택한 문자열이 없습니다.";
        translationLabel.Text = "번역문";
        existingLabel.Text = "기존 번역";
        metadata.Text = "Def Class : -\r\nNode : -\r\n파일  -\r\nID  -    ·    출처 없음    ·    단어 0    ·    안전 적용 아니오";
        suppressEditor = false;
        editorTouched = false;
        translationTouched = false;
        editBaseline = string.Empty;
        editBaselineOrigin = string.Empty;
        pendingEditOrigin = "local";
    }

    private void InitializeSplitters()
    {
        if (outerSplit.ClientSize.Width <= 0 || innerSplit.ClientSize.Width <= 0) return;
        try
        {
            var splitterWidth = Math.Max(2, ToDevice(2));
            if (outerSplit.SplitterWidth != splitterWidth) outerSplit.SplitterWidth = splitterWidth;
            if (innerSplit.SplitterWidth != splitterWidth) innerSplit.SplitterWidth = splitterWidth;
            outerSplit.Panel1MinSize = 0;
            outerSplit.Panel2MinSize = 0;
            var mainWidth = Math.Max(1, ToLogical(outerSplit.ClientSize.Width));
            var leftWidth = Math.Min(410, Math.Max(360, (int)(mainWidth * 0.24)));
            leftWidth = Math.Min(leftWidth, Math.Max(340, mainWidth - 740 - 2));
            var leftDevice = ToDevice(leftWidth);
            var outerDistance = Math.Clamp(leftDevice, 1, Math.Max(1, outerSplit.ClientSize.Width - outerSplit.SplitterWidth - 1));
            if (outerSplit.SplitterDistance != outerDistance) outerSplit.SplitterDistance = outerDistance;

            innerSplit.Panel1MinSize = 0;
            innerSplit.Panel2MinSize = 0;
            var rightWidth = Math.Max(1, ToLogical(innerSplit.ClientSize.Width));
            if (rightWidth < 1040)
            {
                if (innerSplit.Orientation != Orientation.Horizontal) innerSplit.Orientation = Orientation.Horizontal;
                var rightHeight = Math.Max(1, ToLogical(innerSplit.ClientSize.Height));
                var sideHeight = Math.Min(250, Math.Max(170, (int)(rightHeight * 0.28)));
                var innerDistance = Math.Clamp(
                    ToDevice(Math.Max(320, rightHeight - sideHeight - 2)),
                    1,
                    Math.Max(1, innerSplit.ClientSize.Height - innerSplit.SplitterWidth - 1));
                if (innerSplit.SplitterDistance != innerDistance) innerSplit.SplitterDistance = innerDistance;
            }
            else
            {
                if (innerSplit.Orientation != Orientation.Vertical) innerSplit.Orientation = Orientation.Vertical;
                var sideWidth = Math.Min(370, Math.Max(330, (int)(rightWidth * 0.25)));
                var centerWidth = Math.Max(420, rightWidth - sideWidth - 2);
                centerWidth = Math.Min(centerWidth, Math.Max(420, rightWidth - 330 - 2));
                var innerDistance = Math.Clamp(ToDevice(centerWidth), 1, Math.Max(1, innerSplit.ClientSize.Width - innerSplit.SplitterWidth - 1));
                if (innerSplit.SplitterDistance != innerDistance) innerSplit.SplitterDistance = innerDistance;
            }
        }
        catch (InvalidOperationException exception)
        {
            System.Diagnostics.Debug.WriteLine($"Splitter layout deferred ({exception.GetType().Name}).");
        }
        catch (ArgumentException exception)
        {
            System.Diagnostics.Debug.WriteLine($"Splitter layout deferred ({exception.GetType().Name}).");
        }
    }

    private void RestartSearchTimer() { searchTimer.Stop(); searchTimer.Start(); }

    private Task InvokeOnUiThreadAsync(Action action)
    {
        if (disposed || IsDisposed || Disposing) return Task.CompletedTask;
        if (Environment.CurrentManagedThreadId == uiThreadId)
        {
            action();
            return Task.CompletedTask;
        }
        if (!IsHandleCreated) return Task.CompletedTask;

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke((MethodInvoker)(() =>
            {
                try
                {
                    if (!disposed && !IsDisposed && !Disposing) action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }));
        }
        catch (InvalidOperationException) when (disposed || IsDisposed || Disposing || !IsHandleCreated)
        {
            completion.TrySetResult();
        }
        return completion.Task;
    }

    private void SetSaveStatus(string text, string tag)
    {
        saveStatus.Text = text;
        saveStatus.Tag = tag switch
        {
            "warning" => "header-warning",
            "danger-text" => "header-danger",
            _ => "header-muted"
        };
        saveStatus.AccessibleDescription = text;
        var preferred = tag switch
        {
            "warning" => ThemeManager.Current.Warning,
            "danger-text" => ThemeManager.Current.Danger,
            _ => ThemeManager.Current.Muted
        };
        saveStatus.ForeColor = ThemeManager.ReadableForeground(
            ThemeManager.Current.Header,
            preferred,
            ThemeManager.Current.HeaderText);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            disposed = true;
            rmkStatusRevision++;
            SuspendBackgroundUiWorkForClose();
            searchTimer?.Stop();
            searchTimer?.Dispose();
            commandToolTips.Dispose();
        }
        base.Dispose(disposing);
    }

    private static RichTextBox NewRichText(bool readOnly) => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = readOnly,
        BorderStyle = BorderStyle.FixedSingle,
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.Vertical
    };

    private static ComboBox NewChoice<T>(IEnumerable<Choice<T>> values)
    {
        var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(Choice<T>.Text) };
        foreach (var value in values) combo.Items.Add(value);
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        return combo;
    }

    private static void AddTool(Control parent, string text, int width, Action action)
    {
        var button = Ui.Button(text, null, width);
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
    }

    private static int IndexOfReference(IReadOnlyList<ReviewItem> values, ReviewItem target)
    {
        for (var index = 0; index < values.Count; index++) if (ReferenceEquals(values[index], target)) return index;
        return -1;
    }

    private static string Preview(string value, int maximum)
    {
        var text = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maximum ? text : text[..Math.Max(0, maximum - 3)] + "...";
    }

    private static IReadOnlyList<string> GetWarningMessages(ReviewItem item)
    {
        var translation = item.Decision.Text ?? string.Empty;
        var validation = TranslationValidator.Validate(item.Row.Source, translation);
        var messages = new List<string>();
        if (!validation.IsSafe) messages.Add("안전 적용 아님");
        if (string.IsNullOrWhiteSpace(translation)) messages.Add("빈 번역");
        if (validation.Pathological) messages.Add("비정상 개행");
        if (validation.MissingTokens.Count > 0) messages.Add("토큰 누락: " + string.Join('|', validation.MissingTokens));
        if (validation.UnexpectedTokens.Count > 0) messages.Add("추가된 토큰: " + string.Join('|', validation.UnexpectedTokens));
        if (validation.TokenCountMismatches.Count > 0) messages.Add("토큰 개수 변경: " + string.Join('|', validation.TokenCountMismatches));
        if (validation.GrammarPrefixMoved) messages.Add("문법 접두사 위치 변경");
        if (validation.InvalidParticles.Count > 0) messages.Add("림월드 조사 표기 오류: " + string.Join('|', validation.InvalidParticles));
        if (translation.Equals(item.Row.Source, StringComparison.Ordinal)) messages.Add("원문과 동일");
        if (!validation.ContainsKorean) messages.Add("한글 없음");
        return messages;
    }

    private static string StatusText(ReviewItem item) => item.Decision.SourceChanged ? "업데이트로 변경됨 · 미번역" : item.EffectiveStatus switch
    {
        ReviewStatuses.Approved => "검토됨",
        ReviewStatuses.Translated => "번역됨",
        _ => "미번역"
    };

    private static string OriginText(string origin) => origin.ToLowerInvariant() switch
    {
        "rmk" => "RMK 가져옴",
        "local" => "내 번역",
        "ai" => "AI 초벌",
        "mod" => "모드 기존",
        "existing" => "기존 번역",
        _ => "출처 없음"
    };

    private static string BuildHistory(ReviewItem item)
    {
        var lines = new List<string>
        {
            $"현재 상태  {StatusText(item)}",
            $"번역 출처  {OriginText(item.Decision.TranslationOrigin)}",
            $"최근 번역  {FormatTime(item.Decision.TranslationUpdatedAt)}",
            string.Empty,
            "[현재 번역]",
            item.Decision.Text,
            string.Empty,
            "[기존 번역]",
            item.Row.Existing,
            string.Empty,
            "[AI 후보]",
            item.Row.Candidate,
            string.Empty,
            "[원문]",
            item.Row.Source
        };
        if (item.Decision.SourceChanged)
        {
            lines.InsertRange(4, ["[번역 당시 원문]", item.Decision.PreviousSourceText, string.Empty]);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildRmkInfo(ReviewItem item) => string.Join(Environment.NewLine,
        "RMK 연결 정보",
        string.Empty,
        $"식별자  {item.Row.RmkIdentifier}",
        $"번역 출처  {OriginText(item.Row.ExistingOrigin)}",
        $"원문 변경  {(item.Row.RmkSourceChanged ? "예" : "아니오")}",
        $"XLSX  {item.Row.RmkWorkbook}",
        string.Empty,
        "[번역 당시 원문]",
        item.Row.RmkHistoricalSource,
        string.Empty,
        "[현재 원문]",
        item.Row.RmkCurrentSource);

    private static string CategoryText(string category) => category switch
    {
        "Missing" => "미번역",
        "SourceChanged" => "원문 변경",
        "Unsafe" => "안전 실패",
        "TokenOrTag" => "토큰/태그",
        "SameAsSource" => "원문 동일",
        "TooShort" => "너무 짧음",
        "TooLong" => "너무 김",
        "DuplicateIdentity" => "중복 식별자",
        "ExistingChanged" => "기존과 다름",
        _ => category
    };

    private static string FormatTime(string value) => DateTimeOffset.TryParse(value, out var time)
        ? time.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
        : "-";

    private sealed record Choice<T>(string Text, T Value)
    {
        public override string ToString() => Text;
    }

    private sealed record MemoryChoice(string Label, string Text)
    {
        public override string ToString() => Label;
    }
}
