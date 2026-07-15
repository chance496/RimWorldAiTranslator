namespace RimWorldAiTranslator.App.Controls;

internal enum UiCommand
{
    Projects,
    Activity,
    Settings,
    CommandPalette,
    ProjectSearch,
    ProjectCreate,
    ChooseProjectFolder,
    RefreshProjects,
    OpenProject,
    DeleteProject,
    OpenModFolder,
    SaveReview,
    RefreshSource,
    AiTranslate,
    StopOperation,
    ApplyApproved,
    ApplyAll,
    UseRmk,
    ReviewSearch,
    SearchField,
    StatusFilter,
    SortReview,
    PreviousReview,
    NextReview,
    UseCandidate,
    UseExisting,
    CopyTranslation,
    UndoTranslation,
    MarkPending,
    MarkTranslated,
    MarkApproved,
    MarkApprovedAndNext,
    ApproveAll,
    RefreshRmk,
    OpenRmk,
    BuildRmk,
    QualityFilter,
    RefreshQuality,
    ExportQuality
}

internal sealed record UiCommandDefinition(string Description, IReadOnlyList<Keys> Shortcuts)
{
    public string ShortcutText => UiCommandCatalog.FormatShortcuts(Shortcuts);
}

internal static class UiCommandCatalog
{
    private static readonly IReadOnlyDictionary<UiCommand, UiCommandDefinition> Definitions =
        new Dictionary<UiCommand, UiCommandDefinition>
        {
            [UiCommand.Projects] = Define("프로젝트 목록을 엽니다.", Keys.Control | Keys.Home),
            [UiCommand.Activity] = Define("최근 번역, 검수 및 적용 활동을 엽니다."),
            [UiCommand.Settings] = Define("번역 공급자와 화면 설정을 엽니다."),
            [UiCommand.CommandPalette] = Define("명령 팔레트를 엽니다.", Keys.Control | Keys.Shift | Keys.P),
            [UiCommand.ProjectSearch] = Define("프로젝트 이름, Workshop ID 또는 패키지 ID로 검색합니다.", Keys.Control | Keys.F),
            [UiCommand.ProjectCreate] = Define("선택한 모드로 새 번역 프로젝트를 만듭니다."),
            [UiCommand.ChooseProjectFolder] = Define("목록에 없는 로컬 RimWorld 모드 폴더를 선택합니다."),
            [UiCommand.RefreshProjects] = Define("모드 검색 결과와 프로젝트 상태를 다시 불러옵니다.", Keys.F5),
            [UiCommand.OpenProject] = Define("번역 프로젝트의 검수 화면을 엽니다."),
            [UiCommand.DeleteProject] = Define("로컬 번역 프로젝트 삭제 확인을 시작합니다."),
            [UiCommand.OpenModFolder] = Define("현재 모드 폴더를 엽니다."),
            [UiCommand.SaveReview] = Define("현재 편집 내용을 저장합니다.", Keys.Control | Keys.S),
            [UiCommand.RefreshSource] = Define("현재 모드의 원문을 다시 분석합니다.", Keys.F5),
            [UiCommand.AiTranslate] = Define("AI 초벌 번역을 시작합니다.", Keys.F9),
            [UiCommand.StopOperation] = Define("실행 중인 작업을 안전하게 중지합니다.", Keys.Shift | Keys.F9),
            [UiCommand.ApplyApproved] = Define("검토 완료된 안전한 번역만 미리보기 후 적용합니다."),
            [UiCommand.ApplyAll] = Define("번역됨과 검토 완료 상태의 안전한 번역을 미리보기 후 적용합니다."),
            [UiCommand.UseRmk] = Define("로컬 모드 대신 검증된 RMK 작업 클론을 적용 대상으로 사용합니다."),
            [UiCommand.ReviewSearch] = Define("원문, 번역문 또는 키를 검색합니다.", Keys.Control | Keys.F),
            [UiCommand.SearchField] = Define("검색할 문자열 필드를 선택합니다."),
            [UiCommand.StatusFilter] = Define("표시할 검수 상태를 선택합니다."),
            [UiCommand.SortReview] = Define("검수 문자열의 정렬 순서를 선택합니다."),
            [UiCommand.PreviousReview] = Define("이전 검색 결과로 이동합니다.", Keys.Shift | Keys.F3, Keys.Alt | Keys.Up, Keys.Alt | Keys.Left),
            [UiCommand.NextReview] = Define("다음 검색 결과로 이동합니다.", Keys.F3, Keys.Alt | Keys.Down, Keys.Alt | Keys.Right),
            [UiCommand.UseCandidate] = Define("AI 후보를 편집기에 넣습니다.", Keys.Alt | Keys.D1, Keys.Alt | Keys.NumPad1),
            [UiCommand.UseExisting] = Define("기존 번역을 편집기에 넣습니다.", Keys.Alt | Keys.D2, Keys.Alt | Keys.NumPad2),
            [UiCommand.CopyTranslation] = Define("현재 번역문을 클립보드에 복사합니다."),
            [UiCommand.UndoTranslation] = Define("저장된 번역문으로 되돌립니다.", Keys.Alt | Keys.D0, Keys.Alt | Keys.NumPad0),
            [UiCommand.MarkPending] = Define("현재 문자열을 미번역 상태로 표시합니다.", Keys.Control | Keys.D1, Keys.Control | Keys.NumPad1),
            [UiCommand.MarkTranslated] = Define("현재 문자열을 번역됨 상태로 표시합니다.", Keys.Control | Keys.D2, Keys.Control | Keys.NumPad2),
            [UiCommand.MarkApproved] = Define("현재 문자열을 검토 완료 상태로 표시합니다.", Keys.Control | Keys.D3, Keys.Control | Keys.NumPad3, Keys.Control | Keys.Shift | Keys.Enter),
            [UiCommand.MarkApprovedAndNext] = Define("현재 문자열을 검토 완료로 표시하고 다음 검색 결과로 이동합니다.", Keys.Control | Keys.Enter),
            [UiCommand.ApproveAll] = Define("경고가 없고 안전한 번역을 모두 검토 완료 상태로 표시합니다.", Keys.Control | Keys.Shift | Keys.D3, Keys.Control | Keys.Shift | Keys.NumPad3),
            [UiCommand.RefreshRmk] = Define("RMK 구독본과 작업 클론 상태를 다시 확인합니다."),
            [UiCommand.OpenRmk] = Define("현재 RMK 번역 항목 또는 작업 클론 폴더를 엽니다."),
            [UiCommand.BuildRmk] = Define("검증된 RMK 작업 클론의 LoadFoldersBuilder를 실행합니다."),
            [UiCommand.QualityFilter] = Define("표시할 품질 문제의 등급 또는 분류를 선택합니다."),
            [UiCommand.RefreshQuality] = Define("현재 프로젝트의 품질 문제를 다시 계산합니다."),
            [UiCommand.ExportQuality] = Define("원문과 번역문을 제외한 집계 품질 보고서를 저장합니다.")
        };

    public static UiCommandDefinition Get(UiCommand command) => Definitions[command];

    public static bool Matches(UiCommand command, Keys keyData)
    {
        var normalized = keyData & (Keys.KeyCode | Keys.Modifiers);
        return Definitions[command].Shortcuts.Any(shortcut => shortcut == normalized);
    }

    internal static string FormatShortcuts(IEnumerable<Keys> shortcuts)
    {
        var hints = shortcuts
            .Select(FormatShortcut)
            .Select(NormalizeNumpadHint)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return string.Join(" 또는 ", hints);
    }

    private static UiCommandDefinition Define(string description, params Keys[] shortcuts) =>
        new(description, shortcuts);

    private static string FormatShortcut(Keys shortcut)
    {
        var parts = new List<string>(4);
        if (shortcut.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (shortcut.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (shortcut.HasFlag(Keys.Alt)) parts.Add("Alt");
        var key = shortcut & Keys.KeyCode;
        parts.Add(key switch
        {
            >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            >= Keys.NumPad0 and <= Keys.NumPad9 => "NumPad" + ((int)key - (int)Keys.NumPad0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Keys.Up => "위쪽 화살표",
            Keys.Down => "아래쪽 화살표",
            Keys.Left => "왼쪽 화살표",
            Keys.Right => "오른쪽 화살표",
            Keys.Enter => "Enter",
            Keys.Home => "Home",
            _ => key.ToString()
        });
        return string.Join("+", parts);
    }

    private static string NormalizeNumpadHint(string hint) => hint.Replace("NumPad", string.Empty, StringComparison.Ordinal);
}
