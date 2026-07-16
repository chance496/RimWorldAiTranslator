using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Logging;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Validation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static readonly string[] Phase07CredentialExtensionNames =
    [
        "apiToken",
        "secretKey",
        "refreshToken",
        "apiKeyValue",
        "apiKeyBackup",
        "apiKey2",
        "apiKeyBackup2",
        "apiKey2Backup",
        "apiKeySecondary3Fallback",
        "accessTokenBackup",
        "accessTokenValue",
        "api-key:synthetic-property-assignment-marker",
        "ghp_SYNTHETICMARKERONLY123456789",
        "custom=ghp_SYNTHETICMARKERONLY123456789"
    ];
    private static readonly string[] Phase07CredentialAssignments =
    [
        "custom=ghp_SYNTHETICMARKERONLY123456789",
        "foo:AKIASYNTHETICMARKER1"
    ];
    private static readonly string[] Phase07OpaqueProviderTexts =
    [
        "0123456789abcdef0123456789abcdef",
        "550e8400-e29b-41d4-a716-446655440000",
        "550e8400-e29b-41d4-a716-446655440000:fx",
        "ya29.syntheticOpaqueAccessToken1234567890",
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzeW50aGV0aWMifQ.QWxhZGRpbjpvcGVuIHNlc2FtZQ"
    ];
    private static readonly JsonElement[] Phase07StructuredGenericKeyValues =
    [
        JsonSerializer.SerializeToElement(new { value = "synthetic-opaque-provider-secret" }),
        JsonSerializer.SerializeToElement(new[] { "synthetic-opaque-provider-secret" }),
        JsonSerializer.SerializeToElement(42)
    ];

    private static void Phase07SecurityHardening()
    {
        Phase07SourceLanguageBoundary();
        Phase07WorkshopOutputBoundary();
        Phase07DryRunCancellationIsWriteFree();
        Phase07DiscoveryReparseBoundary();
        Phase07DiscoveryMetadataAndCardinalityBounds();
        Phase07DiscoveryRootAndLoadFolderReparseBoundary();
        Phase07ExtractionAggregateLimits();
        Phase07XmlDepthLimit();
        Phase07StorageAndGlossaryLimits();
        Phase07LogSanitization();
        Phase07ComparisonCsvFormulaNeutralization();
        Phase07HttpAndProviderConfiguration();
        Phase07SettingsExtensionSecrets();
        Phase07OversizedEntrySkipsTransport();
        Phase07ProviderRequestBudget();
        Phase07GoogleRequestBudget();
        Phase07RmkBuilderPlanBinding();
        Phase07ReservedNames();
    }

    private static void Phase07DiscoveryReparseBoundary()
    {
        WithTempRoot(root =>
        {
            var modRoot = Path.Combine(root, "SyntheticDiscoveryMod");
            var englishRoot = Path.Combine(modRoot, "Languages", "English", "Keyed");
            Directory.CreateDirectory(englishRoot);
            File.WriteAllText(
                Path.Combine(englishRoot, "Inside.xml"),
                "<LanguageData><Phase07.Discovery>inside</Phase07.Discovery></LanguageData>",
                new UTF8Encoding(false));

            var outsideRoot = Path.Combine(root, "OutsideDiscoveryLanguage");
            Directory.CreateDirectory(outsideRoot);
            File.WriteAllText(
                Path.Combine(outsideRoot, "Outside.xml"),
                "<LanguageData><Phase07.Discovery>outside</Phase07.Discovery></LanguageData>",
                new UTF8Encoding(false));
            var alias = Path.Combine(modRoot, "Languages", "French");
            try
            {
                Directory.CreateSymbolicLink(alias, outsideRoot);
                var options = new RimWorldModDiscoveryService().GetSourceLanguageOptions(modRoot);
                Assert(options.Any(option => option.Folder.Equals("English", StringComparison.Ordinal)),
                    "Safe source-language discovery was lost while filtering reparse points.");
                Assert(!options.Any(option => option.Folder.Equals("French", StringComparison.Ordinal)),
                    "Source-language discovery followed a reparse point outside the mod root.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[INFO] Source-language reparse fixture unavailable ({exception.GetType().Name}).");
            }
        });
    }

    private static void Phase07DiscoveryMetadataAndCardinalityBounds()
    {
        WithTempRoot(root =>
        {
            var steamRoot = Path.Combine(root, "SyntheticSteam");
            var localMods = Path.Combine(steamRoot, "steamapps", "common", "RimWorld", "Mods");
            var firstMod = Path.Combine(localMods, "FirstMod");
            var secondMod = Path.Combine(localMods, "SecondMod");
            Directory.CreateDirectory(Path.Combine(firstMod, "Defs"));
            Directory.CreateDirectory(Path.Combine(secondMod, "Defs"));
            AssertThrows<InvalidDataException>(() => new RimWorldModDiscoveryService(
                new RimWorldModDiscoveryLimits { MaximumDiscoveredMods = 1 }).Discover([steamRoot]));
            AssertThrows<InvalidDataException>(() => new RimWorldModDiscoveryService(
                new RimWorldModDiscoveryLimits { MaximumCandidateDirectories = 1 }).Discover([steamRoot]));

            var aboutMod = Path.Combine(root, "AboutBudgetMod");
            var aboutPath = Path.Combine(aboutMod, "About", "About.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(aboutPath)!);
            Directory.CreateDirectory(Path.Combine(aboutMod, "Defs"));

            void AssertAboutRejected(RimWorldModDiscoveryLimits limits, string xml, string context)
            {
                File.WriteAllText(aboutPath, xml, new UTF8Encoding(false));
                var info = new RimWorldModDiscoveryService(limits).GetModInfo(aboutMod, "Local");
                Assert(info is not null
                       && info.Name.Equals("AboutBudgetMod", StringComparison.Ordinal)
                       && string.IsNullOrEmpty(info.PackageId),
                    $"{context} was not rejected without retaining oversized About.xml metadata.");
            }

            AssertAboutRejected(
                new RimWorldModDiscoveryLimits { MaximumAboutXmlBytes = 32 },
                "<ModMetaData><name>synthetic name beyond byte limit</name></ModMetaData>",
                "About.xml byte bound");
            AssertAboutRejected(
                new RimWorldModDiscoveryLimits { MaximumAboutXmlNodes = 2 },
                "<ModMetaData><name>synthetic</name></ModMetaData>",
                "About.xml node bound");
            AssertAboutRejected(
                new RimWorldModDiscoveryLimits { MaximumAboutMetadataValueCharacters = 4 },
                "<ModMetaData><name>synthetic</name></ModMetaData>",
                "About.xml value bound");
            AssertAboutRejected(
                new RimWorldModDiscoveryLimits { MaximumAboutRootChildren = 1 },
                "<ModMetaData><name>synthetic</name><description>extra</description></ModMetaData>",
                "About.xml root-child bound");
            AssertAboutRejected(
                new RimWorldModDiscoveryLimits { MaximumAboutMetadataElements = 1 },
                "<ModMetaData><name>synthetic</name><packageId>fixture.synthetic</packageId></ModMetaData>",
                "About.xml metadata-cardinality bound");

            var loadMod = Path.Combine(root, "LoadFolderBudgetMod");
            var versionRoot = Path.Combine(loadMod, "v1.6");
            var loadFoldersPath = Path.Combine(loadMod, "LoadFolders.xml");
            Directory.CreateDirectory(Path.Combine(versionRoot, "Defs"));
            const string normalLoadFolders = "<loadFolders><v1.6><li>v1.6</li></v1.6></loadFolders>";
            File.WriteAllText(loadFoldersPath, normalLoadFolders, new UTF8Encoding(false));
            Assert(new RimWorldModDiscoveryService().ResolveContentRoot(loadMod).Equals(
                    Path.GetFullPath(versionRoot),
                    StringComparison.OrdinalIgnoreCase),
                "Normal bounded LoadFolders.xml compatibility was lost.");

            void AssertLoadFoldersRejected(RimWorldModDiscoveryLimits limits, string xml, string context)
            {
                File.WriteAllText(loadFoldersPath, xml, new UTF8Encoding(false));
                var resolved = new RimWorldModDiscoveryService(limits).ResolveContentRoot(loadMod);
                Assert(resolved.Equals(Path.GetFullPath(loadMod), StringComparison.OrdinalIgnoreCase),
                    $"{context} was not rejected before selecting a version root.");
            }

            AssertLoadFoldersRejected(
                new RimWorldModDiscoveryLimits { MaximumLoadFoldersXmlBytes = 32 },
                normalLoadFolders,
                "LoadFolders.xml byte bound");
            AssertLoadFoldersRejected(
                new RimWorldModDiscoveryLimits { MaximumLoadFoldersXmlNodes = 2 },
                normalLoadFolders,
                "LoadFolders.xml node bound");
            AssertLoadFoldersRejected(
                new RimWorldModDiscoveryLimits { MaximumLoadFolderValueCharacters = 3 },
                normalLoadFolders,
                "LoadFolders.xml value bound");
            AssertLoadFoldersRejected(
                new RimWorldModDiscoveryLimits { MaximumLoadFoldersRootChildren = 1 },
                "<loadFolders><v1.5 /><v1.6><li>v1.6</li></v1.6></loadFolders>",
                "LoadFolders.xml root-child bound");
            AssertLoadFoldersRejected(
                new RimWorldModDiscoveryLimits { MaximumLoadFolderVersions = 1 },
                "<loadFolders><v1.5 /><v1.6><li>v1.6</li></v1.6></loadFolders>",
                "LoadFolders.xml version-cardinality bound");
            AssertLoadFoldersRejected(
                new RimWorldModDiscoveryLimits { MaximumLoadFolderChildrenPerVersion = 1 },
                "<loadFolders><v1.6><ignored /><li>v1.6</li></v1.6></loadFolders>",
                "LoadFolders.xml version-child bound");
            AssertLoadFoldersRejected(
                new RimWorldModDiscoveryLimits { MaximumLoadFolderItems = 1 },
                "<loadFolders><v1.6><li>missing</li><li>v1.6</li></v1.6></loadFolders>",
                "LoadFolders.xml item-cardinality bound");
            AssertLoadFoldersRejected(
                new RimWorldModDiscoveryLimits { MaximumLoadFolderItemAttributes = 1 },
                "<loadFolders><v1.6><li first='1' second='2'>v1.6</li></v1.6></loadFolders>",
                "LoadFolders.xml attribute bound");

            File.WriteAllText(aboutPath, "<ModMetaData><name>LOWERCASE SEARCH</name></ModMetaData>", new UTF8Encoding(false));
            var boundedSearch = new RimWorldModDiscoveryService(
                new RimWorldModDiscoveryLimits { MaximumSearchCharacters = 8 })
                .GetModInfo(aboutMod, "Local");
            Assert(boundedSearch is not null
                   && boundedSearch.Search.Length == 8
                   && boundedSearch.Search.Equals("lowercas", StringComparison.Ordinal),
                "The derived discovery search index was not lower-cased within its allocation bound.");
        });
    }

    private static void Phase07DiscoveryRootAndLoadFolderReparseBoundary()
    {
        WithTempRoot(root =>
        {
            var outside = Path.Combine(root, "OutsideDiscoveryMod");
            var outsideVersion = Path.Combine(outside, "NestedVersion");
            Directory.CreateDirectory(Path.Combine(outside, "Defs"));
            Directory.CreateDirectory(Path.Combine(outside, "About"));
            Directory.CreateDirectory(Path.Combine(outsideVersion, "Defs"));
            File.WriteAllText(
                Path.Combine(outside, "About", "About.xml"),
                "<ModMetaData><name>OUTSIDE METADATA</name><packageId>outside.fixture</packageId></ModMetaData>",
                new UTF8Encoding(false));

            try
            {
                var service = new RimWorldModDiscoveryService();
                var selectedAlias = Path.Combine(root, "SelectedAlias");
                Directory.CreateSymbolicLink(selectedAlias, outside);
                AssertThrows<InvalidDataException>(() => service.GetModInfo(selectedAlias, "Local"));

                var versionMod = Path.Combine(root, "VersionAliasMod");
                Directory.CreateDirectory(versionMod);
                Directory.CreateSymbolicLink(Path.Combine(versionMod, "v1.6"), outsideVersion);
                File.WriteAllText(
                    Path.Combine(versionMod, "LoadFolders.xml"),
                    "<loadFolders><v1.6><li>v1.6</li></v1.6></loadFolders>",
                    new UTF8Encoding(false));
                Assert(service.ResolveContentRoot(versionMod).Equals(
                        Path.GetFullPath(versionMod),
                        StringComparison.OrdinalIgnoreCase),
                    "LoadFolders.xml selected a reparse version directory outside the mod root.");

                var childComponentMod = Path.Combine(root, "ChildComponentAliasMod");
                Directory.CreateDirectory(childComponentMod);
                Directory.CreateSymbolicLink(Path.Combine(childComponentMod, "bridge"), outside);
                File.WriteAllText(
                    Path.Combine(childComponentMod, "LoadFolders.xml"),
                    "<loadFolders><v1.6><li>bridge/NestedVersion</li></v1.6></loadFolders>",
                    new UTF8Encoding(false));
                Assert(service.ResolveContentRoot(childComponentMod).Equals(
                        Path.GetFullPath(childComponentMod),
                        StringComparison.OrdinalIgnoreCase),
                    "LoadFolders.xml followed a redirected child component outside the mod root.");

                var nestedAboutMod = Path.Combine(root, "NestedAboutAliasMod");
                Directory.CreateDirectory(Path.Combine(nestedAboutMod, "Defs"));
                Directory.CreateSymbolicLink(Path.Combine(nestedAboutMod, "v1.6"), outside);
                var nestedInfo = service.GetModInfo(nestedAboutMod, "Local");
                Assert(nestedInfo is not null
                       && nestedInfo.Name.Equals("NestedAboutAliasMod", StringComparison.Ordinal)
                       && string.IsNullOrEmpty(nestedInfo.PackageId),
                    "Nested About.xml lookup consumed metadata through a redirected child.");

                var steamRoot = Path.Combine(root, "DiscoverySteam");
                var localMods = Path.Combine(steamRoot, "steamapps", "common", "RimWorld", "Mods");
                Directory.CreateDirectory(localMods);
                Directory.CreateSymbolicLink(Path.Combine(localMods, "RedirectedMod"), outside);
                Assert(!service.Discover([steamRoot]).Any(),
                    "Container discovery admitted a redirected local mod root.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[INFO] Discovery root reparse fixture unavailable ({exception.GetType().Name}).");
            }
        });
    }

    private static void Phase07ExtractionAggregateLimits()
    {
        WithTempRoot(root =>
        {
            var keyedRoot = Path.Combine(root, "Languages", "English", "Keyed");
            Directory.CreateDirectory(keyedRoot);
            File.WriteAllText(
                Path.Combine(keyedRoot, "Aggregate.xml"),
                "<LanguageData><Phase07.One>human one</Phase07.One><Phase07.Two>human two</Phase07.Two><Phase07.Three>human three</Phase07.Three></LanguageData>",
                new UTF8Encoding(false));

            var recordLimited = new SourceExtractor(
                defFieldRulesPath: null,
                maximumExtractedRecords: 2,
                maximumExtractedCharacters: 1_000_000,
                maximumScannedXmlBytes: 1_000_000);
            AssertThrows<InvalidDataException>(() => recordLimited.Extract(root, "English"));

            var characterLimited = new SourceExtractor(
                defFieldRulesPath: null,
                maximumExtractedRecords: 100,
                maximumExtractedCharacters: 1,
                maximumScannedXmlBytes: 1_000_000);
            AssertThrows<InvalidDataException>(() => characterLimited.Extract(root, "English"));

            var byteLimited = new SourceExtractor(
                defFieldRulesPath: null,
                maximumExtractedRecords: 100,
                maximumExtractedCharacters: 1_000_000,
                maximumScannedXmlBytes: 1);
            AssertThrows<InvalidDataException>(() => byteLimited.Extract(root, "English"));
        });
    }

    private static void Phase07SourceLanguageBoundary()
    {
        WithTempRoot(root =>
        {
            var modRoot = Path.Combine(root, "SyntheticMod");
            var keyedRoot = Path.Combine(modRoot, "Languages", "English", "Keyed");
            Directory.CreateDirectory(keyedRoot);
            File.WriteAllText(
                Path.Combine(keyedRoot, "Inside.xml"),
                "<LanguageData><Phase07.Safe>SAFE-INSIDE</Phase07.Safe></LanguageData>",
                new UTF8Encoding(false));

            const string outsideSentinel = "PHASE07-OUTSIDE-SENTINEL-DO-NOT-EXTRACT";
            var outsideRoot = Path.Combine(root, "OutsideLanguage");
            Directory.CreateDirectory(outsideRoot);
            File.WriteAllText(
                Path.Combine(outsideRoot, "Outside.xml"),
                $"<LanguageData><Phase07.Outside>{outsideSentinel}</Phase07.Outside></LanguageData>",
                new UTF8Encoding(false));

            var extractor = new SourceExtractor();
            AssertThrows<InvalidDataException>(() =>
                extractor.GetSourceLanguageRoots([modRoot], outsideRoot));
            AssertThrows<InvalidDataException>(() =>
                extractor.GetSourceLanguageRoots([modRoot], ".."));

            var extraction = extractor.Extract(modRoot, "English");
            Assert(extraction.Entries.Any(entry => entry.Text == "SAFE-INSIDE"),
                "A safe source-language folder was not extracted.");
            Assert(!extraction.Entries.Any(entry => entry.Text.Contains(outsideSentinel, StringComparison.Ordinal)),
                "Source extraction escaped the selected mod and consumed the outside sentinel.");

            var englishRoot = Path.Combine(modRoot, "Languages", "English");
            var defsModRoot = Path.Combine(root, "SyntheticDefsAliasMod");
            var defsAlias = Path.Combine(defsModRoot, "Defs");
            var outsideDefsRoot = Path.Combine(root, "OutsideDefs");
            Directory.CreateDirectory(defsModRoot);
            Directory.CreateDirectory(outsideDefsRoot);
            File.WriteAllText(
                Path.Combine(outsideDefsRoot, "OutsideDefs.xml"),
                $"<Defs><ThingDef><defName>Phase07Outside</defName><label>{outsideSentinel}</label></ThingDef></Defs>",
                new UTF8Encoding(false));
            try
            {
                Directory.Delete(englishRoot, recursive: true);
                Directory.CreateSymbolicLink(englishRoot, outsideRoot);
                AssertThrows<InvalidDataException>(() =>
                    extractor.GetSourceLanguageRoots([modRoot], "English"));
                AssertThrows<InvalidDataException>(() => extractor.Extract(modRoot, "English"));

                Directory.CreateSymbolicLink(defsAlias, outsideDefsRoot);
                var defsExtraction = extractor.Extract(defsModRoot, "Auto");
                Assert(!defsExtraction.Entries.Any(entry =>
                        entry.Text.Contains(outsideSentinel, StringComparison.Ordinal)),
                    "Source extraction trusted an external Defs root reparse target.");
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or PlatformNotSupportedException)
            {
                Console.WriteLine($"[INFO] Source-extraction root reparse fixture unavailable ({exception.GetType().Name}).");
            }
            finally
            {
                if (Directory.Exists(englishRoot)) Directory.Delete(englishRoot);
                if (Directory.Exists(defsAlias)) Directory.Delete(defsAlias);
            }
        });
    }

    private static void Phase07WorkshopOutputBoundary()
    {
        WithTempRoot(root =>
        {
            static void CreateEnglishFixture(string modRoot)
            {
                var keyedRoot = Path.Combine(modRoot, "Languages", "English", "Keyed");
                Directory.CreateDirectory(keyedRoot);
                File.WriteAllText(
                    Path.Combine(keyedRoot, "Inside.xml"),
                    "<LanguageData><Phase07.Workshop>synthetic</Phase07.Workshop></LanguageData>",
                    new UTF8Encoding(false));
            }

            static void CopyDirectoryTree(string source, string destination)
            {
                Directory.CreateDirectory(destination);
                foreach (var file in Directory.EnumerateFiles(source))
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                foreach (var directory in Directory.EnumerateDirectories(source))
                    CopyDirectoryTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }

            static byte[] WriteBackupOnlyDecisionStore(string reviewRoot)
            {
                var decisionPath = ReviewRepository.GetDecisionPath(reviewRoot);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewDecisionDocument
                {
                    Version = ReviewDecisionDocument.CurrentVersion - 1,
                    Items = []
                });
                File.WriteAllBytes(decisionPath + ".bak", bytes);
                return bytes;
            }

            var workshopMod = Path.Combine(
                root,
                "Steam",
                "steamapps",
                "workshop",
                "content",
                "294100",
                "1234567890");
            CreateEnglishFixture(workshopMod);
            var engine = new TranslationEngine(root, new SourceExtractor());
            AssertThrows<InvalidOperationException>(() => engine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = workshopMod,
                SourceLanguageFolder = "English",
                ReviewOnly = false,
                SourceOnly = true
            }).GetAwaiter().GetResult());
            Assert(!Directory.Exists(Path.Combine(workshopMod, "_TranslationAudit"))
                   && !Directory.Exists(Path.Combine(workshopMod, "Languages", "Korean")),
                "Direct translation created output under a Workshop content root.");

            var localMod = Path.Combine(root, "LocalMod");
            CreateEnglishFixture(localMod);
            var workshopReviewRoot = Path.Combine(
                root,
                "Steam",
                "steamapps",
                "workshop",
                "content",
                "294100",
                "9876543210",
                "ReviewOutput");
            Directory.CreateDirectory(workshopReviewRoot);
            AssertThrows<InvalidOperationException>(() => engine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = localMod,
                SourceLanguageFolder = "English",
                ReviewOnly = true,
                ReviewRoot = workshopReviewRoot,
                SourceOnly = true
            }).GetAwaiter().GetResult());
            Assert(!Directory.EnumerateFileSystemEntries(workshopReviewRoot).Any(),
                "Review translation created output under a Workshop content root.");

            var safeReviewsRoot = Path.Combine(root, "SafeReviews");
            var safeRun = engine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = localMod,
                SourceLanguageFolder = "English",
                ReviewOnly = true,
                ReviewRoot = safeReviewsRoot,
                SourceOnly = true
            }).GetAwaiter().GetResult();
            var safeRunRoot = safeRun.ReviewRoot
                ?? throw new InvalidDataException("The safe review fixture did not produce a review root.");
            var workshopWorkspace = Path.Combine(
                root,
                "Steam",
                "steamapps",
                "workshop",
                "content",
                "294100",
                "2468135790",
                "ReviewWorkspace");
            CopyDirectoryTree(safeRunRoot, workshopWorkspace);
            var workshopDecisionPath = ReviewRepository.GetDecisionPath(workshopWorkspace);
            var workshopBackup = WriteBackupOnlyDecisionStore(workshopWorkspace);
            var unrestrictedReviews = new ReviewWorkspaceService(new AtomicJsonStore(), new SourceExtractor());
            AssertThrows<InvalidOperationException>(() => unrestrictedReviews.Load(workshopWorkspace));
            Assert(!File.Exists(workshopDecisionPath)
                   && File.ReadAllBytes(workshopDecisionPath + ".bak").SequenceEqual(workshopBackup),
                "Loading a Workshop-shaped review workspace created a decision store.");

            var workshopActivityProbes = 0;
            var workshopActivity = new ActivityService(new AtomicJsonStore(), safeReviewsRoot)
            {
                BeforeDecisionStoreProbeTestHook = _ => workshopActivityProbes++
            };
            _ = workshopActivity.Load(
            [
                new TranslationProject
                {
                    Name = "Workshop review boundary",
                    LatestReviewRoot = workshopWorkspace
                }
            ]);
            Assert(workshopActivityProbes == 0
                   && !File.Exists(workshopDecisionPath)
                   && File.ReadAllBytes(workshopDecisionPath + ".bak").SequenceEqual(workshopBackup),
                "Activity probing recovered a decision store under Workshop content.");

            var outsideApplicationReviews = Path.Combine(root, "OutsideApplicationReviews");
            CopyDirectoryTree(safeRunRoot, outsideApplicationReviews);
            var outsideDecisionPath = ReviewRepository.GetDecisionPath(outsideApplicationReviews);
            var outsideBackup = WriteBackupOnlyDecisionStore(outsideApplicationReviews);
            var applicationReviews = new ReviewWorkspaceService(
                new AtomicJsonStore(),
                new SourceExtractor(),
                safeReviewsRoot);
            AssertThrows<InvalidDataException>(() => applicationReviews.Load(outsideApplicationReviews));
            Assert(!File.Exists(outsideDecisionPath)
                   && File.ReadAllBytes(outsideDecisionPath + ".bak").SequenceEqual(outsideBackup),
                "A persisted review path outside the application review root created a decision store.");

            var previousProject = new TranslationProject
            {
                Name = "Previous review boundary",
                LatestReviewRoot = workshopWorkspace,
                Runs =
                [
                    new ProjectRun { ReviewRoot = outsideApplicationReviews }
                ]
            };
            _ = applicationReviews.Load(safeRunRoot, previousProject);
            Assert(!File.Exists(workshopDecisionPath)
                   && !File.Exists(outsideDecisionPath)
                   && File.ReadAllBytes(workshopDecisionPath + ".bak").SequenceEqual(workshopBackup)
                   && File.ReadAllBytes(outsideDecisionPath + ".bak").SequenceEqual(outsideBackup),
                "Previous-review discovery recovered a decision store outside the trusted review root.");

            var outsideActivityProbes = 0;
            var outsideActivity = new ActivityService(new AtomicJsonStore(), safeReviewsRoot)
            {
                BeforeDecisionStoreProbeTestHook = _ => outsideActivityProbes++
            };
            _ = outsideActivity.Load(
            [
                new TranslationProject
                {
                    Name = "Outside review boundary",
                    LatestReviewRoot = outsideApplicationReviews
                }
            ]);
            Assert(outsideActivityProbes == 0 && !File.Exists(outsideDecisionPath),
                "Activity probing reached a persisted review path outside the application review root.");

            var repository = new ReviewRepository(new AtomicJsonStore());
            AssertThrows<InvalidOperationException>(() => repository.Load(workshopWorkspace, safeReviewsRoot));
            AssertThrows<InvalidDataException>(() => repository.Load(outsideApplicationReviews, safeReviewsRoot));

            var apply = new ReviewApplyService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                new SourceExtractor(),
                safeReviewsRoot);
            ReviewApplyOptions ApplyOptions(string reviewRoot) => new()
            {
                ModRoot = localMod,
                ReviewRoot = reviewRoot,
                SourceLanguageFolder = "English",
                DryRun = true
            };
            AssertThrows<InvalidOperationException>(() => apply.Apply(ApplyOptions(workshopWorkspace)));
            AssertThrows<InvalidDataException>(() => apply.Apply(ApplyOptions(outsideApplicationReviews)));

            var rmkWorkspace = CreateRmkWorkspace(root, "Phase07ReviewBoundary", out var rmkEntryRoot);
            var rmk = new RmkExportService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                new SourceExtractor(),
                trustedReviewsRoot: safeReviewsRoot);
            RmkExportOptions RmkOptions(string reviewRoot) => new()
            {
                RmkWorkspaceRoot = rmkWorkspace,
                RmkEntryRoot = rmkEntryRoot,
                ReviewRoot = reviewRoot,
                ModRoot = localMod,
                WorkbookPath = Path.Combine(rmkEntryRoot, "phase07-review-boundary.xlsx"),
                DryRun = true
            };
            AssertThrows<InvalidOperationException>(() => rmk.Export(RmkOptions(workshopWorkspace)));
            AssertThrows<InvalidDataException>(() => rmk.Export(RmkOptions(outsideApplicationReviews)));
            Assert(!File.Exists(workshopDecisionPath)
                   && !File.Exists(outsideDecisionPath)
                   && !Directory.EnumerateFiles(workshopWorkspace, "*.corrupt-*", SearchOption.TopDirectoryOnly).Any()
                   && !Directory.EnumerateFiles(outsideApplicationReviews, "*.corrupt-*", SearchOption.TopDirectoryOnly).Any(),
                "A denied review reader left recovery output outside the trusted application review root.");

            var safeRecoveryRoot = Path.Combine(safeReviewsRoot, "SafeRecovery");
            Directory.CreateDirectory(safeRecoveryRoot);
            var safeRecoveryPath = ReviewRepository.GetDecisionPath(safeRecoveryRoot);
            var safeRecoveryBackup = WriteBackupOnlyDecisionStore(safeRecoveryRoot);
            JsonRecoveryNotice? recoveryNotice = null;
            var recoveryStore = new AtomicJsonStore();
            recoveryStore.RecoveredFromBackup += (_, notice) => recoveryNotice = notice;
            Assert(new ReviewRepository(recoveryStore).TryLoad(
                       safeRecoveryRoot,
                       out _,
                       safeReviewsRoot),
                "A valid backup-only decision store inside the trusted review root was not recovered.");
            Assert(File.ReadAllBytes(safeRecoveryPath).SequenceEqual(safeRecoveryBackup)
                   && File.ReadAllBytes(safeRecoveryPath + ".bak").SequenceEqual(safeRecoveryBackup)
                   && recoveryNotice?.StorePath.Equals(safeRecoveryPath, StringComparison.OrdinalIgnoreCase) == true,
                "Trusted decision recovery changed the valid backup or omitted its recovery notice.");
        });
    }

    private static void Phase07DryRunCancellationIsWriteFree()
    {
        WithTempRoot(root =>
        {
            var modRoot = Path.Combine(root, "DryRunMod");
            var keyedRoot = Path.Combine(modRoot, "Languages", "English", "Keyed");
            Directory.CreateDirectory(keyedRoot);
            File.WriteAllText(
                Path.Combine(keyedRoot, "DryRun.xml"),
                "<LanguageData><Phase07.DryRun>synthetic</Phase07.DryRun></LanguageData>",
                new UTF8Encoding(false));
            var engine = new TranslationEngine(root, new SourceExtractor());

            static TranslationEngineOptions Options(string mod, string reviews) => new()
            {
                ModRoot = mod,
                SourceLanguageFolder = "English",
                ReviewOnly = true,
                ReviewRoot = reviews,
                SourceOnly = true,
                DryRun = true
            };

            var preCancelledRoot = Path.Combine(root, "PreCancelledDryRun");
            using (var preCancelled = new CancellationTokenSource())
            {
                preCancelled.Cancel();
                var error = CaptureException<TranslationRunCanceledException>(() =>
                    engine.RunAsync(
                        Options(modRoot, preCancelledRoot),
                        cancellationToken: preCancelled.Token).GetAwaiter().GetResult());
                Assert(!error.CheckpointPersisted && error.PartialResult.ComparisonFile is null,
                    "A pre-cancelled DryRun reported a persisted checkpoint.");
            }
            Assert(!Directory.Exists(preCancelledRoot),
                "A pre-cancelled DryRun created a review workspace.");

            var scanCancelledRoot = Path.Combine(root, "ScanCancelledDryRun");
            using (var scanCancellation = new CancellationTokenSource())
            {
                var error = CaptureException<TranslationRunCanceledException>(() =>
                    engine.RunAsync(
                        Options(modRoot, scanCancelledRoot),
                        new CancelOnTranslationStage(scanCancellation, "scan"),
                        scanCancellation.Token).GetAwaiter().GetResult());
                Assert(!error.CheckpointPersisted && error.PartialResult.ComparisonFile is null,
                    "A scan-cancelled DryRun reported a persisted checkpoint.");
            }
            Assert(!Directory.Exists(scanCancelledRoot),
                "A scan-cancelled DryRun created a review workspace.");
        });
    }

    private sealed class CancelOnTranslationStage(
        CancellationTokenSource cancellation,
        string stage) : IProgress<TranslationProgress>
    {
        public void Report(TranslationProgress value)
        {
            if (value.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)) cancellation.Cancel();
        }
    }

    private static void Phase07XmlDepthLimit()
    {
        WithTempRoot(root =>
        {
            var path = Path.Combine(root, "too-deep.xml");
            var depth = SafeXml.DefaultMaximumDepth + 2;
            var xml = new StringBuilder(depth * 7);
            xml.Append("<root>");
            for (var index = 0; index < depth; index++) xml.Append("<node>");
            xml.Append("value");
            for (var index = 0; index < depth; index++) xml.Append("</node>");
            xml.Append("</root>");
            File.WriteAllText(path, xml.ToString(), new UTF8Encoding(false));

            AssertThrows<InvalidDataException>(() => SafeXml.Load(path));

            var stablePath = Path.Combine(root, "stable.xml");
            File.WriteAllText(
                stablePath,
                "<LanguageData><Phase07.Stable>synthetic</Phase07.Stable></LanguageData>",
                new UTF8Encoding(false));
            var hookInvoked = false;
            var loaded = SafeXml.LoadWithStructureValidatedHook(stablePath, () =>
            {
                hookInvoked = true;
                AssertThrows<IOException>(() => File.WriteAllText(
                    stablePath,
                    "<LanguageData><Phase07.Swapped>outside</Phase07.Swapped></LanguageData>",
                    new UTF8Encoding(false)));
                AssertThrows<IOException>(() => File.Move(stablePath, stablePath + ".swapped"));
            });
            Assert(hookInvoked
                   && loaded.Root?.Element("Phase07.Stable")?.Value == "synthetic"
                   && !File.Exists(stablePath + ".swapped"),
                "SafeXml did not keep one locked input handle across structure validation and materialization.");
        });
    }

    private static void Phase07StorageAndGlossaryLimits()
    {
        WithTempRoot(root =>
        {
            const int maximumJsonBytes = 64;
            var store = new AtomicJsonStore(maximumBytes: maximumJsonBytes);
            var oversizedRead = Path.Combine(root, "oversized-read.json");
            File.WriteAllText(
                oversizedRead,
                JsonSerializer.Serialize(new { value = new string('x', maximumJsonBytes * 2) }),
                new UTF8Encoding(false));
            AssertThrows<InvalidDataException>(() =>
                store.Read<Dictionary<string, string>>(oversizedRead));

            var oversizedWrite = Path.Combine(root, "oversized-write.json");
            AssertThrows<InvalidDataException>(() => store.Write(
                oversizedWrite,
                new Dictionary<string, string> { ["value"] = new string('y', maximumJsonBytes * 2) }));
            Assert(!File.Exists(oversizedWrite),
                "AtomicJsonStore created a file after rejecting an oversized value.");

            var glossaryPath = Path.Combine(root, "oversized-glossary.json");
            using (var stream = new FileStream(glossaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(GlossaryService.MaximumFileBytes + 1);
            AssertThrows<InvalidDataException>(() =>
                new GlossaryService().Load(glossaryPath, null, useCurated: false));
        });
    }

    private static void Phase07LogSanitization()
    {
        const string apiKeySecret = "synthetic-api-key-secret";
        const string headerSecret = "synthetic-header-secret";
        const string bearerSecret = "synthetic-bearer-secret";
        const string cerebrasSecret = "csk-synthetic12345678";
        const string openAiSecret = "sk-synthetic12345678";
        string[] rawCredentials =
        [
            "SK-SYNTHETIC12345678",
            "CSK-SYNTHETIC12345678",
            "gsk_SYNTHETIC12345678",
            "AIzaSYNTHETIC12345678",
            "ghp_SYNTHETIC12345678",
            "gho_SYNTHETIC12345678",
            "ghu_SYNTHETIC12345678",
            "ghs_SYNTHETIC12345678",
            "ghr_SYNTHETIC12345678",
            "github_pat_SYNTHETIC12345678",
            "hf_SYNTHETIC12345678",
            "glpat-SYNTHETIC12345678",
            "xoxb-SYNTHETIC12345678",
            "xoxp-SYNTHETIC12345678",
            "xoxa-SYNTHETIC12345678",
            "xoxr-SYNTHETIC12345678",
            "xoxs-SYNTHETIC12345678",
            "ya29.syntheticOpaqueAccessToken1234567890",
            "AKIA1234567890ABCDEF",
            "ASIA1234567890ABCDEF",
            "550e8400-e29b-41d4-a716-446655440000:fx",
            "550e8400e29b41d4a716446655440000:fx",
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzeW50aGV0aWMifQ.QWxhZGRpbjpvcGVuIHNlc2FtZQ"
        ];
        (string Name, string Value)[] namedCredentials =
        [
            ("password", "synthetic-password-head,synthetic-password-tail;end"),
            ("credential", "synthetic-credential-value"),
            ("subscriptionKey", "synthetic-subscription-value"),
            ("authKey", "synthetic-auth-value"),
            ("privateKey", "synthetic-private-value"),
            ("awsSecretAccessKey", "synthetic-aws-secret-value"),
            ("x-goog-api-key", "synthetic-google-key-value"),
            ("apiKeyBackup2", "synthetic-backup-key-value"),
            ("api key backup", "synthetic-spaced-qualifier-value"),
            ("api   key   backup", "synthetic-multispace-qualifier-value"),
            ("apiKey.backup", "synthetic-dotted-qualifier-value"),
            ("client credential", "synthetic-client-credential-value"),
            ("aws secret access key", "synthetic-spaced-aws-value")
        ];
        var quotedNamedCredentials = Phase07CredentialExtensionNames
            .Select((name, index) =>
            {
                var secret = $"synthetic-quoted-credential-{index}";
                return (Message: $"\"{name}\": \"{secret}\"", Secret: secret);
            })
            .ToArray();
        var unsafeMessage =
            $"first\r\nsecond\t\0\u001b\u202e\u2028{char.ConvertFromUtf32(0xE0001)} api_key={apiKeySecret} "
            + $"x-api-key: {headerSecret} Authorization: Bearer {bearerSecret} "
            + $"{cerebrasSecret} {openAiSecret} Basic synthetic-basic-head,synthetic-basic-tail;end "
            + string.Join(' ', rawCredentials)
            + " "
            + string.Join(' ', rawCredentials.Select(item => item + "."))
            + " "
            + string.Join(' ', namedCredentials.Select(item => $"{item.Name}={item.Value}"))
            + " "
            + string.Join(' ', quotedNamedCredentials.Select(item => item.Message));

        var redacted = AppLogger.Redact(unsafeMessage);
        var allSecrets = new[]
            {
                apiKeySecret, headerSecret, bearerSecret, cerebrasSecret, openAiSecret,
                "synthetic-basic-head", "synthetic-basic-tail", "synthetic-password-head", "synthetic-password-tail"
            }
            .Concat(rawCredentials)
            .Concat(namedCredentials.Select(item => item.Value))
            .Concat(quotedNamedCredentials.Select(item => item.Secret));
        foreach (var secret in allSecrets)
            Assert(!redacted.Contains(secret, StringComparison.Ordinal),
                "AppLogger retained a synthetic credential value.");
        Assert(redacted.Contains("[REDACTED]", StringComparison.Ordinal),
            "AppLogger did not mark credential redaction.");
        Assert(!redacted.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator),
            "AppLogger retained a structural control or bidi-format character.");

        var truncated = AppLogger.Redact(new string('z', AppLogger.MaximumMessageCharacters + 500));
        const string suffix = " [TRUNCATED]";
        Assert(truncated.EndsWith(suffix, StringComparison.Ordinal)
               && truncated.Length == AppLogger.MaximumMessageCharacters + suffix.Length,
            "AppLogger did not enforce its deterministic message-length cap.");

        var supplementaryBoundary = AppLogger.Redact(
            new string('z', AppLogger.MaximumMessageCharacters - 1) + "\U0001F642tail");
        Assert(supplementaryBoundary.EndsWith(suffix, StringComparison.Ordinal)
               && !char.IsSurrogate(supplementaryBoundary[supplementaryBoundary.Length - suffix.Length - 1]),
            "AppLogger split a supplementary Unicode scalar at its truncation boundary.");
    }

    private static void Phase07HttpAndProviderConfiguration()
    {
        using (var handler = TranslationApiClient.CreateProductionHandler())
        {
            Assert(!handler.AllowAutoRedirect,
                "The production HTTP handler permits automatic redirects.");
            Assert(!handler.UseCookies,
                "The production HTTP handler permits ambient cookie state.");
        }

        var unsafeEndpoints = new[]
        {
            "https://provider.invalid/v1?x-api-key=synthetic-direct-value",
            "https://provider.invalid/v1?x%252dapi%252dkey=synthetic-double-encoded-value",
            "https://provider.invalid/v1?api_token=synthetic-secret-value",
            "https://provider.invalid/v1?refresh_token=synthetic-secret-value",
            "https://provider.invalid/v1?secret_key=synthetic-secret-value",
            "https://provider.invalid/v1?x-amz-signature=synthetic-secret-value",
            "https://provider.invalid/api-key/synthetic-opaque-value",
            "https://provider.invalid/%61%70%69%2D%6B%65%79/synthetic-encoded-opaque-value",
            "https://provider.invalid/v1/api-key:synthetic-colon-assignment-value",
            "https://provider.invalid/v1/api-key%253Asynthetic-double-encoded-colon-value",
            "https://provider.invalid/v1/ghp_SYNTHETICMARKERONLY123456789",
            "https://provider.invalid/v1/AKIASYNTHETICMARKER1",
            "https://provider.invalid/v1/xoxb-SYNTHETICMARKERONLY",
            "https://provider.invalid/v1/hf_SYNTHETICMARKERONLY",
            "https://api-key.synthetic-host-marker.provider.invalid/v1",
            "https://AKIASYNTHETICMARKER1.provider.invalid/v1"
        };
        foreach (var endpoint in unsafeEndpoints)
        {
            Assert(ProviderValidator.GetEndpointErrorCode(endpoint, false, false) == "UrlContainsCredential",
                "Provider validation admitted an x-api-key query parameter.");
            AssertThrows<InvalidDataException>(() =>
                TranslationApiClient.ValidateEndpoint(endpoint, allowLoopbackHttp: false));
        }

        foreach (var endpoint in new[]
                 {
                     "https://provider.invalid/v1?api-version=synthetic-opaque-value",
                     "https://provider.invalid/v1?format=syntheticOpaqueValue",
                     "https://provider.invalid/v1?version=1234-opaque-token"
                 })
        {
            Assert(ProviderValidator.GetEndpointErrorCode(endpoint, false, false) == "UrlQueryNotAllowed",
                "Provider validation admitted an opaque value through an allowlisted query name.");
        }
        Assert(ProviderValidator.GetEndpointErrorCode(
                   "https://provider.invalid/v1?api-version=2024-10-21-preview&format=json&version=1.2.3",
                   false,
                   false) is null,
            "Provider validation rejected the bounded safe endpoint query grammar.");
    }

    private static void Phase07ComparisonCsvFormulaNeutralization()
    {
        WithTempRoot(root =>
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Phase07.Formula.Equals"] = "=SUM(A1:A2)",
                ["Phase07.Formula.Plus"] = "+HYPERLINK(\"https://invalid.example\",\"x\")",
                ["Phase07.Formula.Minus"] = "-CMD(ARG)",
                ["Phase07.Formula.At"] = "@SUM(A1:A2)",
                ["Phase07.Formula.Tab"] = "\t=SUM(A1:A2)",
                ["Phase07.Formula.Newline"] = "\n+SUM(A1:A2)",
                ["Phase07.Formula.Bom"] = "\uFEFF-SUM(A1:A2)",
                ["Phase07.Formula.Bidi"] = "\u202E@SUM(A1:A2)",
                ["Phase07.Formula.SupplementaryFormat"] = char.ConvertFromUtf32(0xE0001) + "=SUM(A1:A2)"
            };
            const string safeKey = "Phase07.Formula.Safe";
            const string safeValue = "ordinary human text";

            var keyedRoot = Path.Combine(root, "SyntheticMod", "Languages", "English", "Keyed");
            Directory.CreateDirectory(keyedRoot);
            var document = new XDocument(new XElement(
                "LanguageData",
                values.Select(pair => new XElement(pair.Key, pair.Value))
                    .Append(new XElement(safeKey, safeValue))));
            File.WriteAllText(
                Path.Combine(keyedRoot, "Formula.xml"),
                document.ToString(SaveOptions.DisableFormatting),
                new UTF8Encoding(false));

            var result = new TranslationEngine(root, new SourceExtractor()).RunAsync(new TranslationEngineOptions
            {
                ModRoot = Path.Combine(root, "SyntheticMod"),
                SourceOnly = true,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(root, "reviews"),
                SourceLanguageFolder = "English"
            }).GetAwaiter().GetResult();
            var auditRoot = Path.Combine(result.ReviewRoot!, "_TranslationAudit");
            var jsonPath = Directory.EnumerateFiles(auditRoot, "*-comparison.json").Single();
            var csvPath = Directory.EnumerateFiles(auditRoot, "*-comparison.csv").Single();
            var jsonRows = JsonSerializer.Deserialize<List<ReviewComparisonRow>>(File.ReadAllText(jsonPath))
                ?? throw new InvalidDataException("The comparison JSON fixture could not be read.");
            var jsonByKey = jsonRows.ToDictionary(row => row.Key, row => row.Source, StringComparer.Ordinal);
            var csvRecords = ParsePhase07Csv(File.ReadAllText(csvPath));
            var keyColumn = Array.IndexOf(csvRecords[0], "key");
            var sourceColumn = Array.IndexOf(csvRecords[0], "source");
            Assert(keyColumn >= 0 && sourceColumn >= 0,
                "The comparison CSV fixture is missing its key or source column.");
            var csvByKey = csvRecords.Skip(1)
                .ToDictionary(row => row[keyColumn], row => row[sourceColumn], StringComparer.Ordinal);

            foreach (var pair in values)
            {
                Assert(jsonByKey[pair.Key] == pair.Value,
                    "Formula neutralization changed the canonical comparison JSON.");
                Assert(csvByKey[pair.Key] == "'" + pair.Value,
                    "The comparison CSV did not visibly neutralize a spreadsheet-active cell.");
            }
            Assert(jsonByKey[safeKey] == safeValue && csvByKey[safeKey] == safeValue,
                "Formula neutralization changed an ordinary comparison value.");
        });
    }

    private static List<string[]> ParsePhase07Csv(string text)
    {
        var records = new List<string[]>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else quoted = false;
                }
                else field.Append(character);
                continue;
            }
            if (character == '"') quoted = true;
            else if (character == ',')
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                record.Add(field.ToString());
                field.Clear();
                records.Add(record.ToArray());
                record.Clear();
            }
            else field.Append(character);
        }
        Assert(!quoted, "The comparison CSV formula fixture has an unterminated field.");
        if (record.Count > 0 || field.Length > 0)
        {
            record.Add(field.ToString());
            records.Add(record.ToArray());
        }
        return records;
    }

    private static void Phase07SettingsExtensionSecrets()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(root);
            using var repository = new SettingsRepository(new AtomicJsonStore(), paths);
            var safe = Phase07SettingsDocument();
            safe.ExtensionData = new Dictionary<string, JsonElement>
            {
                ["futureSafeSetting"] = JsonSerializer.SerializeToElement(new { enabled = true }),
                ["futureDocumentationUrl"] = JsonSerializer.SerializeToElement(
                    "https://example.invalid/docs?lang=ko#intro"),
                ["futureAwsLikeWord"] = JsonSerializer.SerializeToElement("akiaalphabetical"),
                ["futureOpaqueIdentifier"] = JsonSerializer.SerializeToElement(
                    "0123456789abcdef0123456789abcdef"),
                ["futureUuidIdentifier"] = JsonSerializer.SerializeToElement(
                    "550e8400-e29b-41d4-a716-446655440000"),
                ["apiKeyMetadata"] = JsonSerializer.SerializeToElement("documentation-only"),
                ["requiresPassword"] = JsonSerializer.SerializeToElement(true),
                ["futureCredentialRequirements"] = JsonSerializer.SerializeToElement(new
                {
                    requiresPassword = true,
                    credentialReference = (string?)null
                }),
                ["futureKeyboardBinding"] = JsonSerializer.SerializeToElement(new
                {
                    key = "Ctrl+K",
                    command = "synthetic-command"
                }),
                ["futureKeyboardVariants"] = JsonSerializer.SerializeToElement(new[]
                {
                    new { key = "ctrl+k", command = "synthetic-lower" },
                    new { key = "CTRL+K", command = "synthetic-upper" },
                    new { key = "F5", command = "synthetic-function" }
                })
            };
            repository.Save(safe);
            var baseline = File.ReadAllBytes(paths.Settings);
            var safeRoundTrip = repository.Load();
            var keyboardBinding = safeRoundTrip.ExtensionData!["futureKeyboardBinding"];
            Assert(keyboardBinding.GetProperty("key").GetString() == "Ctrl+K"
                   && keyboardBinding.GetProperty("command").GetString() == "synthetic-command",
                "A safe unknown nested key field was misclassified as credential material.");
            var keyboardVariants = safeRoundTrip.ExtensionData["futureKeyboardVariants"];
            Assert(keyboardVariants[0].GetProperty("key").GetString() == "ctrl+k"
                   && keyboardVariants[1].GetProperty("key").GetString() == "CTRL+K"
                   && keyboardVariants[2].GetProperty("key").GetString() == "F5",
                "A safe case or single-key shortcut was misclassified as credential material.");
            Assert(safeRoundTrip.ExtensionData["futureDocumentationUrl"].GetString()
                   == "https://example.invalid/docs?lang=ko#intro",
                "A safe unknown URL with a query and fragment was misclassified as credential material.");
            Assert(safeRoundTrip.ExtensionData["futureAwsLikeWord"].GetString() == "akiaalphabetical",
                "A non-credential word with an AWS-like prefix was misclassified as credential material.");
            Assert(safeRoundTrip.ExtensionData["futureOpaqueIdentifier"].GetString()
                   == "0123456789abcdef0123456789abcdef",
                "A safe opaque identifier was destructively misclassified as credential material.");
            Assert(safeRoundTrip.ExtensionData["futureUuidIdentifier"].GetString()
                   == "550e8400-e29b-41d4-a716-446655440000",
                "A safe extension UUID was destructively misclassified as provider credential material.");
            for (var iteration = 0; iteration < 3; iteration++)
            {
                Assert(!ProviderValidator.IsCredentialPropertyName("apiKeyMetadata"),
                    "Credential-property qualifier parsing was not deterministic.");
            }
            Assert(safeRoundTrip.ExtensionData["apiKeyMetadata"].GetString() == "documentation-only",
                "A safe non-credential property suffix was destructively misclassified.");
            var credentialRequirements = safeRoundTrip.ExtensionData["futureCredentialRequirements"];
            Assert(safeRoundTrip.ExtensionData["requiresPassword"].GetBoolean()
                   && credentialRequirements.GetProperty("requiresPassword").GetBoolean()
                   && credentialRequirements.GetProperty("credentialReference").ValueKind == JsonValueKind.Null,
                "Safe boolean/null credential-requirement metadata was destructively misclassified.");
            Assert(!repository.StoredCredentialCorrectionRequired(),
                "Safe boolean/null credential-requirement metadata triggered credential correction.");
            Assert(!ProviderValidator.IsCredentialLikeProviderText(
                       "org/llama-3.1-instruct-long-model-name"),
                "A normal long provider model identifier was misclassified as credential material.");

            File.WriteAllText(
                paths.Settings + ".bak",
                "{\"futureValue\":\"sk-synthetic-shadowed-backup-secret\",\"futureValue\":\"ordinary\"}",
                new UTF8Encoding(false));
            Assert(repository.StoredCredentialCorrectionRequired(),
                "Credential inspection ignored material shadowed by a duplicate JSON property.");
            File.Delete(paths.Settings + ".bak");

            File.WriteAllText(
                paths.Settings + ".bak",
                "{\"apiProviders\":{\"Custom\":{\"model\":\"0123456789abcdef0123456789abcdef\",\"MODEL\":\"safe-model\"}}}",
                new UTF8Encoding(false));
            Assert(repository.StoredCredentialCorrectionRequired(),
                "Credential inspection treated a case-insensitive duplicate provider field as clean.");
            File.Delete(paths.Settings + ".bak");

            foreach (var unsafeEndpoint in new[]
                     {
                         (Url: "https://provider.invalid/v1?api-version=synthetic-opaque-query-secret", Secret: "synthetic-opaque-query-secret"),
                         (Url: "https://provider.invalid/api-key/synthetic-opaque-path-secret", Secret: "synthetic-opaque-path-secret")
                     })
            {
                var unsafeSettings = Phase07SettingsDocument();
                unsafeSettings.ApiProviders["Custom"].Url = unsafeEndpoint.Url;
                var endpointError = CaptureException<InvalidDataException>(() => repository.Save(unsafeSettings));
                Assert(!endpointError.Message.Contains(unsafeEndpoint.Secret, StringComparison.Ordinal)
                       && !endpointError.Message.Contains(unsafeEndpoint.Url, StringComparison.Ordinal),
                    "Settings endpoint validation exposed an opaque synthetic credential.");
                Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                    "Rejected opaque endpoint credential data changed settings.json.");
            }

            foreach (var unsafeMetadataName in (ReadOnlySpan<string>)
                     [
                         "password",
                         "ghp_SYNTHETICBOOLEANMARKER12345678",
                         "api-key:synthetic-assignment",
                         "custom=ghp_SYNTHETICBOOLEANMARKER12345678"
                     ])
            {
                var exactCredentialBoolean = Phase07SettingsDocument();
                exactCredentialBoolean.ExtensionData = new Dictionary<string, JsonElement>
                {
                    [unsafeMetadataName] = unsafeMetadataName.StartsWith("api-key", StringComparison.Ordinal)
                        ? JsonSerializer.SerializeToElement((string?)null)
                        : JsonSerializer.SerializeToElement(true)
                };
                AssertThrows<InvalidDataException>(() => repository.Save(exactCredentialBoolean));
                Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                    "A credential-named boolean changed settings.json.");
            }

            File.WriteAllText(
                paths.Settings + ".bak",
                "{\"ghp_SYNTHETICBOOLEANMARKER12345678\":true}",
                new UTF8Encoding(false));
            Assert(repository.StoredCredentialCorrectionRequired(),
                "Credential material encoded in a boolean property name bypassed correction detection.");
            File.Delete(paths.Settings + ".bak");

            const string nestedSecret = "sk-synthetic-nested-secret";
            var unsafeRoot = Phase07SettingsDocument();
            unsafeRoot.ExtensionData = new Dictionary<string, JsonElement>
            {
                ["futureContainer"] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>
                    {
                        ["nested"] = new Dictionary<string, string>
                        {
                            ["x-api-key"] = nestedSecret
                        }
                    })
            };
            var rootError = CaptureException<InvalidDataException>(() => repository.Save(unsafeRoot));
            Assert(!rootError.Message.Contains(nestedSecret, StringComparison.Ordinal),
                "Settings extension validation exposed a nested synthetic secret.");
            Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                "Rejected root extension data changed settings.json.");

            const string providerSecret = "synthetic-provider-authorization-secret";
            var unsafeProvider = Phase07SettingsDocument();
            unsafeProvider.ApiProviders["Custom"].ExtensionData = new Dictionary<string, JsonElement>
            {
                ["authorization"] = JsonSerializer.SerializeToElement(providerSecret)
            };
            var providerError = CaptureException<InvalidDataException>(() => repository.Save(unsafeProvider));
            Assert(!providerError.Message.Contains(providerSecret, StringComparison.Ordinal),
                "Settings extension validation exposed a provider synthetic secret.");
            Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                "Rejected provider extension data changed settings.json.");

            foreach (var credentialName in Phase07CredentialExtensionNames)
            {
                var unsafeNamedExtension = Phase07SettingsDocument();
                unsafeNamedExtension.ExtensionData = new Dictionary<string, JsonElement>
                {
                    [credentialName] = JsonSerializer.SerializeToElement("synthetic-opaque-value")
                };
                AssertThrows<InvalidDataException>(() => repository.Save(unsafeNamedExtension));
                Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                    "Rejected credential-named extension data changed settings.json.");
            }

            foreach (var useModelField in (ReadOnlySpan<bool>)[false, true])
            {
                var unsafeProviderText = Phase07SettingsDocument();
                if (useModelField)
                    unsafeProviderText.ApiProviders["Custom"].Model = "sk-synthetic-model-secret";
                else
                    unsafeProviderText.ApiProviders["Custom"].Name = "ghp_SYNTHETIC_PROVIDER_NAME_SECRET";
                AssertThrows<InvalidDataException>(() => repository.Save(unsafeProviderText));
                Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                    "Rejected credential-like provider text changed settings.json.");
            }

            foreach (var opaqueProviderText in Phase07OpaqueProviderTexts)
            {
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    Assert(ProviderValidator.IsCredentialLikeProviderText(opaqueProviderText),
                        "Opaque provider credential classification was not deterministic.");
                }
                var unsafeProviderText = Phase07SettingsDocument();
                unsafeProviderText.ApiProviders["Custom"].Model = opaqueProviderText;
                AssertThrows<InvalidDataException>(() => repository.Save(unsafeProviderText));
                Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                    "Rejected opaque provider text changed settings.json.");
            }

            var unsafeOpaqueKey = Phase07SettingsDocument();
            unsafeOpaqueKey.ApiProviders["Custom"].ExtensionData = new Dictionary<string, JsonElement>
            {
                ["futureContainer"] = JsonSerializer.SerializeToElement(new
                {
                    key = "synthetic-opaque-provider-secret"
                })
            };
            AssertThrows<InvalidDataException>(() => repository.Save(unsafeOpaqueKey));
            Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                "Rejected opaque generic-key extension data changed settings.json.");

            var unsafeShortGenericKey = Phase07SettingsDocument();
            unsafeShortGenericKey.ExtensionData = new Dictionary<string, JsonElement>
            {
                ["key"] = JsonSerializer.SerializeToElement("secretABC12")
            };
            AssertThrows<InvalidDataException>(() => repository.Save(unsafeShortGenericKey));
            Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                "Rejected short opaque generic-key data changed settings.json.");

            foreach (var structuredKeyValue in Phase07StructuredGenericKeyValues)
            {
                var unsafeStructuredKey = Phase07SettingsDocument();
                unsafeStructuredKey.ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["key"] = structuredKeyValue
                };
                AssertThrows<InvalidDataException>(() => repository.Save(unsafeStructuredKey));
                Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                    "Rejected structured generic-key extension data changed settings.json.");
            }

            foreach (var credentialAssignment in Phase07CredentialAssignments)
            {
                var unsafeAssignedExtension = Phase07SettingsDocument();
                unsafeAssignedExtension.ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["futureOpaqueAssignment"] = JsonSerializer.SerializeToElement(credentialAssignment)
                };
                AssertThrows<InvalidDataException>(() => repository.Save(unsafeAssignedExtension));
                Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(baseline),
                    "Rejected token-shaped assigned extension data changed settings.json.");
            }

            const string legacyRootSecret = "sk-synthetic-legacy-root-secret";
            const string legacyProviderSecret = "synthetic-legacy-provider-secret";
            const string legacyModelSecret = "sk-synthetic-legacy-model-secret";
            var legacy = Phase07SettingsDocument();
            legacy.ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["futureSafeSetting"] = JsonSerializer.SerializeToElement(new { retained = true }),
                ["futureOpaqueValue"] = JsonSerializer.SerializeToElement(legacyRootSecret),
                ["futureContainer"] = JsonSerializer.SerializeToElement(new
                {
                    safeSibling = new { mode = "retained", key = "Ctrl+K" },
                    apiToken = "synthetic-opaque-legacy-value",
                    apiKeyValue = "synthetic-qualified-opaque-legacy-value",
                    values = new[] { "visible", legacyRootSecret }
                })
            };
            legacy.ApiProviders["Custom"].ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["futureProviderSetting"] = JsonSerializer.SerializeToElement(new { retained = true }),
                ["authorization"] = JsonSerializer.SerializeToElement(legacyProviderSecret)
            };
            legacy.ApiProviders["Custom"].Model = legacyModelSecret;
            File.WriteAllText(paths.Settings, JsonSerializer.Serialize(legacy), new UTF8Encoding(false));
            var legacyBytes = File.ReadAllBytes(paths.Settings);

            var sanitized = repository.Load();
            Assert(repository.UnsafeExtensionDataExcludedOnLastLoad,
                "Loading legacy credential-like extension data did not raise the correction-required flag.");
            Assert(File.ReadAllBytes(paths.Settings).SequenceEqual(legacyBytes),
                "Loading legacy credential-like extension data silently rewrote settings.json.");
            Assert(sanitized.ExtensionData?.ContainsKey("futureSafeSetting") == true,
                "A safe unknown root setting was lost while excluding credential-like extension data.");
            Assert(sanitized.ExtensionData?.ContainsKey("futureOpaqueValue") == false,
                "A credential-like value under a safe unknown name entered live settings.");
            var container = sanitized.ExtensionData!["futureContainer"];
            Assert(container.GetProperty("safeSibling").GetProperty("mode").GetString() == "retained"
                   && container.GetProperty("safeSibling").GetProperty("key").GetString() == "Ctrl+K"
                   && !container.TryGetProperty("apiToken", out _)
                   && !container.TryGetProperty("apiKeyValue", out _)
                   && container.GetProperty("values").GetArrayLength() == 1
                   && container.GetProperty("values")[0].GetString() == "visible",
                "Nested extension sanitization did not preserve safe siblings while excluding credential-like values.");
            Assert(sanitized.ApiProviders["Custom"].ExtensionData?.ContainsKey("futureProviderSetting") == true
                   && sanitized.ApiProviders["Custom"].ExtensionData?.ContainsKey("authorization") == false
                   && sanitized.ApiProviders["Custom"].Model.Length == 0,
                "Provider extension sanitization did not preserve safe unknown data or exclude a credential field.");

            repository.Save(sanitized);
            var correctedText = File.ReadAllText(paths.Settings);
            Assert(File.Exists(paths.Settings + ".bak")
                   && File.ReadAllBytes(paths.Settings + ".bak").SequenceEqual(legacyBytes),
                "An explicit safe settings save did not preserve the legacy source as the documented manual-cleanup backup.");
            Assert(repository.StoredCredentialCorrectionRequired(),
                "The credential correction state cleared while the legacy backup still contained excluded material.");
            Assert(!correctedText.Contains(legacyRootSecret, StringComparison.Ordinal)
                   && !correctedText.Contains(legacyProviderSecret, StringComparison.Ordinal)
                   && !correctedText.Contains(legacyModelSecret, StringComparison.Ordinal)
                   && !correctedText.Contains("synthetic-opaque-legacy-value", StringComparison.Ordinal)
                   && !correctedText.Contains("synthetic-qualified-opaque-legacy-value", StringComparison.Ordinal),
                "An explicit safe settings save retained excluded credential-like extension data.");
            using var corrected = JsonDocument.Parse(correctedText);
            Assert(corrected.RootElement.GetProperty("futureSafeSetting").GetProperty("retained").GetBoolean()
                   && corrected.RootElement.GetProperty("futureContainer")
                        .GetProperty("safeSibling").GetProperty("mode").GetString() == "retained"
                   && corrected.RootElement.GetProperty("futureContainer")
                        .GetProperty("safeSibling").GetProperty("key").GetString() == "Ctrl+K",
                "An explicit safe settings save lost safe unknown extension data.");

            File.Delete(paths.Settings + ".bak");
            repository.Save(sanitized);
            Assert(!repository.StoredCredentialCorrectionRequired(),
                "Credential correction remained active after both primary settings and the new backup were clean.");
            var cleanBackupText = File.ReadAllText(paths.Settings + ".bak");
            Assert(!cleanBackupText.Contains(legacyRootSecret, StringComparison.Ordinal)
                   && !cleanBackupText.Contains(legacyProviderSecret, StringComparison.Ordinal),
                "A clean follow-up save recreated credential material in the settings backup.");
        });
    }

    private static AppSettingsDocument Phase07SettingsDocument() => new()
    {
        ApiProviderId = "Custom",
        ApiProviders = new Dictionary<string, ApiProviderSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom"] = new()
            {
                Name = "Synthetic",
                Url = "https://provider.invalid/v1/chat/completions",
                Model = "synthetic-model",
                Temperature = 0.1
            }
        }
    };

    private static void Phase07OversizedEntrySkipsTransport()
    {
        WithTempRoot(root =>
        {
            var modRoot = Path.Combine(root, "OversizedMod");
            var keyedRoot = Path.Combine(modRoot, "Languages", "English", "Keyed");
            Directory.CreateDirectory(keyedRoot);
            var oversizedText = "Synthetic oversized source text " + new string('x', 512);
            File.WriteAllText(
                Path.Combine(keyedRoot, "Oversized.xml"),
                $"<LanguageData><Phase07.Oversized>{oversizedText}</Phase07.Oversized></LanguageData>",
                new UTF8Encoding(false));

            var transport = new Phase07FailIfCalledHandler();
            var engine = new TranslationEngine(
                root,
                new SourceExtractor(),
                apiClientFactory: keys => new TranslationApiClient(
                    keys,
                    transport,
                    static (_, _) => Task.CompletedTask));
            var result = engine.RunAsync(new TranslationEngineOptions
            {
                ModRoot = modRoot,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(root, "reviews"),
                SourceLanguageFolder = "English",
                ApiKeys = ["synthetic-key-not-real"],
                Provider = ApiProviderCatalog.Get("Custom"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Synthetic",
                    Url = "https://provider.invalid/v1/chat/completions",
                    Model = "synthetic-model",
                    Temperature = 0.1
                },
                BatchSize = 1,
                MaxInputCharactersPerBatch = 128,
                MaxInputTokensPerBatch = 100_000,
                MaxRetries = 1,
                MaxProviderRequestsPerRun = 2,
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                MaxAlwaysGlossaryTerms = 0,
                MaxGeneratedGlossaryTermsPerBatch = 0,
                GeneratedGlossaryPath = Path.Combine(root, "missing-glossary.json")
            }).GetAwaiter().GetResult();

            Assert(transport.RequestCount == 0,
                "An oversized single source entry reached the provider transport.");
            Assert(result.SkippedUnsafe == 1 && result.TranslatedEntries == 0,
                "The oversized source entry was not reported as a safe local skip.");
        });
    }

    private static void Phase07ProviderRequestBudget()
    {
        WithTempRoot(root =>
        {
            var modRoot = Path.Combine(root, "BudgetMod");
            var keyedRoot = Path.Combine(modRoot, "Languages", "English", "Keyed");
            Directory.CreateDirectory(keyedRoot);
            File.WriteAllText(
                Path.Combine(keyedRoot, "Budget.xml"),
                "<LanguageData><Phase07.Budget>Synthetic provider budget input</Phase07.Budget></LanguageData>",
                new UTF8Encoding(false));

            var transport = new Phase07StatusHandler(HttpStatusCode.ServiceUnavailable);
            var engine = new TranslationEngine(
                root,
                new SourceExtractor(),
                apiClientFactory: keys => new TranslationApiClient(
                    keys,
                    transport,
                    static (_, _) => Task.CompletedTask));
            var options = new TranslationEngineOptions
            {
                ModRoot = modRoot,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(root, "reviews"),
                SourceLanguageFolder = "English",
                ApiKeys = ["synthetic-key-not-real"],
                Provider = ApiProviderCatalog.Get("Custom"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Synthetic",
                    Url = "https://provider.invalid/v1/chat/completions",
                    Model = "synthetic-model",
                    Temperature = 0.1
                },
                BatchSize = 1,
                MaxInputCharactersPerBatch = 10_000,
                MaxInputTokensPerBatch = 100_000,
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                MaxRetries = 4,
                MaxProviderRequestsPerRun = 2,
                Timeout = TimeSpan.FromSeconds(2),
                ResponseFormatMode = "JsonObject",
                CompletionTokenParameter = "none",
                ReasoningEffort = string.Empty,
                MaxAlwaysGlossaryTerms = 0,
                MaxGeneratedGlossaryTermsPerBatch = 0,
                GeneratedGlossaryPath = Path.Combine(root, "missing-glossary.json")
            };

            var error = CaptureException<InvalidOperationException>(() =>
                engine.RunAsync(options).GetAwaiter().GetResult());
            Assert(error is ProviderRequestBudgetExceededException budgetError
                   && budgetError.Message.Contains('2', StringComparison.Ordinal)
                   && transport.RequestCount == 2,
                "The per-run provider request-attempt budget did not stop retries at the configured bound.");
        });
    }

    private static void Phase07RmkBuilderPlanBinding()
    {
        WithTempRoot(root =>
        {
            var workspace = CreateRmkWorkspace(root, "Phase07Builder", out _);
            var loadFolders = Path.Combine(workspace, "LoadFolders.xml");
            var modList = Path.Combine(workspace, "ModList.tsv");
            const string originalLoadFolders = "<loadFolders><phase07 /></loadFolders>";
            const string originalModList = "phase07\tentry\tData/phase07\tfixture.phase07";
            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            InstallRmkBuilderFixture(workspace);
            File.WriteAllText(
                Path.Combine(workspace, "builder-fixture-mode.txt"),
                "success",
                new UTF8Encoding(false));
            var builderStartedMarker = Path.Combine(root, "phase07-builder-started.marker");
            File.WriteAllText(
                Path.Combine(workspace, "builder-started-marker-path.txt"),
                builderStartedMarker,
                new UTF8Encoding(false));

            var recoveryAuthorityRoot = Path.Combine(root, "Phase07BuilderRecoveryAuthority");
            Directory.CreateDirectory(recoveryAuthorityRoot);
            var service = new RmkWorkspaceService(
                new FileTransactionRecoveryAuthority(recoveryAuthorityRoot));
            var rejectedPlan = service.CreateBuildPlan(workspace);
            var originalBuilder = File.ReadAllBytes(rejectedPlan.BuilderPath);
            Assert(originalBuilder.Length > 0, "The synthetic RMK Builder fixture was empty.");
            var swappedBuilder = (byte[])originalBuilder.Clone();
            swappedBuilder[^1] ^= 0x01;
            File.WriteAllBytes(rejectedPlan.BuilderPath, swappedBuilder);

            var rejection = CaptureException<InvalidDataException>(() =>
                service.BuildAsync(rejectedPlan).GetAwaiter().GetResult());
            Assert(rejection.Message.Contains("confirmed executable", StringComparison.OrdinalIgnoreCase),
                "A same-length RMK Builder hash swap was not rejected before execution.");
            Assert(!File.Exists(builderStartedMarker),
                "The rejected same-length RMK Builder swap reached process execution.");
            Assert(File.ReadAllText(loadFolders) == originalLoadFolders
                   && File.ReadAllText(modList) == originalModList,
                "The rejected RMK Builder plan modified workspace indexes.");

            File.WriteAllBytes(rejectedPlan.BuilderPath, originalBuilder);
            var successPlan = service.CreateBuildPlan(workspace);
            var result = service.BuildAsync(successPlan).GetAwaiter().GetResult();
            Assert(File.Exists(builderStartedMarker),
                "The unchanged confirmed RMK Builder fixture did not reach process execution.");
            Assert(result.Output.Contains("검증된", StringComparison.Ordinal)
                   && File.ReadAllText(loadFolders).Contains("generated", StringComparison.Ordinal)
                   && File.ReadAllText(modList).Contains("fixture.generated", StringComparison.Ordinal),
                "An unchanged confirmed RMK Builder plan did not complete successfully.");
        });
    }

    private static void Phase07GoogleRequestBudget()
    {
        var queryHandler = new Phase07GoogleQueryHandler();
        using (var client = new TranslationApiClient(
                   [],
                   queryHandler,
                   static (_, _) => Task.CompletedTask))
        {
            var translated = client.TranslateGoogleAsync(
                    "Synthetic source",
                    "https://translate.googleapis.com/translate_a/single?format=json",
                    TimeSpan.FromSeconds(5),
                    1,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var requestUri = queryHandler.RequestUri;
            Assert(translated == "합성 번역"
                   && requestUri is not null
                   && requestUri.Query.Contains("format=json&client=gtx", StringComparison.Ordinal)
                   && !requestUri.Query.Contains("json?client", StringComparison.Ordinal),
                "Google query parameters were not appended to an existing allowed endpoint query.");
        }

        WithTempRoot(root =>
        {
            var modRoot = Path.Combine(root, "GoogleBudgetMod");
            var keyedRoot = Path.Combine(modRoot, "Languages", "English", "Keyed");
            Directory.CreateDirectory(keyedRoot);
            File.WriteAllText(
                Path.Combine(keyedRoot, "Budget.xml"),
                "<LanguageData><Phase07.GoogleA>Synthetic first input</Phase07.GoogleA><Phase07.GoogleB>Synthetic second input</Phase07.GoogleB></LanguageData>",
                new UTF8Encoding(false));

            var transport = new Phase07StatusHandler(HttpStatusCode.ServiceUnavailable);
            var engine = new TranslationEngine(
                root,
                new SourceExtractor(),
                apiClientFactory: keys => new TranslationApiClient(
                    keys,
                    transport,
                    static (_, _) => Task.CompletedTask));
            var error = CaptureException<InvalidOperationException>(() =>
                engine.RunAsync(new TranslationEngineOptions
                {
                    ModRoot = modRoot,
                    ReviewOnly = true,
                    ReviewRoot = Path.Combine(root, "google-reviews"),
                    SourceLanguageFolder = "English",
                    Provider = ApiProviderCatalog.Get("Google"),
                    ProviderSettings = new ApiProviderSettings { Name = "Google" },
                    BatchSize = 2,
                    MaxRetries = 2,
                    MaxProviderRequestsPerRun = 2,
                    RequestsPerMinutePerKey = 0,
                    InputTokensPerMinutePerKey = 0,
                    DailyTokenBudgetPerKey = 0,
                    MaxAlwaysGlossaryTerms = 0,
                    MaxGeneratedGlossaryTermsPerBatch = 0,
                    GeneratedGlossaryPath = Path.Combine(root, "missing-glossary.json")
                }).GetAwaiter().GetResult());

            Assert(error is ProviderRequestBudgetExceededException
                   && transport.RequestCount == 2,
                "Google fallback swallowed the terminal provider request budget or exceeded it.");
        });
    }

    private static void Phase07ReservedNames()
    {
        foreach (var unsafeName in new[]
                 {
                     "CON", "con.txt", "PRN", "AUX.log", "NUL", "CLOCK$",
                     "COM1", "com9.bin", "LPT1", "lpt9.txt", ".", ".."
                 })
        {
            Assert(!PathSafety.IsSafeFileNameSegment(unsafeName),
                $"A reserved Windows name was admitted: {unsafeName}");
        }

        Assert(PathSafety.IsSafeFileNameSegment("English")
               && PathSafety.IsSafeFileNameSegment("COM10")
               && PathSafety.IsSafeFileNameSegment("Synthetic.File.xml"),
            "A normal non-reserved file-name segment was rejected.");
    }

    private sealed class Phase07FailIfCalledHandler : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref requestCount);
            throw new InvalidOperationException("The synthetic zero-request transport was called.");
        }
    }

    private sealed class Phase07GoogleQueryHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[[[\"합성 번역\",\"Synthetic source\",null,null,1]],null,\"en\"]",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class Phase07StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("synthetic provider failure", Encoding.UTF8, "text/plain")
            });
        }
    }
}
