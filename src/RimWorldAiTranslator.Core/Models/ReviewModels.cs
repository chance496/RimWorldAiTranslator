using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWorldAiTranslator.Core.Models;

public static class ReviewStatuses
{
    public const string Pending = "pending";
    public const string Translated = "translated";
    public const string Approved = "approved";
}

public sealed class ReviewDecisionDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 5;

    [JsonPropertyName("sparse")]
    public bool Sparse { get; set; } = true;

    [JsonPropertyName("reviewRoot")]
    public string ReviewRoot { get; set; } = string.Empty;

    [JsonPropertyName("comparison")]
    public string Comparison { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<ReviewDecision> Items { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ReviewDecision
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = ReviewStatuses.Pending;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("translationOrigin")]
    public string TranslationOrigin { get; set; } = string.Empty;

    [JsonPropertyName("translationUpdatedAt")]
    public string TranslationUpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("sourceHash")]
    public string SourceHash { get; set; } = string.Empty;

    [JsonPropertyName("sourceText")]
    public string SourceText { get; set; } = string.Empty;

    [JsonPropertyName("sourceChanged")]
    public bool SourceChanged { get; set; }

    [JsonPropertyName("previousSourceText")]
    public string PreviousSourceText { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ReviewComparisonRow
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("defClass")]
    public string DefClass { get; set; } = string.Empty;

    [JsonPropertyName("node")]
    public string Node { get; set; } = string.Empty;

    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("existing")]
    public string Existing { get; set; } = string.Empty;

    [JsonPropertyName("candidate")]
    public string Candidate { get; set; } = string.Empty;

    [JsonPropertyName("existingOrigin")]
    public string ExistingOrigin { get; set; } = string.Empty;

    [JsonPropertyName("translationOrigin")]
    public string TranslationOrigin { get; set; } = string.Empty;

    [JsonPropertyName("translationUpdatedAt")]
    public string TranslationUpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("rmkIdentifier")]
    public string RmkIdentifier { get; set; } = string.Empty;

    [JsonPropertyName("rmkHistoricalSource")]
    public string RmkHistoricalSource { get; set; } = string.Empty;

    [JsonPropertyName("rmkCurrentSource")]
    public string RmkCurrentSource { get; set; } = string.Empty;

    [JsonPropertyName("rmkSourceChanged")]
    public bool RmkSourceChanged { get; set; }

    [JsonPropertyName("rmkWorkbook")]
    public string RmkWorkbook { get; set; } = string.Empty;

    [JsonPropertyName("existingPresent")]
    public bool ExistingPresent { get; set; }

    [JsonPropertyName("existingHasKorean")]
    public bool ExistingHasKorean { get; set; }

    [JsonPropertyName("candidateHasKorean")]
    public bool CandidateHasKorean { get; set; }

    [JsonPropertyName("existingSameAsSource")]
    public bool ExistingSameAsSource { get; set; }

    [JsonPropertyName("candidateSameAsSource")]
    public bool CandidateSameAsSource { get; set; }

    [JsonPropertyName("candidateBlank")]
    public bool CandidateBlank { get; set; }

    [JsonPropertyName("safeToApply")]
    public bool SafeToApply { get; set; }

    [JsonPropertyName("missingTokens")]
    public string MissingTokens { get; set; } = string.Empty;

    [JsonPropertyName("unexpectedTokens")]
    public string UnexpectedTokens { get; set; } = string.Empty;

    [JsonPropertyName("tokenCountMismatches")]
    public string TokenCountMismatches { get; set; } = string.Empty;

    [JsonPropertyName("grammarPrefixMoved")]
    public bool GrammarPrefixMoved { get; set; }

    [JsonPropertyName("pathologicalCandidate")]
    public bool PathologicalCandidate { get; set; }

    [JsonPropertyName("invalidKoreanParticles")]
    public string InvalidKoreanParticles { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
