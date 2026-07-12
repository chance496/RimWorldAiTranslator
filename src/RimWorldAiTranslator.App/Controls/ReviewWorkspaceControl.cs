using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Quality;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.App.Controls;

internal sealed record WorkspaceApplyRequest(ReviewApplyStatus Status, bool UseRmk);

internal sealed class ReviewWorkspaceControl : UserControl
{
    private readonly AppServices services;
    private readonly Label projectName;
    private readonly Label projectPath;
    private readonly Label operationStatus;
    private readonly ProgressBar operationProgress;
    private readonly Button aiTranslate;
    private readonly Button stop;
    private readonly Button sourceRefresh;
    private readonly Button applyApproved;
    private readonly Button applyTranslated;
    private readonly CheckBox useRmk;
    private TextBox search = null!;
    private ComboBox searchField = null!;
    private ComboBox statusFilter = null!;
    private ComboBox fileFilter = null!;
    private ComboBox sortMode = null!;
    private Label summary = null!;
    private ListView list = null!;
    private Label itemStatus = null!;
    private RichTextBox source = null!;
    private RichTextBox translation = null!;
    private Label defClass = null!;
    private Label node = null!;
    private Label metadata = null!;
    private RichTextBox existing = null!;
    private RichTextBox candidate = null!;
    private RichTextBox history = null!;
    private ListBox terms = null!;
    private TextBox memo = null!;
    private RichTextBox rmkInfo = null!;
    private ListView issues = null!;
    private RichTextBox log = null!;
    private ListBox memory = null!;
    private TabControl sideTabs = null!;
    private readonly System.Windows.Forms.Timer searchTimer;
    private readonly SplitContainer outerSplit;
    private readonly SplitContainer innerSplit;
    private IReadOnlyList<GlossaryTerm> glossary = [];
    private IReadOnlyList<ReviewItem> filtered = [];
    private IReadOnlyList<QualityIssue> qualityIssues = [];
    private TranslationProject? project;
    private ReviewWorkspace? workspace;
    private ReviewItem? current;
    private bool suppressEditor;
    private bool editorTouched;
    private string editBaseline = string.Empty;
    private bool splitInitialized;

    public ReviewWorkspaceControl(AppServices services)
    {
        this.services = services;
        Dock = DockStyle.Fill;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(24, 12, 20, 8), Tag = "surface" };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var identity = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        projectName = Ui.Label("프로젝트", 13f, FontStyle.Bold);
        projectPath = Ui.Label(string.Empty, 8.5f);
        projectPath.AutoEllipsis = true;
        projectPath.MaximumSize = new Size(720, 24);
        identity.Controls.Add(projectName);
        identity.Controls.Add(projectPath);
        var navigation = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var projects = Ui.Button("프로젝트", null, 92);
        projects.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var folder = Ui.Button("폴더", null, 76);
        folder.Click += (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty);
        var save = Ui.Button("저장", null, 76);
        save.Click += (_, _) => CommitWithDuplicatePrompt();
        navigation.Controls.Add(projects);
        navigation.Controls.Add(save);
        navigation.Controls.Add(folder);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        sourceRefresh = Ui.Button("원문 갱신", null, 100);
        sourceRefresh.Click += (_, _) => SourceRefreshRequested?.Invoke(this, EventArgs.Empty);
        aiTranslate = Ui.Button("AI 번역", "primary", 100);
        aiTranslate.Click += (_, _) => AiTranslateRequested?.Invoke(this, EventArgs.Empty);
        stop = Ui.Button("중지", "danger", 76);
        stop.Enabled = false;
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        useRmk = new CheckBox { Text = "RMK에 적용", AutoSize = true, Margin = new Padding(10, 14, 5, 0) };
        applyApproved = Ui.Button("검토됨 적용", "success", 112);
        applyApproved.Click += (_, _) => ApplyRequested?.Invoke(this, new WorkspaceApplyRequest(ReviewApplyStatus.ApprovedOnly, useRmk.Checked));
        applyTranslated = Ui.Button("번역됨까지 적용", "primary", 132);
        applyTranslated.Click += (_, _) => ApplyRequested?.Invoke(this, new WorkspaceApplyRequest(ReviewApplyStatus.TranslatedAndApproved, useRmk.Checked));
        actions.Controls.Add(sourceRefresh);
        actions.Controls.Add(aiTranslate);
        actions.Controls.Add(stop);
        actions.Controls.Add(useRmk);
        actions.Controls.Add(applyApproved);
        actions.Controls.Add(applyTranslated);
        header.Controls.Add(identity, 0, 0);
        header.Controls.Add(navigation, 1, 0);
        header.Controls.Add(actions, 2, 0);

        var progressLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(24, 5, 24, 5), Tag = "surface" };
        progressLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        operationStatus = Ui.Label("준비됨", 8.5f, FontStyle.Bold);
        operationProgress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Blocks, Minimum = 0, Maximum = 100 };
        progressLine.Controls.Add(operationStatus, 0, 0);
        progressLine.Controls.Add(operationProgress, 1, 0);

        outerSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.None, SplitterWidth = 5 };
        innerSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.None, SplitterWidth = 5 };
        outerSplit.Panel2.Controls.Add(innerSplit);
        outerSplit.Panel1.Controls.Add(BuildListPane());
        innerSplit.Panel1.Controls.Add(BuildEditorPane());
        innerSplit.Panel2.Controls.Add(BuildSidePane());

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(progressLine, 0, 1);
        root.Controls.Add(outerSplit, 0, 2);
        Controls.Add(root);

        searchTimer = new System.Windows.Forms.Timer { Interval = 180 };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshFilter(true); };
        Resize += (_, _) => InitializeSplitters();
    }

    public event EventHandler? BackRequested;
    public event EventHandler? OpenFolderRequested;
    public event EventHandler? SourceRefreshRequested;
    public event EventHandler? AiTranslateRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<WorkspaceApplyRequest>? ApplyRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? QualityExportRequested;

    public ReviewWorkspace? Workspace => workspace;
    public TranslationProject? Project => project;

    public void SetGlossary(IReadOnlyList<GlossaryTerm> termsList) => glossary = termsList;

    public void SetWorkspace(TranslationProject selectedProject, ReviewWorkspace loaded)
    {
        SaveCurrentEditor(false);
        project = selectedProject;
        workspace = loaded;
        current = null;
        projectName.Text = selectedProject.Name;
        projectPath.Text = selectedProject.ModRoot;
        fileFilter.BeginUpdate();
        fileFilter.Items.Clear();
        fileFilter.Items.Add(new Choice<string>("전체 파일", string.Empty));
        foreach (var file in loaded.Items.Select(item => item.RelativeTarget).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            fileFilter.Items.Add(new Choice<string>(file, file));
        fileFilter.SelectedIndex = 0;
        fileFilter.EndUpdate();
        BuildQualityIssues();
        RefreshFilter(false);
        operationStatus.Text = $"불러옴 · {loaded.Items.Count:N0}개 문자열";
        if (filtered.Count > 0) SelectFilteredIndex(0);
    }

    public void SetRunning(bool running, string message = "")
    {
        aiTranslate.Enabled = !running && workspace is not null;
        sourceRefresh.Enabled = !running && project is not null;
        applyApproved.Enabled = !running && workspace is not null;
        applyTranslated.Enabled = !running && workspace is not null;
        stop.Enabled = running;
        operationProgress.Style = running ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        operationProgress.MarqueeAnimationSpeed = running ? 24 : 0;
        if (!string.IsNullOrWhiteSpace(message)) operationStatus.Text = message;
    }

    public void UpdateProgress(TranslationProgress progress)
    {
        operationStatus.Text = progress.Message;
        if (progress.Total > 0)
        {
            operationProgress.Style = ProgressBarStyle.Blocks;
            operationProgress.Value = Math.Clamp((int)Math.Round(progress.Current * 100d / progress.Total), 0, 100);
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

    public IReadOnlyList<string> GetRecentLogLines() => log.Lines.TakeLast(20_000).ToArray();

    public IReadOnlyList<QualityEntry> GetQualityEntries() => workspace is null
        ? []
        : workspace.Items.Select((item, index) => new QualityEntry(
            index, item.Row.Key, item.RelativeTarget, item.Row.DefClass, item.Row.Source, item.Decision.Text,
            item.Row.Existing, item.EffectiveStatus, item.Decision.SourceChanged,
            ReviewSafety.IsTranslationSafe(item.Row, item.Decision.Text, services.Extractor))).ToArray();

    public bool HandleShortcut(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S)) { CommitWithDuplicatePrompt(); return true; }
        if (keyData == (Keys.Control | Keys.F)) { search.Focus(); search.SelectAll(); return true; }
        if (keyData == (Keys.Control | Keys.Enter)) { SetCurrentStatus(ReviewStatuses.Approved, true); return true; }
        if (keyData == (Keys.Alt | Keys.Left)) { MoveSelection(-1); return true; }
        if (keyData == (Keys.Alt | Keys.Right)) { MoveSelection(1); return true; }
        if (keyData == (Keys.Control | Keys.D1)) { SetCurrentStatus(ReviewStatuses.Pending, false); return true; }
        if (keyData == (Keys.Control | Keys.D2)) { SetCurrentStatus(ReviewStatuses.Translated, false); return true; }
        if (keyData == (Keys.Control | Keys.D3)) { SetCurrentStatus(ReviewStatuses.Approved, false); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.D3)) { ApproveAllSafe(); return true; }
        return false;
    }

    private Control BuildListPane()
    {
        var panel = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 15, 14, 15), Tag = "surface" };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var crumb = Ui.Label("전체 문자열 · 모든 상태", 11f, FontStyle.Bold);
        search = Ui.TextBox("검색어 입력");
        search.Dock = DockStyle.Fill;
        search.TextChanged += (_, _) => RestartSearchTimer();
        var searchLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        searchLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        searchLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchField = NewChoice([
            new Choice<ReviewSearchField>("텍스트/키", ReviewSearchField.All),
            new Choice<ReviewSearchField>("텍스트", ReviewSearchField.Text),
            new Choice<ReviewSearchField>("키", ReviewSearchField.Key),
            new Choice<ReviewSearchField>("Def Class", ReviewSearchField.DefClass),
            new Choice<ReviewSearchField>("Node", ReviewSearchField.Node)
        ]);
        searchField.SelectedIndexChanged += (_, _) => RestartSearchTimer();
        searchLine.Controls.Add(searchField, 0, 0);
        searchLine.Controls.Add(search, 1, 0);
        var filterLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        filterLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        filterLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        filterLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        statusFilter = NewChoice([
            new Choice<ReviewStatusFilter>("전체", ReviewStatusFilter.All),
            new Choice<ReviewStatusFilter>("미번역", ReviewStatusFilter.Pending),
            new Choice<ReviewStatusFilter>("번역됨", ReviewStatusFilter.Translated),
            new Choice<ReviewStatusFilter>("검토됨", ReviewStatusFilter.Approved),
            new Choice<ReviewStatusFilter>("업데이트로 변경됨", ReviewStatusFilter.Updated),
            new Choice<ReviewStatusFilter>("주의", ReviewStatusFilter.Warning),
            new Choice<ReviewStatusFilter>("후보 있음", ReviewStatusFilter.HasCandidate),
            new Choice<ReviewStatusFilter>("기존 있음", ReviewStatusFilter.HasExisting),
            new Choice<ReviewStatusFilter>("RMK 가져옴", ReviewStatusFilter.Rmk),
            new Choice<ReviewStatusFilter>("내 번역", ReviewStatusFilter.Local)
        ]);
        statusFilter.SelectedIndexChanged += (_, _) => RefreshFilter(true);
        fileFilter = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(Choice<string>.Text) };
        fileFilter.SelectedIndexChanged += (_, _) => RefreshFilter(true);
        sortMode = NewChoice([
            new Choice<ReviewSortMode>("기본 순서", ReviewSortMode.Default),
            new Choice<ReviewSortMode>("최근 번역", ReviewSortMode.TranslationNewest),
            new Choice<ReviewSortMode>("오래된 번역", ReviewSortMode.TranslationOldest),
            new Choice<ReviewSortMode>("키 순서", ReviewSortMode.Key)
        ]);
        sortMode.SelectedIndexChanged += (_, _) => RefreshFilter(true);
        filterLine.Controls.Add(statusFilter, 0, 0);
        filterLine.Controls.Add(fileFilter, 1, 0);
        filterLine.Controls.Add(sortMode, 2, 0);
        summary = Ui.Label("전체 0", 8.5f, FontStyle.Bold);
        list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            VirtualMode = true,
            BorderStyle = BorderStyle.FixedSingle
        };
        list.Columns.Add("상태", 74);
        list.Columns.Add("키", 190);
        list.Columns.Add("원문", 260);
        list.Columns.Add("번역", 250);
        list.RetrieveVirtualItem += RetrieveVirtualItem;
        list.SelectedIndexChanged += (_, _) => SelectionChanged();
        list.DoubleClick += (_, _) => translation.Focus();
        var pager = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var next = Ui.Button(">", null, 44); next.Click += (_, _) => MoveSelection(1);
        var previous = Ui.Button("<", null, 44); previous.Click += (_, _) => MoveSelection(-1);
        pager.Controls.Add(next); pager.Controls.Add(previous);
        root.Controls.Add(crumb, 0, 0);
        root.Controls.Add(searchLine, 0, 1);
        root.Controls.Add(filterLine, 0, 2);
        root.Controls.Add(summary, 0, 3);
        root.Controls.Add(list, 0, 4);
        root.Controls.Add(pager, 0, 5);
        panel.Controls.Add(root);
        return panel;
    }

    private Control BuildEditorPane()
    {
        var panel = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 15, 24, 15), Tag = "surface" };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 29));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 23));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        itemStatus = Ui.Label("문자열을 선택하세요", 9f, FontStyle.Bold);
        source = NewRichText(true);
        var translationLabel = Ui.Label("번역문", 8.5f, FontStyle.Bold);
        translation = NewRichText(false);
        translation.TextChanged += (_, _) => { if (!suppressEditor) editorTouched = true; };
        var context = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 5, 0, 5) };
        defClass = Ui.Label("Def Class : -", 8.5f);
        node = Ui.Label("Node : -", 8.5f);
        metadata = Ui.Label("키 -", 8.2f);
        defClass.AutoEllipsis = node.AutoEllipsis = metadata.AutoEllipsis = true;
        context.Controls.Add(defClass); context.Controls.Add(node); context.Controls.Add(metadata);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        AddTool(toolbar, "<", 44, () => MoveSelection(-1));
        AddTool(toolbar, ">", 44, () => MoveSelection(1));
        AddTool(toolbar, "AI 후보", 82, UseCandidate);
        AddTool(toolbar, "기존", 70, UseExisting);
        AddTool(toolbar, "복사", 70, CopyTranslation);
        AddTool(toolbar, "되돌리기", 84, UndoEdit);
        AddTool(toolbar, "미번역", 78, () => SetCurrentStatus(ReviewStatuses.Pending, false));
        AddTool(toolbar, "번역됨", 78, () => SetCurrentStatus(ReviewStatuses.Translated, false));
        var approved = Ui.Button("검토 완료", "success", 94); approved.Click += (_, _) => SetCurrentStatus(ReviewStatuses.Approved, false); toolbar.Controls.Add(approved);
        var approveNext = Ui.Button("완료 후 다음", "success", 112); approveNext.Click += (_, _) => SetCurrentStatus(ReviewStatuses.Approved, true); toolbar.Controls.Add(approveNext);
        var approveAll = Ui.Button("전체 검토", null, 94); approveAll.Click += (_, _) => ApproveAllSafe(); toolbar.Controls.Add(approveAll);
        var referenceLabel = Ui.Label("참고 번역", 8.5f, FontStyle.Bold);
        var references = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        references.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        references.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        existing = NewRichText(true);
        candidate = NewRichText(true);
        references.Controls.Add(existing, 0, 0);
        references.Controls.Add(candidate, 1, 0);
        memory = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, DisplayMember = nameof(MemoryChoice.Text) };
        memory.DoubleClick += (_, _) => { if (memory.SelectedItem is MemoryChoice choice) SetEditorText(choice.Text); };
        root.Controls.Add(itemStatus, 0, 0);
        root.Controls.Add(source, 0, 1);
        root.Controls.Add(translationLabel, 0, 2);
        root.Controls.Add(translation, 0, 3);
        root.Controls.Add(context, 0, 4);
        root.Controls.Add(toolbar, 0, 5);
        root.Controls.Add(referenceLabel, 0, 6);
        root.Controls.Add(references, 0, 7);
        root.Controls.Add(memory, 0, 8);
        panel.Controls.Add(root);
        return panel;
    }

    private Control BuildSidePane()
    {
        var panel = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 15, 18, 15), Tag = "surface" };
        sideTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(68, 28)
        };
        history = NewRichText(true);
        terms = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        memo = Ui.TextBox("이 문자열에 대한 메모", true);
        memo.Dock = DockStyle.Fill;
        memo.TextChanged += (_, _) => { if (!suppressEditor) editorTouched = true; };
        rmkInfo = NewRichText(true);
        issues = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
        issues.Columns.Add("종류", 110); issues.Columns.Add("키", 180); issues.Columns.Add("설명", 320);
        issues.DoubleClick += (_, _) => SelectIssue();
        log = NewRichText(true);
        AddTab("역사", history);
        AddTab("용어", terms);
        AddTab("메모", memo);
        AddTab("RMK", rmkInfo);
        var issuesPane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        issuesPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        issuesPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var exportQuality = Ui.Button("품질 보고서 저장", null, 150);
        exportQuality.Click += (_, _) => QualityExportRequested?.Invoke(this, EventArgs.Empty);
        issuesPane.Controls.Add(exportQuality, 0, 0);
        issuesPane.Controls.Add(issues, 0, 1);
        AddTab("문제", issuesPane);
        AddTab("로그", log);
        panel.Controls.Add(sideTabs);
        return panel;

        void AddTab(string name, Control control)
        {
            var page = new TabPage(name) { Padding = new Padding(10) };
            page.Controls.Add(control);
            sideTabs.TabPages.Add(page);
        }
    }

    private void RefreshFilter(bool keepCurrent)
    {
        if (workspace is null) { filtered = []; list.VirtualListSize = 0; return; }
        SaveEditor(false);
        var selected = keepCurrent ? current : null;
        var query = new ReviewQuery(
            search.Text,
            (searchField.SelectedItem as Choice<ReviewSearchField>)?.Value ?? ReviewSearchField.All,
            (statusFilter.SelectedItem as Choice<ReviewStatusFilter>)?.Value ?? ReviewStatusFilter.All,
            (fileFilter.SelectedItem as Choice<string>)?.Value ?? string.Empty,
            (sortMode.SelectedItem as Choice<ReviewSortMode>)?.Value ?? ReviewSortMode.Default);
        filtered = services.Reviews.Query(workspace, query);
        list.VirtualListSize = filtered.Count;
        list.Invalidate();
        var stats = services.Reviews.GetStats(workspace);
        summary.Text = $"전체 {stats.Total:N0}  ·  미번역 {stats.Pending:N0}  ·  번역됨 {stats.Translated:N0}  ·  검토됨 {stats.Approved:N0}  ·  변경 {stats.Updated:N0}  ·  주의 {stats.Warnings:N0}";
        if (selected is not null)
        {
            var index = IndexOfReference(filtered, selected);
            if (index >= 0) SelectFilteredIndex(index);
            else if (filtered.Count > 0) SelectFilteredIndex(0);
            else ClearEditor();
        }
        else if (filtered.Count == 0) ClearEditor();
    }

    private void RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= filtered.Count) { e.Item = new ListViewItem(); return; }
        var item = filtered[e.ItemIndex];
        var status = item.Decision.SourceChanged ? "변경됨" : item.EffectiveStatus switch
        {
            ReviewStatuses.Approved => "검토됨",
            ReviewStatuses.Translated => "번역됨",
            _ => "미번역"
        };
        var sourcePreview = Preview(item.Row.Source, 70);
        var translationPreview = Preview(item.Decision.Text, 70);
        e.Item = new ListViewItem([status, item.Row.Key, sourcePreview, translationPreview]) { Tag = item };
        if (item.IsWarning) e.Item.ForeColor = Color.FromArgb(176, 108, 35);
    }

    private void SelectionChanged()
    {
        if (list.SelectedIndices.Count == 0) return;
        SelectFilteredIndex(list.SelectedIndices[0]);
    }

    private void SelectFilteredIndex(int index)
    {
        if (index < 0 || index >= filtered.Count) return;
        if (!ReferenceEquals(current, filtered[index])) SaveEditor(false);
        current = filtered[index];
        if (list.SelectedIndices.Count == 0 || list.SelectedIndices[0] != index)
        {
            list.SelectedIndices.Clear();
            list.SelectedIndices.Add(index);
        }
        list.EnsureVisible(index);
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
            existing.Text = "[기존 번역]\n" + current.Row.Existing;
            candidate.Text = "[AI 후보]\n" + current.Row.Candidate;
            editBaseline = current.Decision.Text;
            editorTouched = false;
            var context = DefContextService.Get(current.Row);
            defClass.Text = context.ClassLine;
            node.Text = context.NodeLine;
            metadata.Text = $"키  {current.Row.Key}    ·    파일  {current.RelativeTarget}    ·    ID  {current.Row.Id}";
            itemStatus.Text = $"{IndexOfReference(filtered, current) + 1:N0} / {filtered.Count:N0}    {StatusText(current)}    ·    출처 {OriginText(current.Decision.TranslationOrigin)}";
            history.Text = BuildHistory(current);
            rmkInfo.Text = BuildRmkInfo(current);
            RefreshTerms(current);
            RefreshMemory(current);
        }
        finally { suppressEditor = false; }
    }

    private void SaveEditor(bool promptDuplicates)
    {
        if (suppressEditor || current is null || workspace is null || !editorTouched) return;
        services.Reviews.UpdateTranslation(workspace, current, translation.Text, memo.Text, true);
        editorTouched = false;
        editBaseline = current.Decision.Text;
        if (promptDuplicates)
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
        RefreshFilter(true);
    }

    private void CommitWithDuplicatePrompt()
    {
        SaveEditor(true);
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetCurrentStatus(string status, bool moveNext)
    {
        if (current is null || workspace is null) return;
        SaveEditor(true);
        if (status != ReviewStatuses.Pending && string.IsNullOrWhiteSpace(current.Decision.Text))
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        if (status == ReviewStatuses.Approved && current.IsWarning)
        {
            var answer = MessageBox.Show(FindForm(), "토큰, 조사 또는 원문 동일 경고가 있습니다. 그래도 검토 완료로 표시할까요?", "번역 안전 경고", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        services.Reviews.SetStatus(workspace, current, status);
        RefreshFilter(true);
        SaveRequested?.Invoke(this, EventArgs.Empty);
        if (moveNext) MoveSelection(1);
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
        RefreshFilter(true);
        SaveRequested?.Invoke(this, EventArgs.Empty);
        MessageBox.Show(FindForm(), $"{approved:N0}개를 검토 완료로 표시했습니다.", "전체 검토", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void MoveSelection(int delta)
    {
        if (filtered.Count == 0) return;
        var index = current is null ? 0 : IndexOfReference(filtered, current);
        SelectFilteredIndex(Math.Clamp(index + delta, 0, filtered.Count - 1));
    }

    private void UseCandidate() { if (current is not null && !string.IsNullOrWhiteSpace(current.Row.Candidate)) SetEditorText(current.Row.Candidate); }
    private void UseExisting() { if (current is not null && !string.IsNullOrWhiteSpace(current.Row.Existing)) SetEditorText(current.Row.Existing); }
    private void UndoEdit() => SetEditorText(editBaseline);
    private void CopyTranslation() { if (!string.IsNullOrEmpty(translation.Text)) Clipboard.SetText(translation.Text); }

    private void SetEditorText(string value)
    {
        suppressEditor = true;
        translation.Text = value;
        suppressEditor = false;
        editorTouched = true;
        translation.Focus();
        translation.SelectionStart = translation.TextLength;
    }

    private void BuildQualityIssues()
    {
        if (workspace is null) return;
        var entries = GetQualityEntries();
        qualityIssues = QualityService.FindIssues(entries);
        issues.BeginUpdate();
        issues.Items.Clear();
        foreach (var issue in qualityIssues)
            issues.Items.Add(new ListViewItem([CategoryText(issue.Category), issue.Key, issue.Detail]) { Tag = issue });
        issues.EndUpdate();
    }

    private void SelectIssue()
    {
        if (workspace is null || issues.SelectedItems.Count == 0 || issues.SelectedItems[0].Tag is not QualityIssue issue) return;
        var target = workspace.Items.ElementAtOrDefault(issue.Index);
        if (target is null) return;
        search.Text = target.Row.Key;
        searchField.SelectedItem = searchField.Items.Cast<Choice<ReviewSearchField>>().First(choice => choice.Value == ReviewSearchField.Key);
        RefreshFilter(false);
        var index = IndexOfReference(filtered, target);
        if (index >= 0) SelectFilteredIndex(index);
    }

    private void RefreshTerms(ReviewItem item)
    {
        terms.BeginUpdate();
        terms.Items.Clear();
        foreach (var term in glossary.Where(term => item.Row.Source.Contains(term.Source, StringComparison.OrdinalIgnoreCase)).OrderBy(term => term.Priority).ThenByDescending(term => term.Source.Length).Take(120))
            terms.Items.Add(string.IsNullOrWhiteSpace(term.Note) ? $"{term.Source}  →  {term.Korean}" : $"{term.Source}  →  {term.Korean}  ·  {term.Note}");
        if (terms.Items.Count == 0) terms.Items.Add("이 문자열에 직접 일치하는 용어가 없습니다.");
        terms.EndUpdate();
    }

    private void RefreshMemory(ReviewItem item)
    {
        if (workspace is null) return;
        var entries = workspace.Items.Select(value => new TranslationMemoryEntry(
            $"{value.RelativeTarget}|{value.Row.Key}", value.Row.Source, value.Decision.Text, value.Decision.TranslationOrigin,
            value.EffectiveStatus, value.RelativeTarget, value.Decision.TranslationUpdatedAt, value.Decision.SourceChanged,
            ReviewSafety.IsTranslationSafe(value.Row, value.Decision.Text, services.Extractor)));
        var suggestions = TranslationMemoryService.Select(entries, item.Row.Source, $"{item.RelativeTarget}|{item.Row.Key}");
        memory.BeginUpdate();
        memory.Items.Clear();
        foreach (var suggestion in suggestions) memory.Items.Add(new MemoryChoice($"{OriginText(suggestion.Origin)} · {Preview(suggestion.Text, 100)}", suggestion.Text));
        if (memory.Items.Count == 0) memory.Items.Add(new MemoryChoice("같은 원문의 다른 번역이 없습니다.", string.Empty));
        memory.EndUpdate();
    }

    private void ClearEditor()
    {
        current = null;
        suppressEditor = true;
        source.Clear(); translation.Clear(); memo.Clear(); existing.Clear(); candidate.Clear(); history.Clear(); rmkInfo.Clear(); terms.Items.Clear(); memory.Items.Clear();
        itemStatus.Text = "표시할 문자열이 없습니다";
        defClass.Text = "Def Class : -";
        node.Text = "Node : -";
        metadata.Text = "키 -";
        suppressEditor = false;
        editorTouched = false;
    }

    private void InitializeSplitters()
    {
        if (Width < 1100 || splitInitialized) return;
        try
        {
            outerSplit.Panel1MinSize = 280;
            outerSplit.Panel2MinSize = 620;
            outerSplit.SplitterDistance = Math.Clamp((int)(Width * 0.27), 280, Width - 625);
            innerSplit.Panel1MinSize = 500;
            innerSplit.Panel2MinSize = 280;
            innerSplit.SplitterDistance = Math.Clamp((int)(outerSplit.Panel2.Width * 0.68), 500, outerSplit.Panel2.Width - 285);
            splitInitialized = true;
        }
        catch { }
    }

    private void RestartSearchTimer() { searchTimer.Stop(); searchTimer.Start(); }

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
        "Unsafe" => "안전 경고",
        "TokenOrTag" => "토큰/태그",
        "SameAsSource" => "원문 동일",
        "TooShort" => "너무 짧음",
        "TooLong" => "너무 김",
        "DuplicateIdentity" => "중복 키",
        _ => category
    };

    private static string FormatTime(string value) => DateTimeOffset.TryParse(value, out var time) ? time.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "-";

    private sealed record Choice<T>(string Text, T Value)
    {
        public override string ToString() => Text;
    }

    private sealed record MemoryChoice(string Label, string Text)
    {
        public override string ToString() => Label;
    }
}
