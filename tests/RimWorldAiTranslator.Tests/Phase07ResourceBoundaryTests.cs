using System.Text;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static readonly string[] Phase07ComparisonStringProperties =
    [
        "id",
        "key",
        "kind",
        "defClass",
        "node",
        "field",
        "target",
        "source",
        "existing",
        "existingSourceHash",
        "existingSourceText",
        "existingPreviousSourceText",
        "candidate",
        "existingOrigin",
        "translationOrigin",
        "translationUpdatedAt",
        "rmkIdentifier",
        "rmkHistoricalSource",
        "rmkCurrentSource",
        "rmkWorkbook",
        "missingTokens",
        "unexpectedTokens",
        "tokenCountMismatches",
        "invalidKoreanParticles"
    ];

    private static void Phase07ResourceBoundaries()
    {
        Phase07ComparisonDocumentBounds();
        Phase07ComparisonCandidateBounds();
        Phase07LegacyComparisonRetention();
        Phase07SteamRootBounds();
        Phase07RmkWorkspaceDiscoveryBounds();
        Phase07PersistedNetworkProjectBounds();
        Phase07ImmediateLanguageDirectoryBounds();
        Phase07ExistingLanguageAggregateBounds();
    }

    private static void Phase07RmkWorkspaceDiscoveryBounds()
    {
        WithTempRoot(root =>
        {
            var firstContainer = Path.Combine(root, "FirstContainer");
            var secondContainer = Path.Combine(root, "SecondContainer");
            Directory.CreateDirectory(Path.Combine(firstContainer, "NotAWorkspace"));
            var workspace = Path.Combine(secondContainer, "RmkWorkspace");
            Directory.CreateDirectory(Path.Combine(workspace, "Data"));
            Directory.CreateDirectory(Path.Combine(workspace, ".git"));
            File.WriteAllText(
                Path.Combine(workspace, ".git", "HEAD"),
                "ref: refs/heads/bus\n",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(workspace, "ModList.tsv"), string.Empty, new UTF8Encoding(false));

            var exactLimit = new RmkWorkspaceService(new RmkWorkspaceDiscoveryLimits
            {
                MaximumContainers = 2,
                MaximumCandidateDirectories = 2
            });
            Assert(exactLimit.FindWritableWorkspace([firstContainer, secondContainer])
                    .Equals(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase),
                "RMK workspace discovery rejected the configured exact aggregate limits.");

            var containerLimited = new RmkWorkspaceService(new RmkWorkspaceDiscoveryLimits
            {
                MaximumContainers = 1,
                MaximumCandidateDirectories = 2
            });
            AssertThrows<InvalidDataException>(() =>
                containerLimited.FindWritableWorkspace([firstContainer, secondContainer]));

            var candidateLimited = new RmkWorkspaceService(new RmkWorkspaceDiscoveryLimits
            {
                MaximumContainers = 2,
                MaximumCandidateDirectories = 1
            });
            AssertThrows<InvalidDataException>(() =>
                candidateLimited.FindWritableWorkspace([firstContainer, secondContainer]));

            using var preCanceled = new CancellationTokenSource();
            preCanceled.Cancel();
            AssertThrows<OperationCanceledException>(() =>
                exactLimit.FindWritableWorkspace([firstContainer], preCanceled.Token));

            using var midEnumerationCancellation = new CancellationTokenSource();
            var cancelingService = new RmkWorkspaceService(
                new RmkWorkspaceDiscoveryLimits
                {
                    MaximumContainers = 1,
                    MaximumCandidateDirectories = 2
                },
                _ => CancelDuringEnumeration(
                    Path.Combine(firstContainer, "NotAWorkspace"),
                    midEnumerationCancellation));
            AssertThrows<OperationCanceledException>(() =>
                cancelingService.FindWritableWorkspace([firstContainer], midEnumerationCancellation.Token));

            var ioIsolated = CreateMoveNextFailureService(firstContainer, IOExceptionFactory);
            Assert(ioIsolated.FindWritableWorkspace([firstContainer, secondContainer])
                    .Equals(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase),
                "An IOException from a lazy candidate iterator prevented later-container discovery.");

            var accessIsolated = CreateMoveNextFailureService(firstContainer, UnauthorizedFactory);
            Assert(accessIsolated.FindWritableWorkspace([firstContainer, secondContainer])
                    .Equals(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase),
                "An UnauthorizedAccessException from a lazy candidate iterator prevented later-container discovery.");

            Assert(string.IsNullOrEmpty(exactLimit.FindWritableWorkspace(
                    new ThrowingMoveNextEnumerable(new IOException("synthetic container iterator failure")))),
                "An IOException from the outer container iterator escaped RMK discovery.");

            RmkWorkspaceService CreateMoveNextFailureService(
                string failingContainer,
                Func<Exception> exceptionFactory) =>
                new(
                    new RmkWorkspaceDiscoveryLimits
                    {
                        MaximumContainers = 2,
                        MaximumCandidateDirectories = 2
                    },
                    container => container.Equals(failingContainer, StringComparison.OrdinalIgnoreCase)
                        ? new ThrowingMoveNextEnumerable(exceptionFactory())
                        : Directory.EnumerateDirectories(container, "*", SearchOption.TopDirectoryOnly));

            static IOException IOExceptionFactory() => new("synthetic candidate iterator failure");
            static UnauthorizedAccessException UnauthorizedFactory() =>
                new("synthetic candidate iterator access failure");
        });

        static IEnumerable<string> CancelDuringEnumeration(
            string candidate,
            CancellationTokenSource cancellation)
        {
            cancellation.Cancel();
            yield return candidate;
        }
    }

    private sealed class ThrowingMoveNextEnumerable(Exception exception) : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator() => new ThrowingMoveNextEnumerator(exception);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class ThrowingMoveNextEnumerator(Exception exception) : IEnumerator<string>
        {
            public string Current => string.Empty;

            object System.Collections.IEnumerator.Current => Current;

            public bool MoveNext() => throw exception;

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }

    private static void Phase07PersistedNetworkProjectBounds()
    {
        WithTempRoot(root =>
        {
            const string networkRoot = @"\\phase07.invalid\synthetic-project";
            Assert(PathSafety.IsNetworkPath(networkRoot)
                   && PathSafety.IsNetworkPath("//phase07.invalid/synthetic-project")
                   && !PathSafety.IsNetworkPath(Path.Combine(root, "LocalMod")),
                "Lexical network-path classification changed unexpectedly.");
            Assert(PathSafety.IsNetworkPath(
                       Path.Combine(root, "MappedNetworkProject"),
                       _ => DriveType.Network)
                   && !PathSafety.IsNetworkPath(
                       Path.Combine(root, "FixedLocalProject"),
                       _ => DriveType.Fixed),
                "Mapped network-drive classification did not fail closed.");
            AssertThrows<InvalidDataException>(() => new SourceExtractor().GetActiveContentRoots(networkRoot));
            AssertThrows<InvalidDataException>(() => new SourceExtractor().ReadExistingLanguageMap(networkRoot));
            AssertThrows<InvalidDataException>(() =>
                new ReviewWorkspaceService(new AtomicJsonStore(), new SourceExtractor()).Load(networkRoot));
            AssertThrows<InvalidDataException>(() => ReviewWorkspaceService.FindComparisonFile(networkRoot));
            Assert(ProjectStatsCacheRepository.CreateStamp(networkRoot).Length == 0,
                "A network review root was admitted to project-statistics stamping.");
            Assert(!new RmkWorkspaceService().IsWorkspaceRoot(networkRoot),
                "A network path was admitted to RMK workspace probing.");
            var localModRoot = Path.Combine(root, "LocalMod");
            Directory.CreateDirectory(localModRoot);
            AssertThrows<InvalidDataException>(() =>
                new TranslationEngine(root, new SourceExtractor()).RunAsync(new TranslationEngineOptions
                {
                    ModRoot = localModRoot,
                    ReviewRoot = Path.Combine(root, "reviews"),
                    ExistingLanguageRoot = networkRoot,
                    SourceOnly = true,
                    ReviewOnly = true
                }).GetAwaiter().GetResult());

            var paths = new AppDataPaths(root);
            var repository = new ProjectRepository(new AtomicJsonStore(), paths);
            repository.Save(new ProjectStoreDocument
            {
                Projects =
                [
                    new TranslationProject
                    {
                        Id = "network-project",
                        Name = "Synthetic network project",
                        ModRoot = networkRoot,
                        SourceKind = "Folder",
                        SourceLanguageFolder = "Auto"
                    }
                ]
            });
            var probed = new List<string>();
            var service = new ProjectService(
                repository,
                new RimWorldModDiscoveryService(),
                new ProjectCleanupService())
            {
                BeforeModRootProbeTestHook = path => probed.Add(path)
            };
            var loaded = service.Load();
            Assert(loaded.Projects.Single().ModRoot == networkRoot && probed.Count == 0,
                "A persisted network project root reached the filesystem probe boundary.");

            var cleanupProject = loaded.Projects.Single();
            cleanupProject.LatestReviewRoot = @"\\phase07.invalid\latest-review";
            cleanupProject.Runs =
            [
                new ProjectRun
                {
                    ReviewRoot = @"\\phase07.invalid\historical-review"
                }
            ];
            var cleanupProbes = 0;
            var cleanup = new ProjectCleanupService
            {
                BeforeDirectoryProbeTestHook = _ => cleanupProbes++
            };
            var cleanupPlan = cleanup.BuildPlan(
                cleanupProject,
                [@"\\phase07.invalid\reviews-root"]);
            Assert(cleanupProbes == 0
                   && cleanupPlan.SafePaths.Count == 0
                   && cleanupPlan.UnsafePaths.Count == 2,
                "A persisted network review path reached the project-cleanup filesystem boundary.");

            var activityProbes = 0;
            var activity = new ActivityService(new AtomicJsonStore())
            {
                BeforeDecisionStoreProbeTestHook = _ => activityProbes++
            };
            _ = activity.Load([cleanupProject]);
            Assert(activityProbes == 0,
                "A persisted network review path reached the activity decision-store boundary.");
        });
    }

    private static void Phase07ExistingLanguageAggregateBounds()
    {
        WithTempRoot(root =>
        {
            var languageRoot = Path.Combine(root, "Korean");
            var keyedRoot = Path.Combine(languageRoot, "Keyed");
            Directory.CreateDirectory(keyedRoot);
            var first = Path.Combine(keyedRoot, "One.xml");
            var second = Path.Combine(keyedRoot, "Two.xml");
            File.WriteAllText(first, "<LanguageData><one>첫째</one></LanguageData>", new UTF8Encoding(false));
            File.WriteAllText(second, "<LanguageData><two>둘째</two></LanguageData>", new UTF8Encoding(false));

            var recordLimited = new SourceExtractor(
                defFieldRulesPath: null,
                maximumExtractedRecords: 1,
                maximumExtractedCharacters: 10_000,
                maximumScannedXmlBytes: 10_000);
            AssertThrows<InvalidDataException>(() => recordLimited.ReadExistingLanguageMap(languageRoot));

            var byteLimit = new FileInfo(first).Length + new FileInfo(second).Length - 1;
            var byteLimited = new SourceExtractor(
                defFieldRulesPath: null,
                maximumExtractedRecords: 100,
                maximumExtractedCharacters: 10_000,
                maximumScannedXmlBytes: byteLimit);
            AssertThrows<InvalidDataException>(() => byteLimited.ReadExistingLanguageMap(languageRoot));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            AssertThrows<OperationCanceledException>(() =>
                new SourceExtractor().ReadExistingLanguageMap(languageRoot, cancellationToken: cancellation.Token));
        });
    }

    private static void Phase07LegacyComparisonRetention()
    {
        WithTempRoot(root =>
        {
            var reviewRoot = Path.Combine(root, "LegacyReview");
            var row = Row(reviewRoot, "retained-row", "fixture.retained", "synthetic source");
            WriteComparison(reviewRoot, [row]);
            var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
            for (var index = 0; index < 4; index++)
            {
                File.WriteAllText(
                    Path.Combine(auditRoot, $"alternate-{index}-comparison.json"),
                    System.Text.Json.JsonSerializer.Serialize(new[] { row }),
                    new UTF8Encoding(false));
            }

            var declaredPath = Path.Combine(auditRoot, "fixture-comparison.json");
            var decision = ReviewDecisionFor(
                row,
                Path.Combine("Keyed", "Fixture.xml"),
                "synthetic translation");
            new AtomicJsonStore().Write(
                ReviewRepository.GetDecisionPath(reviewRoot),
                new ReviewDecisionDocument
                {
                    Version = 4,
                    Comparison = declaredPath,
                    Items = [decision]
                });

            var processed = 0;
            var maximumRetained = 0;
            var service = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
            {
                LegacyComparisonRetentionTestHook = (candidateCount, retainedDocuments) =>
                {
                    processed = candidateCount;
                    maximumRetained = Math.Max(maximumRetained, retainedDocuments);
                }
            };
            var workspace = service.Load(reviewRoot);
            Assert(processed == 5 && maximumRetained <= 2,
                $"Legacy comparison migration retained too many documents (processed={processed}, retained={maximumRetained}).");
            Assert(workspace.ComparisonFile.Equals(declaredPath, StringComparison.OrdinalIgnoreCase)
                   && workspace.Items.Single().Decision.Text == "synthetic translation",
                "Incremental legacy comparison selection changed declared/tied-candidate semantics.");

            var alternatePath = Path.Combine(auditRoot, "alternate-0-comparison.json");
            var byteLimited = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
            {
                LegacyComparisonMaximumAggregateBytes =
                    new FileInfo(declaredPath).Length + new FileInfo(alternatePath).Length - 1,
                LegacyComparisonMaximumRows = 100
            };
            AssertThrows<InvalidDataException>(() => byteLimited.Load(reviewRoot));

            var rowLimited = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
            {
                LegacyComparisonMaximumAggregateBytes = 1_000_000,
                LegacyComparisonMaximumRows = 1
            };
            AssertThrows<InvalidDataException>(() => rowLimited.Load(reviewRoot));

            var invalidReviewRoot = Path.Combine(root, "InvalidLegacyReview");
            var invalidDeclaredRow = Row(
                invalidReviewRoot,
                "invalid-retained-row",
                "fixture.invalid-retained",
                "synthetic source");
            WriteComparison(invalidReviewRoot, [invalidDeclaredRow]);
            var invalidAuditRoot = Path.Combine(invalidReviewRoot, "_TranslationAudit");
            var invalidDeclaredPath = Path.Combine(invalidAuditRoot, "fixture-comparison.json");
            var invalidCandidatePath = Path.Combine(invalidAuditRoot, "invalid-comparison.json");
            File.WriteAllText(invalidCandidatePath, new string('x', 4_096), new UTF8Encoding(false));
            new AtomicJsonStore().Write(
                ReviewRepository.GetDecisionPath(invalidReviewRoot),
                new ReviewDecisionDocument
                {
                    Version = 4,
                    Comparison = invalidDeclaredPath,
                    Items =
                    [
                        ReviewDecisionFor(
                            invalidDeclaredRow,
                            Path.Combine("Keyed", "Fixture.xml"),
                            "synthetic translation")
                    ]
                });
            var invalidBudget = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
            {
                LegacyComparisonMaximumAggregateBytes =
                    new FileInfo(invalidDeclaredPath).Length + new FileInfo(invalidCandidatePath).Length - 1,
                LegacyComparisonMaximumRows = 100
            };
            AssertThrows<InvalidDataException>(() => invalidBudget.Load(invalidReviewRoot));

            var filteredReviewRoot = Path.Combine(root, "FilteredLegacyReview");
            var filteredDeclaredRow = Row(
                filteredReviewRoot,
                "filtered-retained-row",
                "fixture.filtered-retained",
                "synthetic source");
            WriteComparison(filteredReviewRoot, [filteredDeclaredRow]);
            var filteredAuditRoot = Path.Combine(filteredReviewRoot, "_TranslationAudit");
            var filteredDeclaredPath = Path.Combine(filteredAuditRoot, "fixture-comparison.json");
            var internalRow = Row(
                filteredReviewRoot,
                "filtered-internal-row",
                "ThingDef.synthetic.defName",
                "internal identifier");
            internalRow.Kind = "DefInjected";
            internalRow.DefClass = "ThingDef";
            internalRow.Field = "defName";
            internalRow.Node = "defName";
            File.WriteAllText(
                Path.Combine(filteredAuditRoot, "internal-comparison.json"),
                System.Text.Json.JsonSerializer.Serialize(new[] { internalRow }),
                new UTF8Encoding(false));
            new AtomicJsonStore().Write(
                ReviewRepository.GetDecisionPath(filteredReviewRoot),
                new ReviewDecisionDocument
                {
                    Version = 4,
                    Comparison = filteredDeclaredPath,
                    Items =
                    [
                        ReviewDecisionFor(
                            filteredDeclaredRow,
                            Path.Combine("Keyed", "Fixture.xml"),
                            "synthetic translation")
                    ]
                });
            var rawRowBudget = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
            {
                LegacyComparisonMaximumAggregateBytes = 1_000_000,
                LegacyComparisonMaximumRows = 1
            };
            AssertThrows<InvalidDataException>(() => rawRowBudget.Load(filteredReviewRoot));

            var nullScalarRoot = Path.Combine(root, "NullScalarLegacyReview");
            var nullScalarDeclaredRow = Row(
                nullScalarRoot,
                "null-scalar-retained-row",
                "fixture.null-scalar-retained",
                "synthetic source");
            WriteComparison(nullScalarRoot, [nullScalarDeclaredRow]);
            var nullScalarAuditRoot = Path.Combine(nullScalarRoot, "_TranslationAudit");
            var nullScalarDeclaredPath = Path.Combine(nullScalarAuditRoot, "fixture-comparison.json");
            File.WriteAllText(
                Path.Combine(nullScalarAuditRoot, "null-scalar-comparison.json"),
                "[{\"id\":null}]",
                new UTF8Encoding(false));
            new AtomicJsonStore().Write(
                ReviewRepository.GetDecisionPath(nullScalarRoot),
                new ReviewDecisionDocument
                {
                    Version = 4,
                    Comparison = nullScalarDeclaredPath,
                    Items =
                    [
                        ReviewDecisionFor(
                            nullScalarDeclaredRow,
                            Path.Combine("Keyed", "Fixture.xml"),
                            "synthetic translation")
                    ]
                });
            var invalidRawRowBudget = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
            {
                LegacyComparisonMaximumAggregateBytes = 1_000_000,
                LegacyComparisonMaximumRows = 1
            };
            AssertThrows<InvalidDataException>(() => invalidRawRowBudget.Load(nullScalarRoot));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            AssertThrows<OperationCanceledException>(() =>
                new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
                    .Load(reviewRoot, cancellationToken: cancellation.Token));
        });
    }

    private static void Phase07ComparisonDocumentBounds()
    {
        WithTempRoot(root =>
        {
            var reviewRoot = Path.Combine(root, "Review");
            var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
            Directory.CreateDirectory(auditRoot);
            var path = Path.Combine(auditRoot, "synthetic-comparison.json");

            void Write(string json) => File.WriteAllText(path, json, new UTF8Encoding(false));
            static ReviewComparisonLimits Limits(
                int rows = 10,
                int depth = 64,
                long stringBytes = 1_024,
                long aggregateStringBytes = 4_096,
                int tokens = 1_000,
                long rowBytes = 4_096,
                int rowTokens = 256) => new()
                {
                    MaximumRows = rows,
                    MaximumDepth = depth,
                    MaximumStringTokenBytes = stringBytes,
                    MaximumAggregateStringValueBytes = aggregateStringBytes,
                    MaximumJsonTokens = tokens,
                    MaximumRowBytes = rowBytes,
                    MaximumTokensPerRow = rowTokens
                };

            Write("[{\"id\":\"normal\",\"source\":\"synthetic source\"}]");
            var loaded = ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits());
            Assert(loaded.Rows.Count == 1 && loaded.Rows[0].Id == "normal",
                "A bounded normal comparison document did not round-trip.");

            foreach (var propertyName in Phase07ComparisonStringProperties)
            {
                Write($"[{{\"{propertyName}\":null}}]");
                AssertThrows<InvalidDataException>(() =>
                    ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits()));
            }

            Write("{}");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits()));

            Write("[{},{}]");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits(rows: 1)));

            Write("[null]");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits()));

            Write("[{\"id\":\"12345\"}]");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits(stringBytes: 4)));

            Write("[{\"id\":\"abc\",\"key\":\"def\"}]");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits(aggregateStringBytes: 5)));

            Write("[{\"id\":\"ok\",\"future\":{\"nested\":{\"value\":\"deep\"}}}]");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits(depth: 2)));

            Write("[{\"id\":\"x\",\"key\":\"y\"}]");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits(tokens: 7)));

            Write("[{\"id\":\"first\",\"ID\":\"second\"}]");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits()));

            Write("[{\"id\":\"row-too-large\"}]");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits(rowBytes: 8)));

            Write("[{\"id\":\"x\",\"future\":[1,2,3,4,5,6,7,8]}]");
            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.LoadExact(reviewRoot, path, Limits(rowTokens: 8)));
        });
    }

    private static void Phase07ComparisonCandidateBounds()
    {
        WithTempRoot(root =>
        {
            var auditRoot = Path.Combine(root, "_TranslationAudit");
            Directory.CreateDirectory(auditRoot);
            File.WriteAllText(Path.Combine(auditRoot, "a-comparison.json"), "[]", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(auditRoot, "b-comparison.json"), "[]", new UTF8Encoding(false));

            AssertThrows<InvalidDataException>(() =>
                ReviewComparisonDocument.EnumerateCandidateFiles(auditRoot, maximumCandidates: 1));
            Assert(ReviewComparisonDocument.EnumerateCandidateFiles(auditRoot, maximumCandidates: 2).Count == 2,
                "Comparison candidate enumeration rejected the configured exact limit.");
        });
    }

    private static void Phase07SteamRootBounds()
    {
        WithTempRoot(root =>
        {
            var steamRoot = Path.Combine(root, "Steam");
            var libraryOne = Path.Combine(root, "LibraryOne");
            var libraryTwo = Path.Combine(root, "LibraryTwo");
            Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
            Directory.CreateDirectory(libraryOne);
            Directory.CreateDirectory(libraryTwo);
            var manifest = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            var networkPath = @"\\phase07.invalid\synthetic-share";

            static string EscapeVdfPath(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
            static string CreateManifest(params string[] paths) =>
                "\"libraryfolders\"\n{\n" + string.Join(
                    "\n",
                    paths.Select((path, index) =>
                        $"  \"{index}\" {{ \"path\" \"{EscapeVdfPath(path)}\" }}")) + "\n}";

            File.WriteAllText(
                manifest,
                CreateManifest(libraryOne, networkPath),
                new UTF8Encoding(false));
            var probed = new List<string>();
            var service = new RimWorldModDiscoveryService(new RimWorldModDiscoveryLimits
            {
                MaximumKnownSteamRoots = 4,
                MaximumSteamRootCandidates = 8,
                MaximumSteamLibraryEntries = 8
            });
            service.BeforeSteamRootProbeTestHook = path =>
            {
                if (path.StartsWith("\\\\", StringComparison.Ordinal)
                    || path.StartsWith("//", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A network path reached the filesystem probe boundary.");
                }
                probed.Add(path);
            };
            var discovered = service.GetSteamLibraryRoots([steamRoot, networkPath]);
            Assert(discovered.Contains(Path.GetFullPath(steamRoot), StringComparer.OrdinalIgnoreCase)
                   && discovered.Contains(Path.GetFullPath(libraryOne), StringComparer.OrdinalIgnoreCase),
                "Local Steam roots were not preserved while network candidates were skipped.");
            Assert(probed.All(path => !path.Equals(networkPath, StringComparison.OrdinalIgnoreCase)),
                "A network Steam root reached the filesystem probe boundary.");

            var knownRootLimited = new RimWorldModDiscoveryService(new RimWorldModDiscoveryLimits
            {
                MaximumKnownSteamRoots = 1
            });
            AssertThrows<InvalidDataException>(() =>
                knownRootLimited.GetSteamLibraryRoots([steamRoot, libraryOne]));

            File.WriteAllText(manifest, CreateManifest(libraryOne), new UTF8Encoding(false));
            var candidateLimited = new RimWorldModDiscoveryService(new RimWorldModDiscoveryLimits
            {
                MaximumKnownSteamRoots = 4,
                MaximumSteamRootCandidates = 1,
                MaximumSteamLibraryEntries = 4
            });
            var candidateProbes = 0;
            candidateLimited.BeforeSteamRootProbeTestHook = _ => candidateProbes++;
            AssertThrows<InvalidDataException>(() => candidateLimited.GetSteamLibraryRoots([steamRoot]));
            Assert(candidateProbes == 1,
                "The Steam candidate limit was enforced after probing an over-limit candidate.");

            File.WriteAllText(manifest, CreateManifest(libraryOne, libraryTwo), new UTF8Encoding(false));
            var entryLimited = new RimWorldModDiscoveryService(new RimWorldModDiscoveryLimits
            {
                MaximumKnownSteamRoots = 4,
                MaximumSteamRootCandidates = 8,
                MaximumSteamLibraryEntries = 1
            });
            AssertThrows<InvalidDataException>(() => entryLimited.GetSteamLibraryRoots([steamRoot]));

            AssertThrows<InvalidDataException>(() =>
                RimWorldModDiscoveryService.CreateIsolated(networkPath));
            var isolatedRoot = Path.Combine(root, "IsolatedSteam");
            Directory.CreateDirectory(isolatedRoot);
            File.WriteAllText(
                Path.Combine(isolatedRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
                RimWorldModDiscoveryService.IsolationMarkerContent,
                new UTF8Encoding(false));
            var isolated = RimWorldModDiscoveryService.CreateIsolated(isolatedRoot);
            Assert(isolated.GetSteamLibraryRoots([isolatedRoot]).SequenceEqual(
                    [Path.GetFullPath(isolatedRoot)],
                    StringComparer.OrdinalIgnoreCase),
                "The Steam root limits changed normal isolated discovery behavior.");
        });
    }

    private static void Phase07ImmediateLanguageDirectoryBounds()
    {
        WithTempRoot(root =>
        {
            var languagesRoot = Path.Combine(root, "Languages");
            Directory.CreateDirectory(Path.Combine(languagesRoot, "SyntheticOne"));
            Directory.CreateDirectory(Path.Combine(languagesRoot, "SyntheticTwo"));
            var limited = new SourceExtractor(
                defFieldRulesPath: null,
                maximumExtractedRecords: 100,
                maximumExtractedCharacters: 10_000,
                maximumScannedXmlBytes: 10_000,
                maximumImmediateLanguageDirectories: 1);
            AssertThrows<InvalidDataException>(() => limited.GetSourceLanguageRoots([root], "Auto"));

            var exactLimit = new SourceExtractor(
                defFieldRulesPath: null,
                maximumExtractedRecords: 100,
                maximumExtractedCharacters: 10_000,
                maximumScannedXmlBytes: 10_000,
                maximumImmediateLanguageDirectories: 2);
            Assert(exactLimit.GetSourceLanguageRoots([root], "Auto").Count == 0,
                "Language directory discovery rejected the configured exact limit.");

            var secondRoot = Path.Combine(root, "SecondContentRoot");
            Directory.CreateDirectory(secondRoot);
            var contentRootLimited = new SourceExtractor(
                defFieldRulesPath: null,
                maximumExtractedRecords: 100,
                maximumExtractedCharacters: 10_000,
                maximumScannedXmlBytes: 10_000,
                maximumImmediateLanguageDirectories: 10,
                maximumContentRoots: 1);
            AssertThrows<InvalidDataException>(() =>
                contentRootLimited.GetSourceLanguageRoots([root, secondRoot], "Auto"));

            var loadFolderRoot = Path.Combine(root, "LoadFolderRoot");
            Directory.CreateDirectory(Path.Combine(loadFolderRoot, "ExtraContent"));
            File.WriteAllText(
                Path.Combine(loadFolderRoot, "LoadFolders.xml"),
                "<loadFolders><v1.6><li>ExtraContent</li></v1.6></loadFolders>",
                new UTF8Encoding(false));
            AssertThrows<InvalidDataException>(() =>
                contentRootLimited.GetActiveContentRoots(loadFolderRoot));
        });
    }
}
