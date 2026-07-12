using RimWorldAiTranslator.App.Controls;

namespace RimWorldAiTranslator.App.Dialogs;

internal enum TranslationStartMode
{
    Cancel,
    Overwrite,
    MissingOnly
}

internal sealed class TranslationModeDialog : Form
{
    public TranslationModeDialog(int translatedCount)
    {
        Text = "AI 번역 범위";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(620, 300);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(30);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.Controls.Add(Ui.Label("기존 번역을 어떻게 처리할까요?", 14f, FontStyle.Bold), 0, 0);
        var body = Ui.Label($"현재 번역이 들어 있는 문자열이 {translatedCount:N0}개 있습니다. 초벌 번역을 다시 만들 범위를 선택하세요.", 9.5f);
        body.MaximumSize = new Size(560, 58);
        root.Controls.Add(body, 0, 1);
        var notes = Ui.Label("덮어쓰기: 기존 번역도 AI가 다시 번역합니다.\n미번역만: 현재 번역은 유지하고 빈 항목만 번역합니다.", 9f);
        notes.MaximumSize = new Size(560, 90);
        root.Controls.Add(notes, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var overwrite = Ui.Button("덮어쓰기", "danger", 110); overwrite.Click += (_, _) => CloseWith(TranslationStartMode.Overwrite);
        var missing = Ui.Button("미번역만 번역", "primary", 142); missing.Click += (_, _) => CloseWith(TranslationStartMode.MissingOnly);
        var cancel = Ui.Button("취소", null, 90); cancel.Click += (_, _) => CloseWith(TranslationStartMode.Cancel);
        actions.Controls.Add(overwrite); actions.Controls.Add(missing); actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
        CancelButton = cancel;
    }

    public TranslationStartMode Mode { get; private set; } = TranslationStartMode.Cancel;
    private void CloseWith(TranslationStartMode mode) { Mode = mode; DialogResult = mode == TranslationStartMode.Cancel ? DialogResult.Cancel : DialogResult.OK; }
}
