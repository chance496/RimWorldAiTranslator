using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;

namespace RimWorldAiTranslator.App.Controls;

internal sealed record ProjectCardStats(ReviewWorkspaceStats Stats, string Label);

internal sealed class ProjectDashboardControl : UserControl
{
    private readonly TextBox search;
    private readonly ComboBox modPicker;
    private readonly FlowLayoutPanel cards;
    private readonly Label providerStatus;
    private readonly Label providerHint;
    private readonly Button create;
    private readonly Button chooseFolder;
    private readonly Button refresh;
    private readonly CommandToolTipService commandToolTips;
    private IReadOnlyList<RimWorldModInfo> mods = [];
    private IReadOnlyList<TranslationProject> projects = [];
    private IReadOnlyDictionary<string, ProjectCardStats> stats = new Dictionary<string, ProjectCardStats>();
    private bool dataInitialized;
    private bool renderingCards;
    private bool busy;
    private int renderedCardsWidth = -1;

    public ProjectDashboardControl()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.ResizeRedraw,
            true);
        SuspendLayout();
        Dock = DockStyle.Fill;
        TabStop = false;
        AutoScaleMode = AutoScaleMode.None;

        var eyebrow = FixedLabel("RIMWORLD TRANSLATION WORKSPACE", 32, 22, 360, 18, 7.8f, FontStyle.Bold, "accent-label");
        var title = FixedLabel("모드 번역 작업실", 32, 42, 360, 34, 15f, FontStyle.Bold);
        var intro = FixedLabel("원문을 분석하고 초벌 번역을 만든 뒤, 한 줄씩 검토해 안전하게 적용합니다.", 32, 78, 620, 24, 8.8f, tag: "muted");
        providerStatus = FixedLabel("번역 엔진  ·  Google 대체 사용", 760, 26, 380, 26, 9f, FontStyle.Bold);
        providerStatus.TextAlign = ContentAlignment.MiddleRight;
        providerHint = FixedLabel("Cerebras 키 없음 · 설정에서 키 입력 가능", 760, 54, 380, 22, 8.1f, tag: "muted");
        providerHint.TextAlign = ContentAlignment.MiddleRight;

        var workflow = new BufferedFlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        var index = 0;
        foreach (var text in new[] { "모드 선택", "원문 분석", "초벌 번역", "검토 · 적용" })
        {
            index++;
            var step = FixedLabel($"0{index}  {text}", 0, 0, 132, 28, 8.2f, FontStyle.Bold, "muted");
            step.Margin = new Padding(0, 0, 14, 0);
            step.TextAlign = ContentAlignment.MiddleLeft;
            workflow.Controls.Add(step);
        }

        var divider = new Panel { Tag = "divider" };
        var searchLabel = FixedLabel("프로젝트 검색", 32, 166, 170, 20, 8.5f, FontStyle.Bold, "muted");
        search = Ui.TextBox("프로젝트 검색");
        search.Margin = Padding.Empty;
        search.AccessibleDescription = "프로젝트 이름, Workshop ID 또는 패키지 ID로 저장된 프로젝트를 검색합니다.";
        search.TabIndex = 0;
        search.TextChanged += (_, _) => RenderCards(force: true);
        var modLabel = FixedLabel("프로젝트 대상 모드", 378, 166, 170, 20, 8.5f, FontStyle.Bold, "muted");
        modPicker = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(RimWorldModInfo.Display),
            Font = new Font("Malgun Gothic", 9f),
            Margin = Padding.Empty,
            AccessibleName = "프로젝트 대상 모드",
            AccessibleDescription = "새 번역 프로젝트를 만들 RimWorld 모드를 선택합니다.",
            TabIndex = 1
        };
        create = Ui.Button("프로젝트 만들기", "primary", 126);
        create.Margin = Padding.Empty;
        create.AccessibleDescription = "선택한 모드와 원문 언어로 새 로컬 번역 프로젝트를 만듭니다.";
        create.TabIndex = 2;
        modPicker.SelectedIndexChanged += (_, _) => create.Enabled = modPicker.SelectedItem is RimWorldModInfo;
        create.Click += (_, _) =>
        {
            if (modPicker.SelectedItem is RimWorldModInfo mod) CreateProjectRequested?.Invoke(this, mod);
        };
        chooseFolder = Ui.Button("폴더 선택", null, 90);
        chooseFolder.Margin = Padding.Empty;
        chooseFolder.AccessibleDescription = "목록에 없는 로컬 RimWorld 모드 폴더를 선택합니다.";
        chooseFolder.TabIndex = 3;
        chooseFolder.Click += (_, _) => ChooseFolderRequested?.Invoke(this, EventArgs.Empty);
        refresh = Ui.Button("새로고침", null, 90);
        refresh.Margin = Padding.Empty;
        refresh.AccessibleDescription = "모드 검색 결과와 저장된 프로젝트 상태를 다시 불러옵니다.";
        refresh.TabIndex = 4;
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        commandToolTips = new CommandToolTipService();
        commandToolTips.Register(search, UiCommand.ProjectSearch);
        commandToolTips.Register(modPicker, UiCommand.ProjectCreate);
        commandToolTips.Register(
            create,
            UiCommand.ProjectCreate,
            () => busy
                ? "현재 작업이 실행 중이라 사용할 수 없습니다."
                : modPicker.SelectedItem is null ? "먼저 번역할 모드를 선택하세요." : null);
        commandToolTips.Register(
            chooseFolder,
            UiCommand.ChooseProjectFolder,
            () => busy ? "현재 작업이 실행 중이라 사용할 수 없습니다." : null);
        commandToolTips.Register(
            refresh,
            UiCommand.RefreshProjects,
            () => busy ? "현재 작업이 실행 중이라 사용할 수 없습니다." : null);

        cards = new BufferedFlowLayoutPanel
        {
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            TabStop = false,
            TabIndex = 5
        };
        cards.Resize += (_, _) => RenderCards();

        Controls.AddRange([
            eyebrow, title, intro, providerStatus, providerHint, workflow, divider,
            searchLabel, search, modLabel, modPicker, create, chooseFolder, refresh, cards
        ]);
        Resize += (_, _) => ResizeLayout(workflow, divider);
        ResizeLayout(workflow, divider);
        ResumeLayout(false);
    }

    public event EventHandler<RimWorldModInfo>? CreateProjectRequested;
    public event EventHandler? ChooseFolderRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler<TranslationProject>? OpenProjectRequested;
    public event EventHandler<TranslationProject>? DeleteProjectRequested;

    public void FocusSearch()
    {
        search.Focus();
        search.SelectAll();
    }

    public bool ClearSearchAndFocus()
    {
        if (search.TextLength == 0) return false;
        search.Clear();
        FocusSearch();
        return true;
    }

    public void FocusNextWorkRegion(int direction)
    {
        Control[] targets = [search, modPicker, create];
        var available = targets.Where(control => control.Visible && control.Enabled).ToArray();
        if (available.Length == 0) return;
        var currentIndex = Array.FindIndex(available, control => control.Focused || control.ContainsFocus);
        var next = (currentIndex + (direction < 0 ? -1 : 1)) % available.Length;
        if (next < 0) next += available.Length;
        available[next].Focus();
    }

    public void SetProviderStatus(string providerName, string model, int keyCount, bool needsKey)
    {
        if (!needsKey)
        {
            providerStatus.Text = "번역 엔진  ·  Google 번역";
            providerHint.Text = "API 키 없이 실행 · 용어집과 추가 프롬프트는 미지원";
        }
        else if (keyCount > 0)
        {
            providerStatus.Text = $"번역 엔진  ·  {providerName}";
            providerHint.Text = $"키 {keyCount:N0}개 · {model} · 키는 저장하지 않음";
        }
        else
        {
            providerStatus.Text = "번역 엔진  ·  Google 대체 사용";
            providerHint.Text = $"{providerName} 키 없음 · 설정에서 키 입력 가능";
        }
    }

    public void SetData(
        IReadOnlyList<RimWorldModInfo> availableMods,
        IReadOnlyList<TranslationProject> savedProjects,
        IReadOnlyDictionary<string, ProjectCardStats>? projectStats = null)
    {
        mods = availableMods;
        projects = savedProjects;
        stats = projectStats ?? new Dictionary<string, ProjectCardStats>();
        var selectedPath = (modPicker.SelectedItem as RimWorldModInfo)?.Path;
        modPicker.BeginUpdate();
        modPicker.Items.Clear();
        foreach (var mod in mods) modPicker.Items.Add(mod);
        var selected = mods.FirstOrDefault(mod => mod.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) modPicker.SelectedItem = selected;
        if (modPicker.SelectedIndex < 0 && modPicker.Items.Count > 0) modPicker.SelectedIndex = 0;
        modPicker.EndUpdate();
        create.Enabled = modPicker.SelectedItem is RimWorldModInfo;
        dataInitialized = true;
        RenderCards(force: true);
    }

    public void SelectProjectMod(TranslationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var selected = mods.FirstOrDefault(mod =>
            mod.Path.Equals(project.ModRoot, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) modPicker.SelectedItem = selected;
    }

    internal string SelectedModPathForTesting =>
        (modPicker.SelectedItem as RimWorldModInfo)?.Path ?? string.Empty;

    public void SetBusy(bool busy)
    {
        this.busy = busy;
        create.Enabled = !busy && modPicker.SelectedItem is RimWorldModInfo;
        chooseFolder.Enabled = !busy;
        refresh.Enabled = !busy;
        commandToolTips.RefreshAll();
    }

    public void RefreshCommandToolTips() => commandToolTips.RefreshAll();

    internal string CommandToolTipTextForTesting(Control control) => commandToolTips.GetText(control);
    internal int CommandToolTipRegistrationCountForTesting(Control control) => commandToolTips.RegistrationCount(control);
    internal bool CommandToolTipFitsForTesting(Control control, int dpi, Size workingArea) =>
        commandToolTips.FitsWorkingAreaForTesting(control, dpi, workingArea);

    private void ResizeLayout(FlowLayoutPanel workflow, Panel divider)
    {
        var width = Math.Max(860, ClientSize.Width);
        providerStatus.SetBounds(width - 32 - Math.Clamp((int)(width * 0.30), 250, 420), 26, Math.Clamp((int)(width * 0.30), 250, 420), 26);
        providerHint.SetBounds(providerStatus.Left, 54, providerStatus.Width, 22);
        workflow.SetBounds(32, 110, Math.Max(420, width - 64), 30);
        divider.SetBounds(32, 148, Math.Max(320, width - 64), 1);
        var searchWidth = Math.Clamp((int)(width * 0.25), 250, 360);
        var modX = 32 + searchWidth + 28;
        var buttonX = width - 32 - 322;
        search.SetBounds(32, 190, searchWidth, 34);
        foreach (Control label in Controls)
            if (label.Text == "프로젝트 대상 모드") label.SetBounds(modX, 166, 170, 20);
        if (width < 1_000)
        {
            modPicker.SetBounds(modX, 190, Math.Max(240, width - modX - 32), 34);
            create.SetBounds(32, 232, 126, 34);
            chooseFolder.SetBounds(166, 232, 90, 34);
            refresh.SetBounds(264, 232, 90, 34);
            cards.SetBounds(22, 282, Math.Max(320, width - 44), Math.Max(180, ClientSize.Height - 306));
        }
        else
        {
            var comboWidth = Math.Max(180, buttonX - modX - 14);
            modPicker.SetBounds(modX, 190, comboWidth, 34);
            create.SetBounds(buttonX, 190, 126, 34);
            chooseFolder.SetBounds(buttonX + 134, 190, 90, 34);
            refresh.SetBounds(buttonX + 232, 190, 90, 34);
            cards.SetBounds(22, 244, Math.Max(320, width - 44), Math.Max(180, ClientSize.Height - 268));
        }
    }

    private void RenderCards(bool force = false)
    {
        if (!dataInitialized || renderingCards || cards.ClientSize.Width <= 0) return;
        if (!force && renderedCardsWidth == cards.ClientSize.Width) return;

        renderingCards = true;
        renderedCardsWidth = cards.ClientSize.Width;
        var filter = search.Text.Trim();
        var matching = projects
            .Where(project => string.IsNullOrWhiteSpace(filter) || SearchText(project).Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(project => ParseTime(project.UpdatedAt))
            .ToArray();
        var availableWidth = Math.Max(320, cards.ClientSize.Width - 20);
        var columns = Math.Clamp(availableWidth / 360, 1, 4);
        var cardWidth = Math.Clamp(availableWidth / columns - 20, 320, 440);

        cards.SuspendLayout();
        try
        {
            var previousCards = cards.Controls.Cast<Control>().ToArray();
            cards.Controls.Clear();
            foreach (var previousCard in previousCards) previousCard.Dispose();
            if (matching.Length == 0)
            {
                cards.Controls.Add(CreateEmptyState(Math.Max(560, availableWidth - 24), projects.Count == 0));
                return;
            }
            foreach (var project in matching) cards.Controls.Add(CreateCard(project, cardWidth));
        }
        finally
        {
            cards.ResumeLayout(true);
            ThemeManager.Apply(cards, ThemeManager.Current, ThemeManager.CurrentTextSize);
            renderingCards = false;
        }
    }

    private Control CreateEmptyState(int width, bool noProjects)
    {
        var panel = new BufferedPanel
        {
            Size = new Size(width, 248),
            Margin = new Padding(10, 8, 10, 8),
            BorderStyle = BorderStyle.FixedSingle,
            Tag = "surface"
        };
        var radar = new RadarPanel { Bounds = new Rectangle(28, 26, 160, 160), Tag = "radar" };
        var title = FixedLabel(noProjects ? "첫 번역 프로젝트를 준비하세요" : "검색 결과가 없습니다", 218, 34, Math.Max(280, width - 250), 32, 13f, FontStyle.Bold);
        var body = FixedLabel(
            noProjects
                ? "위에서 감지된 모드를 고르면 프로젝트와 원문 언어를 연결합니다.\r\n원본 모드는 읽기 전용으로 분석하며, 번역은 검수 프로젝트에 먼저 저장됩니다."
                : "검색어를 지우거나 다른 프로젝트 이름, Workshop ID 또는 패키지 ID를 입력하세요.",
            218, 78, Math.Max(280, width - 250), 54, 9f, tag: "muted");
        var status = FixedLabel($"감지된 모드 {mods.Count:N0}개  ·  프로젝트 데이터는 로컬에만 저장", 218, 142, Math.Max(280, width - 250), 24, 8.3f, FontStyle.Bold, "faint");
        var start = Ui.Button("선택한 모드로 시작", "primary", 154);
        start.SetBounds(218, 178, 154, 38);
        start.Margin = Padding.Empty;
        start.AccessibleDescription = "현재 선택한 모드로 새 로컬 번역 프로젝트를 만듭니다.";
        start.TabIndex = 0;
        start.Enabled = modPicker.SelectedItem is RimWorldModInfo;
        start.Click += (_, _) => create.PerformClick();
        commandToolTips.Register(
            start,
            UiCommand.ProjectCreate,
            () => busy
                ? "현재 작업이 실행 중이라 사용할 수 없습니다."
                : modPicker.SelectedItem is null ? "먼저 번역할 모드를 선택하세요." : null);
        var find = Ui.Button("폴더에서 찾기", null, 118);
        find.SetBounds(382, 178, 118, 38);
        find.Margin = Padding.Empty;
        find.AccessibleDescription = "목록에 없는 로컬 RimWorld 모드 폴더를 선택합니다.";
        find.TabIndex = 1;
        find.Click += (_, _) => chooseFolder.PerformClick();
        commandToolTips.Register(
            find,
            UiCommand.ChooseProjectFolder,
            () => busy ? "현재 작업이 실행 중이라 사용할 수 없습니다." : null);
        panel.Controls.AddRange([radar, title, body, status, start, find]);
        return panel;
    }

    private Control CreateCard(TranslationProject project, int width)
    {
        var current = stats.TryGetValue(project.Id, out var value)
            ? value.Stats
            : new ReviewWorkspaceStats(0, 0, 0, 0, 0, 0);
        var hasReview = !string.IsNullOrWhiteSpace(project.LatestReviewRoot);
        var progressDescription = hasReview
            ? $"{project.Name} 프로젝트: 전체 {current.Total:N0}개, 번역 {current.Translated:N0}개, 검토 {current.Approved:N0}개, 미번역 {current.Pending:N0}개, 업데이트 변경 {current.Updated:N0}개."
            : $"{project.Name} 프로젝트: 원문을 아직 불러오지 않았습니다.";
        var innerWidth = width - 44;
        var card = new BufferedPanel
        {
            Size = new Size(width, 204),
            Margin = new Padding(10),
            BorderStyle = BorderStyle.FixedSingle,
            Tag = "surface",
            Cursor = Cursors.Hand,
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = $"{project.Name} 프로젝트 카드",
            AccessibleDescription = progressDescription
        };
        var accent = new Panel { Bounds = new Rectangle(0, 0, 4, 202), Tag = "accent" };
        var name = FixedLabel(project.Name, 22, 18, innerWidth, 26, 11.5f, FontStyle.Bold);
        name.AutoEllipsis = true;
        name.AccessibleName = $"{project.Name} 프로젝트 제목";
        name.AccessibleDescription = $"{project.Name} 프로젝트 카드의 제목입니다.";
        var identity = !string.IsNullOrWhiteSpace(project.WorkshopId) ? $"Workshop {project.WorkshopId}" :
            !string.IsNullOrWhiteSpace(project.PackageId) ? project.PackageId : Path.GetFileName(project.ModRoot);
        var meta = FixedLabel($"{identity}  ·  원문 {SourceName(project.SourceLanguageFolder)}", 22, 48, innerWidth, 20, 8.3f, tag: "muted");
        meta.AutoEllipsis = true;
        var total = FixedLabel(hasReview ? $"전체 {current.Total:N0}" : "원문 미로드", 22, 78, 132, 30, hasReview ? 13f : 11f, FontStyle.Bold);
        var coverage = FixedLabel(hasReview ? $"번역 {current.Translated:N0}  ·  검토 {current.Approved:N0}" : "열어서 원문을 불러오세요", 154, 83, Math.Max(140, width - 176), 24, 8.8f, tag: "muted");
        var pending = FixedLabel($"미번역 {current.Pending:N0}", 22, 116, 110, 22, 8.7f, FontStyle.Bold, "warning");
        var updated = FixedLabel($"업데이트 변경 {current.Updated:N0}", 154, 116, Math.Max(140, width - 176), 22, 8.7f, FontStyle.Bold, current.Updated > 0 ? "danger-text" : "faint");
        total.AccessibleName = $"{project.Name} 프로젝트 진행 통계";
        total.AccessibleDescription = progressDescription;
        coverage.AccessibleName = $"{project.Name} 번역 및 검토 통계";
        coverage.AccessibleDescription = progressDescription;
        pending.AccessibleName = $"{project.Name} 미번역 통계";
        pending.AccessibleDescription = progressDescription;
        updated.AccessibleName = $"{project.Name} 업데이트 변경 통계";
        updated.AccessibleDescription = progressDescription;
        pending.Visible = hasReview;
        updated.Visible = hasReview;
        var track = new Panel { Bounds = new Rectangle(22, 146, innerWidth, 5), Tag = "progress-track" };
        var fillWidth = current.Total > 0 ? (int)Math.Round(innerWidth * (current.Translated + current.Approved) / (double)current.Total) : 0;
        track.Controls.Add(new Panel { Bounds = new Rectangle(0, 0, Math.Clamp(fillWidth, 0, innerWidth), 5), Tag = "accent" });
        var recent = FixedLabel($"최근 작업 {FormatDate(project.LatestReviewAt)}", 22, 170, Math.Max(110, width - 218), 20, 8.1f, tag: "faint");
        recent.AutoEllipsis = true;
        var open = Ui.Button("열기", "primary", 86);
        open.SetBounds(width - 106, 158, 86, 36);
        open.Margin = Padding.Empty;
        open.AccessibleName = $"{project.Name} 프로젝트 열기";
        open.AccessibleDescription = $"{project.Name} 번역 프로젝트의 검수 작업 화면을 엽니다.";
        open.TabIndex = 1;
        open.Click += (_, _) => OpenProjectRequested?.Invoke(this, project);
        commandToolTips.Register(
            open,
            UiCommand.OpenProject,
            description: () => $"{project.Name} 번역 프로젝트의 검수 화면을 엽니다.");
        var delete = Ui.Button("삭제", "danger", 70);
        delete.SetBounds(width - 184, 158, 70, 36);
        delete.Margin = Padding.Empty;
        delete.AccessibleName = $"{project.Name} 프로젝트 삭제";
        delete.AccessibleDescription = $"{project.Name} 로컬 번역 프로젝트를 삭제하기 위한 확인 절차를 시작합니다.";
        delete.TabIndex = 0;
        delete.Click += (_, _) => DeleteProjectRequested?.Invoke(this, project);
        commandToolTips.Register(
            delete,
            UiCommand.DeleteProject,
            description: () => $"{project.Name} 로컬 번역 프로젝트 삭제 확인을 시작합니다.");
        foreach (var control in new Control[] { card, accent, name, meta, total, coverage, pending, updated, track, recent })
        {
            control.Click += (_, _) => OpenProjectRequested?.Invoke(this, project);
        }
        card.Controls.AddRange([accent, name, meta, total, coverage, pending, updated, track, recent, delete, open]);
        return card;
    }

    private static Label FixedLabel(string text, int x, int y, int width, int height, float size, FontStyle style = FontStyle.Regular, string? tag = null)
    {
        var label = Ui.Label(text, size, style);
        label.AutoSize = false;
        label.SetBounds(x, y, width, height);
        label.Margin = Padding.Empty;
        label.Tag = tag;
        return label;
    }

    private static string SearchText(TranslationProject project) =>
        string.Join("\n", project.Name, project.ModRoot, project.PackageId, project.WorkshopId, project.SourceLanguageFolder);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.TryParse(value, out var time) ? time : DateTimeOffset.MinValue;
    private static string FormatDate(string value) => DateTimeOffset.TryParse(value, out var time)
        ? time.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
        : "-";
    private static string SourceName(string value) => value.ToLowerInvariant() switch
    {
        "auto" => "자동",
        "english" => "영어",
        "chinese" or "simplifiedchinese" or "chinesesimplified" => "중국어 간체",
        "traditionalchinese" or "chinesetraditional" => "중국어 번체",
        "japanese" => "일본어",
        _ => value
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) commandToolTips?.Dispose();
        base.Dispose(disposing);
    }

    private sealed class RadarPanel : Panel
    {
        public RadarPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var theme = ThemeManager.Current;
            using var accent = new Pen(theme.Accent, 2);
            using var soft = new Pen(theme.Border, 1);
            using var dot = new SolidBrush(theme.Accent);
            foreach (var size in new[] { 132, 92, 52 })
            {
                var offset = (150 - size) / 2;
                e.Graphics.DrawEllipse(soft, offset, offset, size, size);
            }
            e.Graphics.DrawLine(soft, 75, 3, 75, 147);
            e.Graphics.DrawLine(soft, 3, 75, 147, 75);
            e.Graphics.DrawArc(accent, 9, 9, 132, 132, 294, 52);
            e.Graphics.FillEllipse(dot, 104, 45, 9, 9);
            e.Graphics.FillEllipse(dot, 48, 101, 6, 6);
        }
    }
}
