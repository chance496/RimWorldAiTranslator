namespace RimWorldAiTranslator.App.Dialogs;

internal sealed record CommandPaletteAction(
    string Name,
    string Group,
    string Shortcut,
    bool Enabled,
    Action Execute);

internal sealed class CommandPaletteDialog : Form
{
    private readonly IReadOnlyList<CommandPaletteAction> actions;
    private readonly TextBox search;
    private readonly ListView list;
    private readonly Button run;

    public CommandPaletteAction? SelectedAction { get; private set; }

    public CommandPaletteDialog(IReadOnlyList<CommandPaletteAction> actions)
    {
        this.actions = actions;
        Text = "명령 찾기";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(640, 476);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AccessibleName = "명령 찾기 대화상자";
        AccessibleDescription = "이름, 영역 또는 단축키로 실행할 명령을 찾습니다.";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Malgun Gothic", 9f);
        Tag = "surface";

        var accent = new Panel { Bounds = new Rectangle(0, 0, 640, 4), Tag = "accent" };
        var title = Label("명령 찾기", 24, 20, 400, 30, 13f, FontStyle.Bold);
        var hint = Label("작업 이름을 입력하고 Enter를 누르세요. 비활성 명령은 현재 화면에서 실행할 수 없습니다.", 24, 52, 590, 22, 8f, tag: "muted");
        search = new TextBox { Bounds = new Rectangle(24, 86, 592, 34), PlaceholderText = "명령 검색", BorderStyle = BorderStyle.FixedSingle };
        search.AccessibleName = "명령 검색";
        search.AccessibleDescription = "이름, 영역 또는 단축키로 실행할 명령을 검색합니다.";
        search.TabIndex = 0;
        list = new ListView
        {
            Bounds = new Rectangle(24, 132, 592, 280),
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            Font = new Font("Malgun Gothic", 9f),
            AccessibleName = "사용 가능한 명령 목록",
            AccessibleDescription = "검색 결과에서 실행할 수 있는 명령과 현재 사용할 수 없는 명령을 확인합니다.",
            TabIndex = 1,
            TabStop = true
        };
        list.Columns.Add("명령", 350);
        list.Columns.Add("영역", 90);
        list.Columns.Add("단축키", 120);
        run = RimWorldAiTranslator.App.Controls.Ui.Button("실행", "primary", 110);
        run.SetBounds(506, 426, 110, 36);
        run.Margin = Padding.Empty;
        run.AccessibleName = "선택한 명령 실행";
        run.AccessibleDescription = "목록에서 선택한 사용 가능한 명령을 실행합니다.";
        run.TabIndex = 2;
        var close = RimWorldAiTranslator.App.Controls.Ui.Button("닫기", null, 110);
        close.SetBounds(388, 426, 110, 36);
        close.Margin = Padding.Empty;
        close.AccessibleName = "명령 찾기 닫기";
        close.AccessibleDescription = "명령을 실행하지 않고 명령 찾기를 닫습니다.";
        close.TabIndex = 3;
        close.Click += (_, _) => Close();
        CancelButton = close;

        search.TextChanged += (_, _) => RefreshList();
        search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && list.Items.Count > 0)
            {
                list.Focus();
                list.Items[0].Selected = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                ExecuteSelected();
                e.SuppressKeyPress = true;
            }
        };
        list.SelectedIndexChanged += (_, _) => UpdateRunButton();
        list.DoubleClick += (_, _) => ExecuteSelected();
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            ExecuteSelected();
            e.SuppressKeyPress = true;
        };
        run.Click += (_, _) => ExecuteSelected();
        Controls.AddRange([accent, title, hint, search, list, run, close]);
        Shown += (_, _) => search.Focus();
        RefreshList();
    }

    private void RefreshList()
    {
        var query = search.Text.Trim();
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var action in actions)
            {
                var text = $"{action.Name} {action.Group} {action.Shortcut}";
                if (!string.IsNullOrWhiteSpace(query) && !text.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
                var visibleName = action.Enabled ? action.Name : $"{action.Name} (현재 사용 불가)";
                var item = new ListViewItem([visibleName, action.Group, action.Shortcut])
                {
                    Tag = action,
                    ToolTipText = action.Enabled ? action.Name : $"{action.Name}: 현재 화면에서 실행할 수 없습니다."
                };
                if (!action.Enabled)
                    item.ForeColor = ThemeManager.ReadableForeground(
                        ThemeManager.Current.Surface,
                        ThemeManager.Current.Muted,
                        ThemeManager.Current.Text);
                list.Items.Add(item);
            }
            if (list.Items.Count > 0) list.Items[0].Selected = true;
        }
        finally { list.EndUpdate(); }
        UpdateRunButton();
    }

    private void UpdateRunButton() =>
        run.Enabled = list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is CommandPaletteAction { Enabled: true };

    private void ExecuteSelected()
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not CommandPaletteAction { Enabled: true } action) return;
        SelectedAction = action;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Label Label(string text, int x, int y, int width, int height, float size, FontStyle style = FontStyle.Regular, string? tag = null)
    {
        var label = RimWorldAiTranslator.App.Controls.Ui.Label(text, size, style);
        label.AutoSize = false;
        label.SetBounds(x, y, width, height);
        label.Margin = Padding.Empty;
        label.Tag = tag;
        return label;
    }
}
