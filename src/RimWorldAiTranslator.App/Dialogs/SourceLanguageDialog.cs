using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.Core.Discovery;

namespace RimWorldAiTranslator.App.Dialogs;

internal sealed class SourceLanguageDialog : Form
{
    private readonly ListBox list;
    public SourceLanguageDialog(string modName, IReadOnlyList<SourceLanguageOption> options)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "원문 언어 선택";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(570, 360);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AccessibleName = "원문 언어 선택 대화상자";
        AccessibleDescription = "프로젝트에서 사용할 기준 원문 언어 폴더를 선택합니다.";
        Tag = "surface";
        var accent = new Panel { Bounds = new Rectangle(0, 0, 570, 4), Tag = "accent" };
        var title = Ui.Label("번역 기준 원문을 선택하세요", 13f, FontStyle.Bold);
        title.AutoSize = false;
        title.SetBounds(28, 24, 514, 30);
        var message = Ui.Label($"'{modName}' 모드에 원문 언어가 여러 개 있습니다. 이 프로젝트에서 계속 사용할 언어를 선택하세요.", 9f);
        message.AutoSize = false;
        message.SetBounds(28, 62, 514, 54);
        message.Tag = "muted";
        list = new ListBox { DisplayMember = nameof(SourceLanguageOption.Display), IntegralHeight = false, Font = new Font("Malgun Gothic", 10f), BorderStyle = BorderStyle.FixedSingle };
        list.SetBounds(28, 122, 514, 142);
        foreach (var option in options) list.Items.Add(option);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        list.DoubleClick += (_, _) => Accept();
        var select = Ui.Button("이 언어로 프로젝트 만들기", "success", 208);
        select.SetBounds(252, 288, 208, 44);
        select.Margin = Padding.Empty;
        select.Click += (_, _) => Accept();
        var cancel = Ui.Button("취소", null, 72);
        cancel.SetBounds(470, 288, 72, 44);
        cancel.Margin = Padding.Empty;
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.AddRange([accent, title, message, list, select, cancel]);
        AcceptButton = select;
        CancelButton = cancel;
        list.AccessibleName = "원문 언어 목록";
        list.AccessibleDescription = "프로젝트의 기준 원문으로 사용할 언어 폴더를 선택합니다.";
        list.TabIndex = 0;
        select.AccessibleName = "선택한 원문 언어로 프로젝트 만들기";
        select.AccessibleDescription = "선택한 언어 폴더를 기준으로 새 로컬 번역 프로젝트를 만듭니다.";
        select.TabIndex = 1;
        cancel.AccessibleName = "프로젝트 생성 취소";
        cancel.AccessibleDescription = "프로젝트를 만들지 않고 원문 언어 선택을 취소합니다.";
        cancel.TabIndex = 2;
    }

    public string SelectedFolder => (list.SelectedItem as SourceLanguageOption)?.Folder ?? string.Empty;
    private void Accept() { if (list.SelectedItem is not null) DialogResult = DialogResult.OK; }
}
