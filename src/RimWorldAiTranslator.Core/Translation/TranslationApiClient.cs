using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Translation;

public sealed partial class TranslationApiClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;
    private readonly List<KeyState> keyStates;
    private readonly object keyLock = new();

    public TranslationApiClient(IEnumerable<string> keys, HttpMessageHandler? handler = null)
    {
        var unique = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        keyStates = unique.Select((key, index) => new KeyState(key, index)).ToList();
        httpClient = handler is null ? new HttpClient() : new HttpClient(handler, false);
        ownsClient = true;
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RimWorldAiTranslator/1.0");
    }

    public async Task<Dictionary<string, string>> TranslateOpenAiAsync(
        TranslationEngineOptions options,
        IReadOnlyList<SourceEntry> batch,
        string systemPrompt,
        IProgress<TranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (keyStates.Count == 0)
        {
            throw new InvalidOperationException($"No API key provided for {options.Provider.Name}.");
        }
        ValidateEndpoint(options.ProviderSettings.Url, options.AllowInsecureLoopback);
        var body = TranslationPrompt.CreateRequestBody(options, systemPrompt, TranslationPrompt.CreateUserPayload(batch));
        var json = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var estimatedInputTokens = EstimateTokens(json) + 256;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyState = await GetNextKeyAsync(options, estimatedInputTokens, progress, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.ProviderSettings.Url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keyState.Key);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                var raw = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var summary = SafeErrorSummary(raw);
                    HandleFailure(keyState, response.StatusCode, options.InputTokensPerMinutePerKey);
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {summary}", null, response.StatusCode);
                }

                UpdateUsage(keyState, raw, estimatedInputTokens, options.DailyTokenBudgetPerKey);
                return TranslationPrompt.ParseResponse(raw);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException($"Request timed out after {options.Timeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
            {
                lastError = ex;
            }

            keyState.Failures++;
            if (attempt < options.MaxRetries)
            {
                progress?.Report(new TranslationProgress("retry", $"Batch request failed on attempt {attempt}; retrying. {Compact(lastError?.Message)}", attempt, options.MaxRetries, true));
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(30_000, 2_000 * attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Request failed after {options.MaxRetries} attempts. Last error: {Compact(lastError?.Message)}", lastError);
    }

    public async Task<string> TranslateGoogleAsync(
        string text,
        string endpoint,
        TimeSpan timeout,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        ValidateEndpoint(endpoint, false);
        var protectedText = ProtectTokens(text);
        var translated = new StringBuilder();
        foreach (var chunk in SplitGoogleChunks(protectedText.Text))
        {
            Exception? lastError = null;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var url = $"{endpoint}?client=gtx&sl=auto&tl=ko&dt=t&q={Uri.EscapeDataString(chunk)}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutSource.CancelAfter(timeout);
                    using var response = await httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
                    var raw = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    translated.Append(ParseGoogleResponse(raw));
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastError = new TimeoutException($"Google request timed out after {timeout.TotalSeconds:0} seconds.");
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException)
                {
                    lastError = ex;
                }
                if (attempt < maxRetries)
                {
                    await Task.Delay(Math.Min(10_000, 1_000 * (1 + attempt)), cancellationToken).ConfigureAwait(false);
                }
            }
            if (lastError is not null) throw lastError;
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }
        return RestoreTokens(translated.ToString().Replace("\r\n", "\n").Replace('\r', '\n'), protectedText.Map);
    }

    public static IReadOnlyList<string> ParseKeys(IEnumerable<string> candidates)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            foreach (var rawLine in candidate.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var value = rawLine.Trim();
                if (value.Length == 0 || value.StartsWith('#')) continue;
                var assignment = KeyAssignmentRegex().Match(value);
                if (assignment.Success)
                {
                    if (!AllowedKeyNameRegex().IsMatch(assignment.Groups[1].Value)) continue;
                    value = assignment.Groups[2].Value.Trim();
                }
                value = value.Trim('"', '\'');
                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) value = value[7..].Trim();
                foreach (var part in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var key = part.Trim('"', '\'');
                    if (key.Length > 0 && seen.Add(key)) keys.Add(key);
                }
            }
        }
        return keys;
    }

    public void Dispose()
    {
        if (ownsClient) httpClient.Dispose();
    }

    private async Task<KeyState> GetNextKeyAsync(
        TranslationEngineOptions options,
        int estimatedInputTokens,
        IProgress<TranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (options.InputTokensPerMinutePerKey > 0 && estimatedInputTokens > options.InputTokensPerMinutePerKey)
        {
            throw new InvalidOperationException($"Estimated input tokens for one request ({estimatedInputTokens}) exceed the per-minute input limit ({options.InputTokensPerMinutePerKey}). Lower the batch size.");
        }

        while (true)
        {
            KeyState state;
            DateTimeOffset readyAt;
            lock (keyLock)
            {
                foreach (var candidate in keyStates.Where(candidate => !candidate.Disabled))
                {
                    ResetWindow(candidate);
                    if (options.DailyTokenBudgetPerKey > 0 && candidate.DailyTokensUsed + estimatedInputTokens > options.DailyTokenBudgetPerKey)
                    {
                        candidate.Disabled = true;
                    }
                }
                var active = keyStates.Where(candidate => !candidate.Disabled).ToArray();
                if (active.Length == 0) throw new InvalidOperationException("All API keys are disabled or exhausted.");
                state = active.OrderBy(candidate => ReadyAt(candidate, estimatedInputTokens, options.InputTokensPerMinutePerKey))
                    .ThenBy(candidate => candidate.Requests)
                    .ThenBy(candidate => candidate.Index)
                    .First();
                readyAt = ReadyAt(state, estimatedInputTokens, options.InputTokensPerMinutePerKey);
                if (readyAt <= DateTimeOffset.UtcNow)
                {
                    ResetWindow(state);
                    state.AvailableAt = options.RequestsPerMinutePerKey > 0
                        ? DateTimeOffset.UtcNow.AddSeconds(Math.Ceiling(60.0 / options.RequestsPerMinutePerKey))
                        : DateTimeOffset.UtcNow;
                    state.InputTokensInWindow += estimatedInputTokens;
                    state.Requests++;
                    return state;
                }
            }

            var delay = readyAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                progress?.Report(new TranslationProgress("rate-limit", $"Waiting {delay.TotalSeconds:0.0}s for API request/input-token limits."));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static DateTimeOffset ReadyAt(KeyState state, int inputTokens, int inputLimit)
    {
        ResetWindow(state);
        var ready = state.AvailableAt;
        if (inputLimit > 0 && state.InputTokensInWindow + inputTokens > inputLimit)
        {
            var tokenReady = state.InputWindowStart.AddMinutes(1);
            if (tokenReady > ready) ready = tokenReady;
        }
        return ready;
    }

    private static void ResetWindow(KeyState state)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - state.InputWindowStart >= TimeSpan.FromMinutes(1))
        {
            state.InputWindowStart = now;
            state.InputTokensInWindow = 0;
        }
    }

    private static void HandleFailure(KeyState state, HttpStatusCode statusCode, int inputLimit)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            state.Disabled = true;
        }
        else if ((int)statusCode == 429)
        {
            state.AvailableAt = DateTimeOffset.UtcNow.AddMinutes(1);
            state.InputWindowStart = DateTimeOffset.UtcNow;
            state.InputTokensInWindow = inputLimit;
        }
    }

    private static void UpdateUsage(KeyState state, string responseJson, int estimatedInputTokens, long dailyBudget)
    {
        var promptTokens = 0;
        var totalTokens = 0;
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt)) promptTokens = prompt.GetInt32();
                if (usage.TryGetProperty("total_tokens", out var total)) totalTokens = total.GetInt32();
            }
        }
        catch (JsonException)
        {
        }
        if (promptTokens > 0) state.InputTokensInWindow = Math.Max(0, state.InputTokensInWindow + promptTokens - estimatedInputTokens);
        state.DailyTokensUsed += totalTokens > 0 ? totalTokens : Math.Max(estimatedInputTokens, promptTokens);
        if (dailyBudget > 0 && state.DailyTokensUsed >= dailyBudget) state.Disabled = true;
    }

    private static (string Text, Dictionary<string, string> Map) ProtectTokens(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = text;
        var tokens = RimWorldTranslatorValidation.GetProtectedTokenCounts(text).Keys.OrderByDescending(token => token.Length).ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            var placeholder = $"ZXQPROTECTED{index:D3}ZXQ";
            map[placeholder] = tokens[index];
            result = result.Replace(tokens[index], placeholder, StringComparison.Ordinal);
        }
        return (result, map);
    }

    private static string RestoreTokens(string text, IReadOnlyDictionary<string, string> map)
    {
        var result = text;
        foreach (var pair in map) result = result.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return result;
    }

    private static IEnumerable<string> SplitGoogleChunks(string text, int maxCharacters = 3500)
    {
        var remaining = text;
        while (remaining.Length > maxCharacters)
        {
            var breakAt = remaining.LastIndexOf('\n', maxCharacters);
            if (breakAt < maxCharacters * 0.45) breakAt = remaining.LastIndexOf(". ", maxCharacters, StringComparison.Ordinal);
            if (breakAt < maxCharacters * 0.45) breakAt = remaining.LastIndexOf(' ', maxCharacters);
            if (breakAt < 1) breakAt = maxCharacters;
            yield return remaining[..(breakAt + 1)];
            remaining = remaining[(breakAt + 1)..];
        }
        if (remaining.Length > 0) yield return remaining;
    }

    private static string ParseGoogleResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder();
        foreach (var segment in document.RootElement[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 && segment[0].ValueKind != JsonValueKind.Null)
            {
                builder.Append(segment[0].GetString());
            }
        }
        return builder.ToString();
    }

    private static int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 3.0);

    private static string SafeErrorSummary(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var summary = body.Trim();
        try
        {
            using var document = JsonDocument.Parse(summary);
            var error = document.RootElement.TryGetProperty("error", out var errorValue) ? errorValue : document.RootElement;
            var values = new[] { "message", "type", "code" }
                .Select(name => error.ValueKind == JsonValueKind.Object && error.TryGetProperty(name, out var value) ? value.ToString() : string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (values.Length > 0) summary = string.Join(" | ", values);
        }
        catch (JsonException)
        {
        }
        summary = WhitespaceRegex().Replace(summary, " ").Trim();
        return summary.Length > 1200 ? summary[..1200] + "..." : summary;
    }

    private static string Compact(string? message)
    {
        var value = WhitespaceRegex().Replace(message ?? "unknown error", " ").Trim();
        return value.Length > 320 ? value[..320] + "..." : value;
    }

    public static void ValidateEndpoint(string value, bool allowLoopbackHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidDataException("API URL is invalid.");
        var loopback = uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1" or "::1";
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !(allowLoopbackHttp && loopback && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("API URL must use HTTPS.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || CredentialQueryRegex().IsMatch(uri.Query))
        {
            throw new InvalidDataException("API URL must not contain credentials.");
        }
    }

    private sealed class KeyState(string key, int index)
    {
        public string Key { get; } = key;
        public int Index { get; } = index;
        public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset InputWindowStart { get; set; } = DateTimeOffset.UtcNow;
        public int InputTokensInWindow { get; set; }
        public long DailyTokensUsed { get; set; }
        public int Requests { get; set; }
        public int Failures { get; set; }
        public bool Disabled { get; set; }
    }

    [GeneratedRegex("^\\s*([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(.+)\\s*$", RegexOptions.CultureInvariant)] private static partial Regex KeyAssignmentRegex();
    [GeneratedRegex("^(CEREBRAS_API_KEY|CEREBRAS_KEY|OPENAI_API_KEY|GEMINI_API_KEY|GOOGLE_API_KEY|DEEPSEEK_API_KEY|DASHSCOPE_API_KEY|GROQ_API_KEY|MISTRAL_API_KEY|OPENROUTER_API_KEY|ZAI_API_KEY|API_KEY|KEY|RIMWORLD_TRANSLATOR_API_KEYS)$", RegexOptions.CultureInvariant)] private static partial Regex AllowedKeyNameRegex();
    [GeneratedRegex("(?i)(api[_-]?key|token|authorization|secret)=", RegexOptions.CultureInvariant)] private static partial Regex CredentialQueryRegex();
    [GeneratedRegex("[\\r\\n\\t\\s]+", RegexOptions.CultureInvariant)] private static partial Regex WhitespaceRegex();
}
