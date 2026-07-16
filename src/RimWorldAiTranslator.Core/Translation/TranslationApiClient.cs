using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Logging;
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
            var keyState = await GetNextKeyAsync(progress, cancellationToken).ConfigureAwait(false);
            var rateLimitDelayScheduled = false;
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
                ApplyRateLimitHeaders(keyState, response);
                if (!response.IsSuccessStatusCode)
                {
                    var summary = SafeErrorSummary(response.StatusCode);
                    var diagnostic = await ReadProviderErrorDiagnosticAsync(
                            response,
                            options.MaxResponseBytes,
                            batch,
                            systemPrompt,
                            keyState.Key,
                            timeout.Token)
                        .ConfigureAwait(false);
                    var providerException = new ProviderRequestException(
                        response.StatusCode,
                        summary,
                        response.StatusCode == HttpStatusCode.RequestEntityTooLarge,
                        options.Provider.Id,
                        options.ProviderSettings.Model,
                        diagnostic.Code,
                        diagnostic.Message,
                        diagnostic.RequestId);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        keyState.Disabled = true;
                    }
                    if (!IsTransientStatus(response.StatusCode))
                    {
                        throw providerException;
                    }
                    var retryAfter = ReadRetryAfter(response);
                    if (retryAfter is not null)
                    {
                        SetAvailableAfter(keyState, retryAfter.Value);
                        rateLimitDelayScheduled = true;
                    }
                    else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var retryDelay = ReadRateLimitResetDelay(response)
                            ?? ExponentialBackoff(attempt);
                        SetAvailableAfter(keyState, retryDelay);
                        rateLimitDelayScheduled = true;
                    }
                    throw new HttpRequestException(providerException.Message, providerException, response.StatusCode);
                }

                var raw = await ReadBoundedResponseAsync(response.Content, options.MaxResponseBytes, timeout.Token).ConfigureAwait(false);
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
                    0,
                    0,
                    true));
                if (!rateLimitDelayScheduled)
                {
                    await delayAsync(TimeSpan.FromMilliseconds(Math.Min(30_000, 2_000 * attempt)), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (lastError is InvalidDataException invalidData) throw invalidData;
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
        IProgress<TranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            KeyState state;
            long readyAt;
            lock (keyLock)
            {
                var now = timeProvider.GetTimestamp();
                var active = keyStates.Where(candidate => !candidate.Disabled).ToArray();
                if (active.Length == 0) throw new InvalidOperationException("All API keys were rejected by the provider.");
                state = active.OrderBy(candidate => candidate.AvailableAtTimestamp)
                    .ThenBy(candidate => candidate.Requests)
                    .ThenBy(candidate => candidate.Index)
                    .First();
                readyAt = state.AvailableAtTimestamp;
                if (readyAt <= now)
                {
                    state.Requests++;
                    return state;
                }
            }

            var delay = DurationUntil(readyAt, timeProvider.GetTimestamp());
            if (delay > TimeSpan.Zero)
            {
                progress?.Report(new TranslationProgress("rate-limit", $"Waiting {delay.TotalSeconds:0.0}s for provider rate limit."));
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
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

    private void ApplyRateLimitHeaders(KeyState state, HttpResponseMessage response)
    {
        var retryAfter = ReadRetryAfter(response);
        if (retryAfter is not null)
        {
            SetAvailableAfter(state, retryAfter.Value);
            return;
        }

        var remaining = ReadMinimumRemaining(response);
        if (remaining is not <= 0) return;
        var reset = ReadRateLimitResetDelay(response);
        if (reset is not null) SetAvailableAfter(state, reset.Value);
    }

    private static long? ReadMinimumRemaining(HttpResponseMessage response)
    {
        long? minimum = null;
        foreach (var name in new[]
                 {
                     "x-ratelimit-remaining-requests",
                     "x-ratelimit-remaining-tokens",
                     "ratelimit-remaining",
                     "ratelimit-remaining-requests",
                     "ratelimit-remaining-tokens"
                 })
        {
            if (!TryGetHeaderValue(response, name, out var value)
                || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }
            minimum = minimum is null ? parsed : Math.Min(minimum.Value, parsed);
        }
        return minimum;
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return NonNegative(delta);
        if (retryAfter?.Date is { } date) return NonNegative(date - timeProvider.GetUtcNow());
        if (!TryGetHeaderValue(response, "retry-after", out var raw)) return null;
        return ParseResetDelay(raw);
    }

    private TimeSpan? ReadRateLimitResetDelay(HttpResponseMessage response)
    {
        foreach (var name in new[]
                 {
                     "x-ratelimit-reset-requests",
                     "x-ratelimit-reset-tokens",
                     "ratelimit-reset",
                     "ratelimit-reset-requests",
                     "ratelimit-reset-tokens"
                 })
        {
            if (TryGetHeaderValue(response, name, out var value)
                && ParseResetDelay(value) is { } delay)
            {
                return delay;
            }
        }
        return null;
    }

    private TimeSpan? ParseResetDelay(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return null;
        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var date))
        {
            return NonNegative(date - timeProvider.GetUtcNow());
        }

        if (TryParseCompactDuration(text, out var compactDuration)) return compactDuration;

        var multiplier = 1d;
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 0.001d;
            text = text[..^2];
        }
        else if (text.EndsWith('s')) text = text[..^1];
        else if (text.EndsWith('m'))
        {
            multiplier = 60d;
            text = text[..^1];
        }
        else if (text.EndsWith('h'))
        {
            multiplier = 3600d;
            text = text[..^1];
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric)
            || !double.IsFinite(numeric)
            || numeric < 0)
        {
            return null;
        }
        if (multiplier == 1d && numeric > 1_000_000_000d)
        {
            try
            {
                return NonNegative(DateTimeOffset.FromUnixTimeSeconds((long)numeric) - timeProvider.GetUtcNow());
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        var seconds = numeric * multiplier;
        return seconds <= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    private static bool TryParseCompactDuration(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var matches = CompactDurationRegex().Matches(value);
        if (matches.Count == 0 || matches.Cast<Match>().Sum(match => match.Length) != value.Length) return false;

        var totalSeconds = 0d;
        foreach (Match match in matches)
        {
            if (!double.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var amount))
            {
                return false;
            }
            totalSeconds += match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "ms" => amount / 1_000d,
                "s" => amount,
                "m" => amount * 60d,
                "h" => amount * 3_600d,
                _ => double.PositiveInfinity
            };
        }
        if (!double.IsFinite(totalSeconds) || totalSeconds > TimeSpan.MaxValue.TotalSeconds) return false;
        duration = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }

    private static bool TryGetHeaderValue(HttpResponseMessage response, string name, out string value)
    {
        if (response.Headers.TryGetValues(name, out var values)
            || response.Content.Headers.TryGetValues(name, out values))
        {
            value = values.FirstOrDefault() ?? string.Empty;
            return value.Length > 0;
        }
        value = string.Empty;
        return false;
    }

    private void SetAvailableAfter(KeyState state, TimeSpan delay)
    {
        var now = timeProvider.GetTimestamp();
        state.AvailableAtTimestamp = AddDuration(now, NonNegative(delay));
    }

    private static TimeSpan ExponentialBackoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Clamp(attempt - 1, 0, 6))));

    private static TimeSpan NonNegative(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

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

    private static async Task<ProviderErrorDiagnostic> ReadProviderErrorDiagnosticAsync(
        HttpResponseMessage response,
        int configuredMaxBytes,
        IReadOnlyList<SourceEntry> batch,
        string systemPrompt,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var requestId = ReadSafeRequestId(response);
        string raw;
        try
        {
            raw = await ReadBoundedResponseAsync(
                    response.Content,
                    Math.Min(Math.Max(configuredMaxBytes, 1), 64 * 1024),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException)
        {
            return new ProviderErrorDiagnostic(
                string.Empty,
                "공급자 오류 응답을 읽지 못했습니다.",
                requestId);
        }

        var code = string.Empty;
        var message = raw;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array
                && root.GetArrayLength() == 1)
            {
                root = root[0];
            }
            if (root.ValueKind == JsonValueKind.Object)
            {
                var error = root.TryGetProperty("error", out var errorElement)
                    && errorElement.ValueKind == JsonValueKind.Object
                    ? errorElement
                    : root;
                code = ReadJsonScalar(error, "status");
                if (string.IsNullOrWhiteSpace(code)) code = ReadJsonScalar(error, "code");
                var structuredMessage = ReadJsonScalar(error, "message");
                if (!string.IsNullOrWhiteSpace(structuredMessage)) message = structuredMessage;
            }
        }
        catch (JsonException)
        {
            // Some compatible providers return a useful bounded plain-text error.
        }

        return new ProviderErrorDiagnostic(
            SanitizeDiagnosticToken(code, 96),
            SanitizeProviderMessage(message, batch, systemPrompt, apiKey),
            requestId);
    }

    private static string ReadJsonScalar(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static string SanitizeProviderMessage(
        string value,
        IReadOnlyList<SourceEntry> batch,
        string systemPrompt,
        string apiKey)
    {
        var safe = value;
        foreach (var secret in batch.Select(entry => entry.Text)
                     .Append(systemPrompt)
                     .Append(apiKey)
                     .Where(secret => !string.IsNullOrEmpty(secret))
                     .Distinct(StringComparer.Ordinal))
        {
            safe = safe.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        safe = AppLogger.Redact(safe).Trim();
        return safe.Length switch
        {
            0 => "공급자가 오류 상세 내용을 제공하지 않았습니다.",
            > 500 => safe[..500],
            _ => safe
        };
    }

    private static string ReadSafeRequestId(HttpResponseMessage response)
    {
        foreach (var header in new[] { "x-request-id", "request-id", "x-goog-request-id" })
        {
            if (!response.Headers.TryGetValues(header, out var values)) continue;
            var value = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value)) return SanitizeDiagnosticToken(value, 128);
        }

        return string.Empty;
    }

    private static string SanitizeDiagnosticToken(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var safe = new string(value.Trim()
            .Take(maximumLength)
            .Where(character => char.IsLetterOrDigit(character)
                                || character is '_' or '-' or '.' or ':' or ' ')
            .ToArray());
        return safe.Trim();
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
        _ => "The provider returned an error."
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
        public int Requests { get; set; }
        public int Failures { get; set; }
        public bool Disabled { get; set; }
    }

    private sealed record ProviderErrorDiagnostic(string Code, string Message, string RequestId);

    [GeneratedRegex("^\\s*([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(.+)\\s*$", RegexOptions.CultureInvariant)] private static partial Regex KeyAssignmentRegex();
    [GeneratedRegex("^(CEREBRAS_API_KEY|CEREBRAS_KEY|OPENAI_API_KEY|GEMINI_API_KEY|GOOGLE_API_KEY|DEEPSEEK_API_KEY|DASHSCOPE_API_KEY|GROQ_API_KEY|MISTRAL_API_KEY|OPENROUTER_API_KEY|ZAI_API_KEY|API_KEY|KEY|RIMWORLD_TRANSLATOR_API_KEYS)$", RegexOptions.CultureInvariant)] private static partial Regex AllowedKeyNameRegex();
    [GeneratedRegex("(?<value>[0-9]+(?:\\.[0-9]+)?)(?<unit>ms|s|m|h)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CompactDurationRegex();
}

internal sealed class ProviderRequestException : InvalidOperationException
{
    public ProviderRequestException(
        HttpStatusCode statusCode,
        string safeSummary,
        bool canSplitBatch,
        string provider = "",
        string model = "",
        string errorCode = "",
        string providerMessage = "",
        string requestId = "")
        : base($"Provider request failed with HTTP {(int)statusCode}: {safeSummary}")
    {
        StatusCode = statusCode;
        CanSplitBatch = canSplitBatch;
        Provider = provider;
        Model = model;
        ErrorCode = errorCode;
        ProviderMessage = providerMessage;
        RequestId = requestId;
    }

    public HttpStatusCode StatusCode { get; }
    public bool CanSplitBatch { get; }
    public string Provider { get; }
    public string Model { get; }
    public string ErrorCode { get; }
    public string ProviderMessage { get; }
    public string RequestId { get; }
}

internal sealed class ProviderRequestBudgetExceededException : InvalidOperationException
{
    public ProviderRequestBudgetExceededException(int maximumRequests)
        : base($"Provider request budget of {maximumRequests:N0} attempts was exhausted.")
    {
    }
}
