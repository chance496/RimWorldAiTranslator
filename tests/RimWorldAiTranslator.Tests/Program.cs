using System.Text.Json;
using System.Net;
using System.Text;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Quality;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Validation;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Diagnostics;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static readonly List<(string Name, Action Body)> Tests =
    [
        ("Storage.RoundTrip", StorageRoundTrip),
        ("Storage.BackupRecovery", StorageBackupRecovery),
        ("Storage.DoubleCorruptionBlocksWrite", StorageDoubleCorruptionBlocksWrite),
        ("Compatibility.UnknownFields", CompatibilityUnknownFields),
        ("Security.ApiKeysNotPersisted", SecurityApiKeysNotPersisted),
        ("Security.LogRedaction", SecurityLogRedaction),
        ("Validation.TokensAndParticles", ValidationTokensAndParticles),
        ("Provider.Configuration", ProviderConfiguration),
        ("Glossary.OptionalCustomFile", GlossaryOptionalCustomFile),
        ("Review.TranslationMemory", ReviewTranslationMemory),
        ("Quality.Privacy", QualityPrivacy),
        ("Project.CleanupBoundary", ProjectCleanupBoundary),
        ("Discovery.SteamAndLoadFolders", DiscoverySteamAndLoadFolders),
        ("Review.SourceChangeInheritance", ReviewSourceChangeInheritance),
        ("Source.Extraction", SourceExtraction),
        ("Source.DefSafety", SourceDefSafety),
        ("Source.DuplicateIdentity", SourceDuplicateIdentity),
        ("Translation.SourceOnly", TranslationSourceOnly),
        ("Translation.ApiRetryAndKeyRotation", TranslationApiRetryAndKeyRotation),
        ("Translation.CancellationAndResume", TranslationCancellationAndResume),
        ("Translation.DirectOutputRollback", TranslationDirectOutputRollback),
        ("Apply.Local", ApplyLocal),
        ("Apply.TokenSafety", ApplyTokenSafety),
        ("Export.RmkTransaction", RmkExportTransaction),
        ("Export.RmkSourceHistory", RmkSourceHistoryRoundTrip),
        ("Translation.RmkLanguageMismatch", TranslationRmkLanguageMismatch),
        ("Rmk.WorkspaceIndex", RmkWorkspaceIndex),
        ("Diagnostics.Privacy", DiagnosticsPrivacy),
        ("Storage.ProjectStatsCache", ProjectStatsCacheInvalidation),
        ("Rmk.AutoDiscovery", RmkAutoDiscovery)
    ];

    private static int Main()
    {
        var passed = 0;
        foreach (var test in Tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"[PASS] {test.Name}");
                passed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FAIL] {test.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{passed}/{Tests.Count} tests passed.");
        return passed == Tests.Count ? 0 : 1;
    }

    private static void StorageRoundTrip()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(root);
            var store = new AtomicJsonStore();
            var repository = new ProjectRepository(store, paths);
            var project = new TranslationProject
            {
                Id = "fixture",
                Name = "Fixture Mod",
                ModRoot = Path.Combine(root, "mod"),
                SourceLanguageFolder = "English"
            };
            repository.Save(new ProjectStoreDocument { Projects = [project] });
            var loaded = repository.Load();
            Assert(loaded.Version == 2, "Project store version changed.");
            Assert(loaded.Projects.Count == 1 && loaded.Projects[0].Name == "Fixture Mod", "Project did not round-trip.");
            Assert(!File.ReadAllBytes(paths.Projects).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "JSON contains a UTF-8 BOM.");
        });
    }

    private static void StorageBackupRecovery()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(root);
            var store = new AtomicJsonStore();
            var repository = new ProjectRepository(store, paths);
            repository.Save(new ProjectStoreDocument { Projects = [new TranslationProject { Id = "one", Name = "One" }] });
            repository.Save(new ProjectStoreDocument { Projects = [new TranslationProject { Id = "two", Name = "Two" }] });
            File.WriteAllText(paths.Projects, "{broken", new System.Text.UTF8Encoding(false));
            JsonRecoveryNotice? notice = null;
            store.RecoveredFromBackup += (_, value) => notice = value;

            var loaded = repository.Load();
            Assert(loaded.Projects.Single().Id == "one", "Valid backup was not restored.");
            Assert(notice?.PreservedCorruptPath is not null && File.Exists(notice.PreservedCorruptPath), "Corrupt primary was not preserved.");
            using var parsed = JsonDocument.Parse(File.ReadAllText(paths.Projects));
            Assert(parsed.RootElement.GetProperty("projects")[0].GetProperty("id").GetString() == "one", "Recovered primary is invalid.");
        });
    }

    private static void StorageDoubleCorruptionBlocksWrite()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(root);
            File.WriteAllText(paths.Projects, "{primary");
            File.WriteAllText(paths.Projects + ".bak", "{backup");
            var store = new AtomicJsonStore();
            var repository = new ProjectRepository(store, paths);
            AssertThrows<InvalidDataException>(() => repository.Load());
            AssertThrows<InvalidOperationException>(() => repository.Save(new ProjectStoreDocument()));
            Assert(File.ReadAllText(paths.Projects) == "{primary", "Unreadable primary was overwritten.");
            Assert(File.ReadAllText(paths.Projects + ".bak") == "{backup", "Unreadable backup was overwritten.");
        });
    }

    private static void CompatibilityUnknownFields()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(root);
            File.WriteAllText(paths.Projects,
                "{\"version\":2,\"updatedAt\":\"x\",\"futureRoot\":7,\"projects\":[{\"id\":\"p\",\"name\":\"n\",\"modRoot\":\"m\",\"futureProject\":{\"ok\":true},\"runs\":[]}]}");
            var store = new AtomicJsonStore();
            var repository = new ProjectRepository(store, paths);
            var document = repository.Load();
            repository.Save(document);
            using var json = JsonDocument.Parse(File.ReadAllText(paths.Projects));
            Assert(json.RootElement.GetProperty("futureRoot").GetInt32() == 7, "Unknown root property was lost.");
            Assert(json.RootElement.GetProperty("projects")[0].GetProperty("futureProject").GetProperty("ok").GetBoolean(), "Unknown project property was lost.");
        });
    }

    private static void SecurityApiKeysNotPersisted()
    {
        WithTempRoot(root =>
        {
            var secret = "csk-" + "this-must-not-be-written";
            var paths = new AppDataPaths(root);
            var customGlossary = Path.Combine(root, "custom-glossary.txt");
            var repository = new SettingsRepository(new AtomicJsonStore(), paths);
            repository.Save(new AppSettingsDocument
            {
                ApiProviderId = "Cerebras",
                CustomGlossaryPath = customGlossary,
                ApiProviders = new Dictionary<string, ApiProviderSettings>
                {
                    ["Cerebras"] = new() { Name = "Cerebras", Url = "https://api.cerebras.ai/v1/chat/completions", Model = "gemma-4-31b" }
                }
            });
            Assert(!File.ReadAllText(paths.Settings).Contains(secret, StringComparison.Ordinal), "Secret was persisted.");
            Assert(!typeof(ApiProviderSettings).GetProperties().Any(p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)), "Provider settings expose a key property.");
            Assert(repository.Load().CustomGlossaryPath == customGlossary, "Custom glossary path did not round-trip.");
        });
    }

    private static void ValidationTokensAndParticles()
    {
        const string source = "r_logentry->[INITIATOR_nameDef] gave {0} to [RECIPIENT_nameDef].";
        var valid = TranslationValidator.Validate(source, "r_logentry->[INITIATOR_nameDef](이)가 {0}(을)를 [RECIPIENT_nameDef]에게 주었습니다.");
        Assert(valid.IsSafe, "Valid protected tokens were rejected.");
        var missing = TranslationValidator.Validate(source, "[INITIATOR_nameDef]이(가) 선물을 주었습니다.");
        Assert(!missing.IsSafe && missing.MissingTokens.Count > 0, "Missing protected tokens were accepted.");
        var invalidParticle = TranslationValidator.Validate("Hello", "은(는) 안녕하세요");
        Assert(!invalidParticle.IsSafe && invalidParticle.InvalidParticles.Count > 0, "Invalid Korean particle notation was accepted.");
    }

    private static void ProviderConfiguration()
    {
        var profile = ApiProviderCatalog.Get("Cerebras");
        var valid = ProviderValidator.Validate(profile, new ApiProviderSettings
        {
            Name = profile.Name,
            Url = profile.Url,
            Model = profile.DefaultModel,
            Temperature = 0.1
        }, 2);
        Assert(valid.Valid && valid.Capabilities.KnownModel, "Bundled provider was rejected.");
        var credential = ProviderValidator.Validate(profile, new ApiProviderSettings
        {
            Url = "https://api.example.invalid/v1?api_key=secret",
            Model = "fixture",
            Temperature = 0
        }, 1);
        Assert(!credential.Valid && credential.ErrorCodes.Contains("UrlContainsCredential"), "Credential-bearing URL was accepted.");
        var loopback = ProviderValidator.Validate(profile, new ApiProviderSettings
        {
            Url = "http://127.0.0.1:12345/v1/chat/completions",
            Model = "fixture",
            Temperature = 0
        }, 1);
        Assert(loopback.Valid, "Loopback HTTP test provider was rejected.");
    }

    private static void GlossaryOptionalCustomFile()
    {
        WithTempRoot(root =>
        {
            var generated = Path.Combine(root, "generated.json");
            var custom = Path.Combine(root, "custom.txt");
            File.WriteAllText(generated, "{\"terms\":[{\"source\":\"RimWorld\",\"ko\":\"림월드\"}]}");
            File.WriteAllText(custom, "Fixture term => 추가 용어\n");

            var service = new GlossaryService();
            var combined = service.Load(generated, custom, true);
            Assert(combined.Count == 2 && combined.Any(term => term.Korean == "추가 용어"), "Optional custom glossary was not loaded.");

            var officialOnly = service.Load(generated, Path.Combine(root, "missing.txt"), true);
            Assert(officialOnly.Count == 1 && officialOnly[0].Korean == "림월드", "Missing custom glossary blocked the bundled glossary.");
        });
    }

    private static void ReviewTranslationMemory()
    {
        var entries = new[]
        {
            new TranslationMemoryEntry("one", " source\r\ntext ", "번역 1", "ai", "translated", "A.xml", "2026-01-01T00:00:00Z", false, true),
            new TranslationMemoryEntry("two", "source\ntext", "번역 2", "local", "approved", "B.xml", "2026-01-02T00:00:00Z", false, true),
            new TranslationMemoryEntry("three", "source\ntext", "번역 3", "local", "approved", "C.xml", "2026-01-03T00:00:00Z", true, true)
        };
        var suggestions = TranslationMemoryService.Select(entries, "source\ntext");
        Assert(suggestions.Count == 2 && suggestions[0].Text == "번역 2", "Translation memory ranking changed.");
    }

    private static void QualityPrivacy()
    {
        WithTempRoot(root =>
        {
            const string sourceSecret = "PRIVATE-SOURCE-CONTENT";
            const string translationSecret = "PRIVATE-TRANSLATION-CONTENT";
            var entries = new[]
            {
                new QualityEntry(0, "Key", Path.Combine(root, "absolute.xml"), "ThingDef", sourceSecret, translationSecret, "", "translated", false, true)
            };
            var issues = QualityService.FindIssues(entries);
            var path = Path.Combine(root, "quality.html");
            var result = QualityService.ExportHtml(path, entries, issues);
            var html = File.ReadAllText(path);
            Assert(result.Model.EntryCount == 1, "Quality report counts changed.");
            Assert(!html.Contains(sourceSecret, StringComparison.Ordinal) && !html.Contains(translationSecret, StringComparison.Ordinal), "Quality report leaked text.");
            Assert(!html.Contains(root, StringComparison.OrdinalIgnoreCase), "Quality report leaked an absolute path.");
        });
    }

    private static void ProjectCleanupBoundary()
    {
        WithTempRoot(root =>
        {
            var reviewRoot = Path.Combine(root, "reviews");
            var run = Path.Combine(reviewRoot, "owned-run");
            var outside = Path.Combine(root, "outside");
            var modRoot = Path.Combine(root, "mod");
            Directory.CreateDirectory(run);
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(modRoot);
            Directory.CreateDirectory(Path.Combine(modRoot, "Languages", "Korean"));
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(modRoot, "Languages", "Korean", "keep.xml"), "keep");
            var project = new TranslationProject
            {
                Id = "fixture",
                ModRoot = modRoot,
                LatestReviewRoot = run,
                Runs = [new ProjectRun { ReviewRoot = outside }, new ProjectRun { ReviewRoot = modRoot }]
            };
            ProjectCleanupService.WriteOwnershipMarker(project, run);
            var service = new ProjectCleanupService();
            var plan = service.BuildPlan(project, [reviewRoot]);
            Assert(plan.SafePaths.SequenceEqual([Path.GetFullPath(run)]), "Owned review path was not isolated.");
            Assert(plan.UnsafePaths.Count == 2, "Unsafe cleanup paths were not reported.");
            var failures = service.Remove(project, [reviewRoot], plan.SafePaths);
            Assert(failures.Count == 0 && !Directory.Exists(run), "Owned review path was not removed.");
            Assert(File.Exists(Path.Combine(outside, "keep.txt")), "Outside data was removed.");
            Assert(File.Exists(Path.Combine(modRoot, "Languages", "Korean", "keep.xml")), "Korean translation was removed.");
        });
    }

    private static void SourceExtraction()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var extractor = CreateExtractor();
            var result = extractor.Extract(modRoot, "Auto", "CodexAI");
            var keys = result.Entries.Select(entry => entry.Key).ToArray();
            var expected = new[]
            {
                "CodexTranslator.SampleButton",
                "CodexTranslator.SampleMessage",
                "Codex_TestWorkbench.label",
                "Codex_TestWorkbench.description",
                "Codex_TestWorkbench.comps.CompPowerTrader.gizmoLabel",
                "Codex_TestWorkbench.comps.CompPowerTrader.gizmoDescription",
                "Codex_TestJob.reportString"
            };
            Assert(result.Entries.Count == 7, $"Unexpected source entry count: {result.Entries.Count}");
            Assert(expected.All(keys.Contains), "Expected source key was not extracted.");
            Assert(result.SkippedInternalIdentifiers.Count == 1 && result.SkippedInternalIdentifiers[0].Field.Equals("compClass", StringComparison.OrdinalIgnoreCase), "Internal identifier audit changed.");
        });
    }

    private static void SourceDefSafety()
    {
        WithFixture("DefSafetyMod", modRoot =>
        {
            var result = CreateExtractor().Extract(modRoot, "Auto", "CodexAI");
            var keys = result.Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var expected in new[]
            {
                "Codex_RenderTree.label",
                "Codex_RenderTree.nodes.0.label",
                "Codex_AlienRace.label",
                "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.bodyAddons.0.label"
            })
            {
                Assert(keys.Contains(expected), $"Display field was not extracted: {expected}");
            }

            foreach (var forbidden in new[]
            {
                "Codex_RenderTree.renderTree",
                "Codex_RenderTree.name",
                "Codex_RenderTree.nodes.0.tagDef",
                "Codex_RenderTree.nodes.0.texPath",
                "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.colorChannels.0.name",
                "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.bodyAddons.0.name"
            })
            {
                Assert(!keys.Contains(forbidden), $"Internal identifier entered review rows: {forbidden}");
            }

            var skipped = result.SkippedInternalIdentifiers.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            Assert(skipped.Contains("Codex_RenderTree.renderTree"), "Render identifier was not audited.");
            Assert(skipped.Contains("Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.colorChannels.0.name"), "AlienRace identifier was not audited.");
            Assert(result.SkippedInternalIdentifiers.All(entry => !string.IsNullOrWhiteSpace(entry.Reason)), "Skipped row has no reason.");
        });
    }

    private static void SourceDuplicateIdentity()
    {
        WithFixture("DuplicateIdentityMod", modRoot =>
        {
            var extractor = CreateExtractor();
            var result = extractor.Extract(modRoot, "Auto", "CodexAI");
            var unique = result.Entries.GroupBy(entry => entry.Identity, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
            var shared = unique.Where(entry => entry.Key == "Shared.label").ToArray();
            Assert(shared.Length == 3, "Same key in distinct namespaces was collapsed.");
            Assert(shared.Count(entry => entry.Kind == "Keyed") == 1, "Keyed namespace count changed.");
            Assert(shared.Count(entry => entry.TypeName == "ThingDef") == 1, "ThingDef namespace count changed.");
            Assert(shared.Count(entry => entry.TypeName == "HediffDef") == 1, "HediffDef namespace count changed.");
            var existing = extractor.ReadExistingLanguageMap(Path.Combine(modRoot, "Languages", "Korean"));
            Assert(existing[SourceExtractor.GetLocalizationIdentity("Keyed", "Shared.label")].Text == "키", "Keyed existing translation crossed namespaces.");
            Assert(existing[SourceExtractor.GetLocalizationIdentity("ThingDef", "Shared.label")].Text == "사물", "ThingDef existing translation crossed namespaces.");
            Assert(existing[SourceExtractor.GetLocalizationIdentity("HediffDef", "Shared.label")].Text == "상태", "HediffDef existing translation crossed namespaces.");
        });
    }

    private static void TranslationSourceOnly()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var tempRoot = Directory.GetParent(modRoot)!.FullName;
            var engine = new TranslationEngine(RepositoryRoot(), CreateExtractor());
            var result = engine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = modRoot,
                SourceOnly = true,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(tempRoot, "reviews"),
                SourceLanguageFolder = "Auto"
            }).GetAwaiter().GetResult();
            Assert(result.Rows.Count == 7, "Source-only review row count changed.");
            Assert(result.Rows.All(row => string.IsNullOrEmpty(row.Candidate) && !row.SafeToApply), "Source-only run created candidates.");
            Assert(result.ReviewRoot is not null && Directory.Exists(result.ReviewRoot), "Source-only review root was not created.");
            Assert(File.Exists(result.ComparisonFile), "Source-only comparison file was not created.");
            Assert(!Directory.EnumerateFiles(Path.Combine(modRoot, "Languages", "Korean"), "*", SearchOption.AllDirectories).Any(), "Source-only run modified the source mod.");
            using var progress = JsonDocument.Parse(File.ReadAllText(Directory.EnumerateFiles(Path.Combine(result.ReviewRoot!, "_TranslationAudit"), "*-progress.json").Single()));
            Assert(progress.RootElement[0].GetProperty("complete").GetBoolean(), "Source-only checkpoint was not complete.");
        });
    }

    private static void TranslationApiRetryAndKeyRotation()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var handler = new FakeOpenAiHandler(failFirst: 1);
            var engine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(keys, handler));
            var options = new TranslationEngineOptions
            {
                ModRoot = modRoot,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(Directory.GetParent(modRoot)!.FullName, "reviews-api"),
                SourceLanguageFolder = "Auto",
                ApiKeys = ["key-one", "key-two"],
                Provider = ApiProviderCatalog.Get("Cerebras"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Fixture",
                    Url = "http://127.0.0.1:12345/v1/chat/completions",
                    Model = "fixture-model",
                    Temperature = 0.1
                },
                AllowInsecureLoopback = true,
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                BatchSize = 2,
                MaxRetries = 2,
                Timeout = TimeSpan.FromSeconds(5),
                GeneratedGlossaryPath = Path.Combine(RepositoryRoot(), "glossary.generated.ko.json")
            };
            var progress = new List<TranslationProgress>();
            var result = engine.RunAsync(options, new ProgressCollector(progress)).GetAwaiter().GetResult();
            Assert(result.Rows.Count == 7 && result.Rows.All(row => !string.IsNullOrWhiteSpace(row.Candidate)), "API translation lost candidates.");
            Assert(handler.RequestCount == 5, $"Retry or batch count changed: {handler.RequestCount}");
            Assert(handler.AuthorizationValues.Contains("Bearer key-one") && handler.AuthorizationValues.Contains("Bearer key-two"), "Multiple API keys were not rotated.");
            Assert(progress.Count(item => item.Stage == "retry") == 1, "Transient failure was retried by nested loops.");
            Assert(!Directory.EnumerateFiles(Path.Combine(modRoot, "Languages", "Korean"), "*", SearchOption.AllDirectories).Any(), "Review-only API run modified the source mod.");
            var progressPath = Directory.EnumerateFiles(Path.Combine(result.ReviewRoot!, "_TranslationAudit"), "*-progress.json").Single();
            using var checkpoint = JsonDocument.Parse(File.ReadAllText(progressPath));
            Assert(checkpoint.RootElement[0].GetProperty("complete").GetBoolean(), "API checkpoint was not complete.");
            Assert(checkpoint.RootElement[0].GetProperty("completedBatches").GetInt32() == 4, "Completed batch count changed.");
        });
    }

    private static void ApplyLocal()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-apply");
            var reviewLanguageRoot = Path.Combine(run.ReviewRoot!, "Languages", "Korean");
            var decisions = new ReviewDecisionDocument
            {
                ReviewRoot = run.ReviewRoot!,
                Comparison = run.ComparisonFile!,
                Items = run.Rows.Select(row => new ReviewDecision
                {
                    Id = row.Id,
                    Key = row.Key,
                    Target = Path.GetRelativePath(reviewLanguageRoot, row.Target),
                    Status = ReviewStatuses.Approved,
                    Text = "번역 " + row.Source,
                    SourceText = row.Source,
                    SourceHash = RimWorldAiTranslator.Core.Utilities.StableIdentity.Sha256(row.Source)
                }).ToList()
            };
            var store = new AtomicJsonStore();
            new ReviewRepository(store).Save(run.ReviewRoot!, decisions);
            var service = new ReviewApplyService(store, new RimWorldAiTranslator.Core.Xml.LanguageFileService(), CreateExtractor());
            var result = service.Apply(new ReviewApplyOptions
            {
                ModRoot = modRoot,
                ReviewRoot = run.ReviewRoot!,
                Overwrite = true,
                ApplyStatus = ReviewApplyStatus.ApprovedOnly
            });
            Assert(result.AppliedEntries == 7 && result.ApprovedRows == 7, "Approved local apply count changed.");
            var files = Directory.EnumerateFiles(Path.Combine(modRoot, "Languages", "Korean"), "*.xml", SearchOption.AllDirectories).ToArray();
            Assert(files.Length > 0, "Local apply did not create Korean XML.");
            Assert(files.All(file => File.Exists(file + ".bak") || !File.Exists(file + ".bak")), "Unreachable assertion.");
            var dryRun = service.Apply(new ReviewApplyOptions
            {
                ModRoot = modRoot,
                ReviewRoot = run.ReviewRoot!,
                Overwrite = true,
                DryRun = true,
                ApplyStatus = ReviewApplyStatus.ApprovedOnly
            });
            Assert(dryRun.AppliedEntries == 7, "Dry-run counts changed.");
        });
    }

    private static void ApplyTokenSafety()
    {
        WithFixture("TokenSafetyMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-token");
            var rows = run.Rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
            var validGrammar = "r_logentry->[INITIATOR_nameDef](이)가 [RECIPIENT_nameDef]에게 종이접기를 줬다.";
            var missingLodger = "[lodgersLabelSingOrPluralDef](이)가 도착했다. [lodgersObjective](을)를 [shuttleDelayTicks_duration] 동안 보호하라.";
            var validFormat = "{0}(은)는 %s <color=red>$POWER_nameDef$</color>(을)를 사용한다.\\n준비됨.";
            var invalidParticle = "[PAWN_nameDef]은(는) 준비됐다.";
            var values = new Dictionary<string, string>
            {
                ["Token.Grammar"] = validGrammar,
                ["Token.Lodgers"] = missingLodger,
                ["Token.Format"] = validFormat,
                ["Token.Particle"] = invalidParticle
            };
            var reviewLanguageRoot = Path.Combine(run.ReviewRoot!, "Languages", "Korean");
            var document = new ReviewDecisionDocument
            {
                ReviewRoot = run.ReviewRoot!,
                Comparison = run.ComparisonFile!,
                Items = rows.Values.Select(row => new ReviewDecision
                {
                    Id = row.Id,
                    Key = row.Key,
                    Target = Path.GetRelativePath(reviewLanguageRoot, row.Target),
                    Status = ReviewStatuses.Approved,
                    Text = values[row.Key],
                    SourceText = row.Source
                }).ToList()
            };
            var store = new AtomicJsonStore();
            new ReviewRepository(store).Save(run.ReviewRoot!, document);
            var result = new ReviewApplyService(store, new RimWorldAiTranslator.Core.Xml.LanguageFileService(), CreateExtractor()).Apply(new ReviewApplyOptions
            {
                ModRoot = modRoot,
                ReviewRoot = run.ReviewRoot!,
                Overwrite = true
            });
            Assert(result.AppliedEntries == 2 && result.SkippedUnsafe == 2, "Token safety apply count changed.");
            var map = new RimWorldAiTranslator.Core.Xml.LanguageFileService().Read(Path.Combine(modRoot, "Languages", "Korean", "Keyed", "Tokens.xml"));
            Assert(map["Token.Grammar"] == validGrammar && map["Token.Format"] == validFormat, "Safe token translations were not applied.");
            Assert(!map.ContainsKey("Token.Lodgers") && !map.ContainsKey("Token.Particle"), "Unsafe token translation was applied.");
        });
    }

    private static TranslationRunResult CreateSourceOnlyReview(string modRoot, string reviewFolder)
    {
        var engine = new TranslationEngine(RepositoryRoot(), CreateExtractor());
        return engine.RunAsync(new TranslationEngineOptions
        {
            ModRoot = modRoot,
            SourceOnly = true,
            ReviewOnly = true,
            ReviewRoot = Path.Combine(Directory.GetParent(modRoot)!.FullName, reviewFolder),
            SourceLanguageFolder = "Auto"
        }).GetAwaiter().GetResult();
    }

    private static SourceExtractor CreateExtractor() => new(Path.Combine(RepositoryRoot(), "rimworld-def-field-rules.txt"));

    private static void WithFixture(string fixtureName, Action<string> action)
    {
        WithTempRoot(root =>
        {
            var source = Path.Combine(RepositoryRoot(), "testdata", fixtureName);
            var target = Path.Combine(root, fixtureName);
            CopyDirectory(source, target);
            action(target);
        });
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "RimWorldAiTranslator.sln")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    private static void WithTempRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "RimWorldAiTranslator-csharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            action(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class ProgressCollector(List<TranslationProgress> entries) : IProgress<TranslationProgress>
    {
        public void Report(TranslationProgress value) => entries.Add(value);
    }

    private sealed class FakeOpenAiHandler(int failFirst) : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => requestCount;
        public List<string> AuthorizationValues { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref requestCount);
            AuthorizationValues.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert(!body.Contains("key-one", StringComparison.Ordinal) && !body.Contains("key-two", StringComparison.Ordinal), "API key leaked into request JSON.");
            if (current <= failFirst)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"transient\"}}", Encoding.UTF8, "application/json")
                };
            }

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
