using System.Net;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void TranslationCancellationAndResume()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var root = Directory.GetParent(modRoot)!.FullName;
            var cancelledReviewBase = Path.Combine(root, "reviews-cancelled");
            var delayed = new DelayedOpenAiHandler(delayRequest: 2);
            var cancelledEngine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(keys, delayed));
            using var cancellation = new CancellationTokenSource();
            var runTask = cancelledEngine.RunAsync(CreateLoopbackOptions(modRoot, cancelledReviewBase), cancellationToken: cancellation.Token);
            Assert(delayed.DelayedRequestStarted.Task.Wait(TimeSpan.FromSeconds(10)), "Translation did not reach the delayed second batch.");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            cancellation.Cancel();
            AssertThrows<OperationCanceledException>(() => runTask.GetAwaiter().GetResult());
            stopwatch.Stop();
            Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "Cancellation did not stop the active request promptly.");

            var cancelledRun = Directory.EnumerateDirectories(cancelledReviewBase).Single();
            var auditRoot = Path.Combine(cancelledRun, "_TranslationAudit");
            var progressPath = Directory.EnumerateFiles(auditRoot, "*-progress.json").Single();
            using (var progressJson = JsonDocument.Parse(File.ReadAllText(progressPath)))
            {
                var progress = progressJson.RootElement[0];
                Assert(!progress.GetProperty("complete").GetBoolean(), "Cancelled checkpoint was marked complete.");
                Assert(progress.GetProperty("completedBatches").GetInt32() == 1, "Cancelled checkpoint lost its completed batch count.");
            }
            var comparisonPath = Directory.EnumerateFiles(auditRoot, "*-comparison.json").Single();
            var partialRows = JsonSerializer.Deserialize<List<ReviewComparisonRow>>(File.ReadAllText(comparisonPath))!;
            Assert(partialRows.Count == 2 && partialRows.All(row => !string.IsNullOrWhiteSpace(row.Candidate)), "Cancelled checkpoint did not preserve exactly one translated batch.");
            var sourceKoreanRoot = Path.Combine(modRoot, "Languages", "Korean");
            Assert(!Directory.Exists(sourceKoreanRoot) || !Directory.EnumerateFiles(sourceKoreanRoot, "*", SearchOption.AllDirectories).Any(), "Cancelled review translation modified the source mod.");

            var partialLanguageRoot = Path.Combine(cancelledRun, "Languages", "Korean");
            var preservePath = Path.Combine(root, "preserved-partial.json");
            var preserve = new
            {
                version = 1,
                items = partialRows.Select(row => new
                {
                    key = row.Key,
                    kind = row.Kind,
                    defClass = row.DefClass,
                    @namespace = row.Kind == "Keyed" ? "Keyed" : row.DefClass,
                    target = Path.GetRelativePath(partialLanguageRoot, row.Target),
                    text = row.Candidate,
                    origin = "ai"
                }).ToArray()
            };
            File.WriteAllText(preservePath, JsonSerializer.Serialize(preserve), new UTF8Encoding(false));

            var resumedHandler = new DelayedOpenAiHandler(delayRequest: 0);
            var resumedEngine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(keys, resumedHandler));
            var resumed = resumedEngine.RunAsync(CreateLoopbackOptions(
                modRoot,
                Path.Combine(root, "reviews-resumed"),
                preservePath,
                translateMissingOnly: true)).GetAwaiter().GetResult();
            Assert(resumedHandler.RequestCount == 3, "Resume retranslated preserved entries or skipped missing entries.");
            Assert(resumed.Rows.Count == 7, "Resumed translation lost rows.");
            Assert(resumed.Rows.Count(row => string.IsNullOrWhiteSpace(row.Candidate) && !string.IsNullOrWhiteSpace(row.Existing)) == 2, "Resumed translation did not reuse completed candidates.");
            Assert(resumed.Rows.Count(row => !string.IsNullOrWhiteSpace(row.Candidate)) == 5, "Resumed translation did not translate only missing rows.");
        });
    }

    private static TranslationEngineOptions CreateLoopbackOptions(
        string modRoot,
        string reviewRoot,
        string preservePath = "",
        bool translateMissingOnly = false) => new()
    {
        ModRoot = modRoot,
        ApiKeys = ["test-key"],
        SourceLanguageFolder = "English",
        ReviewOnly = true,
        ReviewRoot = reviewRoot,
        BatchSize = 2,
        MaxInputCharactersPerBatch = 100_000,
        MaxInputTokensPerBatch = 100_000,
        RequestsPerMinutePerKey = 0,
        InputTokensPerMinutePerKey = 0,
        DailyTokenBudgetPerKey = 0,
        MaxRetries = 1,
        Timeout = TimeSpan.FromSeconds(30),
        AllowInsecureLoopback = true,
        PreserveTranslationFile = preservePath,
        TranslateMissingOnly = translateMissingOnly,
        Provider = ApiProviderCatalog.Get("Custom"),
        ProviderSettings = new ApiProviderSettings
        {
            Name = "Loopback Fixture",
            Url = "http://127.0.0.1:12345/v1/chat/completions",
            Model = "fixture-model",
            Temperature = 0.1
        }
    };

    private sealed class DelayedOpenAiHandler(int delayRequest) : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => requestCount;
        public TaskCompletionSource<bool> DelayedRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref requestCount);
            if (delayRequest > 0 && current == delayRequest)
            {
                DelayedRequestStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var requestJson = JsonDocument.Parse(body);
            var userPayload = requestJson.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var userJson = JsonDocument.Parse(userPayload);
            var translations = userJson.RootElement.GetProperty("entries").EnumerateArray()
                .Select(entry => new
                {
                    id = entry.GetProperty("id").GetString(),
                    text = "번역 " + entry.GetProperty("text").GetString()
                }).ToArray();
            var content = JsonSerializer.Serialize(new { translations });
            var response = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content } } },
                usage = new { prompt_tokens = 100, total_tokens = 150 }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
