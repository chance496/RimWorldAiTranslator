using System.Net;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Utilities;

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
            var cancellationError = CaptureException<TranslationRunCanceledException>(() => runTask.GetAwaiter().GetResult());
            stopwatch.Stop();
            Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "Cancellation did not stop the active request promptly.");
            Assert(cancellationError is OperationCanceledException
                   && cancellationError.CheckpointPersisted
                   && cancellationError.PartialResult.Cancelled
                   && cancellationError.PartialResult.Rows.Count == 7
                   && cancellationError.PartialResult.TranslatedEntries == 2
                   && cancellationError.PartialResult.WrittenFiles.Count == 0,
                "Cancellation did not expose the safe partial review result through the compatible exception contract.");
            Assert(!cancellationError.ToString().Contains("test-key", StringComparison.Ordinal),
                "Cancellation exception exposed an API key.");

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
            Assert(partialRows.Count == 7
                   && partialRows.Count(row => !string.IsNullOrWhiteSpace(row.Candidate)) == 2
                   && partialRows.Count(row => string.IsNullOrWhiteSpace(row.Candidate)) == 5
                   && partialRows.All(row => !string.IsNullOrWhiteSpace(row.Source)),
                "Cancelled checkpoint did not preserve the completed batch together with every untranslated source row.");
            Assert(Path.GetFullPath(comparisonPath).Equals(Path.GetFullPath(cancellationError.PartialResult.ComparisonFile!), StringComparison.OrdinalIgnoreCase),
                "Cancellation result did not identify its persisted comparison checkpoint.");
            var sourceKoreanRoot = Path.Combine(modRoot, "Languages", "Korean");
            Assert(!Directory.Exists(sourceKoreanRoot) || !Directory.EnumerateFiles(sourceKoreanRoot, "*", SearchOption.AllDirectories).Any(), "Cancelled review translation modified the source mod.");

            var partialLanguageRoot = Path.Combine(cancelledRun, "Languages", "Korean");
            var preservePath = Path.Combine(root, "preserved-partial.json");
            var preserve = new
            {
                version = 2,
                languageRoot = partialLanguageRoot,
                items = partialRows
                    .Where(row => !string.IsNullOrWhiteSpace(row.Candidate))
                    .Select(row => new
                    {
                        key = row.Key,
                        kind = row.Kind,
                        defClass = row.DefClass,
                        @namespace = row.Kind == "Keyed" ? "Keyed" : row.DefClass,
                        target = Path.GetRelativePath(partialLanguageRoot, row.Target),
                        text = row.Candidate,
                        origin = "ai",
                        translationUpdatedAt = string.Empty,
                        sourceChanged = false,
                        sourceHash = RecoverySourceHash(row.Source),
                        sourceText = row.Source,
                        previousSourceText = string.Empty
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

            var staleRow = partialRows[0];
            var noHistoryRow = partialRows[1];
            var relativeStaleTarget = Path.GetRelativePath(partialLanguageRoot, staleRow.Target);
            var legacyPreservePath = Path.Combine(root, "preserved-legacy-v1.json");
            var legacyPreserve = new
            {
                version = 1,
                items = new[]
                {
                    new
                    {
                        key = staleRow.Key,
                        kind = staleRow.Kind,
                        defClass = staleRow.DefClass,
                        @namespace = staleRow.Kind == "Keyed" ? "Keyed" : staleRow.DefClass,
                        target = relativeStaleTarget,
                        text = "레거시 번역",
                        origin = "ai"
                    }
                }
            };
            File.WriteAllText(legacyPreservePath, JsonSerializer.Serialize(legacyPreserve), new UTF8Encoding(false));
            var legacyHandler = new DelayedOpenAiHandler(delayRequest: 0);
            var legacyEngine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(keys, legacyHandler));
            var legacyResult = legacyEngine.RunAsync(CreateLoopbackOptions(
                modRoot,
                Path.Combine(root, "reviews-legacy-preserve"),
                legacyPreservePath,
                translateMissingOnly: true)).GetAwaiter().GetResult();
            var legacyWorkspace = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
                .Load(legacyResult.ReviewRoot!);
            var legacyItem = legacyWorkspace.Items.Single(item => item.Row.Key == staleRow.Key);
            Assert(legacyHandler.RequestCount == 4
                   && legacyResult.Rows.All(row => !string.IsNullOrWhiteSpace(row.Candidate))
                   && legacyItem.Decision.SourceChanged
                   && legacyItem.EffectiveStatus == ReviewStatuses.Pending
                   && legacyItem.Row.Existing == "레거시 번역",
                "Legacy v1 preserved data without source evidence was silently reused as current.");

            var stalePreservePath = Path.Combine(root, "preserved-stale.json");
            var stalePreserve = new
            {
                version = 2,
                languageRoot = partialLanguageRoot,
                items = new[]
                {
                    new
                    {
                        key = staleRow.Key,
                        kind = staleRow.Kind,
                        defClass = staleRow.DefClass,
                        @namespace = staleRow.Kind == "Keyed" ? "Keyed" : staleRow.DefClass,
                        target = relativeStaleTarget,
                        text = "과거 번역",
                        origin = "ai",
                        translationUpdatedAt = string.Empty,
                        sourceChanged = true,
                        sourceHash = RecoverySourceHash(staleRow.Source),
                        sourceText = staleRow.Source,
                        previousSourceText = "synthetic previous source"
                    },
                    new
                    {
                        key = noHistoryRow.Key,
                        kind = noHistoryRow.Kind,
                        defClass = noHistoryRow.DefClass,
                        @namespace = noHistoryRow.Kind == "Keyed" ? "Keyed" : noHistoryRow.DefClass,
                        target = Path.GetRelativePath(partialLanguageRoot, noHistoryRow.Target),
                        text = "근거 없는 과거 번역",
                        origin = "ai",
                        translationUpdatedAt = string.Empty,
                        sourceChanged = true,
                        sourceHash = RecoverySourceHash(noHistoryRow.Source),
                        sourceText = noHistoryRow.Source,
                        previousSourceText = string.Empty
                    }
                }
            };
            File.WriteAllText(stalePreservePath, JsonSerializer.Serialize(stalePreserve), new UTF8Encoding(false));
            var staleHandler = new DelayedOpenAiHandler(delayRequest: 0);
            var staleEngine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(keys, staleHandler));
            var staleResult = staleEngine.RunAsync(CreateLoopbackOptions(
                modRoot,
                Path.Combine(root, "reviews-stale-preserve"),
                stalePreservePath,
                translateMissingOnly: true)).GetAwaiter().GetResult();
            var staleWorkspace = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
                .Load(staleResult.ReviewRoot!);
            var staleItem = staleWorkspace.Items.Single(item => item.Row.Key == staleRow.Key);
            var noHistoryItem = staleWorkspace.Items.Single(item => item.Row.Key == noHistoryRow.Key);
            Assert(staleHandler.RequestCount == 4
                   && staleResult.Rows.Count == 7
                   && staleResult.Rows.All(row => !string.IsNullOrWhiteSpace(row.Candidate))
                   && staleItem.Decision.SourceChanged
                   && staleItem.EffectiveStatus == ReviewStatuses.Pending
                   && staleItem.Decision.PreviousSourceText == "synthetic previous source"
                   && staleItem.Row.Existing == "과거 번역"
                   && !string.IsNullOrWhiteSpace(staleItem.Row.Candidate),
                "Missing-only resume lost or promoted a source-changed preserved translation.");
            Assert(noHistoryItem.Decision.SourceChanged
                   && noHistoryItem.EffectiveStatus == ReviewStatuses.Pending
                   && noHistoryItem.Row.ExistingPreviousSourceText == string.Empty
                   && noHistoryItem.Decision.PreviousSourceText == string.Empty,
                "A source change without historical evidence mislabeled the current source as historical.");
            var staleReviewService = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
            staleReviewService.SetStatus(staleWorkspace, staleItem, ReviewStatuses.Approved);
            staleReviewService.SetStatus(staleWorkspace, noHistoryItem, ReviewStatuses.Approved);
            staleReviewService.Save(staleWorkspace);
            var resolvedStaleWorkspace = staleReviewService.Load(staleResult.ReviewRoot!);
            var resolvedStaleItem = resolvedStaleWorkspace.Items.Single(item => item.Row.Key == staleRow.Key);
            var resolvedNoHistoryItem = resolvedStaleWorkspace.Items.Single(item => item.Row.Key == noHistoryRow.Key);
            Assert(resolvedStaleItem.EffectiveStatus == ReviewStatuses.Approved
                   && !resolvedStaleItem.Decision.SourceChanged
                   && resolvedStaleItem.Decision.PreviousSourceText == "synthetic previous source"
                   && resolvedStaleItem.Row.ExistingPreviousSourceText == "synthetic previous source",
                "A resolved preserved source change was demoted again after save and reload.");
            Assert(resolvedNoHistoryItem.EffectiveStatus == ReviewStatuses.Approved
                   && !resolvedNoHistoryItem.Decision.SourceChanged
                   && resolvedNoHistoryItem.Decision.PreviousSourceText == string.Empty,
                "A resolved source change without history was reintroduced or fabricated after reload.");

            var secondPreserveService = new TranslationRunArtifactService(
                Path.Combine(root, "temp-second-preserve"),
                Path.Combine(root, "reviews-stale-preserve"));
            var secondPreservePath = secondPreserveService.CreatePreservedTranslations(resolvedStaleWorkspace);
            var secondRefreshHandler = new DelayedOpenAiHandler(delayRequest: 0);
            var secondRefreshEngine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(keys, secondRefreshHandler));
            var secondRefreshResult = secondRefreshEngine.RunAsync(CreateLoopbackOptions(
                modRoot,
                Path.Combine(root, "reviews-second-preserve-refresh"),
                secondPreservePath,
                translateMissingOnly: true)).GetAwaiter().GetResult();
            var secondRefreshWorkspace = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
                .Load(secondRefreshResult.ReviewRoot!);
            var secondRefreshItem = secondRefreshWorkspace.Items.Single(item => item.Row.Key == staleRow.Key);
            Assert(secondRefreshHandler.RequestCount == 0
                   && secondRefreshItem.Row.Existing == resolvedStaleItem.Decision.Text
                   && secondRefreshItem.Row.ExistingPreviousSourceText == "synthetic previous source"
                   && secondRefreshItem.Decision.PreviousSourceText == "synthetic previous source"
                   && !secondRefreshItem.Decision.SourceChanged,
                "Resolved source history was lost or unnecessarily retranslated on the next refresh.");
            secondPreserveService.DeletePreservedTranslations(secondPreservePath);

            const string capturedSource = "synthetic source before an after-capture change";
            var mismatchPreservePath = Path.Combine(root, "preserved-source-mismatch.json");
            var mismatchPreserve = new
            {
                version = 2,
                languageRoot = partialLanguageRoot,
                items = new[]
                {
                    new
                    {
                        key = staleRow.Key,
                        kind = staleRow.Kind,
                        defClass = staleRow.DefClass,
                        @namespace = staleRow.Kind == "Keyed" ? "Keyed" : staleRow.DefClass,
                        target = relativeStaleTarget,
                        text = "변경 전 번역",
                        origin = "local",
                        translationUpdatedAt = string.Empty,
                        sourceChanged = false,
                        sourceHash = RecoverySourceHash(capturedSource),
                        sourceText = capturedSource,
                        previousSourceText = string.Empty
                    }
                }
            };
            File.WriteAllText(mismatchPreservePath, JsonSerializer.Serialize(mismatchPreserve), new UTF8Encoding(false));
            var mismatchHandler = new DelayedOpenAiHandler(delayRequest: 0);
            var mismatchEngine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(keys, mismatchHandler));
            var mismatchResult = mismatchEngine.RunAsync(CreateLoopbackOptions(
                modRoot,
                Path.Combine(root, "reviews-mismatch-preserve"),
                mismatchPreservePath,
                translateMissingOnly: true)).GetAwaiter().GetResult();
            var mismatchWorkspace = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
                .Load(mismatchResult.ReviewRoot!);
            var mismatchItem = mismatchWorkspace.Items.Single(item => item.Row.Key == staleRow.Key);
            Assert(mismatchHandler.RequestCount == 4
                   && mismatchResult.Rows.All(row => !string.IsNullOrWhiteSpace(row.Candidate))
                   && mismatchItem.Decision.SourceChanged
                   && mismatchItem.EffectiveStatus == ReviewStatuses.Pending
                   && mismatchItem.Decision.PreviousSourceText == capturedSource
                   && mismatchItem.Row.Existing == "변경 전 번역",
                "A post-capture source change was silently reused or lost its prior source history.");

            var invalidPreserveHandler = new DelayedOpenAiHandler(delayRequest: 0);
            var invalidPreserveEngine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(keys, invalidPreserveHandler));
            void AssertRejectedPreserve(string path, string reviewName)
            {
                AssertThrows<InvalidDataException>(() => invalidPreserveEngine.RunAsync(CreateLoopbackOptions(
                    modRoot,
                    Path.Combine(root, reviewName),
                    path,
                    translateMissingOnly: true)).GetAwaiter().GetResult());
            }

            var invalidHashPath = Path.Combine(root, "preserved-invalid-hash.json");
            File.WriteAllText(
                invalidHashPath,
                File.ReadAllText(mismatchPreservePath).Replace(
                    RecoverySourceHash(capturedSource),
                    new string('0', 64),
                    StringComparison.Ordinal),
                new UTF8Encoding(false));
            AssertRejectedPreserve(invalidHashPath, "reviews-invalid-preserve-hash");

            var missingEvidencePath = Path.Combine(root, "preserved-missing-evidence.json");
            File.WriteAllText(
                missingEvidencePath,
                JsonSerializer.Serialize(new
                {
                    version = 2,
                    languageRoot = partialLanguageRoot,
                    items = legacyPreserve.items
                }),
                new UTF8Encoding(false));
            AssertRejectedPreserve(missingEvidencePath, "reviews-missing-preserve-evidence");

            var duplicatePath = Path.Combine(root, "preserved-duplicate.json");
            File.WriteAllText(
                duplicatePath,
                JsonSerializer.Serialize(new
                {
                    version = 2,
                    languageRoot = partialLanguageRoot,
                    items = new[] { mismatchPreserve.items[0], mismatchPreserve.items[0] }
                }),
                new UTF8Encoding(false));
            AssertRejectedPreserve(duplicatePath, "reviews-duplicate-preserve");

            var invalidUtf8Path = Path.Combine(root, "preserved-invalid-utf8.json");
            File.WriteAllBytes(invalidUtf8Path, [0x7B, 0x22, 0xFF, 0x22, 0x3A, 0x31, 0x7D]);
            AssertRejectedPreserve(invalidUtf8Path, "reviews-invalid-utf8-preserve");
            Assert(invalidPreserveHandler.RequestCount == 0,
                "Invalid preserved-translation data reached the provider transport.");

            using var preCancelled = new CancellationTokenSource();
            preCancelled.Cancel();
            var early = CaptureException<TranslationRunCanceledException>(() => cancelledEngine.RunAsync(
                CreateLoopbackOptions(modRoot, Path.Combine(root, "reviews-pre-cancelled")),
                cancellationToken: preCancelled.Token).GetAwaiter().GetResult());
            Assert(early.PartialResult.Cancelled && early.PartialResult.Rows.Count == 0,
                "A cancellation before extraction did not return a safe empty partial result.");
            Assert(!early.ToString().Contains("test-key", StringComparison.Ordinal),
                "Early cancellation exception exposed an API key.");
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

    private static string RecoverySourceHash(string source) =>
        StableIdentity.Sha256(source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));

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
