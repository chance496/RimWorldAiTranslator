using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Review;

public sealed record DefContext(
    string DefClass,
    string Node,
    string DefName,
    string Field,
    string ClassDescription,
    string NodeDescription)
{
    public string ClassLine => $"Def Class : {DefClass} ({ClassDescription})";
    public string NodeLine => $"Node : {Node} ({NodeDescription})";
}

public static class DefContextService
{
    private static readonly Dictionary<string, string> ClassDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Keyed"] = "Keyed는 코드나 인터페이스에서 호출하는 일반 문자열을 정의",
        ["ThingDef"] = "ThingDef는 아이템, 건물, 식물과 생물 같은 게임 대상을 정의",
        ["PawnKindDef"] = "PawnKindDef는 생성되는 폰의 종류와 기본 장비 및 능력치를 정의",
        ["HediffDef"] = "HediffDef는 질병, 부상, 상태 효과와 신체 변화를 정의",
        ["DamageDef"] = "DamageDef는 피해 유형과 방어 및 전투 반응을 정의",
        ["RecipeDef"] = "RecipeDef는 제작, 수술과 가공 작업을 정의",
        ["ResearchProjectDef"] = "ResearchProjectDef는 연구 항목과 선행 조건을 정의",
        ["TraitDef"] = "TraitDef는 폰의 특성과 단계별 효과를 정의",
        ["GeneDef"] = "GeneDef는 유전자 특성과 능력치 변화를 정의",
        ["AbilityDef"] = "AbilityDef는 사용 가능한 능력과 표시 정보를 정의",
        ["IncidentDef"] = "IncidentDef는 게임에서 발생하는 사건의 종류를 정의",
        ["QuestScriptDef"] = "QuestScriptDef는 퀘스트 생성 규칙과 표시 정보를 정의",
        ["ThoughtDef"] = "ThoughtDef는 생각과 기분 효과를 정의",
        ["BodyPartDef"] = "BodyPartDef는 신체 부위의 종류와 표시 이름을 정의",
        ["FactionDef"] = "FactionDef는 세력의 성격, 관계와 표시 정보를 정의",
        ["JobDef"] = "JobDef는 폰이 수행하는 작업의 종류와 보고 문구를 정의",
        ["ConceptDef"] = "ConceptDef는 학습 도우미와 개념 설명을 정의"
    };

    private static readonly Dictionary<string, string> FieldDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["label"] = "'label' 노드는 화면에 표시되는 짧은 이름",
        ["labelPlural"] = "'labelPlural' 노드는 복수형 이름",
        ["description"] = "'description' 노드는 정보 창이나 도움말에 표시되는 상세 설명",
        ["deathMessage"] = "'deathMessage' 노드는 해당 원인으로 사망했을 때 표시되는 문장",
        ["jobString"] = "'jobString' 노드는 작업 중 폰 상태에 표시되는 문구",
        ["reportString"] = "'reportString' 노드는 진행 중인 작업을 설명하는 문구",
        ["letterLabel"] = "'letterLabel' 노드는 편지 창의 짧은 제목",
        ["letterText"] = "'letterText' 노드는 편지 창의 본문",
        ["message"] = "'message' 노드는 게임 메시지로 표시되는 문장",
        ["verb"] = "'verb' 노드는 행동을 나타내는 문구",
        ["gerund"] = "'gerund' 노드는 진행 중인 행동을 나타내는 문구",
        ["pawnLabel"] = "'pawnLabel' 노드는 폰을 가리킬 때 쓰는 이름",
        ["labelNoun"] = "'labelNoun' 노드는 명사형 표시 이름",
        ["adjective"] = "'adjective' 노드는 설명에 결합되는 형용사형 표현"
    };

    public static DefContext Get(ReviewComparisonRow row)
    {
        var defClass = row.Kind.Equals("Keyed", StringComparison.OrdinalIgnoreCase)
            ? "Keyed"
            : !string.IsNullOrWhiteSpace(row.DefClass) ? row.DefClass : row.Kind;
        var node = !string.IsNullOrWhiteSpace(row.Node) ? row.Node : row.Key;
        var field = !string.IsNullOrWhiteSpace(row.Field)
            ? row.Field.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? row.Field
            : row.Key.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var defName = row.Key;
        if (!string.IsNullOrWhiteSpace(field) && defName.EndsWith("." + field, StringComparison.OrdinalIgnoreCase))
            defName = defName[..^(field.Length + 1)];
        var classDescription = ClassDescriptions.TryGetValue(defClass, out var knownClass)
            ? knownClass
            : $"{defClass} 유형의 RimWorld 정의 데이터를 설명";
        var nodeDescription = FieldDescriptions.TryGetValue(field, out var knownField)
            ? knownField
            : !string.IsNullOrWhiteSpace(field)
                ? $"'{field}' 노드는 이 Def에서 번역 가능한 표시 문자열"
                : "이 노드는 화면이나 로그에 표시되는 번역 문자열";
        return new DefContext(defClass, node, defName, field, classDescription, nodeDescription);
    }
}
