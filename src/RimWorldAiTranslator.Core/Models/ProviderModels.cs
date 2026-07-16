namespace RimWorldAiTranslator.Core.Models;

public enum ProviderModelAccess
{
    Free,
    FreeTier,
    TrialCredit,
    Paid
}

public sealed record ProviderModelOption(
    string Id,
    ProviderModelAccess Access,
    string Note = "")
{
    public string AccessLabel => Access switch
    {
        ProviderModelAccess.Free => "무료",
        ProviderModelAccess.FreeTier => "무료 티어",
        ProviderModelAccess.TrialCredit => "신규 무료 할당량",
        _ => "유료"
    };

    public override string ToString() => string.IsNullOrWhiteSpace(Note)
        ? $"[{AccessLabel}] {Id}"
        : $"[{AccessLabel}] {Id} · {Note}";
}

public sealed record ApiProviderProfile
{
    public ApiProviderProfile(
        string id,
        string name,
        string description,
        string url,
        IReadOnlyList<ProviderModelOption> modelOptions,
        string defaultModel,
        string providerKind,
        string responseFormat,
        string tokenParameter,
        string reasoningEffort,
        int requestsPerMinute,
        int inputTokensPerMinute,
        long dailyTokens,
        int maxOutputTokens,
        bool needsKey,
        double defaultTemperature = 0.1)
    {
        Id = id;
        Name = name;
        Description = description;
        Url = url;
        ModelOptions = modelOptions;
        Models = modelOptions.Select(option => option.Id).ToArray();
        DefaultModel = defaultModel;
        ProviderKind = providerKind;
        ResponseFormat = responseFormat;
        TokenParameter = tokenParameter;
        ReasoningEffort = reasoningEffort;
        RequestsPerMinute = requestsPerMinute;
        InputTokensPerMinute = inputTokensPerMinute;
        DailyTokens = dailyTokens;
        MaxOutputTokens = maxOutputTokens;
        NeedsKey = needsKey;
        DefaultTemperature = defaultTemperature;
    }

    public string Id { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public string Url { get; init; }
    public IReadOnlyList<ProviderModelOption> ModelOptions { get; init; }
    public IReadOnlyList<string> Models { get; init; }
    public string DefaultModel { get; init; }
    public string ProviderKind { get; init; }
    public string ResponseFormat { get; init; }
    public string TokenParameter { get; init; }
    public string ReasoningEffort { get; init; }
    public int RequestsPerMinute { get; init; }
    public int InputTokensPerMinute { get; init; }
    public long DailyTokens { get; init; }
    public int MaxOutputTokens { get; init; }
    public bool NeedsKey { get; init; }
    public double DefaultTemperature { get; init; }

    public ProviderModelOption? FindModel(string? id) => ModelOptions.FirstOrDefault(
        option => option.Id.Equals(id?.Trim(), StringComparison.Ordinal));
}

public static class ApiProviderCatalog
{
    public static IReadOnlyList<ApiProviderProfile> All { get; } =
    [
        new("Cerebras", "Cerebras", "공개 Models API에서 확인된 초고속 추론 모델", "https://api.cerebras.ai/v1/chat/completions", [
            FreeTier("gemma-4-31b"), FreeTier("gpt-oss-120b"), FreeTier("zai-glm-4.7", "미리보기")
        ], "gemma-4-31b", "OpenAICompatible", "JsonSchema", "max_completion_tokens", "none", 0, 0, 0, 32_000, true),
        new("OpenAI", "OpenAI", "현재 GPT 5.6 Chat Completions 모델", "https://api.openai.com/v1/chat/completions", [
            Paid("gpt-5.6-luna"), Paid("gpt-5.6-sol"), Paid("gpt-5.6-terra")
        ], "gpt-5.6-luna", "OpenAICompatible", "JsonSchema", "max_completion_tokens", "none", 0, 0, 0, 16_000, true, -1),
        new("Gemini", "Google Gemini", "현재 Gemini 일반 텍스트 모델", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", [
            FreeTier("gemini-3.5-flash"),
            FreeTier("gemini-3.1-flash-lite"),
            FreeTier("gemini-3-flash-preview", "미리보기"),
            Paid("gemini-3.1-pro-preview", "미리보기")
        ], "gemini-3.5-flash", "OpenAICompatible", "JsonSchema", "max_completion_tokens", "low", 0, 0, 0, 16_000, true),
        new("DeepSeek", "DeepSeek", "현재 DeepSeek V4 공식 API 모델", "https://api.deepseek.com/chat/completions", [
            Paid("deepseek-v4-flash"), Paid("deepseek-v4-pro")
        ], "deepseek-v4-flash", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("Qwen", "Qwen", "Alibaba Cloud Model Studio 권장 텍스트 모델", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions", [
            Trial("qwen3.6-flash"), Trial("qwen3.7-plus"), Trial("qwen3.7-max")
        ], "qwen3.6-flash", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("Groq", "Groq", "GroqCloud 프로덕션 텍스트 모델", "https://api.groq.com/openai/v1/chat/completions", [
            FreeTier("llama-3.1-8b-instant"), FreeTier("llama-3.3-70b-versatile"),
            FreeTier("openai/gpt-oss-20b"), FreeTier("openai/gpt-oss-120b")
        ], "llama-3.1-8b-instant", "OpenAICompatible", "JsonObject", "max_completion_tokens", "", 0, 0, 0, 16_000, true),
        new("Mistral", "Mistral AI", "Mistral 공식 Chat Completions 모델", "https://api.mistral.ai/v1/chat/completions", [
            FreeTier("mistral-small-latest"), Paid("mistral-medium-latest"), Paid("mistral-large-latest")
        ], "mistral-small-latest", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("OpenRouter", "OpenRouter", "무료 라우터와 검증된 현재 모델", "https://openrouter.ai/api/v1/chat/completions", [
            Free("openrouter/free", "가용 무료 모델 자동 선택"),
            Paid("openai/gpt-5.6-luna"), Paid("google/gemini-3.5-flash"), Paid("deepseek/deepseek-v4-flash")
        ], "openrouter/free", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("ZAI", "BigModel / Z.AI", "GLM 공식 텍스트 모델", "https://api.z.ai/api/paas/v4/chat/completions", [
            Free("glm-4.7-flash"), Free("glm-4.5-flash"),
            Paid("glm-5.1"), Paid("glm-5"), Paid("glm-5-turbo"), Paid("glm-4.7"),
            Paid("glm-4.7-flashx"), Paid("glm-4.6"), Paid("glm-4.5"), Paid("glm-4.5-air")
        ], "glm-4.7-flash", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("Custom", "사용자 지정", "OpenAI 호환 Chat Completions", "", [], "", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("Google", "Google 번역 (무료)", "API 키 없이 사용하는 무료 기계 번역", "https://translate.googleapis.com/translate_a/single", [
            Free("Google Translate")
        ], "Google Translate", "Google", "PromptOnly", "none", "", 0, 0, 0, 0, false)
    ];

    private static ProviderModelOption Free(string id, string note = "") =>
        new(id, ProviderModelAccess.Free, note);

    private static ProviderModelOption FreeTier(string id, string note = "") =>
        new(id, ProviderModelAccess.FreeTier, note);

    private static ProviderModelOption Trial(string id, string note = "") =>
        new(id, ProviderModelAccess.TrialCredit, note);

    private static ProviderModelOption Paid(string id, string note = "") =>
        new(id, ProviderModelAccess.Paid, note);

    public static ApiProviderProfile Get(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static Dictionary<string, ApiProviderSettings> CreateDefaultSettings() =>
        All.ToDictionary(
            p => p.Id,
            p => new ApiProviderSettings
            {
                Name = p.Name,
                Url = p.Url,
                Model = p.DefaultModel,
                Temperature = p.DefaultTemperature
            },
            StringComparer.OrdinalIgnoreCase);
}

public sealed record ProviderCapabilities(
    string Source,
    string Provider,
    bool KnownModel,
    string ResponseFormat,
    string TokenParameter,
    int MaxOutput,
    int RequestsPerMinute,
    int InputTokensPerMinute,
    long DailyTokens);

public sealed record ProviderValidationResult(
    bool Valid,
    IReadOnlyList<string> ErrorCodes,
    IReadOnlyList<string> WarningCodes,
    int KeyCount,
    string Model,
    ProviderCapabilities Capabilities);
