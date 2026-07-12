using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Utilities;

namespace RimWorldAiTranslator.Core.Review;

public static class ReviewSafety
{
    public static bool IsStructureSafe(ReviewComparisonRow row, string translation, SourceExtractor extractor)
    {
        if (extractor.GetInternalIdentifierReason(row.Key, row.Kind, row.DefClass, row.Field) is not null) return false;
        var text = Normalize(translation);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var issues = RimWorldTranslatorValidation.GetTokenPreservationIssues(row.Source, text);
        return issues.MissingTokens.Length == 0
            && issues.UnexpectedTokens.Length == 0
            && issues.TokenCountMismatches.Length == 0
            && !issues.GrammarPrefixMoved
            && RimWorldTranslatorValidation.GetInvalidKoreanParticleNotations(text).Length == 0
            && !RimWorldTranslatorValidation.IsPathologicalTranslation(text);
    }

    public static bool IsTranslationSafe(ReviewComparisonRow row, string translation, SourceExtractor extractor)
    {
        var text = Normalize(translation);
        return IsStructureSafe(row, text, extractor)
            && !text.Equals(Normalize(row.Source), StringComparison.Ordinal)
            && text.Any(character => character is >= '\uac00' and <= '\ud7af');
    }

    public static bool DecisionSourceChanged(ReviewDecision decision, ReviewComparisonRow row)
    {
        var source = Normalize(row.Source);
        if (!string.IsNullOrWhiteSpace(decision.SourceHash)) return !decision.SourceHash.Equals(StableIdentity.Sha256(source), StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(decision.SourceText)) return !Normalize(decision.SourceText).Equals(source, StringComparison.Ordinal);
        return false;
    }

    public static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
}
