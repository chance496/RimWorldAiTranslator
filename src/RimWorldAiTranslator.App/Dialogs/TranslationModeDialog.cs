using RimWorldAiTranslator.App.Controls;

namespace RimWorldAiTranslator.App.Dialogs;

internal enum TranslationStartMode
{
    Cancel,
    Overwrite,
    MissingOnly
}

internal sealed record TranslationPreflightInfo(
    string ProjectName,
    string SourceLanguage,
    string Provider,
    string Model,
    int EntryCount,
    int BatchCount,
    string UsageEstimate,
    bool HasExistingTranslation,
    string EndpointHost = "");

internal sealed class TranslationModeDialog : Form
{
    private readonly Button missingOnly;
    private readonly Button overwrite;
    private readonly Label modeHint;

    public TranslationModeDialog(TranslationPreflightInfo info)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "번역 작업 준비";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(760, 512);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        Tag = "surface";
        AccessibleName = "번역 작업 준비 대화상자";
        AccessibleDescription = "외부 전송 범위와 기존 번역 보존 방식을 확인하고 번역 실행 여부를 선택합니다.";

        var accent = new Panel { Bounds = new Rectangle(0, 0, 760, 4), Tag = "accent" };
        var title = FixedLabel("번역 작업 준비", 30, 24, 520, 32, 14f, FontStyle.Bold);
        var subtitle = FixedLabel("외부 전송 범위와 저장 방식을 확인한 뒤 시작합니다.", 30, 58, 620, 22, 9f, tag: "muted");
        var summary = new Panel { Bounds = new Rectangle(30, 96, 700, 178), BorderStyle = BorderStyle.FixedSingle, Tag = "surface-alt" };
        var rows = new[]
        {
            ("프로젝트", info.ProjectName),
            ("원문 기준", info.SourceLanguage),
            ("번역 엔진", $"{info.Provider} · {info.Model}"),
            ("예상 범위", info.EntryCount > 0 ? $"{info.EntryCount:N0}개 · 최대 {info.BatchCount:N0}배치" : "원문 분석 후 확정"),
            ("사용량 추정", info.UsageEstimate)
        };
        var y = 14;
        foreach (var row in rows)
        {
            summary.Controls.Add(FixedLabel(row.Item1, 16, y, 94, 24, 8.5f, FontStyle.Bold, "muted"));
            var value = FixedLabel(row.Item2, 112, y, 564, 24, 9f);
            value.AutoEllipsis = true;
            summary.Controls.Add(value);
            y += 31;
        }

        var privacy = new Panel { Bounds = new Rectangle(30, 286, 700, 52), Tag = "selection-panel" };
        privacy.Controls.Add(FixedLabel("검수 프로젝트에 먼저 저장", 16, 7, 220, 20, 8.8f, FontStyle.Bold));
        var transferText = string.IsNullOrWhiteSpace(info.EndpointHost)
            ? "번역 실행만으로 원본 모드나 Korean 폴더를 수정하지 않습니다. 적용은 검토 화면에서 별도로 실행합니다."
            : $"원문과 요청 문맥은 {info.EndpointHost}로 전송됩니다. 원본 모드 적용은 검토 화면에서 별도로 실행합니다.";
        privacy.Controls.Add(FixedLabel(transferText, 16, 27, 660, 18, 8.2f, tag: "muted"));
        var modeTitle = FixedLabel("번역 범위", 30, 354, 180, 22, 9f, FontStyle.Bold);
        missingOnly = Ui.Button("미번역 부분만", null, 180);
        missingOnly.SetBounds(30, 382, 180, 42);
        missingOnly.Margin = Padding.Empty;
        overwrite = Ui.Button("전체 다시 번역", null, 180);
        overwrite.SetBounds(218, 382, 180, 42);
        overwrite.Margin = Padding.Empty;
        modeHint = FixedLabel(string.Empty, 414, 382, 316, 44, 8.3f, tag: "muted");
        missingOnly.Click += (_, _) => SelectMode(TranslationStartMode.MissingOnly, info.HasExistingTranslation);
        overwrite.Click += (_, _) => SelectMode(TranslationStartMode.Overwrite, info.HasExistingTranslation);
        var divider = new Panel { Bounds = new Rectangle(30, 442, 700, 1), Tag = "divider" };
        var start = Ui.Button("번역 시작", "primary", 132);
        start.SetBounds(486, 456, 132, 40);
        start.Margin = Padding.Empty;
        start.Click += (_, _) => DialogResult = DialogResult.OK;
        var cancel = Ui.Button("취소", null, 102);
        cancel.SetBounds(628, 456, 102, 40);
        cancel.Margin = Padding.Empty;
        cancel.Click += (_, _) => { Mode = TranslationStartMode.Cancel; DialogResult = DialogResult.Cancel; };
        Controls.AddRange([accent, title, subtitle, summary, privacy, modeTitle, missingOnly, overwrite, modeHint, divider, start, cancel]);
        var highDpiLayoutApplied = false;
        Shown += (_, _) =>
        {
            if (highDpiLayoutApplied || DeviceDpi <= 96) return;
            highDpiLayoutApplied = true;
            const int extra = 20;
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + extra);
            privacy.Height += extra;
            privacy.Controls[1].Height += extra;
            modeTitle.Top += extra;
            missingOnly.Top += extra;
            overwrite.Top += extra;
            modeHint.Top += extra;
            divider.Top += extra;
            start.Top += extra;
            cancel.Top += extra;
        };
        AcceptButton = start;
        CancelButton = cancel;

        missingOnly.AccessibleName = "미번역 부분만 번역";
        missingOnly.AccessibleDescription = "기존 번역은 보존하고 번역이 없는 문자열만 번역 대상으로 선택합니다.";
        missingOnly.TabIndex = 0;
        overwrite.AccessibleName = "전체 다시 번역";
        overwrite.AccessibleDescription = "모든 문자열을 다시 번역하고 새 후보로 교체합니다.";
        overwrite.TabIndex = 1;
        start.AccessibleName = "확인한 범위로 번역 시작";
        start.AccessibleDescription = "표시된 범위와 번역 엔진으로 번역 작업을 시작합니다.";
        start.TabIndex = 2;
        cancel.AccessibleName = "번역 준비 취소";
        cancel.AccessibleDescription = "번역 요청을 보내지 않고 준비 창을 닫습니다.";
        cancel.TabIndex = 3;
        SelectMode(info.HasExistingTranslation ? TranslationStartMode.MissingOnly : TranslationStartMode.Overwrite, info.HasExistingTranslation);
    }

    public TranslationStartMode Mode { get; private set; } = TranslationStartMode.Cancel;

    private void SelectMode(TranslationStartMode mode, bool hasExisting)
    {
        Mode = mode;
        missingOnly.Tag = mode == TranslationStartMode.MissingOnly ? "primary" : null;
        overwrite.Tag = mode == TranslationStartMode.Overwrite ? "primary" : null;
        var missingSelected = mode == TranslationStartMode.MissingOnly;
        missingOnly.AccessibleName = missingSelected
            ? "미번역 부분만 번역, 현재 선택됨"
            : "미번역 부분만 번역";
        missingOnly.AccessibleDescription = missingSelected
            ? "기존 번역은 보존하고 번역이 없는 문자열만 번역 대상으로 선택합니다. 현재 선택됨."
            : "기존 번역은 보존하고 번역이 없는 문자열만 번역 대상으로 선택합니다.";
        var overwriteSelected = mode == TranslationStartMode.Overwrite;
        overwrite.AccessibleName = overwriteSelected
            ? "전체 다시 번역, 현재 선택됨"
            : "전체 다시 번역";
        overwrite.AccessibleDescription = overwriteSelected
            ? "모든 문자열을 다시 번역하고 새 후보로 교체합니다. 현재 선택됨."
            : "모든 문자열을 다시 번역하고 새 후보로 교체합니다.";
        modeHint.Text = mode == TranslationStartMode.MissingOnly
            ? hasExisting ? "기존 번역과 직접 편집한 내용은 보존하고 빈 항목만 번역합니다." : "현재 보존할 번역이 없어 모든 미번역 항목이 대상입니다."
            : hasExisting ? "기존 후보를 새 결과로 교체합니다. 이전 작업 이력은 보존됩니다." : "현재 원문 전체의 초벌 번역 후보를 생성합니다.";
        ThemeManager.Apply(missingOnly, ThemeManager.Current, 10);
        ThemeManager.Apply(overwrite, ThemeManager.Current, 10);
    }

    private static Label FixedLabel(string text, int x, int y, int width, int height, float size, FontStyle style = FontStyle.Regular, string? tag = null)
    {
        var label = Ui.Label(text, size, style);
        label.AutoSize = false;
        label.SetBounds(x, y, width, height);
        label.Tag = tag;
        return label;
    }
}
