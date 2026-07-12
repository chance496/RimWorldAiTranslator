namespace RimWorldAiTranslator.Core.Validation;

public sealed record TranslationValidationResult(
    bool IsSafe,
    IReadOnlyList<string> MissingTokens,
    IReadOnlyList<string> UnexpectedTokens,
    IReadOnlyList<string> TokenCountMismatches,
    bool GrammarPrefixMoved,
    bool Pathological,
    IReadOnlyList<string> InvalidParticles,
    bool ContainsKorean);

public static class TranslationValidator
{
    public static TranslationValidationResult Validate(string source, string translation)
    {
        source ??= string.Empty;
        translation ??= string.Empty;
        var tokenIssues = RimWorldTranslatorValidation.GetTokenPreservationIssues(source, translation);
        var particles = RimWorldTranslatorValidation.GetInvalidKoreanParticleNotations(translation);
        var pathological = RimWorldTranslatorValidation.IsPathologicalTranslation(translation);
        var containsKorean = translation.Any(c => c is >= '\uac00' and <= '\ud7a3');
        var safe = !string.IsNullOrWhiteSpace(translation)
            && containsKorean
            && !pathological
            && tokenIssues.MissingTokens.Length == 0
            && tokenIssues.UnexpectedTokens.Length == 0
            && tokenIssues.TokenCountMismatches.Length == 0
            && !tokenIssues.GrammarPrefixMoved
            && particles.Length == 0;

        return new TranslationValidationResult(
            safe,
            tokenIssues.MissingTokens,
            tokenIssues.UnexpectedTokens,
            tokenIssues.TokenCountMismatches,
            tokenIssues.GrammarPrefixMoved,
            pathological,
            particles,
            containsKorean);
    }
}
