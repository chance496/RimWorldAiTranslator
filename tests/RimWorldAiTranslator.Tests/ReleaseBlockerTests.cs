using System.Net;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Logging;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void StorageSemanticRecovery()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(root);
            paths.EnsureExists();
            var store = new AtomicJsonStore();
            var notices = new List<JsonRecoveryNotice>();
            store.RecoveredFromBackup += (_, notice) => notices.Add(notice);

            WritePrimaryAndBackup(paths.Projects, "{}", "{\"version\":2,\"projects\":[{\"id\":\"restored\"}]}");
            WritePrimaryAndBackup(paths.Settings, "{}", "{\"version\":4,\"themeMode\":\"Dark\"}");
            var reviewRoot = Path.Combine(paths.Reviews, "semantic");
            Directory.CreateDirectory(reviewRoot);
            var reviewPath = ReviewRepository.GetDecisionPath(reviewRoot);
            WritePrimaryAndBackup(reviewPath, "{}", "{\"version\":5,\"items\":[{\"id\":\"restored\"}]}");

            var projects = new ProjectRepository(store, paths).Load();
            var settingsRepository = new SettingsRepository(store, paths);
            var settings = settingsRepository.Load();
            var review = new ReviewRepository(store).Load(reviewRoot);
            Assert(projects.Projects.Single().Id == "restored", "Semantic project corruption did not recover its backup.");
            Assert(settings.ThemeMode == "Dark", "Semantic settings corruption did not recover its backup.");
            Assert(review.Items.Single().Id == "restored", "Semantic review corruption did not recover its backup.");
            Assert(notices.Count == 3 && notices.All(notice => notice.PreservedCorruptPath is not null && File.Exists(notice.PreservedCorruptPath)),
                "Semantic backup recovery did not preserve and report every corrupt primary.");

            settingsRepository.Save(settings);
            using (var saved = JsonDocument.Parse(File.ReadAllText(paths.Settings)))
                Assert(saved.RootElement.GetProperty("version").GetInt32() == AppSettingsDocument.CurrentVersion, "Settings schema was not saved as version 4.");

            var blocked = Path.Combine(root, "both-semantic-invalid.json");
            WritePrimaryAndBackup(blocked, "{}", "{}");
            AssertThrows<InvalidDataException>(() => store.Read<ProjectStoreDocument>(blocked));
            Assert(store.IsBlocked(blocked), "A semantically unreadable store was not write-blocked.");
            AssertThrows<InvalidOperationException>(() => store.Write(blocked, new ProjectStoreDocument()));
            Assert(File.ReadAllText(blocked) == "{}" && File.ReadAllText(blocked + ".bak") == "{}", "Blocked semantic stores were overwritten.");
        });
    }

    private static void ProjectRunRegistrationBoundary()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(Path.Combine(root, "appdata"));
            paths.EnsureExists();
            var repository = new ProjectRepository(new AtomicJsonStore(), paths);
            var service = new ProjectService(repository, new RimWorldModDiscoveryService(), new ProjectCleanupService());
            var previousRoot = Path.Combine(paths.Reviews, "previous");
            var project = new TranslationProject
            {
                Id = "fixture",
                ModRoot = Path.Combine(root, "mod"),
                LatestReviewRoot = previousRoot,
                LatestReviewAt = "2026-01-01T00:00:00Z"
            };
            Directory.CreateDirectory(project.ModRoot);
            var document = new ProjectStoreDocument { Projects = [project] };
            repository.Save(document);

            var incomplete = Path.Combine(paths.Reviews, "incomplete");
            Directory.CreateDirectory(Path.Combine(incomplete, "_TranslationAudit"));
            Directory.CreateDirectory(Path.Combine(incomplete, "Languages", "Korean"));
            File.WriteAllText(Path.Combine(incomplete, "_TranslationAudit", "empty-comparison.json"), "[]", new UTF8Encoding(false));
            AssertThrows<InvalidDataException>(() => service.RegisterRun(document, project, incomplete, "fixture"));
            Assert(project.LatestReviewRoot == previousRoot && project.Runs.Count == 0,
                "An incomplete review replaced the latest valid review pointer.");

            var outside = Path.Combine(root, "outside-review");
            WriteComparison(outside, [Row(outside, "E000001", "fixture.key", "source")]);
            AssertThrows<InvalidDataException>(() => service.RegisterRun(document, project, outside, "fixture"));
            Assert(!File.Exists(Path.Combine(outside, ".rimworld-ai-project.json")),
                "An out-of-root review received an ownership marker.");

            var complete = Path.Combine(paths.Reviews, "complete");
            WriteComparison(complete, [Row(complete, "E000001", "fixture.key", "source")]);
            service.RegisterRun(document, project, complete, "fixture");
            Assert(project.LatestReviewRoot == complete && project.Runs.Count == 1,
                "A complete in-root review was not registered.");
        });
    }

    private static void TranslationRunArtifacts()
    {
        Phase05ArtifactValidationTests.RunAll();
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(Path.Combine(root, "appdata"));
            paths.EnsureExists();
            var reviewRoot = Path.Combine(paths.Reviews, "complete");
            var row = Row(reviewRoot, "E000001", "fixture.key", "source");
            WriteComparison(reviewRoot, [row]);
            var reviewService = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
            var workspace = reviewService.Load(reviewRoot);
            reviewService.UpdateTranslation(workspace, workspace.Items.Single(), "번역", string.Empty, true);

            var artifacts = new TranslationRunArtifactService(paths.Temp, paths.Reviews);
            var preserveTask = artifacts.CreatePreservedTranslationsAsync(workspace);
            reviewService.UpdateTranslation(workspace, workspace.Items.Single(), "후속 편집", string.Empty, true);
            var preserved = preserveTask.GetAwaiter().GetResult();
            Assert(File.Exists(preserved), "Preserved translations were not written by the Core artifact service.");
            using (var document = JsonDocument.Parse(File.ReadAllText(preserved)))
            {
                var item = document.RootElement.GetProperty("items")[0];
                Assert(item.GetProperty("key").GetString() == "fixture.key"
                       && item.GetProperty("text").GetString() == "번역",
                    "Preserved translation payload changed.");
                Assert(item.GetProperty("text").GetString() != "후속 편집",
                    "The asynchronous artifact writer observed a mutable review after snapshot capture.");
            }

            var comparison = Directory.EnumerateFiles(Path.Combine(reviewRoot, "_TranslationAudit"), "*-comparison.json").Single();
            var result = new TranslationRunResult(reviewRoot, comparison, [row], [], 1, 1, 0, 0, 0, 0);
            Assert(artifacts.HasCompleteReview(result), "A complete in-root review was rejected.");
            var outside = Path.Combine(root, "outside");
            WriteComparison(outside, [Row(outside, "E000001", "outside.key", "source")]);
            var outsideComparison = Directory.EnumerateFiles(Path.Combine(outside, "_TranslationAudit"), "*-comparison.json").Single();
            Assert(!artifacts.HasCompleteReview(result with { ReviewRoot = outside, ComparisonFile = outsideComparison }),
                "An out-of-root review was accepted as complete.");

            artifacts.DeletePreservedTranslationsAsync(preserved).GetAwaiter().GetResult();
            Assert(!File.Exists(preserved), "Owned preserved translations were not deleted.");
            var sentinel = Path.Combine(root, "preserve-sentinel.json");
            File.WriteAllText(sentinel, "keep", new UTF8Encoding(false));
            AssertThrows<InvalidDataException>(() => artifacts.DeletePreservedTranslations(sentinel));
            Assert(File.ReadAllText(sentinel) == "keep", "An unowned preservation file was modified.");

            var acknowledgement = Path.Combine(paths.Root, "isolated-discovery.ack");
            var acknowledgementService = new IsolatedDiscoveryAcknowledgementService();
            acknowledgementService.Write(acknowledgement, paths.Root);
            Assert(File.ReadAllText(acknowledgement) == IsolatedDiscoveryAcknowledgementService.Content,
                "Isolated-discovery acknowledgement content changed.");
            AssertThrows<InvalidDataException>(() => acknowledgementService.Write(acknowledgement, paths.Root));
            AssertThrows<InvalidDataException>(() => acknowledgementService.Write(Path.Combine(root, "outside.ack"), paths.Root));
        });
    }

    private static void ReviewKeyedOrdinalIsolation()
    {
        WithTempRoot(root =>
        {
            var previousRoot = Path.Combine(root, "previous");
            var currentRoot = Path.Combine(root, "current");
            var previousRows = new List<ReviewComparisonRow>
            {
                Row(previousRoot, "E000001", "old.key", "old source"),
                Row(previousRoot, "E000002", "", "legacy source"),
                Row(previousRoot, "E000003", "", "ambiguous source"),
                Row(previousRoot, "E000004", "", "one-sided source")
            };
            var currentRows = new List<ReviewComparisonRow>
            {
                Row(currentRoot, "E000001", "new.key", "new source"),
                Row(currentRoot, "E000002", "", "legacy source"),
                Row(currentRoot, "E000003", "", "ambiguous source"),
                Row(currentRoot, "E000004", "new.one-sided", "one-sided source")
            };
            WriteComparison(previousRoot, previousRows);
            WriteComparison(currentRoot, currentRows);

            using var futureJson = JsonDocument.Parse("{\"nested\":true}");
            var previous = new ReviewDecisionDocument
            {
                Items =
                [
                    Decision(previousRows[0], "wrong keyed inheritance"),
                    Decision(previousRows[1], "legacy inheritance", futureJson.RootElement.Clone()),
                    Decision(previousRows[2], "ambiguous first"),
                    Decision(previousRows[2], "ambiguous second"),
                    Decision(previousRows[3], "wrong one-sided inheritance")
                ]
            };
            var store = new AtomicJsonStore();
            store.Write(ReviewRepository.GetDecisionPath(previousRoot), previous);
            var project = new TranslationProject
            {
                Id = "fixture",
                LatestReviewRoot = previousRoot,
                Runs = [new ProjectRun { ReviewRoot = previousRoot, CreatedAt = "2026-01-01T00:00:00Z" }]
            };
            var service = new ReviewWorkspaceService(store, CreateExtractor());
            var workspace = service.Load(currentRoot, project);

            Assert(string.IsNullOrEmpty(workspace.Items.Single(item => item.Row.Id == "E000001").Decision.Text), "A different keyed row inherited by ordinal ID.");
            var legacy = workspace.Items.Single(item => item.Row.Id == "E000002");
            Assert(string.IsNullOrEmpty(legacy.Decision.Text), "A both-keyless legacy decision inherited by ordinal ID.");
            Assert(string.IsNullOrEmpty(workspace.Items.Single(item => item.Row.Id == "E000003").Decision.Text), "An ambiguous legacy ID inherited a decision.");
            Assert(string.IsNullOrEmpty(workspace.Items.Single(item => item.Row.Id == "E000004").Decision.Text), "A one-sided keyed row inherited by ID.");
            service.Save(workspace);
            var saved = new ReviewRepository(store).Load(currentRoot);
            Assert(saved.Items.All(item => item.Id != "E000002")
                   && saved.QuarantinedItems.All(item => item.Id != "E000002"),
                "An ignored previous-run ordinal-only legacy decision was persisted into the new review.");

            saved.ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["futureRoot"] = futureJson.RootElement.Clone()
            };
            SaveBoundReviewDocument(store, currentRoot, saved);
            var currentWorkspace = service.Load(currentRoot);
            service.Save(currentWorkspace);
            var roundTrip = new ReviewRepository(store).Load(currentRoot);
            Assert(roundTrip.ExtensionData?["futureRoot"].GetProperty("nested").GetBoolean() == true,
                "Review root extension data was lost during workspace save.");
        });
    }

    private static void SecurityProviderBodyPrivacy()
    {
        WithTempRoot(root =>
        {
            const string reflectedSource = "REFLECTED-SOURCE-SENTINEL-7319";
            var handler = new ReflectedErrorHandler(reflectedSource);
            using var client = new TranslationApiClient(["fixture-key"], handler);
            var options = new TranslationEngineOptions
            {
                ModRoot = root,
                ApiKeys = ["fixture-key"],
                Provider = ApiProviderCatalog.Get("Custom"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Fixture",
                    Url = "http://127.0.0.1:12345/v1/chat/completions",
                    Model = "fixture",
                    Temperature = 0
                },
                AllowInsecureLoopback = true,
                MaxRetries = 2,
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                Timeout = TimeSpan.FromSeconds(5)
            };
            var progress = new List<TranslationProgress>();
            var error = CaptureException<InvalidOperationException>(() => client.TranslateOpenAiAsync(
                options,
                [new SourceEntry { Id = "one", Key = "Fixture.key", Text = reflectedSource, Kind = "Keyed", TypeName = "Keyed" }],
                "fixture system prompt",
                new ProgressCollector(progress),
                CancellationToken.None).GetAwaiter().GetResult());
            Assert(!error.ToString().Contains(reflectedSource, StringComparison.Ordinal), "Provider-reflected source entered the exception chain.");
            Assert(progress.All(item => !item.Message.Contains(reflectedSource, StringComparison.Ordinal)), "Provider-reflected source entered progress messages.");
            using (var logger = new AppLogger(root)) logger.Error(error.Message);
            Assert(!File.ReadAllText(Directory.EnumerateFiles(root, "*.log").Single()).Contains(reflectedSource, StringComparison.Ordinal),
                "Provider-reflected source entered the persistent log.");
        });
    }

    private static void ApplyWriteBoundaries()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-boundary");
            var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "번역 시작")]);
            var store = new AtomicJsonStore();
            var service = new ReviewApplyService(store, new LanguageFileService(), CreateExtractor());

            var englishPath = Path.Combine(modRoot, "Languages", "English", "Keyed", "SampleKeys.xml");
            File.WriteAllText(englishPath, File.ReadAllText(englishPath).Replace("Translate now", "Translate later", StringComparison.Ordinal), new UTF8Encoding(false));
            var stale = service.Apply(new ReviewApplyOptions
            {
                ModRoot = modRoot,
                ReviewRoot = run.ReviewRoot!,
                SourceLanguageFolder = "English",
                DryRun = true,
                Overwrite = true
            });
            Assert(stale.AppliedEntries == 0 && stale.SkippedSourceChanged >= 1, "A stale reviewed source remained locally applicable.");

            var root = Directory.GetParent(modRoot)!.FullName;
            var workshop = Path.Combine(root, "steamapps", "workshop", "content", "294100", "1234567890");
            Directory.CreateDirectory(Path.Combine(workshop, "Languages", "Korean"));
            var sentinel = Path.Combine(workshop, "Languages", "Korean", "keep.txt");
            File.WriteAllText(sentinel, "unchanged", new UTF8Encoding(false));
            AssertThrows<InvalidOperationException>(() => service.Apply(new ReviewApplyOptions
            {
                ModRoot = workshop,
                ReviewRoot = run.ReviewRoot!,
                SourceKind = "Workshop",
                DryRun = true
            }));
            Assert(File.ReadAllText(sentinel) == "unchanged", "Workshop data changed during a denied apply.");
        });
    }

    private static void RmkWriteBoundaries()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-rmk-boundary");
            var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "번역 시작")]);
            var root = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(root, "BoundaryEntry", out var entryRoot);
            var head = Path.Combine(workspaceRoot, ".git", "HEAD");
            var service = CreateRmkExportService();
            RmkExportOptions Options() => new()
            {
                RmkWorkspaceRoot = workspaceRoot,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                RmkLanguageFolderName = "Korean",
                SourceLanguage = "English",
                WorkbookPath = Path.Combine(entryRoot, "history.xlsx"),
                Overwrite = true,
                DryRun = true
            };

            File.WriteAllText(head, "ref: refs/heads/main\n", new UTF8Encoding(false));
            AssertThrows<InvalidOperationException>(() => service.Export(Options()));
            File.WriteAllText(head, "0123456789abcdef\n", new UTF8Encoding(false));
            AssertThrows<InvalidOperationException>(() => service.Export(Options()));
            Assert(!File.Exists(Path.Combine(entryRoot, "history.xlsx")), "A denied RMK branch wrote a workbook.");

            File.WriteAllText(head, "ref: refs/heads/bus\n", new UTF8Encoding(false));
            var englishPath = Path.Combine(modRoot, "Languages", "English", "Keyed", "SampleKeys.xml");
            File.WriteAllText(englishPath, File.ReadAllText(englishPath).Replace("Translate now", "Translate after review", StringComparison.Ordinal), new UTF8Encoding(false));
            var stale = service.Export(Options());
            Assert(stale.EligibleEntries == 0 && stale.SkippedSourceChanged >= 1, "A stale reviewed source remained RMK-applicable.");
            Assert(!File.Exists(Path.Combine(entryRoot, "history.xlsx")), "RMK DryRun wrote a workbook.");
        });
    }

    private static ReviewComparisonRow Row(string reviewRoot, string id, string key, string source)
    {
        var target = Path.Combine(reviewRoot, "Languages", "Korean", "Keyed", "Fixture.xml");
        return new ReviewComparisonRow
        {
            Id = id,
            Key = key,
            Kind = "Keyed",
            DefClass = "Keyed",
            Node = key,
            Target = target,
            Source = source,
            Candidate = string.Empty
        };
    }

    private static ReviewDecision Decision(ReviewComparisonRow row, string text, JsonElement? future = null)
    {
        var languageRoot = Directory.GetParent(Directory.GetParent(row.Target)!.FullName)!.FullName;
        return new ReviewDecision
        {
            Id = row.Id,
            Key = row.Key,
            Target = Path.GetRelativePath(languageRoot, row.Target),
            Status = ReviewStatuses.Approved,
            Text = text,
            Note = "preserved note",
            SourceText = row.Source,
            PreviousSourceText = "preserved previous source",
            ExtensionData = future is null ? null : new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["futureItem"] = future.Value.Clone() }
        };
    }

    private static void WriteComparison(string reviewRoot, IReadOnlyList<ReviewComparisonRow> rows)
    {
        Directory.CreateDirectory(Path.Combine(reviewRoot, "Languages", "Korean", "Keyed"));
        var audit = Path.Combine(reviewRoot, "_TranslationAudit");
        Directory.CreateDirectory(audit);
        File.WriteAllText(Path.Combine(audit, "fixture-comparison.json"), JsonSerializer.Serialize(rows), new UTF8Encoding(false));
    }

    private static void WritePrimaryAndBackup(string path, string primary, string backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, primary, new UTF8Encoding(false));
        File.WriteAllText(path + ".bak", backup, new UTF8Encoding(false));
    }

    private sealed class ReflectedErrorHandler(string sentinel) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = sentinel, code = sentinel } }), Encoding.UTF8, "application/json")
            });
    }
}
