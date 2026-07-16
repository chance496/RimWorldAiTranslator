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
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static readonly string[] ExpectedSourceKeys =
    [
        "CodexTranslator.SampleButton",
        "CodexTranslator.SampleMessage",
        "Codex_TestWorkbench.label",
        "Codex_TestWorkbench.description",
        "Codex_TestWorkbench.comps.CompPowerTrader.gizmoLabel",
        "Codex_TestWorkbench.comps.CompPowerTrader.gizmoDescription",
        "Codex_TestJob.reportString"
    ];

    private static readonly string[] ExpectedSafeDefKeys =
    [
        "Codex_RenderTree.label",
        "Codex_RenderTree.nodes.0.label",
        "Codex_AlienRace.label",
        "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.bodyAddons.0.label"
    ];

    private static readonly string[] ForbiddenDefKeys =
    [
        "Codex_RenderTree.renderTree",
        "Codex_RenderTree.name",
        "Codex_RenderTree.nodes.0.tagDef",
        "Codex_RenderTree.nodes.0.texPath",
        "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.colorChannels.0.name",
        "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.bodyAddons.0.name"
    ];

    private static readonly List<(string Name, Action Body)> Tests =
    [
        ("Storage.RoundTrip", StorageRoundTrip),
        ("Storage.Compatibility", Phase05StorageTests.RunAll),
        ("Security.Hardening", Phase07SecurityHardening),
        ("Security.ResourceBoundaries", Phase07ResourceBoundaries),
        ("Native.ArchiveAndWriteBoundary", Phase07NativeArchiveAndWriteBoundary),
        ("Native.ActiveContentAndLimits", Phase07NativeActiveContentAndLimits),
        ("Reliability.Core", Phase08Reliability),
        ("Storage.AtomicFaults", Phase08AtomicStorageFaults),
        ("Project.LoadCancellation", Phase08ProjectLoadCancellation),
        ("Translation.MonotonicRateLimiter", Phase08MonotonicRateLimiter),
        ("Storage.SimpleRecoveryThreatModel", SimpleStorageRecoveryThreatModel),
        ("Compatibility.XmlAndReadOnlyRoundTrip", Phase05XmlAndReadOnlyRoundTrip),
        ("Compatibility.RmkPackageRoundTrip", Phase05RmkPackageRoundTrip),
        ("Storage.BackupRecovery", StorageBackupRecovery),
        ("Storage.DoubleCorruptionBlocksWrite", StorageDoubleCorruptionBlocksWrite),
        ("Storage.SemanticRecovery", StorageSemanticRecovery),
        ("Compatibility.UnknownFields", CompatibilityUnknownFields),
        ("Compatibility.LegacyPowerShellProject", LegacyPowerShellProjectCompatibility),
        ("Security.ApiKeysNotPersisted", SecurityApiKeysNotPersisted),
        ("Security.LogRedaction", SecurityLogRedaction),
        ("Security.OperationErrorPresentation", OperationErrorPresentationPrivacy),
        ("Security.TranslationProgressPrivacy", TranslationProgressPrivacy),
        ("Security.ProviderBodyPrivacy", SecurityProviderBodyPrivacy),
        ("Validation.TokensAndParticles", ValidationTokensAndParticles),
        ("Provider.Configuration", ProviderConfiguration),
        ("Glossary.OptionalCustomFile", GlossaryOptionalCustomFile),
        ("Review.TranslationMemory", ReviewTranslationMemory),
        ("Quality.Privacy", QualityPrivacy),
        ("Project.CleanupBoundary", ProjectCleanupBoundary),
        ("Project.RunRegistrationBoundary", ProjectRunRegistrationBoundary),
        ("Storage.TranslationRunArtifacts", TranslationRunArtifacts),
        ("Storage.AsyncTransactionRollback", AsyncTransactionRollback),
        ("Storage.SettingsAsyncSnapshot", SettingsAsyncSnapshot),
        ("Storage.AtomicJsonBackupSingleRead", AtomicJsonBackupSingleRead),
        ("Storage.ProjectRevisionConcurrency", ProjectRevisionConcurrency),
        ("Review.ConcurrentSaveRevision", ReviewConcurrentSaveRevision),
        ("Review.SaveCoordinatorBarrier", ReviewSaveCoordinatorBarrier),
        ("Discovery.SteamAndLoadFolders", DiscoverySteamAndLoadFolders),
        ("Discovery.IsolatedRootBoundary", DiscoveryIsolatedRootBoundary),
        ("Review.SourceChangeInheritance", ReviewSourceChangeInheritance),
        ("Review.SafeDuplicateSourceBulk", Phase10SafeDuplicateSourceBulk),
        ("Review.KeyedOrdinalIsolation", ReviewKeyedOrdinalIsolation),
        ("Review.LegacySourceVerificationFailClosed", ReviewLegacySourceVerificationFailClosed),
        ("Review.AmbiguousIdentityFailClosed", ReviewAmbiguousIdentityFailClosed),
        ("Review.ComparisonHashAndWhitespace", ReviewComparisonHashAndWhitespace),
        ("Review.KeylessV6RoundTrip", ReviewKeylessV6RoundTrip),
        ("Review.LegacyUnmatchedQuarantine", ReviewLegacyUnmatchedQuarantine),
        ("Source.Extraction", SourceExtraction),
        ("Source.DefSafety", SourceDefSafety),
        ("Source.DuplicateIdentity", SourceDuplicateIdentity),
        ("Translation.SourceOnly", TranslationSourceOnly),
        ("Translation.GoogleBlankEndpointFallback", TranslationGoogleBlankEndpointFallback),
        ("Translation.ApiRetryAndKeyRotation", TranslationApiRetryAndKeyRotation),
        ("Translation.CancellationAndResume", TranslationCancellationAndResume),
        ("Translation.DirectOutputRollback", TranslationDirectOutputRollback),
        ("Apply.Local", ApplyLocal),
        ("Apply.TokenSafety", ApplyTokenSafety),
        ("Apply.WriteBoundaries", ApplyWriteBoundaries),
        ("Apply.PhysicalWorkshopBoundary", ApplyPhysicalWorkshopBoundary),
        ("Apply.IdentitySwapInjection", ApplyIdentitySwapInjection),
        ("Safety.OperationPlanFingerprintBinding", OperationPlanFingerprintBinding),
        ("Export.RmkTransaction", RmkExportTransaction),
        ("Export.RmkSourceHistory", RmkSourceHistoryRoundTrip),
        ("Translation.RmkLanguageMismatch", TranslationRmkLanguageMismatch),
        ("Rmk.WorkspaceIndex", RmkWorkspaceIndex),
        ("Diagnostics.Privacy", DiagnosticsPrivacy),
        ("Storage.ProjectStatsCache", ProjectStatsCacheInvalidation),
        ("Rmk.AutoDiscovery", RmkAutoDiscovery),
        ("Rmk.WriteBoundaries", RmkWriteBoundaries),
        ("Rmk.EligibilityCounts", RmkEligibilityCounts),
        ("Rmk.WorkshopWriteBoundary", RmkWorkshopWriteBoundary),
        ("Rmk.BuilderTransaction", RmkBuilderTransaction)
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 4
            && args[0].Equals("--simple-storage-crash-child", StringComparison.Ordinal))
        {
            return RunSimpleStorageCrashChild(args[1], args[2], args[3]);
        }

        if (args.Length == 2
            && args[0].Equals("--simple-storage-benchmark", StringComparison.Ordinal)
            && int.TryParse(args[1], out var benchmarkTargetCount)
            && benchmarkTargetCount > 0)
        {
            RunSimpleStorageBenchmark(benchmarkTargetCount);
            return 0;
        }

        if (args.Length == 1
            && args[0].Equals("--simple-storage-only", StringComparison.Ordinal))
        {
            try
            {
                SimpleStorageRecoveryThreatModel();
                Console.WriteLine("PASS Storage.SimpleRecoveryThreatModel");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL Storage.SimpleRecoveryThreatModel: {exception}");
                return 1;
            }
        }

        if (args.Length == 1
            && args[0].Equals("--legacy-compatibility-only", StringComparison.Ordinal))
        {
            try
            {
                LegacyPowerShellProjectCompatibility();
                Console.WriteLine("PASS Compatibility.LegacyPowerShellProject");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL Compatibility.LegacyPowerShellProject: {exception}");
                return 1;
            }
        }

        if (args.Length == 1
            && args[0].Equals("--phase08-storage-faults-only", StringComparison.Ordinal))
        {
            try
            {
                Phase08AtomicStorageFaults();
                Console.WriteLine("PASS Storage.AtomicFaults");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL Storage.AtomicFaults: {exception}");
                return 1;
            }
        }

        if (args.Length == 1
            && args[0].Equals("--phase07-native-only", StringComparison.Ordinal))
        {
            try
            {
                Phase07NativeArchiveAndWriteBoundary();
                Console.WriteLine("PASS Native.ArchiveAndWriteBoundary");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL Native.ArchiveAndWriteBoundary: {exception}");
                return 1;
            }
        }

        if (args.Length == 1
            && args[0].Equals("--rmk-builder-only", StringComparison.Ordinal))
        {
            try
            {
                RmkBuilderTransaction();
                Console.WriteLine("PASS Rmk.BuilderTransaction");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL Rmk.BuilderTransaction: {exception}");
                return 1;
            }
        }

        if (args.Length == 1
            && args[0].Equals("--project-cleanup-only", StringComparison.Ordinal))
        {
            try
            {
                ProjectCleanupBoundary();
                Console.WriteLine("PASS Project.CleanupBoundary");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL Project.CleanupBoundary: {exception}");
                return 1;
            }
        }

        if (args.Length == 1
            && args[0].Equals("--phase10-local-gap-only", StringComparison.Ordinal))
        {
            try
            {
                Phase10SafeDuplicateSourceBulk();
                Console.WriteLine("PASS Review.SafeDuplicateSourceBulk");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL Review.SafeDuplicateSourceBulk: {exception}");
                return 1;
            }
        }

        if (args.Length == 1 && args[0].Equals("--emit-phase05-canonical", StringComparison.Ordinal))
        {
            return EmitPhase05CanonicalCapture();
        }

        if (args.Length == 1 && args[0].Equals("--rmk-contained-child", StringComparison.Ordinal))
        {
            return RunRmkContainedChildFixture();
        }

        if (IsRmkBuilderFixtureProcess())
        {
            return RunRmkBuilderFixtureProcess();
        }

        RegisterPhase05ApiParityTests();
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
                Console.Error.WriteLine($"[FAIL] {test.Name}: {ex}");
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
            Assert(loaded.Version == ProjectStoreDocument.CurrentVersion, "Project store version changed.");
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
            var next = repository.Load();
            next.Projects = [new TranslationProject { Id = "two", Name = "Two" }];
            repository.Save(next);
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
            var paths = new AppDataPaths(root);
            var customGlossary = Path.Combine(root, "custom-glossary.txt");
            using var repository = new SettingsRepository(new AtomicJsonStore(), paths);
            var safeSettings = new AppSettingsDocument
            {
                ApiProviderId = "Cerebras",
                CustomGlossaryPath = customGlossary,
                ApiProviders = new Dictionary<string, ApiProviderSettings>
                {
                    ["Cerebras"] = new() { Name = "Cerebras", Url = "https://api.cerebras.ai/v1/chat/completions", Model = "gemma-4-31b" }
                }
            };
            repository.Save(safeSettings);
            var safeBaseline = File.ReadAllBytes(paths.Settings);
            Assert(repository.Load().CustomGlossaryPath == customGlossary, "Custom glossary path did not round-trip.");

            var unsafeEndpoints = new (string Url, string Secret)[]
            {
                ("https://synthetic-user:synthetic-userinfo-secret@provider.invalid/v1", "synthetic-userinfo-secret"),
                ("https://provider.invalid/v1?API-KEY=synthetic-api-key-secret", "synthetic-api-key-secret"),
                ("https://provider.invalid/v1?a.p.i.k.e.y=synthetic-apikey-secret", "synthetic-apikey-secret"),
                ("https://provider.invalid/v1?K_E_Y=synthetic-key-secret", "synthetic-key-secret"),
                ("https://provider.invalid/v1?access-token=synthetic-access-token-secret", "synthetic-access-token-secret"),
                ("https://provider.invalid/v1?ToKeN=synthetic-token-secret", "synthetic-token-secret"),
                ("https://provider.invalid/v1?a_u_t_h=synthetic-auth-secret", "synthetic-auth-secret"),
                ("https://provider.invalid/v1?AUTHORIZATION=synthetic-authorization-secret", "synthetic-authorization-secret"),
                ("https://provider.invalid/v1?client.secret=synthetic-client-secret", "synthetic-client-secret"),
                ("https://provider.invalid/v1?S_E_C_R_E_T=synthetic-secret", "synthetic-secret"),
                ("https://provider.invalid/v1?pass-word=synthetic-password", "synthetic-password"),
                ("https://provider.invalid/v1?CREDENTIAL=synthetic-credential", "synthetic-credential"),
                ("https://provider.invalid/v1#ACCESS_TOKEN=synthetic-fragment-secret", "synthetic-fragment-secret"),
                ("https://provider.invalid/v1?value=sk-synthetic-credential", "sk-synthetic-credential"),
                ("https://provider.invalid/v1?sk-synthetic-raw-query", "sk-synthetic-raw-query"),
                ("https://provider.invalid/v1#csk-synthetic-raw-fragment", "csk-synthetic-raw-fragment"),
                ("https://provider.invalid/v1?%61%70%69%5F%6B%65%79=synthetic-encoded-name", "synthetic-encoded-name"),
                ("https://provider.invalid/v1/gsk_synthetic-path-credential", "gsk_synthetic-path-credential"),
                ("https://provider.invalid/v1/%41%49%7A%61syntheticEncodedPath", "AIza" + "syntheticEncodedPath"),
                ("https://provider.invalid/v1?Basic%20synthetic-basic-credential", "synthetic-basic-credential"),
                ("https://provider.invalid/v1#Bearer%20synthetic-bearer-credential", "synthetic-bearer-credential")
            };
            foreach (var endpoint in unsafeEndpoints)
            {
                Assert(
                    ProviderValidator.GetEndpointErrorCode(endpoint.Url, allowLoopbackHttp: false, allowEmpty: false)
                    == "UrlContainsCredential",
                    "A credential-bearing provider URL was accepted by common validation.");

                try
                {
                    TranslationApiClient.ValidateEndpoint(endpoint.Url, allowLoopbackHttp: false);
                    throw new InvalidOperationException("The API client accepted a credential-bearing provider URL.");
                }
                catch (InvalidDataException exception)
                {
                    Assert(!exception.Message.Contains(endpoint.Secret, StringComparison.Ordinal), "API validation exposed a credential value.");
                    Assert(!exception.Message.Contains(endpoint.Url, StringComparison.Ordinal), "API validation exposed the provider URL.");
                }

                var unsafeSettings = new AppSettingsDocument
                {
                    ApiProviderId = "Custom",
                    ApiProviders = new Dictionary<string, ApiProviderSettings>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Custom"] = new() { Name = "Synthetic", Url = endpoint.Url, Model = "synthetic-model" }
                    }
                };
                try
                {
                    repository.Save(unsafeSettings);
                    throw new InvalidOperationException("SettingsRepository accepted a credential-bearing provider URL.");
                }
                catch (InvalidDataException exception)
                {
                    Assert(!exception.Message.Contains(endpoint.Secret, StringComparison.Ordinal), "Settings validation exposed a credential value.");
                    Assert(!exception.Message.Contains(endpoint.Url, StringComparison.Ordinal), "Settings validation exposed the provider URL.");
                }
                Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(safeBaseline), "A rejected provider URL changed the settings file.");
            }

            const string safeQueryUrl = "https://provider.invalid/oauth/token?format=json&version=1";
            safeSettings.ApiProviders["Cerebras"].Url = safeQueryUrl;
            repository.Save(safeSettings);
            Assert(repository.Load().ApiProviders["Cerebras"].Url == safeQueryUrl, "A non-sensitive provider query did not round-trip.");
            Assert(
                ProviderValidator.GetEndpointErrorCode(
                    "https://provider.invalid/oauth/token?opaque=synthetic-value",
                    allowLoopbackHttp: false,
                    allowEmpty: false) == "UrlQueryNotAllowed",
                "An unrecognized endpoint query parameter bypassed the safe allowlist.");
            Assert(
                ProviderValidator.GetEndpointErrorCode(
                    "https://provider.invalid/oauth/token#section",
                    allowLoopbackHttp: false,
                    allowEmpty: false) == "UrlFragmentNotAllowed",
                "A provider URL fragment was accepted even though fragments are not sent to the server.");

            const string storedSecret = "synthetic-stored-secret";
            const string storedUnsafeUrl = "https://provider.invalid/v1?api_key=" + storedSecret;
            var storedUnsafeDocument = new AppSettingsDocument
            {
                ApiProviderId = "Custom",
                ApiProviders = new Dictionary<string, ApiProviderSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Custom"] = new() { Name = "Synthetic", Url = storedUnsafeUrl, Model = "synthetic-model" }
                }
            };
            File.WriteAllText(paths.Settings, JsonSerializer.Serialize(storedUnsafeDocument), new UTF8Encoding(false));
            var storedUnsafeBytes = File.ReadAllBytes(paths.Settings);
            var loadedUnsafe = repository.Load();
            Assert(loadedUnsafe.ApiProviders["Custom"].Url == storedUnsafeUrl, "An existing unsafe URL was silently overwritten while loading.");
            try
            {
                repository.Save(loadedUnsafe);
                throw new InvalidOperationException("An existing unsafe URL was written back without correction.");
            }
            catch (InvalidDataException exception)
            {
                Assert(!exception.Message.Contains(storedSecret, StringComparison.Ordinal), "Existing unsafe URL validation exposed a credential value.");
                Assert(!exception.Message.Contains(storedUnsafeUrl, StringComparison.Ordinal), "Existing unsafe URL validation exposed the provider URL.");
            }
            Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(storedUnsafeBytes), "A rejected existing unsafe URL changed the settings file.");

            Assert(!typeof(ApiProviderSettings).GetProperties().Any(p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)), "Provider settings expose a key property.");
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
        Assert(!loopback.Valid && loopback.ErrorCodes.Contains("HttpsRequired"),
            "The public provider configuration accepted an insecure loopback HTTP endpoint.");
        var google = ApiProviderCatalog.Get("Google");
        var googleWithoutConfiguredUrl = ProviderValidator.Validate(google, new ApiProviderSettings
        {
            Name = google.Name,
            Url = string.Empty,
            Model = string.Empty,
            Temperature = 0
        }, 0);
        Assert(googleWithoutConfiguredUrl.Valid, "The key-free Google fallback now requires a configured URL.");
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
            var swapRun = Path.Combine(reviewRoot, "swap-run");
            var missingMarker = Path.Combine(reviewRoot, "missing-marker");
            var malformedMarker = Path.Combine(reviewRoot, "malformed-marker");
            var otherProjectMarker = Path.Combine(reviewRoot, "other-project-marker");
            var wrongModMarker = Path.Combine(reviewRoot, "wrong-mod-marker");
            var legacyMarker = Path.Combine(reviewRoot, "legacy-marker");
            var copiedMarker = Path.Combine(reviewRoot, "copied-marker");
            var outside = Path.Combine(root, "outside");
            var modRoot = Path.Combine(root, "mod");
            foreach (var directory in new[]
                     {
                         run, swapRun, missingMarker, malformedMarker, otherProjectMarker, wrongModMarker, legacyMarker, copiedMarker
                     })
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "keep.txt"), "keep");
            }
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(modRoot);
            Directory.CreateDirectory(Path.Combine(run, "nested"));
            File.WriteAllText(Path.Combine(run, "nested", "owned.txt"), "owned");
            Directory.CreateDirectory(Path.Combine(modRoot, "Languages", "Korean"));
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(modRoot, "Languages", "Korean", "keep.xml"), "keep");
            var project = new TranslationProject
            {
                Id = "fixture",
                ModRoot = modRoot,
                LatestReviewRoot = run,
                Runs =
                [
                    new ProjectRun { ReviewRoot = outside },
                    new ProjectRun { ReviewRoot = modRoot },
                    new ProjectRun { ReviewRoot = swapRun },
                    new ProjectRun { ReviewRoot = missingMarker },
                    new ProjectRun { ReviewRoot = malformedMarker },
                    new ProjectRun { ReviewRoot = otherProjectMarker },
                    new ProjectRun { ReviewRoot = wrongModMarker },
                    new ProjectRun { ReviewRoot = legacyMarker },
                    new ProjectRun { ReviewRoot = copiedMarker }
                ]
            };
            ProjectCleanupService.WriteOwnershipMarker(project, run);
            ProjectCleanupService.WriteOwnershipMarker(project, swapRun);
            File.Copy(
                Path.Combine(run, ".rimworld-ai-project.json"),
                Path.Combine(copiedMarker, ".rimworld-ai-project.json"));
            File.WriteAllText(Path.Combine(malformedMarker, ".rimworld-ai-project.json"), "{broken", new UTF8Encoding(false));
            ProjectCleanupService.WriteOwnershipMarker(new TranslationProject
            {
                Id = "another-project",
                ModRoot = modRoot
            }, otherProjectMarker);
            var differentMod = Path.Combine(root, "different-mod");
            Directory.CreateDirectory(differentMod);
            ProjectCleanupService.WriteOwnershipMarker(new TranslationProject
            {
                Id = project.Id,
                ModRoot = differentMod
            }, wrongModMarker);
            File.WriteAllText(
                Path.Combine(legacyMarker, ".rimworld-ai-project.json"),
                JsonSerializer.Serialize(new
                {
                    version = 1,
                    projectId = project.Id,
                    modRoot,
                    createdAt = DateTimeOffset.Now.ToString("O")
                }),
                new UTF8Encoding(false));

            var service = new ProjectCleanupService();
            var plan = service.BuildPlan(project, [reviewRoot]);
            Assert(plan.SafePaths.SequenceEqual([Path.GetFullPath(run), Path.GetFullPath(swapRun), Path.GetFullPath(legacyMarker)]),
                "Owned current and legacy v1 review paths were not isolated.");
            Assert(plan.SafePathIdentities.Count == 3, "Confirmed cleanup paths were not pinned to stable directory identities.");
            Assert(plan.UnsafePaths.Count == 7, "Copied, marker-less, mismatched, malformed, or out-of-bound cleanup paths were not reported.");

            var mappedCleanup = new ProjectCleanupService(_ => DriveType.Network)
            {
                BeforeDirectoryProbeTestHook = _ =>
                    throw new InvalidOperationException("A mapped-network cleanup path reached a filesystem probe.")
            };
            var mappedPlan = mappedCleanup.BuildPlan(project, [reviewRoot]);
            Assert(mappedPlan.SafePaths.Count == 0
                   && mappedPlan.UnsafePaths.Contains(Path.GetFullPath(run), StringComparer.OrdinalIgnoreCase),
                "A synthetic mapped-network volume entered the cleanup plan.");
            var mappedMarker = Path.Combine(reviewRoot, "mapped-network-marker");
            _ = CaptureException<InvalidDataException>(() =>
                ProjectCleanupService.WriteOwnershipMarker(
                    project,
                    mappedMarker,
                    _ => DriveType.Network));
            Assert(!Directory.Exists(mappedMarker),
                "A synthetic mapped-network volume was written while creating an ownership marker.");
            var mappedRemovalFailures = mappedCleanup.Remove(project, plan);
            Assert(mappedRemovalFailures.Count == 1
                   && Directory.Exists(run)
                   && Directory.Exists(swapRun),
                "A synthetic mapped-network volume entered destructive project cleanup.");

            var lateRun = Path.Combine(reviewRoot, "late-run");
            ProjectCleanupService.WriteOwnershipMarker(project, lateRun);
            var movedSwapRun = Path.Combine(reviewRoot, "swap-run-original");
            Directory.Move(swapRun, movedSwapRun);
            Directory.CreateDirectory(swapRun);
            File.Copy(
                Path.Combine(movedSwapRun, ".rimworld-ai-project.json"),
                Path.Combine(swapRun, ".rimworld-ai-project.json"));
            File.WriteAllText(Path.Combine(swapRun, "replacement.txt"), "replacement");

            var tamperedProject = new TranslationProject { Id = project.Id, ModRoot = Path.Combine(root, "tampered-mod") };
            var tamperedFailures = service.Remove(tamperedProject, plan);
            Assert(tamperedFailures.Count == 1 && Directory.Exists(run) && Directory.Exists(swapRun),
                "A tampered project record authorized deletion of an owned review.");

            var planFailures = service.Remove(project, plan);
            Assert(planFailures.Count == 1
                   && !Directory.Exists(run)
                   && Directory.Exists(swapRun)
                   && !Directory.Exists(legacyMarker),
                "A plan-time directory identity swap was not rejected while unchanged current and v1 runs were removed.");
            Assert(Directory.Exists(movedSwapRun) && Directory.Exists(lateRun),
                "Cleanup expanded beyond the confirmed plan allowlist.");

            var denied = new[] { missingMarker, malformedMarker, otherProjectMarker, wrongModMarker, copiedMarker };
            var failures = service.Remove(project, [reviewRoot], denied);
            Assert(failures.Count == denied.Length, "An invalid ownership marker authorized deletion.");
            Assert(denied.All(Directory.Exists), "A review with an invalid ownership marker was removed.");
            Assert(File.Exists(Path.Combine(outside, "keep.txt")), "Outside data was removed.");
            Assert(File.Exists(Path.Combine(modRoot, "Languages", "Korean", "keep.xml")), "Korean translation was removed.");

            var containmentCandidate = Path.Combine(reviewRoot, "contains-source-mod");
            var containedModRoot = Path.Combine(containmentCandidate, "actual-source-mod");
            Directory.CreateDirectory(containedModRoot);
            var containmentProject = new TranslationProject
            {
                Id = "containment-project",
                ModRoot = containedModRoot,
                LatestReviewRoot = containmentCandidate
            };
            ProjectCleanupService.WriteOwnershipMarker(containmentProject, containmentCandidate);
            var containmentPlan = service.BuildPlan(containmentProject, [reviewRoot]);
            Assert(containmentPlan.SafePaths.Count == 0 && containmentPlan.UnsafePaths.Contains(Path.GetFullPath(containmentCandidate)),
                "A cleanup candidate containing the physical source mod was authorized.");
            Assert(service.Remove(containmentProject, [reviewRoot], [containmentCandidate]).Count == 1
                   && Directory.Exists(containedModRoot),
                "Physical source-mod containment was not rechecked at deletion.");

            var aliasedSourceCandidate = Path.Combine(reviewRoot, "aliased-source-candidate");
            Directory.CreateDirectory(aliasedSourceCandidate);
            var sourceAlias = Path.Combine(root, "source-mod-alias");
            Directory.CreateSymbolicLink(sourceAlias, aliasedSourceCandidate);
            var aliasProject = new TranslationProject
            {
                Id = "alias-project",
                ModRoot = sourceAlias,
                LatestReviewRoot = aliasedSourceCandidate
            };
            var aliasPlan = service.BuildPlan(aliasProject, [reviewRoot]);
            Assert(aliasPlan.SafePaths.Count == 0 && aliasPlan.UnsafePaths.Contains(Path.GetFullPath(aliasedSourceCandidate)),
                "A cleanup candidate identical to an aliased physical source mod was authorized.");
            Assert(service.Remove(aliasProject, [reviewRoot], [aliasedSourceCandidate]).Count == 1
                   && Directory.Exists(aliasedSourceCandidate),
                "An aliased physical source mod was deleted through a review path.");

            var finalSwapRun = Path.Combine(reviewRoot, "final-swap-run");
            var displacedFinalSwapRun = Path.Combine(reviewRoot, "final-swap-original");
            var finalSwapProject = new TranslationProject
            {
                Id = "final-swap-project",
                ModRoot = modRoot,
                LatestReviewRoot = finalSwapRun,
                Runs = [new ProjectRun { ReviewRoot = finalSwapRun }]
            };
            ProjectCleanupService.WriteOwnershipMarker(finalSwapProject, finalSwapRun);
            File.WriteAllText(Path.Combine(finalSwapRun, "owned.txt"), "owned");
            var finalSwapPlan = service.BuildPlan(finalSwapProject, [reviewRoot]);
            try
            {
                service.BeforeDirectoryQuarantineTestHook = candidate =>
                {
                    Assert(candidate.Equals(finalSwapRun, StringComparison.OrdinalIgnoreCase),
                        "The final cleanup swap hook received an unexpected path.");
                    Directory.Move(finalSwapRun, displacedFinalSwapRun);
                    Directory.CreateDirectory(finalSwapRun);
                    File.WriteAllText(Path.Combine(finalSwapRun, "replacement.txt"), "replacement");
                };
                var finalSwapFailures = service.Remove(finalSwapProject, finalSwapPlan);
                Assert(finalSwapFailures.Count == 1
                       && Directory.Exists(finalSwapRun)
                       && File.ReadAllText(Path.Combine(finalSwapRun, "replacement.txt")) == "replacement"
                       && Directory.Exists(displacedFinalSwapRun)
                       && File.ReadAllText(Path.Combine(displacedFinalSwapRun, "owned.txt")) == "owned",
                    "A final cleanup namespace swap deleted the canonical replacement or displaced original.");
            }
            finally
            {
                service.BeforeDirectoryQuarantineTestHook = null;
            }

            var rewriteParent = Path.Combine(root, "same-id-rewrite-parent");
            var rewriteRun = Path.Combine(rewriteParent, "same-id-rewrite-run");
            var rewriteFile = Path.Combine(rewriteRun, "rewrite.bin");
            Directory.CreateDirectory(rewriteRun);
            File.WriteAllBytes(rewriteFile, "before"u8.ToArray());
            var rewriteIdentity = PathSafety.GetExistingDirectoryIdentity(rewriteRun);
            var rewriteCleanup = ExactDirectoryCleanup.QuarantineAndDelete(
                rewriteRun,
                rewriteIdentity,
                afterQuarantineInventoryTestHook: quarantinedPath =>
                {
                    using var writer = new FileStream(
                        Path.Combine(quarantinedPath, "rewrite.bin"),
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    writer.Position = 0;
                    writer.Write("after!"u8);
                    writer.Flush(flushToDisk: true);
                });
            var rewritePreservedPath = rewriteCleanup.PreservedPath;
            Assert(!rewriteCleanup.Removed
                   && !string.IsNullOrWhiteSpace(rewritePreservedPath)
                   && Directory.Exists(rewritePreservedPath)
                   && File.ReadAllBytes(Path.Combine(rewritePreservedPath!, "rewrite.bin"))
                        .SequenceEqual("after!"u8.ToArray()),
                "A same-file-ID rewrite after inventory was deleted instead of preserving its quarantine.");

            var unknownEntryRun = Path.Combine(root, "late-unknown-entry-run");
            Directory.CreateDirectory(unknownEntryRun);
            File.WriteAllText(
                Path.Combine(unknownEntryRun, "owned.txt"),
                "owned",
                new UTF8Encoding(false));
            var unknownEntryCleanup = ExactDirectoryCleanup.QuarantineAndDelete(
                unknownEntryRun,
                PathSafety.GetExistingDirectoryIdentity(unknownEntryRun),
                afterQuarantineInventoryTestHook: quarantinedPath =>
                    File.WriteAllText(
                        Path.Combine(quarantinedPath, "late-unknown.txt"),
                        "unknown-winner",
                        new UTF8Encoding(false)));
            var unknownEntryPreservedPath = unknownEntryCleanup.PreservedPath;
            Assert(!unknownEntryCleanup.Removed
                   && !string.IsNullOrWhiteSpace(unknownEntryPreservedPath)
                   && File.ReadAllText(Path.Combine(unknownEntryPreservedPath!, "owned.txt")) == "owned"
                   && File.ReadAllText(Path.Combine(unknownEntryPreservedPath!, "late-unknown.txt")) == "unknown-winner",
                "A late unknown quarantine entry or the verified original was not preserved.");

            var pinnedParent = Path.Combine(root, "pinned-cleanup-parent");
            var pinnedRun = Path.Combine(pinnedParent, "owned-run");
            var displacedParent = Path.Combine(root, "displaced-cleanup-parent");
            Directory.CreateDirectory(pinnedRun);
            File.WriteAllText(Path.Combine(pinnedRun, "owned.txt"), "owned", new UTF8Encoding(false));
            var pinnedRunIdentity = PathSafety.GetExistingDirectoryIdentity(pinnedRun);
            var parentRenameBlocked = false;
            var pinnedCleanup = ExactDirectoryCleanup.QuarantineAndDelete(
                pinnedRun,
                pinnedRunIdentity,
                beforeQuarantineTestHook: _ =>
                {
                    try
                    {
                        Directory.Move(pinnedParent, displacedParent);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        parentRenameBlocked = true;
                    }
                });
            Assert(parentRenameBlocked
                   && pinnedCleanup.Removed
                   && Directory.Exists(pinnedParent)
                   && !Directory.Exists(pinnedRun)
                   && !Directory.Exists(displacedParent)
                   && !Directory.EnumerateFileSystemEntries(pinnedParent).Any(),
                $"The exact cleanup parent namespace was not pinned throughout quarantine rename. "
                + $"blocked={parentRenameBlocked}; removed={pinnedCleanup.Removed}; "
                + $"parent={Directory.Exists(pinnedParent)}; source={Directory.Exists(pinnedRun)}; "
                + $"displaced={Directory.Exists(displacedParent)}; "
                + $"entries={(Directory.Exists(pinnedParent) ? Directory.EnumerateFileSystemEntries(pinnedParent).Count() : -1)}; "
                 + $"failure={pinnedCleanup.Failure}");

            var retryParent = Path.Combine(root, "transient-quarantine-retry-parent");
            var retryRun = Path.Combine(retryParent, "owned-run");
            Directory.CreateDirectory(retryRun);
            File.WriteAllText(Path.Combine(retryRun, "owned.txt"), "owned", new UTF8Encoding(false));
            var retryAttempts = 0;
            var retryCleanup = ExactDirectoryCleanup.QuarantineAndDelete(
                retryRun,
                PathSafety.GetExistingDirectoryIdentity(retryRun),
                quarantineRenameErrorTestHook: attempt =>
                {
                    retryAttempts = attempt;
                    return attempt == 1 ? 5 : null;
                });
            Assert(retryCleanup.Removed
                   && retryAttempts == 2
                   && Directory.Exists(retryParent)
                   && !Directory.Exists(retryRun)
                   && !Directory.EnumerateFileSystemEntries(retryParent).Any(),
                $"A transient quarantine access denial was not retried against the pinned identity. "
                + $"attempts={retryAttempts}; removed={retryCleanup.Removed}; failure={retryCleanup.Failure}");

            var appPaths = new AppDataPaths(Path.Combine(root, "plan-drift-appdata"));
            appPaths.EnsureExists();
            var repository = new ProjectRepository(new AtomicJsonStore(), appPaths);
            var projectService = new ProjectService(repository, new RimWorldModDiscoveryService(), new ProjectCleanupService());
            var confirmedRun = Path.Combine(appPaths.Reviews, "confirmed-run");
            var newlyDiscoveredRun = Path.Combine(appPaths.Reviews, "newly-discovered-run");
            var confirmedProject = new TranslationProject
            {
                Id = "confirmed-project",
                ModRoot = modRoot,
                LatestReviewRoot = confirmedRun,
                Runs = [new ProjectRun { ReviewRoot = confirmedRun }]
            };
            ProjectCleanupService.WriteOwnershipMarker(confirmedProject, confirmedRun);
            var confirmedPlan = projectService.GetRemovalPlan(confirmedProject, [appPaths.Reviews]);
            ProjectCleanupService.WriteOwnershipMarker(confirmedProject, newlyDiscoveredRun);
            var document = new ProjectStoreDocument { Projects = [confirmedProject] };
            var projectRemoval = projectService.Remove(document, confirmedProject, confirmedPlan);
            Assert(projectRemoval.ProjectRecordRemoved
                   && projectRemoval.CleanupFailures.Count == 0
                   && !Directory.Exists(confirmedRun)
                   && Directory.Exists(newlyDiscoveredRun),
                "Project removal discarded the confirmed plan and expanded to a newly discovered folder.");
            Assert(document.Projects.Count == 0, "A successful exact-plan project removal retained the project record.");
        });
    }

    private static void DiscoveryIsolatedRootBoundary()
    {
        WithTempRoot(root =>
        {
            var synthetic = Path.Combine(root, "SyntheticSteam");
            var outside = Path.Combine(root, "OutsideSteam");
            Directory.CreateDirectory(synthetic);
            Directory.CreateDirectory(outside);
            File.WriteAllText(
                Path.Combine(synthetic, RimWorldModDiscoveryService.IsolationMarkerFileName),
                RimWorldModDiscoveryService.IsolationMarkerContent + Environment.NewLine,
                new System.Text.UTF8Encoding(false));

            var safeMod = Path.Combine(synthetic, "steamapps", "workshop", "content", "294100", "111");
            var outsideMod = Path.Combine(outside, "steamapps", "workshop", "content", "294100", "222");
            WriteDiscoveryFixture(safeMod, "Safe Fixture", "fixture.safe");
            WriteDiscoveryFixture(outsideMod, "Outside Fixture", "fixture.outside");
            Directory.CreateDirectory(Path.Combine(synthetic, "steamapps"));
            File.WriteAllText(
                Path.Combine(synthetic, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\"\n{{\n  \"1\" {{ \"path\" \"{outside.Replace("\\", "\\\\")}\" }}\n}}\n");

            var service = RimWorldModDiscoveryService.CreateIsolated(synthetic);
            Assert(service.IsIsolated && service.IsolatedSteamRoot == Path.GetFullPath(synthetic), "Isolated discovery mode was not retained.");
            Assert(service.GetSteamLibraryRoots().SequenceEqual([Path.GetFullPath(synthetic)], StringComparer.OrdinalIgnoreCase), "Isolated discovery expanded a Steam library file.");
            var found = service.Discover();
            Assert(found.Count == 1 && found[0].PackageId == "fixture.safe", "Isolated discovery escaped its synthetic root.");
            AssertThrows<InvalidOperationException>(() => service.GetSteamLibraryRoots([outside]));
            AssertThrows<InvalidOperationException>(() => service.GetModInfo(outsideMod, "Workshop"));

            var unmarked = Path.Combine(root, "UnmarkedSteam");
            Directory.CreateDirectory(unmarked);
            AssertThrows<InvalidDataException>(() => RimWorldModDiscoveryService.CreateIsolated(unmarked));
        });
    }

    private static void WriteDiscoveryFixture(string modRoot, string name, string packageId)
    {
        Directory.CreateDirectory(Path.Combine(modRoot, "About"));
        Directory.CreateDirectory(Path.Combine(modRoot, "Defs"));
        File.WriteAllText(
            Path.Combine(modRoot, "About", "About.xml"),
            $"<ModMetaData><name>{name}</name><packageId>{packageId}</packageId></ModMetaData>");
    }

    private static void SourceExtraction()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var extractor = CreateExtractor();
            var result = extractor.Extract(modRoot, "Auto", "CodexAI");
            var keys = result.Entries.Select(entry => entry.Key).ToArray();
            Assert(result.Entries.Count == 7, $"Unexpected source entry count: {result.Entries.Count}");
            Assert(ExpectedSourceKeys.All(keys.Contains), "Expected source key was not extracted.");
            Assert(result.SkippedInternalIdentifiers.Count == 1 && result.SkippedInternalIdentifiers[0].Field.Equals("compClass", StringComparison.OrdinalIgnoreCase), "Internal identifier audit changed.");
        });
    }

    private static void SourceDefSafety()
    {
        WithFixture("DefSafetyMod", modRoot =>
        {
            var result = CreateExtractor().Extract(modRoot, "Auto", "CodexAI");
            var keys = result.Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var expected in ExpectedSafeDefKeys)
            {
                Assert(keys.Contains(expected), $"Display field was not extracted: {expected}");
            }

            foreach (var forbidden in ForbiddenDefKeys)
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
            Assert(!ContainsFiles(Path.Combine(modRoot, "Languages", "Korean")), "Source-only run modified the source mod.");
            using var progress = JsonDocument.Parse(File.ReadAllText(Directory.EnumerateFiles(Path.Combine(result.ReviewRoot!, "_TranslationAudit"), "*-progress.json").Single()));
            Assert(progress.RootElement[0].GetProperty("complete").GetBoolean(), "Source-only checkpoint was not complete.");
        });
    }

    private static void TranslationGoogleBlankEndpointFallback()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var handler = new FakeGoogleHandler();
            var engine = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                apiClientFactory: keys => new TranslationApiClient(keys, handler));
            var result = engine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = modRoot,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(Directory.GetParent(modRoot)!.FullName, "reviews-google-blank"),
                SourceLanguageFolder = "Auto",
                ApiKeys = [],
                Provider = ApiProviderCatalog.Get("Google"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Google",
                    Url = string.Empty,
                    Model = string.Empty,
                    Temperature = 0
                },
                BatchSize = 40,
                MaxRetries = 1,
                Timeout = TimeSpan.FromSeconds(5),
                GeneratedGlossaryPath = Path.Combine(RepositoryRoot(), "src", "RimWorldAiTranslator.App", "Assets", "glossary.generated.ko.json")
            }).GetAwaiter().GetResult();

            var catalogEndpoint = new Uri(ApiProviderCatalog.Get("Google").Url);
            Assert(handler.RequestUris.Count == result.Rows.Count && handler.RequestUris.Count > 0,
                "Explicit Google translation with a blank configured URL did not issue requests.");
            Assert(handler.RequestUris.All(uri =>
                    uri.Scheme.Equals(catalogEndpoint.Scheme, StringComparison.OrdinalIgnoreCase)
                    && uri.Host.Equals(catalogEndpoint.Host, StringComparison.OrdinalIgnoreCase)
                    && uri.Port == catalogEndpoint.Port
                    && uri.AbsolutePath.Equals(catalogEndpoint.AbsolutePath, StringComparison.Ordinal)
                    && uri.Query.Contains("client=gtx", StringComparison.Ordinal)
                    && uri.Query.Contains("q=", StringComparison.Ordinal)),
                "A blank explicit Google URL did not fall back to the catalog endpoint.");
            Assert(result.Rows.All(row => !string.IsNullOrWhiteSpace(row.Candidate)),
                "Google blank-endpoint fallback lost translated candidates.");
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
                GeneratedGlossaryPath = Path.Combine(RepositoryRoot(), "src", "RimWorldAiTranslator.App", "Assets", "glossary.generated.ko.json")
            };
            var progress = new List<TranslationProgress>();
            var result = engine.RunAsync(options, new ProgressCollector(progress)).GetAwaiter().GetResult();
            Assert(result.Rows.Count == 7 && result.Rows.All(row => !string.IsNullOrWhiteSpace(row.Candidate)), "API translation lost candidates.");
            Assert(handler.RequestCount == 5, $"Retry or batch count changed: {handler.RequestCount}");
            Assert(handler.AuthorizationValues.Contains("Bearer key-one") && handler.AuthorizationValues.Contains("Bearer key-two"), "Multiple API keys were not rotated.");
            Assert(progress.Count(item => item.Stage == "retry") == 1, "Transient failure was retried by nested loops.");
            var workProgress = progress.Where(item => item.Stage is "translate" or "retry").ToArray();
            Assert(workProgress.Length > 0
                   && workProgress.All(item => item.Total == 7)
                   && workProgress.Select(item => item.Current).SequenceEqual(
                       workProgress.Select(item => item.Current).OrderBy(value => value)),
                "AI translation progress was not reported as monotonic completed-entry percentages.");
            Assert(workProgress.Last().Current == 7,
                "AI translation progress did not explicitly reach 100 percent.");
            Assert(!ContainsFiles(Path.Combine(modRoot, "Languages", "Korean")), "Review-only API run modified the source mod.");
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
            SaveBoundReviewDocument(store, run.ReviewRoot!, decisions);
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
            Assert(files.All(file => !File.Exists(file + ".bak")),
                "A first-time local apply created unexpected backup files for previously absent outputs.");
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
            SaveBoundReviewDocument(store, run.ReviewRoot!, document);
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

    private static SourceExtractor CreateExtractor() => new(Path.Combine(RepositoryRoot(), "src", "RimWorldAiTranslator.App", "Assets", "rimworld-def-field-rules.txt"));

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

    private static bool ContainsFiles(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();

    private static void SaveBoundReviewDocument(
        AtomicJsonStore store,
        string reviewRoot,
        ReviewDecisionDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Comparison))
            document.Comparison = ReviewWorkspaceService.FindComparisonFile(reviewRoot);
        var evidence = ReviewComparisonDocument.LoadExact(reviewRoot, document.Comparison).Evidence;
        new ReviewRepository(store).Save(reviewRoot, document, evidence);
    }

    private static void WriteBoundReviewDocumentUnchecked(
        AtomicJsonStore store,
        string reviewRoot,
        ReviewDecisionDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Comparison))
            document.Comparison = ReviewWorkspaceService.FindComparisonFile(reviewRoot);
        var evidence = ReviewComparisonDocument.LoadExact(reviewRoot, document.Comparison).Evidence;
        document.Version = ReviewDecisionDocument.CurrentVersion;
        document.Sparse = true;
        document.ReviewRoot = Path.GetFullPath(reviewRoot);
        document.Comparison = evidence.Path;
        document.ComparisonSha256 = evidence.Sha256;
        store.Write(ReviewRepository.GetDecisionPath(reviewRoot), document);
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

    private sealed class FakeGoogleHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUris.Add(request.RequestUri ?? throw new InvalidOperationException("Google request URI was missing."));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[[[\"합성 번역\",null,null,null]],null,\"en\"]", Encoding.UTF8, "application/json")
            });
        }
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
