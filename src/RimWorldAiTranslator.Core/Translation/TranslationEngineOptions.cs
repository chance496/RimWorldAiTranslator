using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Translation;

public sealed class TranslationEngineOptions
{
    public const int DefaultMaxResponseBytes = 4 * 1024 * 1024;

    public required string ModRoot { get; init; }
    public IReadOnlyList<string> ApiKeys { get; init; } = [];
    public ApiProviderProfile Provider { get; init; } = ApiProviderCatalog.Get("Cerebras");
    public ApiProviderSettings ProviderSettings { get; init; } = new()
    {
        Name = "Cerebras",
        Url = "https://api.cerebras.ai/v1/chat/completions",
        Model = "gemma-4-31b",
        Temperature = 0.1
    };
    public string LanguageFolderName { get; init; } = "Korean";
    public string SourceLanguageFolder { get; init; } = "Auto";
    public int RequestsPerMinutePerKey { get; init; } = 5;
    public int InputTokensPerMinutePerKey { get; init; } = 30_000;
    public long DailyTokenBudgetPerKey { get; init; } = 1_000_000;
    public int BatchSize { get; init; } = 40;
    public int MaxInputCharactersPerBatch { get; init; } = 12_000;
    public int MaxInputTokensPerBatch { get; init; } = 5_500;
    public int MaxCompletionTokens { get; init; } = 32_000;
    public string CompletionTokenParameter { get; init; } = "max_completion_tokens";
    public string ResponseFormatMode { get; init; } = "JsonSchema";
    public string ReasoningEffort { get; init; } = "none";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(180);
    public int MaxRetries { get; init; } = 4;
    public int MaxProviderRequestsPerRun { get; init; } = 2_000;
    public int MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;
    public int Limit { get; init; }
    public bool IncludePatches { get; init; }
    public bool Overwrite { get; init; }
    public bool DryRun { get; init; }
    public bool SourceOnly { get; init; }
    public bool ReviewOnly { get; init; } = true;
    public bool TranslateMissingOnly { get; init; }
    public bool UseCuratedGlossary { get; init; }
    public bool NoStructuredOutputs { get; init; }
    public bool AllowInsecureLoopback { get; init; }
    public string ExistingLanguageRoot { get; init; } = string.Empty;
    public IReadOnlyList<string> ReferenceLanguageRoots { get; init; } = [];
    public string ReferenceSourceWorkbook { get; init; } = string.Empty;
    public string PreserveTranslationFile { get; init; } = string.Empty;
    public string ReviewRoot { get; init; } = string.Empty;
    public string GeneratedGlossaryPath { get; init; } = string.Empty;
    public string CuratedGlossaryPath { get; init; } = string.Empty;
    public int MaxAlwaysGlossaryTerms { get; init; } = 180;
    public int MaxGeneratedGlossaryTermsPerBatch { get; init; } = 140;
    public string ExtraPrompt { get; init; } = string.Empty;
    public string OutputFilePrefix { get; init; } = "CodexAI";
}

public sealed record TranslationProgress(
    string Stage,
    string Message,
    int Current = 0,
    int Total = 0,
    bool IsWarning = false);

public sealed record TranslationRunResult(
    string? ReviewRoot,
    string? ComparisonFile,
    IReadOnlyList<ReviewComparisonRow> Rows,
    IReadOnlyList<string> WrittenFiles,
    int SourceEntries,
    int ReviewEntries,
    int TranslatedEntries,
    int SkippedDuplicates,
    int SkippedUnsafe,
    int TokenWarnings,
    bool Cancelled = false);

public sealed class TranslationRunCanceledException : OperationCanceledException
{
    public TranslationRunCanceledException(
        TranslationRunResult partialResult,
        bool checkpointPersisted,
        CancellationToken cancellationToken)
        : base(
            checkpointPersisted
                ? "Translation was canceled after preserving partial results."
                : "Translation was canceled before partial results could be persisted.",
            cancellationToken)
    {
        PartialResult = partialResult;
        CheckpointPersisted = checkpointPersisted;
    }

    public TranslationRunResult PartialResult { get; }
    public bool CheckpointPersisted { get; }
}
