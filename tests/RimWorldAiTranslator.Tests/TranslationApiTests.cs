using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Diagnostics;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    internal static int EmitPhase05CanonicalCapture()
    {
        const string glossary = "- pawn => 폰\n- colony => 식민지";
        var item = new SourceEntry
        {
            Id = "id-1",
            Key = "키<&",
            Kind = "DefInjected",
            TypeName = "ThingDef",
            Field = "label",
            Text = "\"quoted\"\n{0}"
        };
        var system = TranslationPrompt.CreateSystem(glossary, null);
        var user = TranslationPrompt.CreateUserPayload([item]);
        var request = TranslationPrompt.CreateRequestBody(
            CreateOptions("https://fixture.invalid", 1),
            system,
            user).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        const string response = "{\"choices\":[{\"message\":{\"content\":\"{\\\"translations\\\":[{\\\"id\\\":\\\"id-1\\\",\\\"text\\\":\\\"번역 {0}\\\"}]}\"}}]}";
        var parsed = TranslationPrompt.ParseResponse(response, ["id-1"]);
        var output = JsonSerializer.Serialize(new
        {
            translations = new[] { new { id = "id-1", text = parsed["id-1"] } }
        });

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            systemLength = system.Length,
            systemSha256 = Sha256Utf8(system),
            userLength = user.Length,
            userSha256 = Sha256Utf8(user),
            requestLength = request.Length,
            requestSha256 = Sha256Utf8(request),
            responseSha256 = Sha256Utf8(response),
            outputSha256 = Sha256Utf8(output),
            systemBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(system)),
            userBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(user)),
            requestBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(request)),
            responseBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(response)),
            outputBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(output))
        }));
        return 0;
    }

    private static string Sha256Utf8(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static void RegisterPhase05ApiParityTests()
    {
        Tests.AddRange(
        [
            ("Translation.ApiUrlAndTransport", Phase05ApiUrlAndTransport),
            ("Translation.ApiValidationAndRetry", Phase05ApiValidationAndRetry),
            ("Translation.ApiHttpErrorMatrix", Phase05ApiHttpErrorMatrix),
            ("Translation.ProviderErrorDiagnostics", Phase05ProviderErrorDiagnostics),
            ("Translation.HeaderDrivenRateLimitsAndGemini", Phase05HeaderDrivenRateLimitsAndGemini),
            ("Translation.ApiConnectionAndEmptyRetry", Phase05ApiConnectionAndEmptyRetry),
            ("Translation.BatchSplitAndFalseComplete", Phase05BatchSplitAndFalseComplete),
            ("Translation.ApiResponseLimit", Phase05ApiResponseLimit),
            ("Translation.GlossaryNullDefaults", Phase05GlossaryNullDefaults),
            ("Translation.CanonicalPrompt", Phase05CanonicalPrompt)
        ]);
    }

    private static void Phase05ApiUrlAndTransport()
    {
        var variants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://fixture.invalid"] = "https://fixture.invalid/v1/chat/completions",
            ["https://fixture.invalid/"] = "https://fixture.invalid/v1/chat/completions",
            ["https://fixture.invalid/v1"] = "https://fixture.invalid/v1/chat/completions",
            ["https://fixture.invalid/v1beta/openai"] = "https://fixture.invalid/v1beta/openai/chat/completions",
            ["https://fixture.invalid/compatible-mode/v1"] = "https://fixture.invalid/compatible-mode/v1/chat/completions",
            ["https://fixture.invalid/api/v1"] = "https://fixture.invalid/api/v1/chat/completions",
            ["https://fixture.invalid/paas/v4"] = "https://fixture.invalid/paas/v4/chat/completions",
            ["https://fixture.invalid/CHAT/COMPLETIONS/"] = "https://fixture.invalid/CHAT/COMPLETIONS",
            ["https://fixture.invalid/v1?api-version=2024-10-21-preview"] =
                "https://fixture.invalid/v1/chat/completions?api-version=2024-10-21-preview",
            ["https://fixture.invalid/custom/chat/completions?format=json&version=1.2.3"] =
                "https://fixture.invalid/custom/chat/completions?format=json&version=1.2.3"
        };
        foreach (var pair in variants)
        {
            Assert(
                TranslationApiClient.GetChatCompletionsUrl(pair.Key) == pair.Value,
                $"Chat-completions URL normalization changed for {pair.Key}.");
        }

        var handler = new SyntheticHttpHandler(
        [
            () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "번역"))))
        ]);
        using (var client = CreateClient(handler))
        {
            Assert(client.TransportTimeout == System.Threading.Timeout.InfiniteTimeSpan,
                "HttpClient.Timeout can race the authoritative per-request timeout CTS.");
            var result = client.TranslateOpenAiAsync(
                    CreateOptions("https://fixture.invalid/base/", 1),
                    CreateBatch()[..1],
                    "synthetic system prompt",
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(result["id-1"] == "번역", "Synthetic provider result was not returned.");
            Assert(handler.RequestUris.Single().AbsoluteUri == "https://fixture.invalid/base/v1/chat/completions",
                "The normalized URL was not used by the HTTP request.");
            Assert(handler.AuthorizationSchemes.Single() == "Bearer", "The synthetic request lost Bearer authentication.");
        }

        var queryHandler = new SyntheticHttpHandler(
        [
            () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "쿼리 보존"))))
        ]);
        using (var client = CreateClient(queryHandler))
        {
            const string queryEndpoint =
                "https://fixture.invalid/v1?api-version=2024-10-21-preview&format=json";
            var result = client.TranslateOpenAiAsync(
                    CreateOptions(queryEndpoint, 1),
                    CreateBatch()[..1],
                    "synthetic system prompt",
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(result["id-1"] == "쿼리 보존"
                   && queryHandler.RequestUris.Single().AbsoluteUri
                       == "https://fixture.invalid/v1/chat/completions?api-version=2024-10-21-preview&format=json",
                "A validated endpoint query was not preserved ahead of the generated completion path.");
        }

        using var timeoutClient = CreateClient(new CancellationOnlyHandler());
        var timeoutError = CaptureException<InvalidOperationException>(() => timeoutClient.TranslateOpenAiAsync(
                CreateOptions("https://fixture.invalid/v1", 1, timeout: TimeSpan.FromMilliseconds(25)),
                CreateBatch()[..1],
                "synthetic system prompt",
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult());
        Assert(timeoutError.InnerException is TimeoutException,
            "The per-request timeout CTS did not remain authoritative.");
    }

    private static void Phase05ApiValidationAndRetry()
    {
        var batch = CreateBatch();
        var malformed = "{";
        var arrayRoot = "[]";
        var structural = "{\"choices\":[]}";
        var nonObjectUsage = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "{" } } },
            usage = Array.Empty<object>()
        });
        var nonStringDirectMap = ResponseWithContent(
            "{\"id-1\":{\"value\":\"첫째\"},\"id-2\":\"둘째\"}");
        var duplicate = ResponseWithContent(
            "{\"translations\":[{\"id\":\"id-1\",\"text\":\"첫째\"},{\"id\":\"ID-1\",\"text\":\"둘째\"},{\"id\":\"id-2\",\"text\":\"셋째\"}]}");
        var missing = ResponseWithContent(
            "{\"translations\":[{\"id\":\"id-1\",\"text\":\"첫째\"}]}");
        var unexpected = ResponseWithContent(
            "{\"translations\":[{\"id\":\"id-1\",\"text\":\"첫째\"},{\"id\":\"id-2\",\"text\":\"둘째\"},{\"id\":\"id-extra\",\"text\":\"추가\"}]}");
        var reordered = ResponseWithContent(TranslationContent(("id-2", "둘째"), ("id-1", "첫째")));

        AssertInvalidProviderData(malformed, batch);
        AssertInvalidProviderData(arrayRoot, batch);
        AssertInvalidProviderData(structural, batch);
        AssertInvalidProviderData(nonObjectUsage, batch);
        AssertInvalidProviderData(nonStringDirectMap, batch);
        AssertInvalidProviderData(missing, batch);

        var duplicateMap = TranslationPrompt.ParseResponse(duplicate, batch.Select(entry => entry.Id));
        Assert(duplicateMap["id-1"] == "둘째" && duplicateMap["id-2"] == "셋째",
            "The stable parser no longer accepts the provider's last value for a duplicate id.");
        var unexpectedMap = TranslationPrompt.ParseResponse(unexpected, batch.Select(entry => entry.Id));
        Assert(unexpectedMap["id-1"] == "첫째" && unexpectedMap["id-2"] == "둘째",
            "An extra provider id caused valid expected translations to be rejected.");

        var handler = new SyntheticHttpHandler(
        [
            () => SuccessResponse(malformed),
            () => SuccessResponse(arrayRoot),
            () => SuccessResponse(structural),
            () => SuccessResponse(nonObjectUsage),
            () => SuccessResponse(nonStringDirectMap),
            () => SuccessResponse(missing),
            () => SuccessResponse(reordered)
        ]);
        using var client = CreateClient(handler);
        var result = client.TranslateOpenAiAsync(
                CreateOptions("https://fixture.invalid", 7),
                batch,
                "synthetic system prompt",
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(handler.RequestUris.Count == 7, "Malformed or incomplete model results were not retried within MaxRetries.");
        Assert(result.Count == 2 && result["id-1"] == "첫째" && result["id-2"] == "둘째",
            "A complete reordered result was not accepted.");

        var budgetedMalformed = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "{" } } },
            usage = new { prompt_tokens = 100, total_tokens = 5_000 }
        });
        var budgetHandler = new SyntheticHttpHandler(
        [
            () => SuccessResponse(budgetedMalformed),
            () => SuccessResponse(reordered)
        ]);
        using var budgetClient = CreateClient(budgetHandler);
        var budgetOptions = CreateOptions(
            "https://fixture.invalid",
            2,
            dailyTokenBudget: 5_000);
        var budgetResult = budgetClient.TranslateOpenAiAsync(
                budgetOptions,
                batch,
                "synthetic system prompt",
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(budgetHandler.RequestUris.Count == 2 && budgetResult["id-1"] == "첫째",
            "A legacy fixed daily-token value blocked calls without a provider limit response.");
    }

    private static void Phase05ApiResponseLimit()
    {
        var handler = new SyntheticHttpHandler(
        [
            OversizedResponse
        ]);
        using var client = CreateClient(handler);
        var error = CaptureException<InvalidDataException>(() => client.TranslateOpenAiAsync(
                CreateOptions("https://fixture.invalid", 1, maxResponseBytes: 64),
                CreateBatch()[..1],
                "synthetic system prompt",
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult());
        Assert(error.Message.Contains("64 byte limit", StringComparison.Ordinal),
            "An unknown-length oversized response did not fail explicitly at the configured byte cap.");
    }

    private static void Phase05ApiHttpErrorMatrix()
    {
        var retryableStatuses = new[]
        {
            HttpStatusCode.RequestTimeout,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway,
            HttpStatusCode.ServiceUnavailable
        };
        foreach (var status in retryableStatuses)
        {
            var rotatesKey = status is HttpStatusCode.TooManyRequests;
            var handler = AssertProviderRetry(
                $"HTTP {(int)status}",
                () => StatusResponse(status),
                rotatesKey ? ["fixture-key-one", "fixture-key-two"] : ["fixture-key-one"]);
            if (rotatesKey)
            {
                Assert(handler.AuthorizationParameters.SequenceEqual(
                        ["fixture-key-one", "fixture-key-two"],
                        StringComparer.Ordinal),
                    $"HTTP {(int)status} did not rotate away from the unavailable synthetic key.");
            }
        }

        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            const string rawBody = "SYNTHETIC_SECRET_PROVIDER_BODY";
            var handler = new SyntheticHttpHandler(
            [
                () => new HttpResponseMessage(status)
                {
                    Content = new StringContent(rawBody, Encoding.UTF8, "text/plain")
                },
                () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "should-not-run"))))
            ]);
            using var client = CreateClient(handler);
            var error = CaptureException<ProviderRequestException>(() => client.TranslateOpenAiAsync(
                    CreateOptions("https://fixture.invalid", 2),
                    CreateBatch()[..1],
                    "synthetic system prompt",
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult());
            Assert(handler.RequestUris.Count == 1,
                $"HTTP {(int)status} was retried even though the request must be corrected first.");
            Assert(!error.Message.Contains(rawBody, StringComparison.Ordinal),
                $"HTTP {(int)status} exposed the raw provider response body.");
        }
    }

    private static void Phase05HeaderDrivenRateLimitsAndGemini()
    {
        var noHeaderHandler = new SyntheticHttpHandler(
        [
            () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "first")))),
            () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "second"))))
        ]);
        var noHeaderDelays = new List<TimeSpan>();
        using (var client = new TranslationApiClient(
                   ["fixture-key-one"],
                   noHeaderHandler,
                   (delay, token) =>
                   {
                       token.ThrowIfCancellationRequested();
                       noHeaderDelays.Add(delay);
                       return Task.CompletedTask;
                   }))
        {
            var options = CreateOptions("https://fixture.invalid", 1, requestsPerMinute: 1);
            _ = client.TranslateOpenAiAsync(options, CreateBatch()[..1], "system", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            _ = client.TranslateOpenAiAsync(options, CreateBatch()[..1], "system", null, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        Assert(noHeaderDelays.Count == 0,
            "A configured fixed RPM value delayed requests even though the provider sent no limit headers or 429.");

        var timeProvider = new AdvancingTimeProvider();
        var headerDelays = new List<TimeSpan>();
        var headerHandler = new SyntheticHttpHandler(
        [
            () => ResponseWithRateHeaders(remaining: "0", reset: "1m2s"),
            () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "after-reset"))))
        ]);
        using (var client = CreateAdvancingClient(headerHandler, timeProvider, headerDelays))
        {
            var options = CreateOptions("https://fixture.invalid", 1);
            _ = client.TranslateOpenAiAsync(options, CreateBatch()[..1], "system", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            _ = client.TranslateOpenAiAsync(options, CreateBatch()[..1], "system", null, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        Assert(headerDelays.Count == 1 && headerDelays[0] == TimeSpan.FromSeconds(62),
            "A zero remaining-request header did not parse the provider's compact reset duration.");

        timeProvider = new AdvancingTimeProvider();
        var retryAfterDelays = new List<TimeSpan>();
        var retryAfterHandler = new SyntheticHttpHandler(
        [
            () => ResponseWithRetryAfter(TimeSpan.FromSeconds(7)),
            () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "retry-after"))))
        ]);
        using (var client = CreateAdvancingClient(retryAfterHandler, timeProvider, retryAfterDelays))
        {
            var result = client.TranslateOpenAiAsync(
                    CreateOptions("https://fixture.invalid", 2),
                    CreateBatch()[..1],
                    "system",
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(result["id-1"] == "retry-after", "The Retry-After fixture did not recover.");
        }
        Assert(retryAfterDelays.SequenceEqual([TimeSpan.FromSeconds(7)]),
            "HTTP 429 did not honor Retry-After exactly or added a second generic retry delay.");

        timeProvider = new AdvancingTimeProvider();
        var unavailableDelays = new List<TimeSpan>();
        var unavailableHandler = new SyntheticHttpHandler(
        [
            () => ResponseWithRetryAfter(TimeSpan.FromSeconds(9), HttpStatusCode.ServiceUnavailable),
            () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "after-overload"))))
        ]);
        using (var client = CreateAdvancingClient(unavailableHandler, timeProvider, unavailableDelays))
        {
            var result = client.TranslateOpenAiAsync(
                    CreateOptions("https://fixture.invalid", 2),
                    CreateBatch()[..1],
                    "system",
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(result["id-1"] == "after-overload", "The temporary 503 fixture did not recover.");
        }
        Assert(unavailableDelays.SequenceEqual([TimeSpan.FromSeconds(9)]),
            "HTTP 503 did not honor Retry-After exactly or added a second generic retry delay.");

        timeProvider = new AdvancingTimeProvider();
        var backoffDelays = new List<TimeSpan>();
        var backoffHandler = new SyntheticHttpHandler(
        [
            () => StatusResponse(HttpStatusCode.TooManyRequests),
            () => StatusResponse(HttpStatusCode.TooManyRequests),
            () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "backoff"))))
        ]);
        using (var client = CreateAdvancingClient(backoffHandler, timeProvider, backoffDelays))
        {
            _ = client.TranslateOpenAiAsync(
                    CreateOptions("https://fixture.invalid", 3),
                    CreateBatch()[..1],
                    "system",
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        Assert(backoffDelays.SequenceEqual([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]),
            "Headerless HTTP 429 responses did not use one exponential backoff sequence.");

        var gemini = ApiProviderCatalog.Get("Gemini");
        var gemini35 = CreateOptions(
            "https://fixture.invalid",
            1,
            provider: gemini,
            model: "gemini-3.5-flash",
            reasoningEffort: gemini.ReasoningEffort);
        var gemini35Body = TranslationPrompt.CreateRequestBody(gemini35, "system", "{}");
        Assert(gemini35Body["reasoning_effort"]?.GetValue<string>() == "low",
            "Gemini 3.5 Flash did not use the supported low reasoning level.");
        Assert(gemini35Body["response_format"]?["type"]?.GetValue<string>() == "json_schema",
            "Gemini 3.5 Flash lost strict structured output support.");

        string[] expectedTextModels =
        [
            "gemini-3.5-flash",
            "gemini-3.1-flash-lite",
            "gemini-3-flash-preview",
            "gemini-3.1-pro-preview"
        ];
        Assert(gemini.Models.SequenceEqual(expectedTextModels, StringComparer.Ordinal),
            "The Gemini selector did not match the active general text/chat model catalog.");
        var removedModels = new[]
        {
            "gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite",
            "gemini-flash-latest", "gemini-pro-latest", "gemini-3.5-pro"
        };
        Assert(!gemini.Models.Intersect(removedModels, StringComparer.Ordinal).Any(),
            "The Gemini selector still exposed a retired, unstable alias, or nonexistent model id.");

        foreach (var profile in ApiProviderCatalog.All)
        {
            Assert(profile.Models.SequenceEqual(profile.ModelOptions.Select(option => option.Id), StringComparer.Ordinal),
                $"{profile.Id} displayed a model label that did not map to its exact API id.");
            Assert(profile.Models.Distinct(StringComparer.Ordinal).Count() == profile.Models.Count,
                $"{profile.Id} contained a duplicate model id.");
            Assert(profile.ModelOptions.Select(option => option.Access)
                    .SequenceEqual(profile.ModelOptions.Select(option => option.Access).OrderBy(access => access)),
                $"{profile.Id} did not list free/free-tier models before paid models.");
            if (!profile.Id.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                Assert(profile.FindModel(profile.DefaultModel) is not null,
                    $"{profile.Id} defaulted to a model outside its supported catalog.");
            }
        }

        Assert(ApiProviderCatalog.Get("OpenRouter").ModelOptions[0] is
        { Id: "openrouter/free", Access: ProviderModelAccess.Free },
            "OpenRouter did not expose its free router separately from paid models.");
        Assert(!ApiProviderCatalog.Get("OpenRouter").Models.Contains("~openai/gpt-latest", StringComparer.Ordinal),
            "OpenRouter retained the unstable pseudo-model alias.");
        Assert(ApiProviderCatalog.Get("ZAI").ModelOptions.Take(2)
                .All(option => option.Access == ProviderModelAccess.Free),
            "Z.AI free model variants were not separated from paid models.");
        Assert(ApiProviderCatalog.Get("Qwen").ModelOptions
                .All(option => option.Access == ProviderModelAccess.TrialCredit),
            "Qwen's one-time new-user quota was mislabeled as a permanently free model.");
    }

    private static void Phase05ProviderErrorDiagnostics()
    {
        const string credential = "SYNTHETIC_GEMINI_KEY_4E91";
        const string source = "SYNTHETIC_PRIVATE_SOURCE_37D8";
        const string systemPrompt = "SYNTHETIC_PRIVATE_SYSTEM_8B12";
        const string safeProviderDetail = "Unsupported field strict in response_format";
        var responseBody = JsonSerializer.Serialize(new
        {
            error = new
            {
                code = 400,
                message = $"{safeProviderDetail}; source={source}; key={credential}",
                status = "INVALID_ARGUMENT"
            }
        });
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("x-goog-request-id", "request-abc-123");
        var handler = new SyntheticHttpHandler([() => response]);
        using var client = CreateClient(handler, [credential]);
        var gemini = ApiProviderCatalog.Get("Gemini");
        var options = CreateOptions(
            "https://fixture.invalid",
            1,
            provider: gemini,
            model: "gemini-3.5-flash",
            reasoningEffort: gemini.ReasoningEffort);
        var error = CaptureException<ProviderRequestException>(() => client.TranslateOpenAiAsync(
                options,
                [new SourceEntry { Id = "id-1", Key = "Synthetic.One", Kind = "Keyed", Text = source }],
                systemPrompt,
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult());

        Assert(error.StatusCode == HttpStatusCode.BadRequest
               && error.Provider == "Gemini"
               && error.Model == "gemini-3.5-flash"
               && error.ErrorCode == "INVALID_ARGUMENT"
               && error.RequestId == "request-abc-123"
               && error.ProviderMessage.Contains(safeProviderDetail, StringComparison.Ordinal),
            "The provider's structured diagnostic fields were discarded.");
        Assert(!error.ProviderMessage.Contains(source, StringComparison.Ordinal)
               && !error.ProviderMessage.Contains(credential, StringComparison.Ordinal),
            "The provider diagnostic retained an API key or full source entry.");

        var wrapped = new InvalidOperationException(
            "synthetic wrapper",
            new HttpRequestException("synthetic transport wrapper", error, HttpStatusCode.BadRequest));
        var log = OperationErrorPresentation.CreateLogMessage("Gemini 번역", wrapped);
        var user = OperationErrorPresentation.CreateUserDetail(wrapped);
        foreach (var expected in new[]
                 {
                     "Provider=Gemini", "Model=gemini-3.5-flash", "HTTP=400",
                     "Code=INVALID_ARGUMENT", "RequestId=request-abc-123", safeProviderDetail
                 })
        {
            Assert(log.Contains(expected, StringComparison.Ordinal),
                $"The local diagnostic log omitted '{expected}'.");
        }
        Assert(user.Contains("HTTP 400", StringComparison.Ordinal)
               && user.Contains("INVALID_ARGUMENT", StringComparison.Ordinal)
               && user.Contains("request-abc-123", StringComparison.Ordinal),
            "The provider error UI omitted its actionable status, code, or request id.");
        Assert(!log.Contains(source, StringComparison.Ordinal)
               && !log.Contains(credential, StringComparison.Ordinal)
               && !user.Contains(source, StringComparison.Ordinal)
               && !user.Contains(credential, StringComparison.Ordinal),
            "Provider diagnostics exposed an API key or full source entry.");

        const string unavailableMessage = "This model is currently experiencing high demand. Please try again later.";
        var unavailableBody = JsonSerializer.Serialize(new[]
        {
            new
            {
                error = new
                {
                    code = 503,
                    message = unavailableMessage,
                    status = "UNAVAILABLE"
                }
            }
        });
        var unavailableHandler = new SyntheticHttpHandler(
        [
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(unavailableBody, Encoding.UTF8, "application/json")
            }
        ]);
        using var unavailableClient = CreateClient(unavailableHandler, [credential]);
        var unavailableError = CaptureException<InvalidOperationException>(() => unavailableClient.TranslateOpenAiAsync(
                CreateOptions(
                    "https://fixture.invalid",
                    1,
                    provider: gemini,
                    model: "gemini-3.5-flash",
                    reasoningEffort: gemini.ReasoningEffort),
                [new SourceEntry { Id = "id-1", Key = "Synthetic.One", Kind = "Keyed", Text = source }],
                systemPrompt,
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult());
        var unavailableLog = OperationErrorPresentation.CreateLogMessage("Gemini 번역", unavailableError);
        var unavailableUser = OperationErrorPresentation.CreateUserDetail(unavailableError);
        Assert(unavailableLog.Contains("HTTP=503", StringComparison.Ordinal)
               && unavailableLog.Contains("Code=UNAVAILABLE", StringComparison.Ordinal)
               && unavailableLog.Contains(unavailableMessage, StringComparison.Ordinal)
               && !unavailableLog.Contains("[{", StringComparison.Ordinal),
            "The array-wrapped Gemini overload response was not normalized for local diagnostics.");
        Assert(unavailableUser.Contains("일시적으로 과부하", StringComparison.Ordinal)
               && unavailableUser.Contains("HTTP 503", StringComparison.Ordinal)
               && unavailableUser.Contains("잠시 후 다시 시도", StringComparison.Ordinal)
               && !unavailableUser.Contains(unavailableMessage, StringComparison.Ordinal)
               && !unavailableUser.Contains("[{", StringComparison.Ordinal),
            "The basic UI exposed the raw 503 provider body or omitted temporary-overload guidance.");
    }

    private static void Phase05ApiConnectionAndEmptyRetry()
    {
        AssertProviderRetry(
            "connection exception",
            static () => throw new HttpRequestException("Synthetic connection failure."),
            ["fixture-key-one"]);
        AssertProviderRetry(
            "response stream drop",
            static () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new DroppingContent() },
            ["fixture-key-one"]);
        AssertProviderRetry(
            "empty HTTP 200 response",
            static () => SuccessResponse(string.Empty),
            ["fixture-key-one"]);
    }

    private static void Phase05BatchSplitAndFalseComplete()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var handler = new SplitFailureHandler();
            var engine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(
                    keys,
                    handler,
                    static (_, _) => Task.CompletedTask));
            var reviewRoot = Path.Combine(Directory.GetParent(modRoot)!.FullName, "phase05-split-failure");
            var error = CaptureException<InvalidOperationException>(() => engine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = modRoot,
                ReviewRoot = reviewRoot,
                ReviewOnly = true,
                SourceLanguageFolder = "Auto",
                ApiKeys = ["fixture-key-not-real"],
                Provider = ApiProviderCatalog.Get("Custom"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Synthetic",
                    Url = "https://fixture.invalid/v1/chat/completions",
                    Model = "synthetic-model",
                    Temperature = 0.1
                },
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                BatchSize = 2,
                MaxRetries = 1,
                Timeout = TimeSpan.FromSeconds(2),
                ResponseFormatMode = "JsonObject",
                CompletionTokenParameter = "none",
                ReasoningEffort = string.Empty,
                GeneratedGlossaryPath = Path.Combine(RepositoryRoot(), "src", "RimWorldAiTranslator.App", "Assets", "glossary.generated.ko.json")
            }).GetAwaiter().GetResult());

            Assert(error.Message.Contains("single-entry fallback", StringComparison.OrdinalIgnoreCase),
                "The binary split fixture did not reach an explicit single-entry failure.");
            Assert(handler.RequestCount == 4,
                $"The binary split request sequence changed: {handler.RequestCount}.");
            var runRoot = Directory.EnumerateDirectories(reviewRoot).Single();
            var auditRoot = Path.Combine(runRoot, "_TranslationAudit");
            var progressPath = Directory.EnumerateFiles(auditRoot, "*-progress.json").Single();
            var comparisonPath = Directory.EnumerateFiles(auditRoot, "*-comparison.json").Single();
            using var progress = JsonDocument.Parse(File.ReadAllText(progressPath));
            var checkpoint = progress.RootElement[0];
            Assert(!checkpoint.GetProperty("complete").GetBoolean()
                   && checkpoint.GetProperty("completedBatches").GetInt32() == 1,
                "A partially failed split batch was falsely marked complete or lost the last whole-batch checkpoint.");
            var rows = JsonSerializer.Deserialize<List<ReviewComparisonRow>>(File.ReadAllText(comparisonPath))!;
            Assert(rows.Count == 7
                   && rows.Select(row => row.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 7
                   && rows.Count(row => !string.IsNullOrWhiteSpace(row.Candidate)) == 2
                   && rows.All(row => !string.IsNullOrWhiteSpace(row.Source)),
                "The failed run did not retain the completed batch together with every untranslated source row.");
            Assert(!ContainsFiles(Path.Combine(runRoot, "Languages")),
                "A failed split batch committed partial language output.");
        });
    }

    private static void Phase05GlossaryNullDefaults()
    {
        WithTempRoot(root =>
        {
            var emptyPath = Path.Combine(root, "terms-null.json");
            File.WriteAllText(emptyPath, "{\"terms\":null,\"futureRoot\":true}", new UTF8Encoding(false));
            var service = new GlossaryService();
            Assert(service.Load(emptyPath, null, false).Count == 0,
                "An explicit null terms property did not use the Golden empty default.");

            var termPath = Path.Combine(root, "term-null-fields.json");
            File.WriteAllText(termPath,
                "{\"terms\":[null,{\"source\":\"Pawn\",\"ko\":\"폰\",\"note\":null,\"priority\":null,\"count\":null,\"origin\":null,\"futureTerm\":{\"value\":1}}],\"futureRoot\":true}",
                new UTF8Encoding(false));
            var loaded = service.Load(termPath, null, false);
            Assert(loaded.Count == 1
                   && loaded[0].Priority == 1000
                   && loaded[0].Count == 1
                   && loaded[0].Note.Length == 0
                   && loaded[0].Origin.Length == 0,
                "Nullable glossary fields or unknown fields did not retain Golden defaults.");
        });
    }

    private static void Phase05CanonicalPrompt()
    {
        const string glossary = "- pawn => 폰\n- colony => 식민지";
        var expectedSystem = string.Join("\r\n",
        [
            "You translate RimWorld mod localization entries into natural Korean.",
            "Return only JSON matching this shape: {\"translations\":[{\"id\":\"same id\",\"text\":\"Korean translation\"}]}.",
            string.Empty,
            "Rules:",
            "- Translate only the text value. Never translate ids, XML keys, defNames, file names, class names, or paths.",
            " - Preserve placeholders, grammar-rule prefixes, and markup exactly: {0}, {PAWN_nameDef}, [pawn_nameDef], r_logentry->, $variable, <color=...>, </color>, \\n, %, and XML-like tags.",
            " - A grammar-rule prefix such as r_logentry-> must remain unchanged at the beginning of the translated value.",
            " - When a Korean particle follows a placeholder or dynamic noun, use RimWorld's automatic particle notation with the consonant-final form in parentheses first: (은)는, (이)가, (을)를, (과)와, (으)로.",
            " - Attach that notation directly to the placeholder, for example [lodgersLabelSingOrPluralDef](이)가. Never use reversed forms such as 은(는), 이(가), 을(를), 과(와), or 으로(로).",
            " - Keep label fields short, usually a noun phrase.",
            "- Use polite declarative Korean for descriptions and letters when appropriate.",
            "- Preserve meaningful line breaks, but never add padding blank lines or more than two consecutive \\n escapes.",
            "- Do not output repeated \\u000a escapes.",
            "- Keep RimWorld/DLC terms consistent with the glossary.",
            "- If a value is already a proper noun, keep the proper noun or transliterate naturally.",
            "- Do not add comments, explanations, markdown, or missing ids.",
            string.Empty,
            "Glossary:",
            glossary,
            string.Empty
        ]);
        var system = TranslationPrompt.CreateSystem(glossary, null);
        Assert(system == expectedSystem, "The canonical Golden system message changed.");
        Assert(Sha256Utf8(system) == "8e948e9e1f925d593909c65476fec7a63645cde87ca830991c38870b3de71a7d",
            "The system message differs from the actual Golden function capture.");
        const string extraWithTrailingCrLf = "Preserve the synthetic term.\r\n";
        var expectedExtraSystem = expectedSystem
            + "\r\nAdditional user instructions:\r\n"
            + extraWithTrailingCrLf;
        var extraSystem = TranslationPrompt.CreateSystem(glossary, extraWithTrailingCrLf);
        Assert(extraSystem == expectedExtraSystem && extraSystem.EndsWith("\r\n", StringComparison.Ordinal),
            "The Golden ExtraPrompt block did not preserve its exact trailing CRLF.");

        var item = new SourceEntry
        {
            Id = "id-1",
            Key = "키<&",
            Kind = "DefInjected",
            TypeName = "ThingDef",
            Field = "label",
            Text = "\"quoted\"\n{0}"
        };
        var expectedUser = string.Join("\r\n",
        [
            "{",
            "    \"entries\":  [",
            "                    {",
            "                        \"id\":  \"id-1\",",
            "                        \"key\":  \"키\\u003c\\u0026\",",
            "                        \"kind\":  \"DefInjected\",",
            "                        \"defType\":  \"ThingDef\",",
            "                        \"field\":  \"label\",",
            "                        \"text\":  \"\\\"quoted\\\"\\n{0}\"",
            "                    }",
            "                ]",
            "}"
        ]);
        var user = TranslationPrompt.CreateUserPayload([item]);
        Assert(user == expectedUser, "The canonical Golden user message changed.");
        Assert(Sha256Utf8(user) == "fad5e15e9f404c9a9d0f87854f6e57c2ff451f6b4d4682c1606f9f1039029fe2",
            "The user message differs from the actual Golden function capture.");
        using var parsedUser = JsonDocument.Parse(user);
        Assert(parsedUser.RootElement.GetProperty("entries")[0].GetProperty("text").GetString() == item.Text,
            "Canonical user-message serialization changed its semantic text.");

        var body = TranslationPrompt.CreateRequestBody(CreateOptions("https://fixture.invalid", 1), system, user);
        var messages = body["messages"]!.AsArray();
        Assert(messages[0]!["content"]!.GetValue<string>() == expectedSystem
               && messages[1]!["content"]!.GetValue<string>() == expectedUser,
            "The request body did not carry the canonical Golden system and user messages exactly.");
    }

    private static TranslationApiClient CreateClient(HttpMessageHandler handler) =>
        CreateClient(handler, ["fixture-key-not-real"]);

    private static TranslationApiClient CreateClient(HttpMessageHandler handler, IReadOnlyList<string> keys) =>
        new(keys, handler, static (_, _) => Task.CompletedTask);

    private static TranslationApiClient CreateAdvancingClient(
        HttpMessageHandler handler,
        AdvancingTimeProvider timeProvider,
        List<TimeSpan> delays) =>
        new(
            ["fixture-key-one"],
            handler,
            (delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                delays.Add(delay);
                timeProvider.Advance(delay);
                return Task.CompletedTask;
            },
            timeProvider);

    private static TranslationEngineOptions CreateOptions(
        string url,
        int maxRetries,
        int maxResponseBytes = TranslationEngineOptions.DefaultMaxResponseBytes,
        TimeSpan? timeout = null,
        long dailyTokenBudget = 0,
        int requestsPerMinute = 0,
        ApiProviderProfile? provider = null,
        string model = "synthetic-model",
        string reasoningEffort = "") =>
        new()
        {
            ModRoot = "synthetic-mod",
            Provider = provider ?? ApiProviderCatalog.Get("Custom"),
            ProviderSettings = new ApiProviderSettings
            {
                Name = provider?.Name ?? "Synthetic",
                Url = url,
                Model = model,
                Temperature = 0.1
            },
            RequestsPerMinutePerKey = requestsPerMinute,
            InputTokensPerMinutePerKey = 0,
            DailyTokenBudgetPerKey = dailyTokenBudget,
            MaxRetries = maxRetries,
            MaxResponseBytes = maxResponseBytes,
            Timeout = timeout ?? TimeSpan.FromSeconds(2),
            ResponseFormatMode = provider?.ResponseFormat ?? "JsonObject",
            CompletionTokenParameter = provider?.TokenParameter ?? "none",
            ReasoningEffort = reasoningEffort
        };

    private static SourceEntry[] CreateBatch() =>
    [
        new() { Id = "id-1", Key = "Synthetic.One", Kind = "Keyed", Text = "first {0}" },
        new() { Id = "id-2", Key = "Synthetic.Two", Kind = "Keyed", Text = "second [pawn]" }
    ];

    private static void AssertInvalidProviderData(string response, IReadOnlyList<SourceEntry> batch)
    {
        var error = CaptureException<InvalidDataException>(() =>
            TranslationPrompt.ParseResponse(response, batch.Select(entry => entry.Id)));
        Assert(!string.IsNullOrWhiteSpace(error.Message), "Invalid provider data failed without an explicit reason.");
    }

    private static string TranslationContent(params (string Id, string Text)[] values) =>
        JsonSerializer.Serialize(new
        {
            translations = values.Select(value => new { id = value.Id, text = value.Text }).ToArray()
        });

    private static string ResponseWithContent(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content } } }
    });

    private static HttpResponseMessage SuccessResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage StatusResponse(HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent("synthetic provider error", Encoding.UTF8, "text/plain")
        };

    private static HttpResponseMessage ResponseWithRateHeaders(string remaining, string reset)
    {
        var response = SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "rate-header"))));
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining-Requests", remaining);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset-Requests", reset);
        return response;
    }

    private static HttpResponseMessage ResponseWithRetryAfter(
        TimeSpan delay,
        HttpStatusCode status = HttpStatusCode.TooManyRequests)
    {
        var response = StatusResponse(status);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        return response;
    }

    private static SyntheticHttpHandler AssertProviderRetry(
        string label,
        Func<HttpResponseMessage> firstResponse,
        IReadOnlyList<string> keys)
    {
        var handler = new SyntheticHttpHandler(
        [
            firstResponse,
            () => SuccessResponse(ResponseWithContent(TranslationContent(("id-1", "재시도 성공"))))
        ]);
        using var client = CreateClient(handler, keys);
        var result = client.TranslateOpenAiAsync(
                CreateOptions("https://fixture.invalid", 2),
                CreateBatch()[..1],
                "synthetic system prompt",
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(handler.RequestUris.Count == 2 && result["id-1"] == "재시도 성공",
            $"{label} did not retry once and recover without losing the batch result.");
        return handler;
    }

    private static HttpResponseMessage OversizedResponse()
    {
        var content = new StringContent(new string('x', 512), Encoding.UTF8, "application/json");
        content.Headers.ContentLength = null;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class SyntheticHttpHandler(IEnumerable<Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> responses = new(responses);

        public List<Uri> RequestUris { get; } = [];
        public List<string> AuthorizationSchemes { get; } = [];
        public List<string> AuthorizationParameters { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUris.Add(request.RequestUri ?? throw new InvalidOperationException("Synthetic request URI was absent."));
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme ?? string.Empty);
            AuthorizationParameters.Add(request.Headers.Authorization?.Parameter ?? string.Empty);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (responses.Count == 0) throw new InvalidOperationException("Synthetic response queue was exhausted.");
            return responses.Dequeue()();
        }
    }

    private sealed class DroppingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            _ = stream;
            _ = context;
            return Task.FromException(new HttpRequestException("Synthetic response stream dropped."));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CancellationOnlyHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The synthetic timeout request unexpectedly completed.");
        }
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utcNow = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => 1_000;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan duration)
        {
            timestamp += (long)Math.Ceiling(duration.TotalSeconds * TimestampFrequency);
            utcNow += duration;
        }
    }

    private sealed class SplitFailureHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var requestDocument = JsonDocument.Parse(body);
            var user = requestDocument.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var userDocument = JsonDocument.Parse(user);
            var ids = userDocument.RootElement.GetProperty("entries")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetString()!)
                .ToArray();

            if (RequestCount is 2 or 4)
            {
                return SuccessResponse("{\"choices\":[]}");
            }

            return SuccessResponse(ResponseWithContent(TranslationContent(
                ids.Select(id => (id, $"synthetic-{id}")).ToArray())));
        }
    }
}
