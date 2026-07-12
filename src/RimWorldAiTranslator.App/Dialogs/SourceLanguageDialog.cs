using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.Core.Discovery;

namespace RimWorldAiTranslator.App.Dialogs;

internal sealed class SourceLanguageDialog : Form
{
    private readonly ListBox list;
    public SourceLanguageDialog(string modName, IReadOnlyList<SourceLanguageOption> options)
    {
        Text = "원문 언어 선택";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(600, 390);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(28);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.Controls.Add(Ui.Label("번역 기준 원문을 선택하세요", 14f, FontStyle.Bold), 0, 0);
        var message = Ui.Label($"'{modName}' 모드에 원문 언어가 여러 개 있습니다. 이 프로젝트에서 계속 사용할 언어를 선택하세요.", 9f);
        message.MaximumSize = new Size(540, 60);
        root.Controls.Add(message, 0, 1);
        list = new ListBox { Dock = DockStyle.Fill, DisplayMember = nameof(SourceLanguageOption.Display), IntegralHeight = false, Margin = new Padding(0, 14, 0, 12) };
        foreach (var option in options) list.Items.Add(option);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        list.DoubleClick += (_, _) => Accept();
        root.Controls.Add(list, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var select = Ui.Button("이 언어로 시작", "primary", 140); select.Click += (_, _) => Accept();
        var cancel = Ui.Button("취소", null, 90); cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        actions.Controls.Add(select); actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
        AcceptButton = select;
        CancelButton = cancel;
    }

    public string SelectedFolder => (list.SelectedItem as SourceLanguageOption)?.Folder ?? string.Empty;
    private void Accept() { if (list.SelectedItem is not null) DialogResult = DialogResult.OK; }
}
