using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Validation;

namespace RimWorldAiTranslator.Core.Translation;

public sealed partial class TranslationApiClient : IDisposable
{
    internal const int MaximumRequestBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;
    private readonly List<KeyState> keyStates;
    private readonly object keyLock = new();
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly TimeProvider timeProvider;
    private int requestAttemptCount;
    private int requestAttemptLimit = int.MaxValue;

    public TranslationApiClient(IEnumerable<string> keys, HttpMessageHandler? handler = null)
        : this(keys, handler, null)
    {
    }

    internal TranslationApiClient(
        IEnumerable<string> keys,
        HttpMessageHandler? handler,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        TimeProvider? timeProvider = null)
    {
        var unique = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var initialTimestamp = this.timeProvider.GetTimestamp();
        keyStates = unique.Select((key, index) => new KeyState(key, index, initialTimestamp)).ToList();
        httpClient = handler is null
            ? new HttpClient(CreateProductionHandler(), disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        ownsClient = true;
        httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RimWorldAiTranslator/1.0");
        this.delayAsync = delayAsync ?? (static (delay, token) => Task.Delay(delay, token));
    }

    internal TimeSpan TransportTimeout => httpClient.Timeout;

    internal void SetRequestAttemptLimit(int maximumRequests)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRequests);
        requestAttemptLimit = maximumRequests;
        requestAttemptCount = 0;
    }

    internal static HttpClientHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false
    };

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
        var endpoint = GetChatCompletionsUrl(options.ProviderSettings.Url);
        ValidateEndpoint(endpoint, options.AllowInsecureLoopback);
        if (options.MaxResponseBytes <= 0)
            throw new InvalidDataException("MaxResponseBytes must be positive.");
        var body = TranslationPrompt.CreateRequestBody(options, systemPrompt, TranslationPrompt.CreateUserPayload(batch));
        var json = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var estimatedInputTokens = EstimateTokens(json) + 256;
        if (Utf8Strict.GetByteCount(json) > MaximumRequestBytes)
            throw new InvalidDataException($"Provider request exceeds the {MaximumRequestBytes:N0}-byte limit.");
        if (options.MaxInputTokensPerBatch > 0 && estimatedInputTokens > options.MaxInputTokensPerBatch)
            throw new InvalidDataException(
                $"Provider request estimate ({estimatedInputTokens:N0} tokens) exceeds the configured batch limit ({options.MaxInputTokensPerBatch:N0}).");
        Exception? lastError = null;

        for (var attempt = 1; attempt <= options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyState = await GetNextKeyAsync(options, estimatedInputTokens, progress, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keyState.Key);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);
                ReserveRequestAttempt();
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var summary = SafeErrorSummary(response.StatusCode);
                    HandleFailure(keyState, response.StatusCode, options.InputTokensPerMinutePerKey);
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {summary}", null, response.StatusCode);
                }

                var raw = await ReadBoundedResponseAsync(response.Content, options.MaxResponseBytes, timeout.Token).ConfigureAwait(false);
                UpdateUsage(keyState, raw, estimatedInputTokens, options.DailyTokenBudgetPerKey);
                var result = TranslationPrompt.ParseResponse(raw, batch.Select(entry => entry.Id));
                return result;
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
                progress?.Report(new TranslationProgress(
                    "retry",
                    $"Provider request failed; retrying attempt {attempt + 1} of {options.MaxRetries}.",
                    attempt + 1,
                    options.MaxRetries,
                    true));
                await delayAsync(TimeSpan.FromMilliseconds(Math.Min(30_000, 2_000 * attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Provider request failed after {options.MaxRetries} attempts ({SafeExceptionType(lastError)}).",
            lastError);
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
                    var separator = endpoint.Contains('?') ? '&' : '?';
                    var url = $"{endpoint}{separator}client=gtx&sl=auto&tl=ko&dt=t&q={Uri.EscapeDataString(chunk)}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutSource.CancelAfter(timeout);
                    ReserveRequestAttempt();
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var raw = await ReadBoundedResponseAsync(
                        response.Content,
                        TranslationEngineOptions.DefaultMaxResponseBytes,
                        timeoutSource.Token).ConfigureAwait(false);
                    translated.Append(ParseGoogleResponse(raw));
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastError = new TimeoutException($"Google request timed out after {timeout.TotalSeconds:0} seconds.");
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
                {
                    lastError = ex;
                }
                if (attempt < maxRetries)
                {
                    await delayAsync(TimeSpan.FromMilliseconds(Math.Min(10_000, 1_000 * (1 + attempt))), cancellationToken).ConfigureAwait(false);
                }
            }
            if (lastError is not null) throw lastError;
            await delayAsync(TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(false);
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

    internal static string GetChatCompletionsUrl(string value)
    {
        var trimmed = value.Trim();
        var componentIndex = trimmed.IndexOfAny(['?', '#']);
        var endpointPath = (componentIndex < 0 ? trimmed : trimmed[..componentIndex]).TrimEnd('/');
        var trailingComponents = componentIndex < 0 ? string.Empty : trimmed[componentIndex..];
        if (endpointPath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return endpointPath + trailingComponents;
        if (endpointPath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || endpointPath.EndsWith("/v1beta/openai", StringComparison.OrdinalIgnoreCase)
            || endpointPath.EndsWith("/compatible-mode/v1", StringComparison.OrdinalIgnoreCase)
            || endpointPath.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            || endpointPath.EndsWith("/paas/v4", StringComparison.OrdinalIgnoreCase))
        {
            return endpointPath + "/chat/completions" + trailingComponents;
        }
        return endpointPath + "/v1/chat/completions" + trailingComponents;
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
            long readyAt;
            lock (keyLock)
            {
                var now = timeProvider.GetTimestamp();
                foreach (var candidate in keyStates.Where(candidate => !candidate.Disabled))
                {
                    ResetWindow(candidate, now);
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
                if (readyAt <= now)
                {
                    ResetWindow(state, now);
                    state.AvailableAtTimestamp = options.RequestsPerMinutePerKey > 0
                        ? AddDuration(now, TimeSpan.FromSeconds(Math.Ceiling(60.0 / options.RequestsPerMinutePerKey)))
                        : now;
                    state.InputTokensInWindow += estimatedInputTokens;
                    state.Requests++;
                    return state;
                }
            }

            var delay = DurationUntil(readyAt, timeProvider.GetTimestamp());
            if (delay > TimeSpan.Zero)
            {
                progress?.Report(new TranslationProgress("rate-limit", $"Waiting {delay.TotalSeconds:0.0}s for API request/input-token limits."));
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private long ReadyAt(KeyState state, int inputTokens, int inputLimit)
    {
        var ready = state.AvailableAtTimestamp;
        if (inputLimit > 0 && state.InputTokensInWindow + inputTokens > inputLimit)
        {
            var tokenReady = AddDuration(state.InputWindowStartTimestamp, TimeSpan.FromMinutes(1));
            if (tokenReady > ready) ready = tokenReady;
        }
        return ready;
    }

    private void ResetWindow(KeyState state, long now)
    {
        if (timeProvider.GetElapsedTime(state.InputWindowStartTimestamp, now) >= TimeSpan.FromMinutes(1))
        {
            state.InputWindowStartTimestamp = now;
            state.InputTokensInWindow = 0;
        }
    }

    private void HandleFailure(KeyState state, HttpStatusCode statusCode, int inputLimit)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            state.Disabled = true;
        }
        else if ((int)statusCode == 429)
        {
            var now = timeProvider.GetTimestamp();
            state.AvailableAtTimestamp = AddDuration(now, TimeSpan.FromMinutes(1));
            state.InputWindowStartTimestamp = now;
            state.InputTokensInWindow = inputLimit;
        }
    }

    private long AddDuration(long timestamp, TimeSpan duration)
    {
        var delta = (long)Math.Ceiling(duration.TotalSeconds * timeProvider.TimestampFrequency);
        return timestamp > long.MaxValue - delta ? long.MaxValue : timestamp + delta;
    }

    private TimeSpan DurationUntil(long readyTimestamp, long nowTimestamp)
    {
        if (readyTimestamp <= nowTimestamp) return TimeSpan.Zero;
        return TimeSpan.FromSeconds((readyTimestamp - nowTimestamp) / (double)timeProvider.TimestampFrequency);
    }

    private static void UpdateUsage(KeyState state, string responseJson, int estimatedInputTokens, long dailyBudget)
    {
        var promptTokens = 0;
        var totalTokens = 0;
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt)
                    && prompt.ValueKind == JsonValueKind.Number)
                {
                    prompt.TryGetInt32(out promptTokens);
                }
                if (usage.TryGetProperty("total_tokens", out var total)
                    && total.ValueKind == JsonValueKind.Number)
                {
                    total.TryGetInt32(out totalTokens);
                }
            }
        }
        catch (JsonException exception)
        {
            System.Diagnostics.Debug.WriteLine($"Provider usage metadata ignored ({exception.GetType().Name}).");
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

    private static async Task<string> ReadBoundedResponseAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
            throw new InvalidDataException($"Provider response exceeded the {maxBytes} byte limit.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maxBytes, 81_920));
        var buffer = new byte[81_920];
        var total = 0;
        while (true)
        {
            var requested = (int)Math.Min(buffer.Length, (long)maxBytes - total + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException($"Provider response exceeded the {maxBytes} byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return Utf8Strict.GetString(output.GetBuffer(), 0, total);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Provider response was not valid UTF-8.", exception);
        }
    }

    private void ReserveRequestAttempt()
    {
        if (Interlocked.Increment(ref requestAttemptCount) <= requestAttemptLimit) return;
        throw new ProviderRequestBudgetExceededException(requestAttemptLimit);
    }

    private static string SafeErrorSummary(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => "The provider rejected the request.",
        HttpStatusCode.Unauthorized => "The provider rejected authentication.",
        HttpStatusCode.Forbidden => "The provider refused this request.",
        HttpStatusCode.NotFound => "The configured provider endpoint was not found.",
        HttpStatusCode.RequestTimeout => "The provider timed out the request.",
        HttpStatusCode.TooManyRequests => "The provider rate limit was reached.",
        _ when (int)statusCode >= 500 => "The provider is temporarily unavailable.",
        _ => "The provider returned an error; response details were omitted for privacy."
    };

    private static string SafeExceptionType(Exception? exception) => exception switch
    {
        TimeoutException => nameof(TimeoutException),
        HttpRequestException => nameof(HttpRequestException),
        JsonException => nameof(JsonException),
        InvalidDataException => nameof(InvalidDataException),
        _ => nameof(Exception)
    };

    public static void ValidateEndpoint(string value, bool allowLoopbackHttp)
        => ProviderValidator.EnsureValidEndpoint(value, allowLoopbackHttp);

    private sealed class KeyState(string key, int index, long initialTimestamp)
    {
        public string Key { get; } = key;
        public int Index { get; } = index;
        public long AvailableAtTimestamp { get; set; } = initialTimestamp;
        public long InputWindowStartTimestamp { get; set; } = initialTimestamp;
        public int InputTokensInWindow { get; set; }
        public long DailyTokensUsed { get; set; }
        public int Requests { get; set; }
        public int Failures { get; set; }
        public bool Disabled { get; set; }
    }

    [GeneratedRegex("^\\s*([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(.+)\\s*$", RegexOptions.CultureInvariant)] private static partial Regex KeyAssignmentRegex();
    [GeneratedRegex("^(CEREBRAS_API_KEY|CEREBRAS_KEY|OPENAI_API_KEY|GEMINI_API_KEY|GOOGLE_API_KEY|DEEPSEEK_API_KEY|DASHSCOPE_API_KEY|GROQ_API_KEY|MISTRAL_API_KEY|OPENROUTER_API_KEY|ZAI_API_KEY|API_KEY|KEY|RIMWORLD_TRANSLATOR_API_KEYS)$", RegexOptions.CultureInvariant)] private static partial Regex AllowedKeyNameRegex();
}

internal sealed class ProviderRequestBudgetExceededException : InvalidOperationException
{
    public ProviderRequestBudgetExceededException(int maximumRequests)
        : base($"Provider request budget of {maximumRequests:N0} attempts was exhausted.")
    {
    }
}
