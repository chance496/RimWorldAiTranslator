using System.Net;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Diagnostics;
using RimWorldAiTranslator.Core.Logging;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void OperationErrorPresentationPrivacy()
    {
        const string credential = "synthetic-ui-secret-7A24B913";
        const string sourceText = "SYNTHETIC_PRIVATE_SOURCE_SENTINEL_91D2";
        const string userName = "synthetic-private-user";
        const string query = "synthetic-query-value-58F7";
        var rawMessage =
            $"Authorization: Bearer {credential}\n"
            + $@"C:\Users\{userName}\Private Mod\Languages\English\Keyed.xml "
            + $"https://provider.invalid/v1/translate?api_key={query}&q={sourceText}";
        var exception = CaptureException<InvalidOperationException>(() => ThrowOperationError(rawMessage));

        var userDetail = OperationErrorPresentation.CreateUserDetail(exception);
        var logMessage = OperationErrorPresentation.CreateLogMessage("synthetic operation", exception);
        var forbidden = new[]
        {
            credential,
            sourceText,
            userName,
            query,
            "Authorization",
            "provider.invalid",
            @"C:\Users"
        };

        Assert(userDetail.Length <= OperationErrorPresentation.MaximumUserDetailLength,
            "Operation error UI detail exceeded its fixed length bound.");
        Assert(logMessage.Length <= OperationErrorPresentation.MaximumLogMessageLength,
            "Operation error log summary exceeded its fixed length bound.");
        Assert(forbidden.All(value => !userDetail.Contains(value, StringComparison.OrdinalIgnoreCase)),
            "Operation error UI detail exposed raw exception content.");
        Assert(forbidden.All(value => !logMessage.Contains(value, StringComparison.OrdinalIgnoreCase)),
            "Operation error log summary exposed raw exception content.");
        Assert(!userDetail.Contains(nameof(InvalidOperationException), StringComparison.Ordinal)
               && userDetail.Contains("진단 기록", StringComparison.Ordinal),
            "Operation error UI detail exposed an internal exception name or omitted user guidance.");
        Assert(logMessage.Contains(nameof(InvalidOperationException), StringComparison.Ordinal)
               && logMessage.Contains("HResult=0x", StringComparison.Ordinal)
               && logMessage.Contains("Stack=", StringComparison.Ordinal)
               && logMessage.Contains(nameof(ThrowOperationError), StringComparison.Ordinal),
            "Operation error log summary omitted its safe diagnostic fields or method-only stack trace.");

        var sink = new StringWriter();
        using (var logger = new AppLogger(sink))
            logger.Error(logMessage);
        var persisted = sink.ToString();
        Assert(persisted.Contains("synthetic operation", StringComparison.Ordinal)
               && persisted.Contains(nameof(InvalidOperationException), StringComparison.Ordinal),
            "The safe operation error summary did not pass through AppLogger.");
        Assert(forbidden.All(value => !persisted.Contains(value, StringComparison.OrdinalIgnoreCase)),
            "AppLogger output exposed raw operation exception content.");

        var longTitle = new string('x', 2_000);
        var boundedLogMessage = OperationErrorPresentation.CreateLogMessage(longTitle, exception);
        Assert(boundedLogMessage.Length <= OperationErrorPresentation.MaximumLogMessageLength,
            "A long operation title bypassed the log message bound.");

        var unknownIo = OperationErrorPresentation.CreateUserDetail(new IOException("synthetic unknown I/O failure"));
        Assert(!unknownIo.Contains("자동으로 되돌", StringComparison.Ordinal),
            "A generic I/O failure was falsely presented as a completed rollback.");

        var invalidDataDetail = OperationErrorPresentation.CreateUserDetail(
            new InvalidDataException(@"synthetic corrupt input at C:\Users\private\review-decisions.json"));
        Assert(invalidDataDetail.Contains("작업 데이터", StringComparison.Ordinal)
               && !invalidDataDetail.Contains(nameof(InvalidDataException), StringComparison.Ordinal)
               && !invalidDataDetail.Contains(@"C:\Users", StringComparison.OrdinalIgnoreCase),
            "Invalid data UI guidance exposed an internal exception name or file path.");

        const string rollbackSecret = "SYNTHETIC_ROLLBACK_PRIVATE_SENTINEL";
        var incomplete = new IOException($"fixture rollback was incomplete {rollbackSecret}");
        var incompleteDetail = OperationErrorPresentation.CreateUserDetail(incomplete);
        var incompleteLog = OperationErrorPresentation.CreateLogMessage("synthetic rollback", incomplete);
        Assert(incompleteDetail.Contains("자동 복구를 완료하지 못했습니다", StringComparison.Ordinal)
               && incompleteLog.Contains("Outcome=RollbackIncomplete", StringComparison.Ordinal)
               && !incompleteDetail.Contains(rollbackSecret, StringComparison.Ordinal)
               && !incompleteLog.Contains(rollbackSecret, StringComparison.Ordinal),
            "Incomplete rollback guidance was inaccurate or exposed raw exception content.");

        var concurrentPath = Path.Combine(Path.GetTempPath(), "synthetic-concurrent-recovery.txt");
        var concurrent = new ConcurrentLeafChangeException("synthetic concurrent detail", concurrentPath);
        var concurrentDetail = OperationErrorPresentation.CreateUserDetail(concurrent);
        var concurrentLog = OperationErrorPresentation.CreateLogMessage("synthetic concurrent", concurrent);
        Assert(concurrentDetail.Contains("덮어쓰지 않았습니다", StringComparison.Ordinal)
               && concurrentLog.Contains("Outcome=ConcurrentChange", StringComparison.Ordinal)
               && !concurrentDetail.Contains(concurrentPath, StringComparison.OrdinalIgnoreCase)
               && !concurrentLog.Contains(concurrentPath, StringComparison.OrdinalIgnoreCase),
            "Concurrent-save guidance was inaccurate or exposed a recovery path.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowOperationError(string message) => throw new InvalidOperationException(message);

    private static void TranslationProgressPrivacy()
    {
        const string credential = "synthetic-progress-secret-1D45C8";
        const string sourceText = "SYNTHETIC_PRIVATE_SOURCE_SENTINEL_91D2";
        const string userName = "synthetic-private-user";
        const string query = "synthetic-query-value-58F7";
        var rawMessage =
            $"Authorization: Bearer {credential}\n"
            + $@"C:\Users\{userName}\Private Mod\Languages\English\Keyed.xml "
            + $"https://provider.invalid/v1/translate?api_key={query}&q={sourceText}";
        var forbidden = new[]
        {
            credential,
            sourceText,
            userName,
            query,
            "Authorization",
            "provider.invalid",
            @"C:\Users"
        };

        var safeHostileProgress = OperationErrorPresentation.CreateSafeProgress(
            new TranslationProgress("retry-" + sourceText, rawMessage, -1, -2, true));
        Assert(safeHostileProgress.Stage == "progress"
               && safeHostileProgress.Message.Length <= OperationErrorPresentation.MaximumProgressMessageLength,
            "Hostile progress metadata was not normalized and bounded.");
        Assert(forbidden.All(value => !safeHostileProgress.Message.Contains(value, StringComparison.OrdinalIgnoreCase)
                                      && !safeHostileProgress.Stage.Contains(value, StringComparison.OrdinalIgnoreCase)),
            "The progress presentation exposed hostile stage or message content.");

        var safeRateLimit = OperationErrorPresentation.CreateSafeProgress(
            new TranslationProgress("rate-limit", "Waiting 11.7s for provider rate limit."));
        Assert(safeRateLimit.Message == "Waiting 11.7s for the provider rate limit.",
            "A safe rate-limit wait duration was hidden from the activity log.");

        WithTempRoot(root =>
        {
            var apiProgress = new List<TranslationProgress>();
            using (var client = new TranslationApiClient(
                       [credential],
                       new ThrowingProgressHandler(rawMessage),
                       static (_, _) => Task.CompletedTask))
            {
                var options = new TranslationEngineOptions
                {
                    ModRoot = root,
                    ApiKeys = [credential],
                    Provider = ApiProviderCatalog.Get("Custom"),
                    ProviderSettings = new ApiProviderSettings
                    {
                        Name = "Synthetic",
                        Url = "http://127.0.0.1:12345/v1/chat/completions",
                        Model = "synthetic-model",
                        Temperature = 0
                    },
                    AllowInsecureLoopback = true,
                    MaxRetries = 2,
                    RequestsPerMinutePerKey = 0,
                    InputTokensPerMinutePerKey = 0,
                    DailyTokenBudgetPerKey = 0,
                    Timeout = TimeSpan.FromSeconds(1)
                };
                var error = CaptureException<InvalidOperationException>(() => client.TranslateOpenAiAsync(
                    options,
                    [new SourceEntry { Id = "one", Key = "Fixture.key", Text = sourceText, Kind = "Keyed", TypeName = "Keyed" }],
                    "synthetic system prompt",
                    new ProgressCollector(apiProgress),
                    CancellationToken.None).GetAwaiter().GetResult());
                Assert(forbidden.All(value => !error.Message.Contains(value, StringComparison.OrdinalIgnoreCase)),
                    "The provider failure wrapper exposed raw transport exception content.");
            }

            Assert(apiProgress.Count(item => item.Stage == "retry") == 1,
                "The synthetic transport failure did not exercise retry progress.");
            Assert(apiProgress.All(item => forbidden.All(value =>
                       !item.Message.Contains(value, StringComparison.OrdinalIgnoreCase)
                       && !item.Stage.Contains(value, StringComparison.OrdinalIgnoreCase))),
                "API retry progress exposed an exception, credential, path, URI, query, or source sentinel.");

            var extractor = CreateExtractor();
            var privateRoot = Path.Combine(root, userName, sourceText);
            Directory.CreateDirectory(privateRoot);
            File.WriteAllText(Path.Combine(privateRoot, "LoadFolders.xml"), $"<loadFolders><v1.5><li>{sourceText}");
            var extractionWarnings = new List<string>();
            extractor.GetActiveContentRoots(privateRoot, extractionWarnings);

            var existingRoot = Path.Combine(privateRoot, "existing", "Keyed");
            Directory.CreateDirectory(existingRoot);
            File.WriteAllText(Path.Combine(existingRoot, sourceText + ".xml"), $"<LanguageData><Broken>{sourceText}");
            File.WriteAllText(Path.Combine(existingRoot, "duplicate-one.xml"),
                $"<LanguageData><{sourceText}>one</{sourceText}></LanguageData>");
            File.WriteAllText(Path.Combine(existingRoot, "duplicate-two.xml"),
                $"<LanguageData><{sourceText}>two</{sourceText}></LanguageData>");
            extractor.ReadExistingLanguageMap(Path.GetDirectoryName(existingRoot)!, extractionWarnings);

            var brokenMod = Path.Combine(privateRoot, "broken-mod");
            var keyed = Path.Combine(brokenMod, "Languages", "English", "Keyed");
            var defs = Path.Combine(brokenMod, "Defs");
            Directory.CreateDirectory(keyed);
            Directory.CreateDirectory(defs);
            File.WriteAllText(Path.Combine(keyed, sourceText + ".xml"), $"<LanguageData><Broken>{sourceText}");
            File.WriteAllText(Path.Combine(defs, sourceText + ".xml"), $"<Defs><Broken>{sourceText}");
            extractionWarnings.AddRange(extractor.Extract(brokenMod, "English").Warnings);
            Assert(extractionWarnings.Count >= 5,
                "The synthetic extractor fixture did not exercise every warning category.");
            Assert(extractionWarnings.All(warning => forbidden.All(value =>
                       !warning.Contains(value, StringComparison.OrdinalIgnoreCase))),
                "Source extraction warnings exposed a path, localization identity, exception message, or source sentinel.");

            var fixtureRoot = Path.Combine(privateRoot, "source-progress-mod");
            CopyDirectory(Path.Combine(RepositoryRoot(), "testdata", "SampleMod"), fixtureRoot);
            var sourceFolder = "Private-" + sourceText;
            Directory.Move(
                Path.Combine(fixtureRoot, "Languages", "English"),
                Path.Combine(fixtureRoot, "Languages", sourceFolder));
            var engineProgress = new List<TranslationProgress>();
            var engine = new TranslationEngine(RepositoryRoot(), extractor);
            engine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = fixtureRoot,
                SourceOnly = true,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(root, "reviews"),
                SourceLanguageFolder = sourceFolder
            }, new ProgressCollector(engineProgress)).GetAwaiter().GetResult();
            Assert(engineProgress.Any(item => item.Stage == "initialize")
                   && engineProgress.Any(item => item.Stage == "source"),
                "The synthetic source-only run did not exercise initialization and source progress.");
            Assert(engineProgress.All(item => forbidden.All(value =>
                       !item.Message.Contains(value, StringComparison.OrdinalIgnoreCase)
                       && !item.Stage.Contains(value, StringComparison.OrdinalIgnoreCase))),
                "Translation engine progress exposed the mod path, source folder, or source sentinel.");

            var googleProgress = new List<TranslationProgress>();
            var googleEngine = new TranslationEngine(
                RepositoryRoot(),
                extractor,
                apiClientFactory: keys => new TranslationApiClient(
                    keys,
                    new ThrowingProgressHandler(rawMessage),
                    static (_, _) => Task.CompletedTask));
            googleEngine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = fixtureRoot,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(root, "google-reviews"),
                SourceLanguageFolder = sourceFolder,
                ApiKeys = [],
                Provider = ApiProviderCatalog.Get("Google"),
                ProviderSettings = new ApiProviderSettings { Name = "Google" },
                MaxRetries = 1,
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                Timeout = TimeSpan.FromSeconds(1),
                GeneratedGlossaryPath = Path.Combine(RepositoryRoot(), "src", "RimWorldAiTranslator.App", "Assets", "glossary.generated.ko.json")
            }, new ProgressCollector(googleProgress)).GetAwaiter().GetResult();
            Assert(googleProgress.Any(item => item.Stage == "warning")
                   && googleProgress.All(item => forbidden.All(value =>
                       !item.Message.Contains(value, StringComparison.OrdinalIgnoreCase))),
                "Google fallback progress exposed its raw transport exception or source value.");

            var splitProgress = new List<TranslationProgress>();
            var splitEngine = new TranslationEngine(
                RepositoryRoot(),
                extractor,
                apiClientFactory: keys => new TranslationApiClient(
                    keys,
                    new ThrowingProgressHandler(rawMessage),
                    static (_, _) => Task.CompletedTask));
            var splitError = CaptureException<InvalidOperationException>(() => splitEngine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = fixtureRoot,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(root, "split-reviews"),
                SourceLanguageFolder = sourceFolder,
                ApiKeys = [credential],
                Provider = ApiProviderCatalog.Get("Custom"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Synthetic",
                    Url = "http://127.0.0.1:12345/v1/chat/completions",
                    Model = "synthetic-model",
                    Temperature = 0
                },
                AllowInsecureLoopback = true,
                BatchSize = 2,
                MaxRetries = 1,
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                Timeout = TimeSpan.FromSeconds(1),
                GeneratedGlossaryPath = Path.Combine(RepositoryRoot(), "src", "RimWorldAiTranslator.App", "Assets", "glossary.generated.ko.json")
            }, new ProgressCollector(splitProgress)).GetAwaiter().GetResult());
            Assert(!splitError.Message.Contains(rawMessage, StringComparison.Ordinal)
                   && splitProgress.All(item => item.Stage != "retry")
                   && splitProgress.All(item => forbidden.All(value =>
                        !item.Message.Contains(value, StringComparison.OrdinalIgnoreCase))),
                "A transport failure was split as a data-shape failure or exposed raw provider content.");

            var logRoot = Path.Combine(root, "logs");
            using (var logger = new AppLogger(logRoot))
            {
                foreach (var item in apiProgress
                             .Concat(engineProgress)
                             .Concat(googleProgress)
                             .Concat(splitProgress)
                             .Where(item => item.IsWarning))
                    logger.Warning(OperationErrorPresentation.CreateSafeProgress(item).Message);
                logger.Warning(safeHostileProgress.Message);
            }
            var persisted = string.Join("\n", Directory.EnumerateFiles(logRoot, "*.log").Select(File.ReadAllText));
            Assert(forbidden.All(value => !persisted.Contains(value, StringComparison.OrdinalIgnoreCase)),
                "Persistent progress logging exposed a credential, path, URI, query, or source sentinel.");
        });
    }

    private sealed class ThrowingProgressHandler(string rawMessage) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(rawMessage));
    }
}
