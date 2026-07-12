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
    private readonly Label modCount;
    private readonly Button create;
    private readonly Button chooseFolder;
    private readonly Button refresh;
    private IReadOnlyList<RimWorldModInfo> mods = [];
    private IReadOnlyList<TranslationProject> projects = [];
    private IReadOnlyDictionary<string, ProjectCardStats> stats = new Dictionary<string, ProjectCardStats>();

    public ProjectDashboardControl()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(38, 28, 38, 28);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var headingText = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        headingText.Controls.Add(Ui.Label("프로젝트", 17f, FontStyle.Bold));
        headingText.Controls.Add(Ui.Label("모드 하나를 프로젝트 하나로 관리합니다. 번역과 검수 상태는 로컬에 저장됩니다.", 9.5f));
        modCount = Ui.Label("모드 검색 준비 중", 9f, FontStyle.Bold);
        heading.Controls.Add(headingText, 0, 0);
        heading.Controls.Add(modCount, 1, 0);

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 92, ColumnCount = 5, Padding = new Padding(0, 12, 0, 12) };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 162));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        search = Ui.TextBox("프로젝트 검색");
        search.Dock = DockStyle.Fill;
        search.TextChanged += (_, _) => RenderCards();
        modPicker = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(RimWorldModInfo.Display), Margin = new Padding(10, 4, 8, 4) };
        modPicker.SelectedIndexChanged += (_, _) => { if (create is not null) create.Enabled = modPicker.SelectedItem is RimWorldModInfo; };
        create = Ui.Button("프로젝트 만들기", "primary", 150);
        create.Dock = DockStyle.Fill;
        create.Enabled = false;
        create.Click += (_, _) => { if (modPicker.SelectedItem is RimWorldModInfo mod) CreateProjectRequested?.Invoke(this, mod); };
        chooseFolder = Ui.Button("폴더 선택", null, 110);
        chooseFolder.Dock = DockStyle.Fill;
        chooseFolder.Click += (_, _) => ChooseFolderRequested?.Invoke(this, EventArgs.Empty);
        refresh = Ui.Button("새로고침", null, 110);
        refresh.Dock = DockStyle.Fill;
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.Add(Field("프로젝트 검색", search), 0, 0);
        toolbar.Controls.Add(Field("프로젝트 대상 모드", modPicker, new Padding(10, 0, 8, 0)), 1, 0);
        toolbar.Controls.Add(Action(create), 2, 0);
        toolbar.Controls.Add(Action(chooseFolder), 3, 0);
        toolbar.Controls.Add(Action(refresh), 4, 0);

        cards = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 14, 0, 0)
        };
        cards.Resize += (_, _) => ResizeCards();
        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(cards, 0, 2);
        Controls.Add(root);

        static Control Field(string label, Control control, Padding? margin = null)
        {
            var group = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = margin ?? Padding.Empty };
            group.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            group.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            group.Controls.Add(Ui.Label(label, 8.5f, FontStyle.Bold), 0, 0);
            group.Controls.Add(control, 0, 1);
            return group;
        }

        static Control Action(Control control)
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5, 25, 5, 0) };
            panel.Controls.Add(control);
            control.Dock = DockStyle.Fill;
            return panel;
        }
    }

    public event EventHandler<RimWorldModInfo>? CreateProjectRequested;
    public event EventHandler? ChooseFolderRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler<TranslationProject>? OpenProjectRequested;
    public event EventHandler<TranslationProject>? DeleteProjectRequested;

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
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            var selected = mods.FirstOrDefault(mod => mod.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selected is not null) modPicker.SelectedItem = selected;
        }
        if (modPicker.SelectedIndex < 0 && modPicker.Items.Count > 0) modPicker.SelectedIndex = 0;
        modPicker.EndUpdate();
        modCount.Text = $"감지된 모드 {mods.Count:N0}개";
        RenderCards();
    }

    public void SetBusy(bool busy)
    {
        create.Enabled = !busy && modPicker.SelectedItem is RimWorldModInfo;
        chooseFolder.Enabled = !busy;
        refresh.Enabled = !busy;
    }

    private void RenderCards()
    {
        cards.SuspendLayout();
        cards.Controls.Clear();
        var query = search.Text.Trim();
        foreach (var project in projects.Where(project => string.IsNullOrWhiteSpace(query)
                     || project.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                     || project.PackageId.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || project.WorkshopId.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            cards.Controls.Add(CreateCard(project));
        }
        if (cards.Controls.Count == 0)
        {
            var empty = Ui.Label(projects.Count == 0 ? "아직 프로젝트가 없습니다. 위에서 모드를 선택해 시작하세요." : "검색 결과가 없습니다.", 10f);
            empty.Padding = new Padding(12, 20, 0, 0);
            cards.Controls.Add(empty);
        }
        cards.ResumeLayout(true);
        ResizeCards();
    }

    private Control CreateCard(TranslationProject project)
    {
        var card = new BufferedPanel
        {
            Width = 480,
            Height = 238,
            Margin = new Padding(0, 0, 22, 22),
            Padding = new Padding(22),
            BorderStyle = BorderStyle.FixedSingle,
            Tag = "surface"
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 5));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        var name = Ui.Label(project.Name, 11.5f, FontStyle.Bold);
        name.AutoEllipsis = true;
        name.MaximumSize = new Size(420, 48);
        var meta = Ui.Label(!string.IsNullOrWhiteSpace(project.WorkshopId) ? $"Workshop {project.WorkshopId}  ·  원문 {project.SourceLanguageFolder}" : $"로컬 모드  ·  원문 {project.SourceLanguageFolder}", 8.5f);
        var summary = stats.TryGetValue(project.Id, out var current)
            ? current.Label
            : string.IsNullOrWhiteSpace(project.LatestReviewRoot) ? "검수 없음" : "검수 상태 불러오는 중";
        var status = Ui.Label(summary, 10f, FontStyle.Bold);
        status.MaximumSize = new Size(420, 60);
        var progress = new Panel { Height = 5, Dock = DockStyle.Fill, BackColor = Color.LightGray };
        if (current?.Stats.Total > 0)
        {
            var fill = new Panel
            {
                Dock = DockStyle.Left,
                Width = Math.Max(1, (int)(420d * (current.Stats.Translated + current.Stats.Approved) / current.Stats.Total)),
                BackColor = Color.FromArgb(174, 126, 65)
            };
            progress.Controls.Add(fill);
        }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var open = Ui.Button("열기", "primary", 96);
        open.Click += (_, _) => OpenProjectRequested?.Invoke(this, project);
        var delete = Ui.Button("삭제", "danger", 82);
        delete.Click += (_, _) => DeleteProjectRequested?.Invoke(this, project);
        var recent = Ui.Label(string.IsNullOrWhiteSpace(project.LatestReviewAt) ? "최근 작업 -" : $"최근 작업 {FormatTime(project.LatestReviewAt)}", 8.3f);
        recent.Margin = new Padding(0, 13, 12, 0);
        actions.Controls.Add(open);
        actions.Controls.Add(delete);
        actions.Controls.Add(recent);
        layout.Controls.Add(name, 0, 0);
        layout.Controls.Add(meta, 0, 1);
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(progress, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        card.Controls.Add(layout);
        return card;
    }

    private void ResizeCards()
    {
        if (cards.ClientSize.Width <= 0) return;
        var columns = cards.ClientSize.Width >= 1500 ? 3 : cards.ClientSize.Width >= 940 ? 2 : 1;
        var width = Math.Max(360, (cards.ClientSize.Width - 22 * (columns - 1) - SystemInformation.VerticalScrollBarWidth - 8) / columns);
        foreach (Control control in cards.Controls)
        {
            if (control is BufferedPanel) control.Width = width;
        }
    }

    private static string FormatTime(string value) => DateTimeOffset.TryParse(value, out var time) ? time.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : value;
}
