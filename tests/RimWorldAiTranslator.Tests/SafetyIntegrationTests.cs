using RimWorldAiTranslator.Core.Logging;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Translation;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void SecurityLogRedaction()
    {
        WithTempRoot(root =>
        {
            const string drivePath = @"C:\Users\private-user\RimWorld Project\secret.txt";
            const string uncPath = @"\\private-server\private-share\User Data\settings.json";
            const string basicCredential = "synthetic-basic-credential";
            const string bearerCredential = "synthetic-bearer-credential";
            const string jsonCredential = "synthetic-json-credential with spaces";
            const string queryCredential = "synthetic-query-credential";
            const string headerCredential = "synthetic-header-credential";
            const string accessCredential = "synthetic-access-credential";
            var structuredCredentials =
                $"Authorization: Basic {basicCredential}; "
                + $"Bearer {bearerCredential}; "
                + $"\"api_key\": \"{jsonCredential}\", "
                + $"https://provider.invalid/v1?api_key={queryCredential}&mode=safe; "
                + $"X-API-Key: {headerCredential}; access_token={accessCredential}&scope=translate";
            var subscriberLines = new List<string>();
            var logger = new AppLogger(root);
            using (logger)
            {
                logger.MessageWritten += (_, line) => subscriberLines.Add(line);
                logger.Error($"Authorization=secret-value API key: second-secret Bearer abcdefgh csk-1234567890 sk-abcdefghijk at '{drivePath}' and \"{uncPath}\"");
                logger.Error(structuredCredentials);
                logger.MessageWritten += (_, _) => throw new InvalidOperationException("synthetic subscriber failure");
                logger.Info("subscriber-failure-isolated");
            }
            logger.Info("post-dispose-write-is-a-no-op");
            var text = File.ReadAllText(Directory.EnumerateFiles(root, "*.log").Single());
            Assert(!text.Contains("secret-value") && !text.Contains("second-secret") && !text.Contains("csk-1234567890") && !text.Contains("sk-abcdefghijk"), "Logger retained a credential.");
            Assert(!text.Contains("private-user") && !text.Contains("private-server") && !text.Contains("private-share"), "Logger retained an absolute Windows path.");
            Assert(text.Contains("[REDACTED]"), "Logger did not mark redacted data.");
            Assert(text.Contains("[PATH REDACTED]"), "Logger did not mark redacted paths.");
            Assert(text.Contains("subscriber-failure-isolated", StringComparison.Ordinal), "A subscriber failure blocked persistent logging.");
            Assert(!text.Contains("post-dispose-write-is-a-no-op", StringComparison.Ordinal), "Logger wrote after disposal.");
            Assert(
                !new[] { basicCredential, bearerCredential, jsonCredential, queryCredential, headerCredential, accessCredential }
                    .Any(credential => text.Contains(credential, StringComparison.Ordinal)),
                "Persistent logging retained a structured credential variant.");
            Assert(
                subscriberLines.Count >= 2
                && subscriberLines.Any(line => line.Contains("\"api_key\": \"[REDACTED]\"", StringComparison.Ordinal))
                && !new[] { basicCredential, bearerCredential, jsonCredential, queryCredential, headerCredential, accessCredential }
                    .Any(credential => subscriberLines.Any(line => line.Contains(credential, StringComparison.Ordinal))),
                "MessageWritten exposed an unredacted structured credential.");

            var unquoted = AppLogger.Redact(@"I/O failure at C:\Users\another-user\private.xml");
            Assert(!unquoted.Contains("another-user") && unquoted.Contains("[PATH REDACTED]"), "Logger retained an unquoted drive path.");

            var variants = AppLogger.Redact(structuredCredentials);
            Assert(
                !variants.Contains(basicCredential, StringComparison.Ordinal)
                && !variants.Contains(jsonCredential, StringComparison.Ordinal)
                && !variants.Contains(queryCredential, StringComparison.Ordinal)
                && !variants.Contains(headerCredential, StringComparison.Ordinal)
                && !variants.Contains(accessCredential, StringComparison.Ordinal),
                "Logger retained a header, JSON, or query credential variant.");
            Assert(
                variants.Contains("\"api_key\": \"[REDACTED]\"", StringComparison.Ordinal)
                && variants.Contains("mode=safe", StringComparison.Ordinal)
                && variants.Contains("scope=translate", StringComparison.Ordinal),
                "Credential redaction removed unrelated structured fields.");

            const string ampersandHead = "synthetic-ampersand-head";
            const string ampersandTail = "synthetic-ampersand-tail";
            var ampersandCredential = AppLogger.Redact(
                $"password={ampersandHead}&{ampersandTail} Authorization: Basic {ampersandHead}&{ampersandTail}");
            Assert(!ampersandCredential.Contains(ampersandHead, StringComparison.Ordinal)
                   && !ampersandCredential.Contains(ampersandTail, StringComparison.Ordinal),
                "Credential redaction exposed an ambiguous ampersand-delimited credential tail.");
            var leadingAmpersandCredential = AppLogger.Redact(
                $"password=&{ampersandTail} Authorization: Basic &{ampersandTail}");
            Assert(!leadingAmpersandCredential.Contains(ampersandTail, StringComparison.Ordinal),
                "Credential redaction exposed a leading-ampersand credential value.");

            const string ordinary = "The token bucket is empty; authorization failed; rotate the API key tomorrow; keep the secret garden label.";
            Assert(
                AppLogger.Redact(ordinary).Equals(ordinary, StringComparison.Ordinal),
                "Credential redaction changed ordinary non-credential text.");

            var blockedLogRoot = Path.Combine(root, "blocked-log-root");
            File.WriteAllText(blockedLogRoot, "synthetic blocker");
            using var unavailableLogger = new AppLogger(blockedLogRoot);
            unavailableLogger.Warning("persistent logging unavailable");

            var hardLinkLogRoot = Path.Combine(root, "hardlink-logs");
            Directory.CreateDirectory(hardLinkLogRoot);
            var hardLinkSentinel = Path.Combine(root, "hardlink-sentinel.txt");
            File.WriteAllText(hardLinkSentinel, "hardlink-sentinel", new UTF8Encoding(false));
            var hardLinkLogPath = Path.Combine(
                hardLinkLogRoot,
                $"RimWorldAiTranslator-{DateTime.Now:yyyyMMdd}.log");
            CreateHardLinkOrThrow(hardLinkLogPath, hardLinkSentinel);
            using (var hardLinkLogger = new AppLogger(hardLinkLogRoot))
                hardLinkLogger.Error("must-not-touch-hardlink-sentinel");
            Assert(File.ReadAllText(hardLinkSentinel, Encoding.UTF8) == "hardlink-sentinel",
                "Persistent logging wrote through a prepositioned hard link.");

            var reparseLogRoot = Path.Combine(root, "reparse-logs");
            Directory.CreateDirectory(reparseLogRoot);
            var reparseSentinel = Path.Combine(root, "reparse-sentinel.txt");
            File.WriteAllText(reparseSentinel, "reparse-sentinel", new UTF8Encoding(false));
            var reparseLogPath = Path.Combine(
                reparseLogRoot,
                $"RimWorldAiTranslator-{DateTime.Now:yyyyMMdd}.log");
            var reparseFixtureCreated = false;
            try
            {
                File.CreateSymbolicLink(reparseLogPath, reparseSentinel);
                reparseFixtureCreated = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[INFO] Log reparse fixture unavailable ({exception.GetType().Name}).");
            }
            if (reparseFixtureCreated)
            {
                using (var reparseLogger = new AppLogger(reparseLogRoot))
                    reparseLogger.Error("must-not-touch-reparse-sentinel");
                Assert(File.ReadAllText(reparseSentinel, Encoding.UTF8) == "reparse-sentinel",
                    "Persistent logging wrote through a prepositioned file reparse point.");
            }
        });

        var slowWriter = new BlockingTextWriter();
        var slowLogger = new AppLogger(slowWriter, queueCapacity: 8);
        var notificationThread = -1;
        var notifications = new List<string>();
        slowLogger.MessageWritten += (_, line) =>
        {
            notificationThread = Environment.CurrentManagedThreadId;
            notifications.Add(line);
        };
        try
        {
            var callerThread = Environment.CurrentManagedThreadId;
            slowLogger.Info("ordered-first");
            Assert(notificationThread == callerThread && notifications.Count == 1, "MessageWritten moved off its calling thread or was deferred.");
            Assert(slowWriter.WriteEntered.Wait(TimeSpan.FromSeconds(5)), "Background log drain did not start.");
            var stopwatch = Stopwatch.StartNew();
            slowLogger.Info("ordered-second");
            stopwatch.Stop();
            Assert(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), "A log call waited for slow filesystem I/O.");
            Assert(notifications.Count == 2
                && notifications[0].Contains("ordered-first", StringComparison.Ordinal)
                && notifications[1].Contains("ordered-second", StringComparison.Ordinal), "MessageWritten changed notification order.");
            var disposal = Task.Run(slowLogger.Dispose);
            Assert(!disposal.Wait(TimeSpan.FromMilliseconds(100)), "Logger disposal did not wait for its accepted writes to drain.");
            slowWriter.ReleaseWrite.Set();
            Assert(disposal.Wait(TimeSpan.FromSeconds(5)), "Logger disposal did not finish after the sink resumed.");
            Assert(slowWriter.Lines.Count == 2, "Logger disposal lost an accepted message.");
            Assert(slowWriter.Lines[0].Contains("ordered-first", StringComparison.Ordinal)
                && slowWriter.Lines[1].Contains("ordered-second", StringComparison.Ordinal), "Background logging changed message order.");
        }
        finally
        {
            TryRelease(slowWriter);
            slowLogger.Dispose();
        }

        var boundedWriter = new BlockingTextWriter();
        var boundedLogger = new AppLogger(boundedWriter, queueCapacity: 1);
        try
        {
            boundedLogger.Info("bounded-first");
            Assert(boundedWriter.WriteEntered.Wait(TimeSpan.FromSeconds(5)), "Bounded log drain did not start.");
            boundedLogger.Info("bounded-second");
            boundedLogger.Info("bounded-overflow");
            boundedWriter.ReleaseWrite.Set();
            boundedLogger.Dispose();
            Assert(boundedWriter.Lines.Count == 3, "Bounded logging did not retain its accepted entries and omission summary.");
            Assert(!boundedWriter.Lines.Any(line => line.Contains("bounded-overflow", StringComparison.Ordinal)), "A full log queue exceeded its configured bound.");
            Assert(boundedWriter.Lines[^1].Contains("1 message(s) omitted", StringComparison.Ordinal), "A bounded log omission was not recorded.");
        }
        finally
        {
            TryRelease(boundedWriter);
            boundedLogger.Dispose();
        }

        var stalledWriter = new BlockingTextWriter();
        var stalledLogger = new AppLogger(
            stalledWriter,
            queueCapacity: 1,
            disposeDrainTimeout: TimeSpan.FromMilliseconds(100));
        try
        {
            stalledLogger.Info("stalled-sink");
            Assert(stalledWriter.WriteEntered.Wait(TimeSpan.FromSeconds(5)), "Stalled log drain did not start.");
            var stopwatch = Stopwatch.StartNew();
            stalledLogger.Dispose();
            stopwatch.Stop();
            Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "A stalled log sink blocked disposal without a bound.");
            Assert(!stalledLogger.DrainCompletionForTesting.IsCompleted, "The stalled sink unexpectedly completed before deferred drain verification.");
            stalledLogger.Info("rejected-after-timeout");
            stalledWriter.ReleaseWrite.Set();
            Assert(stalledLogger.DrainCompletionForTesting.Wait(TimeSpan.FromSeconds(5)), "Deferred log drain did not finish after the sink resumed.");
            Assert(stalledWriter.Lines.Count == 1
                && !stalledWriter.Lines[0].Contains("rejected-after-timeout", StringComparison.Ordinal), "A late write entered a logger after bounded disposal.");
        }
        finally
        {
            TryRelease(stalledWriter);
            stalledLogger.Dispose();
        }

        var failingWriter = new FailingTextWriter();
        var failingLogger = new AppLogger(failingWriter, queueCapacity: 2);
        var successfulNotifications = 0;
        failingLogger.MessageWritten += (_, _) => throw new InvalidOperationException("synthetic subscriber failure");
        failingLogger.MessageWritten += (_, _) => successfulNotifications++;
        failingLogger.Warning("sink-failure-isolated");
        failingLogger.Dispose();
        failingLogger.Warning("late-write");
        Assert(failingWriter.WriteAttempts == 1, "A sink failure escaped isolation or a late write reached the sink.");
        Assert(successfulNotifications == 1, "A failing subscriber blocked another subscriber or a late write was announced.");

        var raceWriter = new CapturingTextWriter();
        var raceLogger = new AppLogger(
            raceWriter,
            queueCapacity: 2,
            disposeDrainTimeout: TimeSpan.FromMilliseconds(250));
        using var notificationEntered = new ManualResetEventSlim(false);
        using var releaseNotification = new ManualResetEventSlim(false);
        var raceNotifications = 0;
        Task? inFlightWrite = null;
        raceLogger.MessageWritten += (_, _) =>
        {
            Interlocked.Increment(ref raceNotifications);
            notificationEntered.Set();
            releaseNotification.Wait(TimeSpan.FromSeconds(10));
        };
        try
        {
            inFlightWrite = Task.Run(() => raceLogger.Info("accepted-before-dispose"));
            Assert(notificationEntered.Wait(TimeSpan.FromSeconds(5)), "Concurrent write did not reach its subscriber.");
            var stopwatch = Stopwatch.StartNew();
            raceLogger.Dispose();
            stopwatch.Stop();
            Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "A concurrent subscriber blocked logger disposal indefinitely.");
            raceLogger.Info("rejected-after-dispose-race");
            Assert(raceNotifications == 1, "A write that began after disposal reached subscribers.");
            releaseNotification.Set();
            Assert(inFlightWrite.Wait(TimeSpan.FromSeconds(5)), "The accepted concurrent write did not finish after its subscriber resumed.");
            Assert(raceWriter.Lines.Count == 1
                && raceWriter.Lines[0].Contains("accepted-before-dispose", StringComparison.Ordinal), "Dispose lost an accepted concurrent log entry.");
        }
        finally
        {
            releaseNotification.Set();
            inFlightWrite?.Wait(TimeSpan.FromSeconds(5));
            raceLogger.Dispose();
        }
    }

    private static void TryRelease(BlockingTextWriter writer)
    {
        try { writer.ReleaseWrite.Set(); }
        catch (ObjectDisposedException) { }
    }

    private sealed class BlockingTextWriter : TextWriter
    {
        private readonly ConcurrentQueue<string> lines = new();

        public override Encoding Encoding => Encoding.UTF8;
        internal ManualResetEventSlim WriteEntered { get; } = new(false);
        internal ManualResetEventSlim ReleaseWrite { get; } = new(false);
        internal IReadOnlyList<string> Lines => lines.ToArray();

        public override void WriteLine(string? value)
        {
            WriteEntered.Set();
            if (!ReleaseWrite.Wait(TimeSpan.FromSeconds(10)))
                throw new IOException("Synthetic slow writer was not released.");
            lines.Enqueue(value ?? string.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WriteEntered.Dispose();
                ReleaseWrite.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class FailingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        internal int WriteAttempts { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            throw new IOException("Synthetic persistent log failure.");
        }
    }

    private sealed class CapturingTextWriter : TextWriter
    {
        private readonly ConcurrentQueue<string> lines = new();

        public override Encoding Encoding => Encoding.UTF8;
        internal IReadOnlyList<string> Lines => lines.ToArray();

        public override void WriteLine(string? value) => lines.Enqueue(value ?? string.Empty);
    }

    private static void TranslationDirectOutputRollback()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var defPath = Path.Combine(modRoot, "Languages", "Korean", "DefInjected", "ThingDef", "CodexAI.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(defPath)!);
            File.WriteAllText(defPath,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<LanguageData>\n  <Existing.Rollback>BEFORE</Existing.Rollback>\n  <Codex_TestWorkbench.label>OLD_LABEL</Codex_TestWorkbench.label>\n</LanguageData>\n",
                new System.Text.UTF8Encoding(false));
            var before = File.ReadAllBytes(defPath);
            var blocked = Path.Combine(modRoot, "Languages", "Korean", "Keyed", "SampleKeys.xml");
            Directory.CreateDirectory(blocked);
            var handler = new FakeOpenAiHandler(0);
            var engine = new TranslationEngine(RepositoryRoot(), CreateExtractor(), apiClientFactory: keys => new TranslationApiClient(keys, handler));
            var options = withReviewOnlyFalse();
            var error = CaptureException<IOException>(() => engine.RunAsync(options).GetAwaiter().GetResult());
            Assert(error.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase), "Direct output failure did not report rollback.");
            Assert(File.ReadAllBytes(defPath).SequenceEqual(before), "Direct output rollback did not restore the first XML file.");
            Assert(Directory.Exists(blocked), "Direct output rollback modified the blocking directory.");
            Assert(!Directory.EnumerateFiles(Path.Combine(modRoot, "Languages", "Korean"), "*.transaction.bak", SearchOption.AllDirectories).Any(), "Direct output left transaction snapshots.");
            var progressPath = Directory.EnumerateFiles(Path.Combine(modRoot, "_TranslationAudit"), "*-progress.json").Single();
            using var progress = System.Text.Json.JsonDocument.Parse(File.ReadAllText(progressPath));
            Assert(!progress.RootElement[0].GetProperty("complete").GetBoolean(), "Failed direct output was marked complete.");

            TranslationEngineOptions withReviewOnlyFalse() => new()
            {
                ModRoot = modRoot,
                ApiKeys = ["test-key"],
                SourceLanguageFolder = "English",
                ReviewOnly = false,
                BatchSize = 40,
                MaxInputCharactersPerBatch = 100_000,
                MaxInputTokensPerBatch = 100_000,
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                MaxRetries = 1,
                Timeout = TimeSpan.FromSeconds(10),
                AllowInsecureLoopback = true,
                Overwrite = true,
                Provider = ApiProviderCatalog.Get("Custom"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Loopback Fixture",
                    Url = "http://127.0.0.1:12345/v1/chat/completions",
                    Model = "fixture-model",
                    Temperature = 0.1
                }
            };
        });
    }

    private static void RmkWorkspaceIndex()
    {
        WithTempRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/bus\n", new System.Text.UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(root, "Data", "Category", "Fixture Mod - 123", "1.6", "Languages", "Korean (한국어)"));
            File.WriteAllText(Path.Combine(root, "ModList.tsv"), "123\tFixture Mod\tData/Category\tfixture.package\n");
            var yamlPath = Path.Combine(root, "Data", "Category", "Fixture Mod - 123", "1.6", "LoadFolders.Build.yaml");
            File.WriteAllText(yamlPath,
                "BuildRule:\n  Binding:\n    PackageID: [\"fixture.package\"]\n  Version:\n    Default: \"1.6\"\nMetadata:\n  WorkshopID: \"123\"\n  ModName: \"Fixture Mod\"\n");
            var unrelated = Path.Combine(root, "Data", "Unrelated - 999", "1.6");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "LoadFolders.Build.yaml"), "Metadata:\n  WorkshopID: \"999\"\n");
            var service = new RmkWorkspaceService();
            var project = new TranslationProject { Name = "Fixture Mod", WorkshopId = "123", PackageId = "fixture.package" };
            var targets = service.FindTargets(root, project);
            Assert(targets.Count == 1 && targets[0].Version == "1.6", "RMK indexed target was not resolved.");
            Assert(targets[0].LanguageRoot.EndsWith(Path.Combine("Languages", "Korean (한국어)"), StringComparison.Ordinal), "RMK Korean language root changed.");
            var missing = service.FindTargets(root, new TranslationProject { WorkshopId = "777", PackageId = "missing.package" });
            Assert(missing.Count == 0, "RMK searched unrelated Data entries despite a usable index miss.");

            var created = service.CreateTarget(root, new TranslationProject
            {
                Name = "New Fixture",
                WorkshopId = "777",
                PackageId = "new.fixture"
            }, "1.6");
            Assert(File.Exists(created.YamlPath) && Directory.Exists(created.LanguageRoot), "RMK target creation did not create its YAML and Korean folder.");
            Assert(created.Root.StartsWith(Path.Combine(root, "Data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "RMK target escaped the Data root.");

            var outside = Path.Combine(
                Path.GetDirectoryName(root)!,
                $"{Path.GetFileName(root)}-rmk-traversal-outside-{Guid.NewGuid():N}");
            var link = Path.Combine(root, "Data", "Category", "Escaped - 123");
            Directory.CreateDirectory(outside);
            try
            {
                File.WriteAllText(
                    Path.Combine(outside, "LoadFolders.Build.yaml"),
                    "BuildRule:\n  Binding:\n    PackageID: [\"fixture.package\"]\nMetadata:\n  WorkshopID: \"123\"\n  ModName: \"Escaped\"\n");
                var linkAvailable = false;
                try
                {
                    Directory.CreateSymbolicLink(link, outside);
                    linkAvailable = true;
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or PlatformNotSupportedException)
                {
                    Console.WriteLine($"[INFO] RMK traversal link fixture unavailable ({exception.GetType().Name}); bounded traversal remains covered.");
                }
                if (linkAvailable)
                {
                    var linkSafeTargets = service.FindTargets(root, project);
                    Assert(linkSafeTargets.Count == 1 && linkSafeTargets[0].YamlPath.Equals(yamlPath, StringComparison.OrdinalIgnoreCase),
                        "RMK target discovery followed a directory reparse point outside the workspace.");
                }
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(outside, recursive: true);
            }

            var deep = Path.Combine(root, "Data", "Category", "Deep - 123");
            Directory.CreateDirectory(deep);
            for (var depth = 0; depth < 70; depth++)
            {
                deep = Path.Combine(deep, $"d{depth:D2}");
                Directory.CreateDirectory(deep);
            }
            File.WriteAllText(
                Path.Combine(deep, "LoadFolders.Build.yaml"),
                "BuildRule:\n  Binding:\n    PackageID: [\"fixture.package\"]\nMetadata:\n  WorkshopID: \"123\"\n  ModName: \"Deep\"\n");
            AssertThrows<InvalidDataException>(() => service.FindTargets(root, project));
        });
    }
}
