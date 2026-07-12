namespace RimWorldAiTranslator.Core.Models;

public sealed record ApiProviderProfile(
    string Id,
    string Name,
    string Description,
    string Url,
    IReadOnlyList<string> Models,
    string DefaultModel,
    string ProviderKind,
    string ResponseFormat,
    string TokenParameter,
    string ReasoningEffort,
    int RequestsPerMinute,
    int InputTokensPerMinute,
    long DailyTokens,
    int MaxOutputTokens,
    bool NeedsKey,
    double DefaultTemperature = 0.1);

public static class ApiProviderCatalog
{
    public static IReadOnlyList<ApiProviderProfile> All { get; } =
    [
        new("Cerebras", "Cerebras", "Gemma 4와 초고속 추론 모델", "https://api.cerebras.ai/v1/chat/completions", ["gemma-4-31b", "gpt-oss-120b"], "gemma-4-31b", "OpenAICompatible", "JsonSchema", "max_completion_tokens", "none", 5, 30_000, 1_000_000, 32_000, true),
        new("OpenAI", "OpenAI", "GPT 계열 공식 API", "https://api.openai.com/v1/chat/completions", ["gpt-5.6", "gpt-5.5", "gpt-5.4", "gpt-5"], "gpt-5.6", "OpenAICompatible", "JsonSchema", "max_completion_tokens", "none", 0, 0, 0, 16_000, true, -1),
        new("Gemini", "Google Gemini", "Gemini OpenAI 호환 API", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", ["gemini-3.5-flash", "gemini-3.5-pro", "gemini-flash-latest"], "gemini-3.5-flash", "OpenAICompatible", "JsonSchema", "max_completion_tokens", "", 0, 0, 0, 16_000, true),
        new("DeepSeek", "DeepSeek", "DeepSeek 공식 API", "https://api.deepseek.com/chat/completions", ["deepseek-v4-flash", "deepseek-v4-pro"], "deepseek-v4-flash", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("Qwen", "Qwen", "Alibaba Cloud Model Studio", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions", ["qwen3.7-plus", "qwen3.7-max", "qwen3.6-flash"], "qwen3.7-plus", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("Groq", "Groq", "빠른 오픈 모델 추론", "https://api.groq.com/openai/v1/chat/completions", ["openai/gpt-oss-120b", "llama-3.3-70b-versatile", "openai/gpt-oss-20b"], "openai/gpt-oss-120b", "OpenAICompatible", "JsonSchema", "max_completion_tokens", "", 0, 0, 0, 16_000, true),
        new("Mistral", "Mistral AI", "Mistral 공식 Chat API", "https://api.mistral.ai/v1/chat/completions", ["mistral-small-latest", "mistral-medium-latest", "mistral-large-latest"], "mistral-small-latest", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("OpenRouter", "OpenRouter", "여러 모델을 한 API로 사용", "https://openrouter.ai/api/v1/chat/completions", ["~openai/gpt-latest", "openai/gpt-5.4", "google/gemini-3.5-flash"], "~openai/gpt-latest", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("ZAI", "BigModel / Z.AI", "GLM 계열 공식 API", "https://api.z.ai/api/paas/v4/chat/completions", ["glm-5.1", "glm-5", "glm-4.7"], "glm-5.1", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("Custom", "사용자 지정", "OpenAI 호환 Chat Completions", "", [], "", "OpenAICompatible", "JsonObject", "max_tokens", "", 0, 0, 0, 16_000, true),
        new("Google", "Google 번역", "API 키 없이 빠른 기계 번역", "https://translate.googleapis.com/translate_a/single", ["Google Translate"], "Google Translate", "Google", "PromptOnly", "none", "", 0, 0, 0, 0, false)
    ];

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
