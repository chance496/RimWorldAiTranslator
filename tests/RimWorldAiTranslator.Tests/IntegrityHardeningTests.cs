using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Utilities;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void ReviewLegacySourceVerificationFailClosed()
    {
        WithTempRoot(root =>
        {
            for (var version = 1; version <= 5; version++)
            {
                var reviewRoot = Path.Combine(root, $"legacy-v{version}");
                var row = Row(reviewRoot, "E000001", "fixture.legacy", "current source");
                WriteComparison(reviewRoot, [row]);
                var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
                var priorComparison = version switch
                {
                    1 => Path.Combine(auditRoot, "missing-prior.json"),
                    2 => Path.Combine(auditRoot, "corrupt-prior.json"),
                    3 => string.Empty,
                    _ => Path.Combine(reviewRoot, "outside", "unusable-prior.json")
                };
                if (version == 2)
                    File.WriteAllText(priorComparison, "{ damaged", new UTF8Encoding(false));
                if (version == 4)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(priorComparison)!);
                    var unusable = Row(reviewRoot, "E999999", "different.key", "unrelated source");
                    unusable.Target = Path.Combine(reviewRoot, "outside", "Fixture.xml");
                    File.WriteAllText(priorComparison, JsonSerializer.Serialize(new[] { unusable }), new UTF8Encoding(false));
                }

                using var future = JsonDocument.Parse("{\"kept\":true}");
                var document = new ReviewDecisionDocument
                {
                    Version = version,
                    Comparison = priorComparison,
                    Items =
                    [
                        new ReviewDecision
                        {
                            Id = row.Id,
                            Key = row.Key,
                            Target = Path.Combine("Keyed", "Fixture.xml"),
                            Status = ReviewStatuses.Approved,
                            Text = "보존 번역",
                            Note = "보존 메모",
                            PreviousSourceText = "보존 과거 원문",
                            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                            {
                                ["futureItem"] = future.RootElement.Clone()
                            }
                        }
                    ]
                };
                var store = new AtomicJsonStore();
                store.Write(ReviewRepository.GetDecisionPath(reviewRoot), document);
                var service = new ReviewWorkspaceService(store, CreateExtractor());

                var workspace = service.Load(reviewRoot);
                var item = workspace.Items.Single();
                Assert(item.Decision.Text == "보존 번역" && item.Decision.Note == "보존 메모",
                    $"v{version} migration lost translation or note.");
                Assert(item.Decision.PreviousSourceText == "보존 과거 원문",
                    $"v{version} migration lost source history.");
                Assert(item.Decision.SourceChanged && item.EffectiveStatus == ReviewStatuses.Pending,
                    $"v{version} decision without source evidence was not demoted.");
                Assert(string.IsNullOrWhiteSpace(item.Decision.SourceHash)
                       && string.IsNullOrWhiteSpace(item.Decision.SourceText),
                    $"v{version} decision was silently normalized to the current source.");
                Assert(SourceVerificationUnavailable(item.Decision)
                       && item.Decision.ExtensionData?["futureItem"].GetProperty("kept").GetBoolean() == true,
                    $"v{version} verification evidence or future data was not preserved.");

                service.Save(workspace);
                var reloaded = service.Load(reviewRoot).Items.Single();
                Assert(reloaded.Decision.SourceChanged
                       && string.IsNullOrWhiteSpace(reloaded.Decision.SourceHash)
                       && string.IsNullOrWhiteSpace(reloaded.Decision.SourceText)
                       && SourceVerificationUnavailable(reloaded.Decision),
                    $"v{version} second load silently normalized unverifiable source metadata.");

                var reloadedWorkspace = service.Load(reviewRoot);
                var revalidated = reloadedWorkspace.Items.Single();
                service.SetStatus(reloadedWorkspace, revalidated, ReviewStatuses.Approved);
                Assert(!revalidated.Decision.SourceChanged
                       && revalidated.Decision.SourceText == row.Source
                       && revalidated.Decision.SourceHash == StableIdentity.Sha256(row.Source)
                       && !SourceVerificationUnavailable(revalidated.Decision),
                    $"v{version} explicit revalidation did not establish a current source snapshot.");
            }

            var semanticRoot = Path.Combine(root, "legacy-semantic-mismatch");
            var semanticRow = Row(semanticRoot, "E000011", "fixture.semantic", "current semantic source");
            WriteComparison(semanticRoot, [semanticRow]);
            var semanticAuditRoot = Path.Combine(semanticRoot, "_TranslationAudit");
            var currentSemanticComparison = Path.Combine(semanticAuditRoot, "fixture-comparison.json");
            var mismatchedSemanticComparison = Path.Combine(semanticAuditRoot, "newer-mismatched-comparison.json");
            var unrelatedSemanticRow = Row(semanticRoot, "E999991", "different.semantic.key", "unrelated source");
            unrelatedSemanticRow.Target = Path.Combine(semanticRoot, "Languages", "Korean", "Keyed", "Other.xml");
            File.WriteAllText(
                mismatchedSemanticComparison,
                JsonSerializer.Serialize(new[] { unrelatedSemanticRow }),
                new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(currentSemanticComparison, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(mismatchedSemanticComparison, DateTime.UtcNow);
            var semanticDecision = ReviewDecisionFor(
                semanticRow,
                Path.Combine("Keyed", "Fixture.xml"),
                "preserved semantic translation");
            semanticDecision.Note = "preserved semantic note";
            semanticDecision.PreviousSourceText = "preserved semantic history";
            semanticDecision.SourceHash = string.Empty;
            semanticDecision.SourceText = string.Empty;
            new AtomicJsonStore().Write(
                ReviewRepository.GetDecisionPath(semanticRoot),
                new ReviewDecisionDocument
                {
                    Version = 4,
                    Comparison = mismatchedSemanticComparison,
                    Items = [semanticDecision]
                });

            var semanticWorkspace = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor()).Load(semanticRoot);
            var migratedSemantic = semanticWorkspace.Items.Single();
            Assert(semanticWorkspace.ComparisonFile.Equals(currentSemanticComparison, StringComparison.OrdinalIgnoreCase)
                   && migratedSemantic.Row.Key == semanticRow.Key
                   && migratedSemantic.Decision.Text == "preserved semantic translation"
                   && migratedSemantic.Decision.Note == "preserved semantic note"
                   && migratedSemantic.Decision.PreviousSourceText == "preserved semantic history"
                   && migratedSemantic.Decision.SourceChanged
                   && migratedSemantic.EffectiveStatus == ReviewStatuses.Pending
                   && string.IsNullOrWhiteSpace(migratedSemantic.Decision.SourceHash)
                   && string.IsNullOrWhiteSpace(migratedSemantic.Decision.SourceText)
                   && SourceVerificationUnavailable(migratedSemantic.Decision),
                "A semantically unusable v4 comparison was not migrated by stable identity with preservation and demotion.");

            var unscopedExactRoot = Path.Combine(root, "legacy-unscoped-exact");
            var unscopedExactRow = Row(unscopedExactRoot, "E000012", "fixture.unscoped.exact", "exact source");
            WriteComparison(unscopedExactRoot, [unscopedExactRow]);
            var unscopedExactDecision = ReviewDecisionFor(
                unscopedExactRow,
                string.Empty,
                "preserved unscoped translation");
            new AtomicJsonStore().Write(
                ReviewRepository.GetDecisionPath(unscopedExactRoot),
                new ReviewDecisionDocument
                {
                    Version = 4,
                    Comparison = Path.Combine(unscopedExactRoot, "_TranslationAudit", "fixture-comparison.json"),
                    Items = [unscopedExactDecision]
                });
            var unscopedExactService = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
            var unscopedExactWorkspace = unscopedExactService.Load(unscopedExactRoot);
            var migratedUnscopedExact = unscopedExactWorkspace.Items.Single();
            Assert(string.IsNullOrWhiteSpace(migratedUnscopedExact.Decision.Text),
                "An unscoped legacy decision attached without a stable target+key identity.");
            unscopedExactService.Save(unscopedExactWorkspace);
            var quarantinedUnscoped = new ReviewRepository(new AtomicJsonStore())
                .Load(unscopedExactRoot).QuarantinedItems.Single();
            Assert(quarantinedUnscoped.Text == "preserved unscoped translation"
                   && quarantinedUnscoped.Key == unscopedExactDecision.Key,
                "An unscoped legacy decision was not preserved in quarantine.");

            var ambiguousFallbackRoot = Path.Combine(root, "legacy-ambiguous-fallback");
            var ambiguousFallbackRow = Row(
                ambiguousFallbackRoot,
                "E000013",
                "fixture.ambiguous.fallback",
                "first candidate source");
            WriteComparison(ambiguousFallbackRoot, [ambiguousFallbackRow]);
            var alternateFallbackRow = JsonSerializer.Deserialize<ReviewComparisonRow>(
                JsonSerializer.Serialize(ambiguousFallbackRow))!;
            alternateFallbackRow.Source = "second candidate source";
            File.WriteAllText(
                Path.Combine(ambiguousFallbackRoot, "_TranslationAudit", "alternate-comparison.json"),
                JsonSerializer.Serialize(new[] { alternateFallbackRow }),
                new UTF8Encoding(false));
            var ambiguousFallbackDecision = ReviewDecisionFor(
                ambiguousFallbackRow,
                Path.Combine("Keyed", "Fixture.xml"),
                "preserved ambiguous translation");
            var ambiguousFallbackDecisionPath = ReviewRepository.GetDecisionPath(ambiguousFallbackRoot);
            new AtomicJsonStore().Write(
                ambiguousFallbackDecisionPath,
                new ReviewDecisionDocument
                {
                    Version = 4,
                    Comparison = Path.Combine(
                        ambiguousFallbackRoot,
                        "_TranslationAudit",
                        "missing-declared-comparison.json"),
                    Items = [ambiguousFallbackDecision]
                });
            var ambiguousFallbackBytes = File.ReadAllBytes(ambiguousFallbackDecisionPath);
            AssertThrows<InvalidDataException>(() =>
                new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor()).Load(ambiguousFallbackRoot));
            Assert(File.ReadAllBytes(ambiguousFallbackDecisionPath).SequenceEqual(ambiguousFallbackBytes),
                "Ambiguous legacy comparison fallback changed the primary decision store.");

            var decisionOnlyRoot = Path.Combine(root, "decision-only-run");
            var unrelatedComparisonRoot = Path.Combine(root, "unrelated-comparison-run");
            var currentRoot = Path.Combine(root, "current-run");
            Directory.CreateDirectory(decisionOnlyRoot);
            var currentRow = Row(currentRoot, "E000021", "fixture.cross-run", "current source");
            WriteComparison(currentRoot, [currentRow]);
            WriteComparison(unrelatedComparisonRoot,
                [Row(unrelatedComparisonRoot, "E000021", "fixture.cross-run", "unrelated source")]);
            var crossRunDecision = ReviewDecisionFor(currentRow, Path.Combine("Keyed", "Fixture.xml"), "보존 번역");
            crossRunDecision.SourceHash = string.Empty;
            crossRunDecision.SourceText = string.Empty;
            new AtomicJsonStore().Write(
                ReviewRepository.GetDecisionPath(decisionOnlyRoot),
                new ReviewDecisionDocument
                {
                    Version = 4,
                    Items = [crossRunDecision]
                });
            var project = new TranslationProject
            {
                Id = "fixture",
                LatestReviewRoot = decisionOnlyRoot,
                Runs =
                [
                    new ProjectRun
                    {
                        ReviewRoot = unrelatedComparisonRoot,
                        CreatedAt = "2026-01-01T00:00:00Z"
                    }
                ]
            };
            var crossRun = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor()).Load(currentRoot, project).Items.Single();
            Assert(crossRun.Decision.Text == "보존 번역"
                   && crossRun.Decision.SourceChanged
                   && string.IsNullOrWhiteSpace(crossRun.Decision.SourceHash)
                   && string.IsNullOrWhiteSpace(crossRun.Decision.SourceText)
                   && SourceVerificationUnavailable(crossRun.Decision),
                "A decision was incorrectly verified with a comparison from a different run.");
        });
    }

    private static void ReviewAmbiguousIdentityFailClosed()
    {
        WithTempRoot(root =>
        {
            var service = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());

            var duplicateTargetRoot = Path.Combine(root, "duplicate-target-rows");
            WriteComparison(duplicateTargetRoot,
            [
                Row(duplicateTargetRoot, "E000001", "duplicate.key", "first"),
                Row(duplicateTargetRoot, "E000002", "duplicate.key", "second")
            ]);
            AssertThrows<InvalidDataException>(() => service.Load(duplicateTargetRoot));

            var duplicateIdRoot = Path.Combine(root, "duplicate-keyless-rows");
            var firstDuplicateKeyless = Row(duplicateIdRoot, "E000001", string.Empty, "first");
            var secondDuplicateKeyless = Row(duplicateIdRoot, "E000002", string.Empty, "second");
            firstDuplicateKeyless.Node = "stable.node";
            secondDuplicateKeyless.Node = "stable.node";
            WriteComparison(duplicateIdRoot, [firstDuplicateKeyless, secondDuplicateKeyless]);
            AssertThrows<InvalidDataException>(() => service.Load(duplicateIdRoot));

            var decisionRoot = Path.Combine(root, "duplicate-decisions");
            var decisionRow = Row(decisionRoot, "E000001", "decision.key", "source");
            WriteComparison(decisionRoot, [decisionRow]);
            var target = Path.Combine("Keyed", "Fixture.xml");
            var duplicateDecisions = new ReviewDecisionDocument
            {
                Items =
                [
                    ReviewDecisionFor(decisionRow, target, "첫 번역"),
                    ReviewDecisionFor(decisionRow, target.Replace('\\', '/'), "둘째 번역")
                ]
            };
            AssertThrows<InvalidDataException>(() =>
                SaveBoundReviewDocument(new AtomicJsonStore(), decisionRoot, duplicateDecisions));

            var blankTargetRoot = Path.Combine(root, "duplicate-blank-target-decisions");
            var blankTargetRow = Row(blankTargetRoot, "E000008", "blank.target.key", "source");
            WriteComparison(blankTargetRoot, [blankTargetRow]);
            AssertThrows<InvalidDataException>(() => WriteBoundReviewDocumentUnchecked(new AtomicJsonStore(), blankTargetRoot, new ReviewDecisionDocument
            {
                Items =
                [
                    ReviewDecisionFor(blankTargetRow, string.Empty, "첫 번역"),
                    ReviewDecisionFor(blankTargetRow, "   ", "둘째 번역")
                ]
            }));

            var unscopedRoot = Path.Combine(root, "ambiguous-unscoped-decision");
            var unscopedFirst = Row(unscopedRoot, "E000011", "shared.key", "first");
            var unscopedSecond = Row(unscopedRoot, "E000012", "shared.key", "second");
            unscopedSecond.Target = Path.Combine(unscopedRoot, "Languages", "Korean", "Keyed", "Other.xml");
            WriteComparison(unscopedRoot, [unscopedFirst, unscopedSecond]);
            AssertThrows<InvalidDataException>(() => WriteBoundReviewDocumentUnchecked(new AtomicJsonStore(), unscopedRoot, new ReviewDecisionDocument
            {
                Items = [ReviewDecisionFor(unscopedFirst, string.Empty, "공유 금지 번역")]
            }));

            var keylessDecisionRoot = Path.Combine(root, "duplicate-keyless-decisions");
            var keylessRow = Row(keylessDecisionRoot, "E000009", string.Empty, "source");
            keylessRow.Node = "stable.node";
            WriteComparison(keylessDecisionRoot, [keylessRow]);
            AssertThrows<InvalidDataException>(() => SaveBoundReviewDocument(new AtomicJsonStore(), keylessDecisionRoot, new ReviewDecisionDocument
            {
                Items =
                [
                    ReviewDecisionFor(keylessRow, string.Empty, "첫 번역"),
                    ReviewDecisionFor(keylessRow, string.Empty, "둘째 번역")
                ]
            }));
        });

        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-ambiguous-identity");
            var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
            var relativeTarget = Path.GetRelativePath(
                Path.Combine(run.ReviewRoot!, "Languages", "Korean"),
                row.Target);
            var ambiguousDocument = new ReviewDecisionDocument
            {
                Comparison = run.ComparisonFile!,
                Items =
                [
                    ReviewDecisionFor(row, relativeTarget, "번역 시작"),
                    ReviewDecisionFor(row, relativeTarget.Replace('\\', '/'), "다른 번역")
                ]
            };
            var ambiguousEvidence = ReviewComparisonDocument.LoadExact(run.ReviewRoot!, run.ComparisonFile!).Evidence;
            ambiguousDocument.Version = ReviewDecisionDocument.CurrentVersion;
            ambiguousDocument.ReviewRoot = Path.GetFullPath(run.ReviewRoot!);
            ambiguousDocument.Comparison = ambiguousEvidence.Path;
            ambiguousDocument.ComparisonSha256 = ambiguousEvidence.Sha256;
            File.WriteAllText(
                ReviewRepository.GetDecisionPath(run.ReviewRoot!),
                JsonSerializer.Serialize(ambiguousDocument),
                new UTF8Encoding(false));

            var apply = new ReviewApplyService(new AtomicJsonStore(), new LanguageFileService(), CreateExtractor());
            ReviewApplyOptions ApplyOptions() => new()
            {
                ModRoot = modRoot,
                ReviewRoot = run.ReviewRoot!,
                SourceLanguageFolder = "English",
                Overwrite = true,
                DryRun = true
            };
            AssertThrows<InvalidDataException>(() => apply.Apply(ApplyOptions()));

            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "AmbiguousEntry", out var entryRoot);
            var rmk = CreateRmkExportService();
            RmkExportOptions RmkOptions() => new()
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
            AssertThrows<InvalidDataException>(() => rmk.Export(RmkOptions()));

            SaveReviewDecisions(run, [(row, "번역 시작")]);
            var duplicatedRows = run.Rows.Concat([row]).ToArray();
            File.WriteAllText(run.ComparisonFile!, JsonSerializer.Serialize(duplicatedRows), new UTF8Encoding(false));
            AssertThrows<InvalidDataException>(() => apply.Apply(ApplyOptions()));
            AssertThrows<InvalidDataException>(() => rmk.Export(RmkOptions()));
            Assert(!File.Exists(Path.Combine(entryRoot, "history.xlsx")),
                "An ambiguous RMK input wrote a workbook.");

            var alternateRow = JsonSerializer.Deserialize<ReviewComparisonRow>(JsonSerializer.Serialize(row))!;
            alternateRow.Id = "E999998";
            alternateRow.Target = Path.Combine(run.ReviewRoot!, "Languages", "Korean", "Keyed", "Alternate.xml");
            File.WriteAllText(
                run.ComparisonFile!,
                JsonSerializer.Serialize(run.Rows.Concat([alternateRow])),
                new UTF8Encoding(false));
            AssertThrows<InvalidDataException>(() => rmk.Export(RmkOptions()));
        });

        ReviewComparisonBindingAndDecisionScope();
        ApplyAndRmkReviewBinding();
    }

    private static void StorageTransactionCleanupFaults()
    {
        WithTempRoot(root =>
        {
            var commitRoot = Path.Combine(root, "file-commit");
            Directory.CreateDirectory(commitRoot);
            var committed = Path.Combine(commitRoot, "value.txt");
            File.WriteAllText(committed, "before", new UTF8Encoding(false));
            FileTransaction.Execute(
                [committed],
                () => File.WriteAllText(committed, "after", new UTF8Encoding(false)),
                "commit fixture",
                _ => throw new IOException("injected cleanup failure"));
            Assert(File.ReadAllText(committed) == "after", "Snapshot cleanup failure replaced a successful commit.");
            Assert(TransactionSnapshots(commitRoot).Length == 1,
                "Failed commit cleanup did not preserve its recovery snapshot.");

            var asyncCommitRoot = Path.Combine(root, "file-async-commit");
            Directory.CreateDirectory(asyncCommitRoot);
            var asyncCommitted = Path.Combine(asyncCommitRoot, "value.txt");
            File.WriteAllText(asyncCommitted, "before", new UTF8Encoding(false));
            var asyncResult = FileTransaction.ExecuteAsync(
                [asyncCommitted],
                async () =>
                {
                    await Task.Yield();
                    File.WriteAllText(asyncCommitted, "after", new UTF8Encoding(false));
                    return 7;
                },
                "async commit fixture",
                _ => throw new IOException("injected cleanup failure")).GetAwaiter().GetResult();
            Assert(asyncResult == 7
                   && File.ReadAllText(asyncCommitted) == "after"
                   && TransactionSnapshots(asyncCommitRoot).Length == 1,
                "Async snapshot cleanup failure replaced commit success or removed recovery evidence.");

            var rollbackRoot = Path.Combine(root, "file-rollback");
            Directory.CreateDirectory(rollbackRoot);
            var rolledBack = Path.Combine(rollbackRoot, "value.txt");
            File.WriteAllText(rolledBack, "before", new UTF8Encoding(false));
            var operationError = CaptureException<IOException>(() => FileTransaction.Execute(
                [rolledBack],
                () =>
                {
                    File.WriteAllText(rolledBack, "changed", new UTF8Encoding(false));
                    throw new InvalidDataException("injected operation failure");
                },
                "rollback fixture",
                _ => throw new IOException("injected cleanup failure")));
            Assert(operationError.InnerException is InvalidDataException
                   && operationError.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase),
                "Cleanup failure replaced the original operation/rollback result.");
            Assert(File.ReadAllText(rolledBack) == "before" && TransactionSnapshots(rollbackRoot).Length == 1,
                "Rollback or recoverable snapshot preservation failed after cleanup fault.");

            var incompleteRoot = Path.Combine(root, "file-incomplete-rollback");
            Directory.CreateDirectory(incompleteRoot);
            var incomplete = Path.Combine(incompleteRoot, "value.txt");
            File.WriteAllText(incomplete, "before", new UTF8Encoding(false));
            var cleanupInvoked = false;
            var rollbackError = CaptureException<IOException>(() => FileTransaction.Execute(
                [incomplete],
                () =>
                {
                    File.Delete(incomplete);
                    Directory.CreateDirectory(incomplete);
                    throw new InvalidDataException("injected operation failure");
                },
                "incomplete rollback fixture",
                _ =>
                {
                    cleanupInvoked = true;
                    throw new IOException("injected cleanup failure");
                }));
            Assert(rollbackError.InnerException is InvalidDataException
                   && rollbackError.Message.Contains("rollback was incomplete", StringComparison.OrdinalIgnoreCase)
                   && rollbackError.Message.Contains("snapshots were preserved", StringComparison.OrdinalIgnoreCase),
                "Incomplete rollback diagnosis was replaced or omitted.");
            Assert(!cleanupInvoked && TransactionSnapshots(incompleteRoot).Length == 1,
                "An incomplete rollback attempted to delete its recovery snapshot.");

            var languageCommitRoot = Path.Combine(root, "language-commit");
            Directory.CreateDirectory(languageCommitRoot);
            var languageCommit = Path.Combine(languageCommitRoot, "A.xml");
            File.WriteAllText(languageCommit, "<LanguageData><A>before</A></LanguageData>", new UTF8Encoding(false));
            var languageFiles = new LanguageFileService();
            languageFiles.WriteTransaction(
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [languageCommit] = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "after" }
                },
                true,
                _ => throw new IOException("injected cleanup failure"));
            Assert(languageFiles.Read(languageCommit)["A"] == "after"
                   && TransactionSnapshots(languageCommitRoot).Length == 1,
                "Language snapshot cleanup failure replaced commit success or removed recovery evidence.");

            var languageRollbackRoot = Path.Combine(root, "language-rollback");
            Directory.CreateDirectory(languageRollbackRoot);
            var first = Path.Combine(languageRollbackRoot, "A.xml");
            var second = Path.Combine(languageRollbackRoot, "B.xml");
            const string firstBefore = "<LanguageData><A>before-a</A></LanguageData>";
            const string secondBefore = "<LanguageData><B>before-b</B></LanguageData>";
            File.WriteAllText(first, firstBefore, new UTF8Encoding(false));
            File.WriteAllText(second, secondBefore, new UTF8Encoding(false));
            var languageError = CaptureException<IOException>(() => languageFiles.WriteTransaction(
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [first] = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "changed" },
                    [second] = new Dictionary<string, string>(StringComparer.Ordinal) { ["invalid key"] = "failure" }
                },
                true,
                _ => throw new IOException("injected cleanup failure")));
            Assert(languageError.InnerException is InvalidDataException
                   && languageError.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase),
                "Language cleanup failure replaced the write/rollback error.");
            Assert(File.ReadAllText(first) == firstBefore
                   && File.ReadAllText(second) == secondBefore
                   && TransactionSnapshots(languageRollbackRoot).Length == 2,
                "Language rollback or cleanup-fault snapshot preservation failed.");
        });

        StorageCapturePhaseFaults();
        TransactionBackupSidecars();
        LanguageDuplicateKeysFailClosed();
    }

    private static void TransactionBackupSidecars()
    {
        WithTempRoot(root =>
        {
            var service = new LanguageFileService();
            var first = Path.Combine(root, "A.xml");
            var second = Path.Combine(root, "B.xml");
            var third = Path.Combine(root, "C.xml");
            var firstBackup = first + ".bak";
            var secondBackup = second + ".bak";
            var thirdBackup = third + ".bak";
            File.WriteAllText(first, "<LanguageData><A>before-a</A></LanguageData>", new UTF8Encoding(false));
            File.WriteAllText(second, "<LanguageData><B>before-b</B></LanguageData>", new UTF8Encoding(false));
            File.WriteAllText(third, "<LanguageData><C>before-c</C></LanguageData>", new UTF8Encoding(false));
            File.WriteAllBytes(firstBackup, [0x00, 0x42, 0xff, 0x19]);
            var before = new[] { first, second, third }
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            var originalFirstBackup = File.ReadAllBytes(firstBackup);

            var failure = CaptureException<IOException>(() => service.WriteTransaction(
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [first] = new Dictionary<string, string> { ["A"] = "changed-a" },
                    [second] = new Dictionary<string, string> { ["B"] = "changed-b" },
                    [third] = new Dictionary<string, string> { ["invalid key"] = "failure" }
                },
                true));
            Assert(failure.InnerException is InvalidDataException
                   && failure.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase),
                "Language sidecar fixture did not fail after intermediate writes.");
            Assert(before.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                   && File.ReadAllBytes(firstBackup).SequenceEqual(originalFirstBackup)
                   && !File.Exists(secondBackup)
                   && !File.Exists(thirdBackup)
                   && TransactionSnapshots(root).Length == 0,
                "Language rollback did not exactly restore pre-existing and absent backup sidecars.");

            var committed = service.WriteTransaction(
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [first] = new Dictionary<string, string> { ["A"] = "committed-a" },
                    [second] = new Dictionary<string, string> { ["B"] = "committed-b" },
                    [third] = new Dictionary<string, string> { ["C"] = "committed-c" }
                },
                true);
            Assert(committed.Count == 3
                   && File.ReadAllBytes(firstBackup).SequenceEqual(before[first])
                   && File.ReadAllBytes(secondBackup).SequenceEqual(before[second])
                   && File.ReadAllBytes(thirdBackup).SequenceEqual(before[third])
                   && TransactionSnapshots(root).Length == 0,
                "Successful Language transaction changed backup-retention semantics.");
        });
    }

    private static void ReviewComparisonBindingAndDecisionScope()
    {
        WithTempRoot(root =>
        {
            var reviewRoot = Path.Combine(root, "declared-comparison");
            var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
            Directory.CreateDirectory(auditRoot);
            Directory.CreateDirectory(Path.Combine(reviewRoot, "Languages", "Korean", "Keyed"));
            var declaredRow = Row(reviewRoot, "E000101", "declared.key", "declared source");
            var unrelatedRow = Row(reviewRoot, "E000101", "declared.key", "unrelated newest source");
            var declaredPath = Path.Combine(auditRoot, "declared-comparison.json");
            var newestPath = Path.Combine(auditRoot, "newest-comparison.json");
            File.WriteAllText(declaredPath, JsonSerializer.Serialize(new[] { declaredRow }), new UTF8Encoding(false));
            File.WriteAllText(newestPath, JsonSerializer.Serialize(new[] { unrelatedRow }), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(declaredPath, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newestPath, DateTime.UtcNow);

            var decision = ReviewDecisionFor(
                declaredRow,
                Path.Combine("Keyed", "Fixture.xml"),
                "\uBC88\uC5ED");
            SaveBoundReviewDocument(new AtomicJsonStore(), reviewRoot, new ReviewDecisionDocument
            {
                Comparison = declaredPath,
                Items = [decision]
            });
            var service = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
            var workspace = service.Load(reviewRoot);
            Assert(workspace.Items.Single().Row.Source == declaredRow.Source,
                "Review load selected an unrelated newer comparison instead of the declared comparison.");

            var decisionPath = ReviewRepository.GetDecisionPath(reviewRoot);
            var decisionBytes = File.ReadAllBytes(decisionPath);
            declaredRow.Source = "comparison changed after load";
            File.WriteAllText(declaredPath, JsonSerializer.Serialize(new[] { declaredRow }), new UTF8Encoding(false));
            AssertThrows<InvalidDataException>(() => service.Save(workspace));
            Assert(File.ReadAllBytes(decisionPath).SequenceEqual(decisionBytes),
                "A comparison swap changed review decisions during a rejected save.");

            var unmatchedRoot = Path.Combine(root, "unmatched-current-decision");
            var unmatchedRow = Row(unmatchedRoot, "E000102", "unmatched.key", "source");
            WriteComparison(unmatchedRoot, [unmatchedRow]);
            var unmatchedComparison = Path.Combine(unmatchedRoot, "_TranslationAudit", "fixture-comparison.json");
            var unmatchedDecision = ReviewDecisionFor(unmatchedRow, Path.Combine("Keyed", "Missing.xml"), "\uBC88\uC5ED");
            SaveBoundReviewDocument(new AtomicJsonStore(), unmatchedRoot, new ReviewDecisionDocument
            {
                Comparison = unmatchedComparison,
                Items = [unmatchedDecision]
            });
            var unmatchedPath = ReviewRepository.GetDecisionPath(unmatchedRoot);
            var unmatchedBytes = File.ReadAllBytes(unmatchedPath);
            AssertThrows<InvalidDataException>(() => service.Load(unmatchedRoot));
            Assert(File.ReadAllBytes(unmatchedPath).SequenceEqual(unmatchedBytes),
                "Loading an unmatched current decision modified its decision document.");

            foreach (var invalidTarget in new[]
                     {
                         Path.Combine("..", "Keyed", "Fixture.xml"),
                         Path.Combine(unmatchedRoot, "Languages", "Korean", "Keyed", "Fixture.xml")
                     })
            {
                var invalidRoot = Path.Combine(root, "invalid-target-" + Guid.NewGuid().ToString("N"));
                var invalidRow = Row(invalidRoot, "E000103", "invalid.target", "source");
                WriteComparison(invalidRoot, [invalidRow]);
                var invalidComparison = Path.Combine(invalidRoot, "_TranslationAudit", "fixture-comparison.json");
                var invalidDecision = ReviewDecisionFor(invalidRow, invalidTarget, "\uBC88\uC5ED");
                AssertThrows<InvalidDataException>(() => WriteBoundReviewDocumentUnchecked(new AtomicJsonStore(), invalidRoot, new ReviewDecisionDocument
                {
                    Comparison = invalidComparison,
                    Items = [invalidDecision]
                }));
            }

            var aliasRoot = Path.Combine(root, "canonical-alias-duplicate");
            var aliasRow = Row(aliasRoot, "E000104", "alias.key", "source");
            WriteComparison(aliasRoot, [aliasRow]);
            var aliasComparison = Path.Combine(aliasRoot, "_TranslationAudit", "fixture-comparison.json");
            AssertThrows<InvalidDataException>(() => SaveBoundReviewDocument(new AtomicJsonStore(), aliasRoot, new ReviewDecisionDocument
            {
                Comparison = aliasComparison,
                Items =
                [
                    ReviewDecisionFor(aliasRow, Path.Combine("Keyed", "Fixture.xml"), "\uCCAB\uBC88\uC9F8"),
                    ReviewDecisionFor(aliasRow, @"Keyed\\.\Fixture.xml", "\uB450\uBC88\uC9F8")
                ]
            }));

            var previousRoot = Path.Combine(root, "unscoped-previous-without-evidence");
            var currentRoot = Path.Combine(root, "unscoped-current");
            Directory.CreateDirectory(previousRoot);
            var currentRow = Row(currentRoot, "E000105", "unscoped.safe.key", "current source");
            WriteComparison(currentRoot, [currentRow]);
            var unscopedPrevious = ReviewDecisionFor(currentRow, string.Empty, "\uC798\uBABB\uB41C \uC2B9\uACC4");
            unscopedPrevious.SourceHash = string.Empty;
            unscopedPrevious.SourceText = string.Empty;
            new AtomicJsonStore().Write(ReviewRepository.GetDecisionPath(previousRoot), new ReviewDecisionDocument
            {
                Version = 5,
                Comparison = string.Empty,
                Items = [unscopedPrevious]
            });
            var project = new TranslationProject
            {
                Id = "fixture",
                LatestReviewRoot = previousRoot,
                Runs = [new ProjectRun { ReviewRoot = previousRoot, CreatedAt = "2026-01-01T00:00:00Z" }]
            };
            var unscopedWorkspace = service.Load(currentRoot, project);
            Assert(unscopedWorkspace.ImportedDecisions == 0
                   && string.IsNullOrWhiteSpace(unscopedWorkspace.Items.Single().Decision.Text),
                "A bare-key previous decision inherited without exact source evidence.");

            var evidencedPreviousRoot = Path.Combine(root, "unscoped-evidenced-previous");
            var evidencedCurrentRoot = Path.Combine(root, "unscoped-evidenced-current");
            var previousRow = Row(evidencedPreviousRoot, "E000106", "shared.cross.file.key", "same source text");
            previousRow.Target = Path.Combine(evidencedPreviousRoot, "Languages", "Korean", "Keyed", "A.xml");
            previousRow.DefClass = "ThingDef";
            var movedRow = Row(evidencedCurrentRoot, "E000107", "shared.cross.file.key", "same source text");
            movedRow.Target = Path.Combine(evidencedCurrentRoot, "Languages", "Korean", "Keyed", "B.xml");
            movedRow.DefClass = "RecipeDef";
            WriteComparison(evidencedPreviousRoot, [previousRow]);
            WriteComparison(evidencedCurrentRoot, [movedRow]);
            var evidencedDecision = ReviewDecisionFor(previousRow, string.Empty, "must not inherit by bare key");
            new AtomicJsonStore().Write(ReviewRepository.GetDecisionPath(evidencedPreviousRoot), new ReviewDecisionDocument
            {
                Version = 4,
                Comparison = Path.Combine(evidencedPreviousRoot, "_TranslationAudit", "fixture-comparison.json"),
                Items = [evidencedDecision]
            });
            var evidencedProject = new TranslationProject
            {
                Id = "evidenced-fixture",
                LatestReviewRoot = evidencedPreviousRoot,
                Runs = [new ProjectRun { ReviewRoot = evidencedPreviousRoot, CreatedAt = "2026-01-02T00:00:00Z" }]
            };
            var evidencedWorkspace = service.Load(evidencedCurrentRoot, evidencedProject);
            var evidencedCurrent = evidencedWorkspace.Items.Single();
            Assert(evidencedWorkspace.ImportedDecisions == 0
                   && string.IsNullOrWhiteSpace(evidencedCurrent.Decision.Text)
                   && string.IsNullOrWhiteSpace(evidencedCurrent.Decision.Note)
                   && !evidencedCurrent.Decision.SourceChanged
                   && string.IsNullOrWhiteSpace(evidencedCurrent.Decision.PreviousSourceText),
                "A source-evidenced bare-key decision or source history crossed target and Def class boundaries.");
        });
    }

    private static void ApplyAndRmkReviewBinding()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-exact-binding");
            var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
            var languageRoot = Path.Combine(run.ReviewRoot!, "Languages", "Korean");
            var relativeTarget = Path.GetRelativePath(languageRoot, row.Target);
            var decision = ReviewDecisionFor(row, relativeTarget, "\uBC88\uC5ED \uC2DC\uC791");
            var decisionStore = new AtomicJsonStore();
            void SaveDecision(ReviewDecision value, string comparison)
            {
                var document = new ReviewDecisionDocument
                {
                    Comparison = comparison,
                    Items = [value]
                };
                var auditRoot = Path.Combine(run.ReviewRoot!, "_TranslationAudit");
                if (PathSafety.IsStrictlyInside(comparison, auditRoot))
                {
                    SaveBoundReviewDocument(decisionStore, run.ReviewRoot!, document);
                    return;
                }
                document.Version = ReviewDecisionDocument.CurrentVersion;
                document.ComparisonSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(comparison)));
                decisionStore.Write(ReviewRepository.GetDecisionPath(run.ReviewRoot!), document);
            }
            SaveDecision(decision, run.ComparisonFile!);

            var newest = JsonSerializer.Deserialize<ReviewComparisonRow>(JsonSerializer.Serialize(row))!;
            newest.Source = "unrelated newest source";
            var newestPath = Path.Combine(run.ReviewRoot!, "_TranslationAudit", "zzz-comparison.json");
            File.WriteAllText(newestPath, JsonSerializer.Serialize(new[] { newest }), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(run.ComparisonFile!, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newestPath, DateTime.UtcNow);

            var apply = new ReviewApplyService(new AtomicJsonStore(), new LanguageFileService(), CreateExtractor());
            ReviewApplyOptions ApplyOptions(bool dryRun = true) => new()
            {
                ModRoot = modRoot,
                ReviewRoot = run.ReviewRoot!,
                SourceLanguageFolder = "English",
                Overwrite = true,
                DryRun = dryRun
            };
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "ExactBindingEntry", out var entryRoot);
            var workbookPath = Path.Combine(entryRoot, "history.xlsx");
            var rmk = CreateRmkExportService();
            RmkExportOptions RmkOptions(bool dryRun = true) => new()
            {
                RmkWorkspaceRoot = workspaceRoot,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                RmkLanguageFolderName = "Korean",
                SourceLanguage = "English",
                WorkbookPath = workbookPath,
                Overwrite = true,
                DryRun = dryRun
            };

            var exactApply = apply.Apply(ApplyOptions());
            var exactRmk = rmk.Export(RmkOptions());
            Assert(exactApply.AppliedEntries == 1 && exactRmk.EligibleEntries == 1,
                "Apply or RMK export selected an unrelated newer comparison.");

            var validWorkspace = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
                .Load(run.ReviewRoot!);
            var validStoredItem = validWorkspace.Items.Single(item => item.Row.Key == row.Key);
            Assert(validStoredItem.Decision.Text == decision.Text
                   && validStoredItem.Decision.Target == relativeTarget,
                "A valid v6 exact target+key decision did not load with its original scope.");

            var targetless = ReviewDecisionFor(row, string.Empty, "must not apply without target");
            targetless.Note = "preserved targetless note";
            targetless.PreviousSourceText = "preserved targetless source history";
            var targetlessDecisionPath = ReviewRepository.GetDecisionPath(run.ReviewRoot!);
            var validDecisionBytes = File.ReadAllBytes(targetlessDecisionPath);
            var targetlessDecisionBackupPath = targetlessDecisionPath + ".bak";
            var validDecisionBackupBytes = File.Exists(targetlessDecisionBackupPath)
                ? File.ReadAllBytes(targetlessDecisionBackupPath)
                : null;
            var evidence = ReviewComparisonDocument.LoadExact(run.ReviewRoot!, run.ComparisonFile!).Evidence;
            foreach (var invalidTarget in new[] { string.Empty, "   ", "." })
            {
                var rejected = ReviewDecisionFor(row, invalidTarget, targetless.Text);
                AssertThrows<InvalidDataException>(() =>
                    new ReviewRepository(new AtomicJsonStore()).Save(
                        run.ReviewRoot!,
                        new ReviewDecisionDocument
                        {
                            Comparison = run.ComparisonFile!,
                            Items = [rejected]
                        },
                        evidence));
            }
            Assert(File.ReadAllBytes(targetlessDecisionPath).SequenceEqual(validDecisionBytes)
                   && (validDecisionBackupBytes is null
                       ? !File.Exists(targetlessDecisionBackupPath)
                       : File.ReadAllBytes(targetlessDecisionBackupPath).SequenceEqual(validDecisionBackupBytes)),
                "ReviewRepository changed decision JSON while refusing a keyed decision without a canonical target.");

            var targetlessDocument = new ReviewDecisionDocument
            {
                Comparison = run.ComparisonFile!,
                Items = [targetless]
            };
            targetlessDocument.Version = ReviewDecisionDocument.CurrentVersion;
            targetlessDocument.ReviewRoot = Path.GetFullPath(run.ReviewRoot!);
            targetlessDocument.Comparison = evidence.Path;
            targetlessDocument.ComparisonSha256 = evidence.Sha256;
            File.WriteAllText(
                targetlessDecisionPath,
                JsonSerializer.Serialize(targetlessDocument),
                new UTF8Encoding(false));
            if (File.Exists(targetlessDecisionBackupPath)) File.Delete(targetlessDecisionBackupPath);
            var targetlessDecisionBytes = File.ReadAllBytes(targetlessDecisionPath);
            Assert(!File.Exists(targetlessDecisionBackupPath),
                "The no-recovery targetless fixture unexpectedly retained a valid backup.");
            var localOutputPath = Path.Combine(modRoot, "Languages", "Korean", relativeTarget);
            var localOutputBefore = File.Exists(localOutputPath) ? File.ReadAllBytes(localOutputPath) : null;
            var rmkOutputPath = Path.Combine(entryRoot, "Languages", "Korean", relativeTarget);
            var rmkOutputBefore = File.Exists(rmkOutputPath) ? File.ReadAllBytes(rmkOutputPath) : null;
            AssertThrows<InvalidDataException>(() =>
                new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor()).Load(run.ReviewRoot!));
            AssertThrows<InvalidDataException>(() => apply.Apply(ApplyOptions(dryRun: false)));
            AssertThrows<InvalidDataException>(() => rmk.Export(RmkOptions(dryRun: false)));
            Assert((localOutputBefore is null
                        ? !File.Exists(localOutputPath)
                        : File.Exists(localOutputPath)
                          && File.ReadAllBytes(localOutputPath).SequenceEqual(localOutputBefore))
                   && (rmkOutputBefore is null
                        ? !File.Exists(rmkOutputPath)
                        : File.Exists(rmkOutputPath)
                          && File.ReadAllBytes(rmkOutputPath).SequenceEqual(rmkOutputBefore))
                   && !File.Exists(workbookPath)
                   && File.ReadAllBytes(targetlessDecisionPath).SequenceEqual(targetlessDecisionBytes)
                   && !File.Exists(targetlessDecisionBackupPath)
                   && TransactionSnapshots(modRoot).Length == 0
                   && TransactionSnapshots(entryRoot).Length == 0,
                "A rejected targetless decision changed local/RMK output, workbook, or decision JSON.");

            var mismatched = ReviewDecisionFor(row, Path.Combine("Keyed", "Different.xml"), "\uBC88\uC5ED \uC2DC\uC791");
            SaveDecision(mismatched, run.ComparisonFile!);
            var mismatchBytes = File.ReadAllBytes(ReviewRepository.GetDecisionPath(run.ReviewRoot!));
            var mismatchApply = apply.Apply(ApplyOptions());
            var mismatchRmk = rmk.Export(RmkOptions());
            Assert(mismatchApply.AppliedEntries == 0 && mismatchApply.SkippedUnmapped >= 1
                   && mismatchRmk.EligibleEntries == 0 && mismatchRmk.SkippedUnmapped >= 1,
                "An explicitly scoped decision fell back to a bare-key match.");
            AssertThrows<InvalidDataException>(() =>
                new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor()).Load(run.ReviewRoot!));
            Assert(File.ReadAllBytes(ReviewRepository.GetDecisionPath(run.ReviewRoot!)).SequenceEqual(mismatchBytes),
                "A mismatched current decision changed while review load failed.");

            var noEvidence = ReviewDecisionFor(row, relativeTarget, "\uBC88\uC5ED \uC2DC\uC791");
            noEvidence.SourceHash = string.Empty;
            noEvidence.SourceText = string.Empty;
            SaveDecision(noEvidence, run.ComparisonFile!);
            var noEvidenceApply = apply.Apply(ApplyOptions());
            var noEvidenceRmk = rmk.Export(RmkOptions());
            Assert(noEvidenceApply.AppliedEntries == 0 && noEvidenceApply.SkippedSourceChanged >= 1
                   && noEvidenceRmk.EligibleEntries == 0 && noEvidenceRmk.SkippedSourceChanged >= 1,
                "Apply or RMK export accepted a decision without source evidence.");

            var keyless = ReviewDecisionFor(row, relativeTarget, "\uBC88\uC5ED \uC2DC\uC791");
            keyless.Key = string.Empty;
            keyless.DefClass = "Keyed";
            SaveDecision(keyless, run.ComparisonFile!);
            var keylessApply = apply.Apply(ApplyOptions(dryRun: false));
            var keylessRmk = rmk.Export(RmkOptions(dryRun: false));
            Assert(keylessApply.AppliedEntries == 0 && keylessApply.SkippedUnmapped >= 1
                   && keylessApply.WrittenFiles == 0
                   && keylessRmk.EligibleEntries == 0 && keylessRmk.SkippedUnmapped >= 1
                   && keylessRmk.WrittenFiles == 0
                   && !File.Exists(workbookPath),
                "A keyless ordinal decision was applied or exported.");

            SaveDecision(decision, run.ComparisonFile!);
            RimWorldTranslatorRmkXlsxWriter.Write(
                workbookPath,
                [
                    new RimWorldTranslatorRmkHistoryRow { Identifier = "Keyed+CodexTranslator.SampleButton", Source = row.Source }
                ],
                "English");
            RewriteWorksheet(workbookPath, document =>
            {
                var sheetData = document.Descendants().Single(element => element.Name.LocalName == "sheetData");
                var sourceRow = sheetData.Elements().Single(element => element.Name.LocalName == "row" && (string?)element.Attribute("r") == "2");
                var duplicateRow = new System.Xml.Linq.XElement(sourceRow);
                duplicateRow.SetAttributeValue("r", "3");
                foreach (var cell in duplicateRow.Elements().Where(element => element.Name.LocalName == "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? string.Empty;
                    cell.SetAttributeValue("r", reference.Replace("2", "3", StringComparison.Ordinal));
                }
                sheetData.Add(duplicateRow);
            });
            var workbookBytes = File.ReadAllBytes(workbookPath);
            AssertThrows<InvalidDataException>(() => rmk.Export(RmkOptions(dryRun: false)));
            Assert(File.ReadAllBytes(workbookPath).SequenceEqual(workbookBytes),
                "A duplicate RMK workbook identifier modified the workbook before failing.");

            var outsideComparison = Path.Combine(fixtureRoot, "outside-comparison.json");
            File.WriteAllText(outsideComparison, JsonSerializer.Serialize(new[] { row }), new UTF8Encoding(false));
            SaveDecision(decision, outsideComparison);
            AssertThrows<InvalidDataException>(() => apply.Apply(ApplyOptions()));
            AssertThrows<InvalidDataException>(() => rmk.Export(RmkOptions()));

            var decisionPath = ReviewRepository.GetDecisionPath(run.ReviewRoot!);
            File.Delete(decisionPath);
            File.Delete(decisionPath + ".bak");
            AssertThrows<FileNotFoundException>(() => apply.Apply(ApplyOptions()));
        });
    }

    private static void StorageCapturePhaseFaults()
    {
        WithTempRoot(root =>
        {
            var first = Path.Combine(root, "A.txt");
            var second = Path.Combine(root, "B.txt");
            File.WriteAllText(first, "first", new UTF8Encoding(false));
            File.WriteAllText(second, "second", new UTF8Encoding(false));
            var actionInvoked = false;
            var copyCount = 0;
            AssertThrows<IOException>(() => FileTransaction.Execute(
                [first, second],
                () => actionInvoked = true,
                "capture fixture",
                File.Delete,
                (source, snapshot) =>
                {
                    File.Copy(source, snapshot, true);
                    copyCount++;
                    if (copyCount == 2) throw new IOException("injected capture failure");
                }));
            Assert(!actionInvoked && TransactionSnapshots(root).Length == 0
                   && File.ReadAllText(first) == "first" && File.ReadAllText(second) == "second",
                "A partial FileTransaction capture invoked the action, changed input, or left snapshots.");

            copyCount = 0;
            var cleanupError = CaptureException<IOException>(() => FileTransaction.Execute(
                [first, second],
                () => actionInvoked = true,
                "capture cleanup fixture",
                _ => throw new IOException("injected capture cleanup failure"),
                (source, snapshot) =>
                {
                    File.Copy(source, snapshot, true);
                    copyCount++;
                    if (copyCount == 2) throw new IOException("injected capture failure");
                }));
            var preservedFileSnapshots = TransactionSnapshots(root);
            Assert(preservedFileSnapshots.Length == 2
                   && preservedFileSnapshots.All(path => cleanupError.Message.Contains(path, StringComparison.OrdinalIgnoreCase)),
                "Capture cleanup failure did not identify every preserved FileTransaction snapshot.");
            foreach (var snapshot in preservedFileSnapshots) File.Delete(snapshot);

            var language = new LanguageFileService();
            var languageFirst = Path.Combine(root, "A.xml");
            var languageSecond = Path.Combine(root, "B.xml");
            const string languageFirstBytes = "<LanguageData><A>first</A></LanguageData>";
            const string languageSecondBytes = "<LanguageData><B>second</B></LanguageData>";
            File.WriteAllText(languageFirst, languageFirstBytes, new UTF8Encoding(false));
            File.WriteAllText(languageSecond, languageSecondBytes, new UTF8Encoding(false));
            var groups = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [languageFirst] = new Dictionary<string, string> { ["A"] = "changed" },
                [languageSecond] = new Dictionary<string, string> { ["B"] = "changed" }
            };
            copyCount = 0;
            AssertThrows<IOException>(() => language.WriteTransaction(
                groups,
                true,
                File.Delete,
                (source, snapshot) =>
                {
                    File.Copy(source, snapshot, true);
                    copyCount++;
                    if (copyCount == 2) throw new IOException("injected language capture failure");
                }));
            Assert(TransactionSnapshots(root).Length == 0
                   && File.ReadAllText(languageFirst) == languageFirstBytes
                   && File.ReadAllText(languageSecond) == languageSecondBytes,
                "A partial language capture changed input or left snapshots.");

            copyCount = 0;
            var languageCleanupError = CaptureException<IOException>(() => language.WriteTransaction(
                groups,
                true,
                _ => throw new IOException("injected language cleanup failure"),
                (source, snapshot) =>
                {
                    File.Copy(source, snapshot, true);
                    copyCount++;
                    if (copyCount == 2) throw new IOException("injected language capture failure");
                }));
            var preservedLanguageSnapshots = TransactionSnapshots(root);
            Assert(preservedLanguageSnapshots.Length == 2
                   && preservedLanguageSnapshots.All(path => languageCleanupError.Message.Contains(path, StringComparison.OrdinalIgnoreCase)),
                "Language capture cleanup failure did not identify every preserved snapshot.");
        });
    }

    private static void LanguageDuplicateKeysFailClosed()
    {
        WithTempRoot(root =>
        {
            var service = new LanguageFileService();
            var duplicate = Path.Combine(root, "duplicate.xml");
            var duplicateBackup = duplicate + ".bak";
            const string duplicateXml = "<LanguageData><A>one</A><A>two</A></LanguageData>";
            File.WriteAllText(duplicate, duplicateXml, new UTF8Encoding(false));
            File.WriteAllText(duplicateBackup, "backup sentinel", new UTF8Encoding(false));
            var original = File.ReadAllBytes(duplicate);
            var originalBackup = File.ReadAllBytes(duplicateBackup);
            AssertThrows<InvalidDataException>(() => service.Write(
                duplicate,
                new Dictionary<string, string> { ["B"] = "changed" },
                true));
            Assert(File.ReadAllBytes(duplicate).SequenceEqual(original)
                   && File.ReadAllBytes(duplicateBackup).SequenceEqual(originalBackup),
                "A direct write changed duplicate-key LanguageData or its backup.");

            var clean = Path.Combine(root, "A-clean.xml");
            var duplicateSecond = Path.Combine(root, "B-duplicate.xml");
            File.WriteAllText(clean, "<LanguageData><A>before</A></LanguageData>", new UTF8Encoding(false));
            File.WriteAllText(duplicateSecond, duplicateXml, new UTF8Encoding(false));
            File.WriteAllText(clean + ".bak", "clean backup", new UTF8Encoding(false));
            File.WriteAllText(duplicateSecond + ".bak", "duplicate backup", new UTF8Encoding(false));
            var filesBefore = new[] { clean, duplicateSecond, clean + ".bak", duplicateSecond + ".bak" }
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            AssertThrows<InvalidDataException>(() => service.WriteTransaction(
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [clean] = new Dictionary<string, string> { ["A"] = "changed" },
                    [duplicateSecond] = new Dictionary<string, string> { ["B"] = "changed" }
                },
                true));
            Assert(filesBefore.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                   && TransactionSnapshots(root).Length == 0,
                "A transaction modified files or backups before rejecting duplicate LanguageData keys.");
        });

        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-duplicate-language-keys");
            var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "\uBC88\uC5ED \uC2DC\uC791")]);
            const string duplicateXml = "<LanguageData><Duplicate>one</Duplicate><Duplicate>two</Duplicate></LanguageData>";

            var applyTarget = Path.Combine(modRoot, "Languages", "Korean", "Keyed", "SampleKeys.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(applyTarget)!);
            File.WriteAllText(applyTarget, duplicateXml, new UTF8Encoding(false));
            File.WriteAllText(applyTarget + ".bak", "apply backup", new UTF8Encoding(false));
            var applyBytes = File.ReadAllBytes(applyTarget);
            var applyBackupBytes = File.ReadAllBytes(applyTarget + ".bak");
            var apply = new ReviewApplyService(new AtomicJsonStore(), new LanguageFileService(), CreateExtractor());
            AssertThrows<InvalidDataException>(() => apply.Apply(new ReviewApplyOptions
            {
                ModRoot = modRoot,
                ReviewRoot = run.ReviewRoot!,
                SourceLanguageFolder = "English",
                Overwrite = true
            }));
            Assert(File.ReadAllBytes(applyTarget).SequenceEqual(applyBytes)
                   && File.ReadAllBytes(applyTarget + ".bak").SequenceEqual(applyBackupBytes),
                "Apply modified duplicate-key LanguageData or its backup.");

            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "DuplicateLanguageEntry", out var entryRoot);
            var rmkTarget = Path.Combine(entryRoot, "Languages", "Korean", "Keyed", "SampleKeys.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(rmkTarget)!);
            File.WriteAllText(rmkTarget, duplicateXml, new UTF8Encoding(false));
            File.WriteAllText(rmkTarget + ".bak", "rmk backup", new UTF8Encoding(false));
            var rmkBytes = File.ReadAllBytes(rmkTarget);
            var rmkBackupBytes = File.ReadAllBytes(rmkTarget + ".bak");
            var workbook = Path.Combine(entryRoot, "history.xlsx");
            AssertThrows<InvalidDataException>(() => CreateRmkExportService().Export(new RmkExportOptions
            {
                RmkWorkspaceRoot = workspaceRoot,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                RmkLanguageFolderName = "Korean",
                SourceLanguage = "English",
                WorkbookPath = workbook,
                Overwrite = true
            }));
            Assert(File.ReadAllBytes(rmkTarget).SequenceEqual(rmkBytes)
                   && File.ReadAllBytes(rmkTarget + ".bak").SequenceEqual(rmkBackupBytes)
                   && !File.Exists(workbook),
                "RMK export modified duplicate-key LanguageData, its backup, or a workbook.");
        });
    }

    private static void ReviewComparisonHashAndWhitespace()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-comparison-hash");
            var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
            var reviewLanguageRoot = Path.Combine(run.ReviewRoot!, "Languages", "Korean");
            var relativeTarget = Path.GetRelativePath(reviewLanguageRoot, row.Target);
            var decision = ReviewDecisionFor(row, relativeTarget, "\uBC88\uC5ED \uC2DC\uC791");
            var decisionPath = ReviewRepository.GetDecisionPath(run.ReviewRoot!);
            var store = new AtomicJsonStore();
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "ComparisonHashEntry", out var entryRoot);
            var workbookPath = Path.Combine(entryRoot, "history.xlsx");
            ReviewApplyOptions ApplyOptions() => new()
            {
                ModRoot = modRoot,
                ReviewRoot = run.ReviewRoot!,
                SourceLanguageFolder = "English",
                Overwrite = true
            };
            RmkExportOptions RmkOptions() => new()
            {
                RmkWorkspaceRoot = workspaceRoot,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                RmkLanguageFolderName = "Korean",
                SourceLanguage = "English",
                WorkbookPath = workbookPath,
                Overwrite = true
            };
            var apply = new ReviewApplyService(store, new LanguageFileService(), CreateExtractor());
            var rmk = CreateRmkExportService();

            for (var version = 0; version <= 5; version++)
            {
                File.Delete(decisionPath);
                File.Delete(decisionPath + ".bak");
                if (version == 0)
                {
                    File.WriteAllText(
                        decisionPath,
                        JsonSerializer.Serialize(new
                        {
                            comparison = run.ComparisonFile,
                            items = new[] { decision }
                        }),
                        new UTF8Encoding(false));
                }
                else
                {
                    store.Write(decisionPath, new ReviewDecisionDocument
                    {
                        Version = version,
                        Comparison = run.ComparisonFile!,
                        Items = [decision]
                    });
                }
                var unboundBytes = File.ReadAllBytes(decisionPath);
                AssertThrows<InvalidDataException>(() => apply.Apply(ApplyOptions()));
                AssertThrows<InvalidDataException>(() => rmk.Export(RmkOptions()));
                Assert(File.ReadAllBytes(decisionPath).SequenceEqual(unboundBytes)
                       && !File.Exists(workbookPath),
                    $"Unbound review decision schema v{version} was written through or mutated.");
            }

            SaveBoundReviewDocument(store, run.ReviewRoot!, new ReviewDecisionDocument
            {
                Comparison = run.ComparisonFile!,
                Items = [decision]
            });
            var primaryBefore = File.ReadAllBytes(decisionPath);
            var backupBefore = File.Exists(decisionPath + ".bak")
                ? File.ReadAllBytes(decisionPath + ".bak")
                : null;
            var comparisonRows = JsonSerializer.Deserialize<List<ReviewComparisonRow>>(
                File.ReadAllText(run.ComparisonFile!))!;
            var mutated = comparisonRows.Single(value => value.Key == row.Key);
            mutated.Node += ".same-source-metadata-swap";
            mutated.DefClass += "Changed";
            mutated.RmkWorkbook = "changed-without-source-or-key-change.xlsx";
            File.WriteAllText(
                run.ComparisonFile!,
                JsonSerializer.Serialize(comparisonRows),
                new UTF8Encoding(false));

            AssertThrows<InvalidDataException>(() =>
                new ReviewWorkspaceService(store, CreateExtractor()).Load(run.ReviewRoot!));
            AssertThrows<InvalidDataException>(() => apply.Apply(ApplyOptions()));
            AssertThrows<InvalidDataException>(() => rmk.Export(RmkOptions()));
            var localOutput = Path.Combine(modRoot, "Languages", "Korean", relativeTarget);
            var rmkOutput = Path.Combine(entryRoot, "Languages", "Korean", relativeTarget);
            Assert(File.ReadAllBytes(decisionPath).SequenceEqual(primaryBefore)
                   && (backupBefore is null
                       ? !File.Exists(decisionPath + ".bak")
                       : File.ReadAllBytes(decisionPath + ".bak").SequenceEqual(backupBefore))
                   && !File.Exists(localOutput)
                   && !File.Exists(rmkOutput)
                   && !File.Exists(workbookPath),
                "A same-path comparison metadata swap changed decisions or produced local/RMK output.");

            var nextRoot = Path.Combine(fixtureRoot, "comparison-hash-next-run");
            var nextRow = JsonSerializer.Deserialize<ReviewComparisonRow>(
                JsonSerializer.Serialize(row))!;
            nextRow.Target = Path.Combine(nextRoot, "Languages", "Korean", relativeTarget);
            WriteComparison(nextRoot, [nextRow]);
            var nextProject = new TranslationProject
            {
                Id = "comparison-hash-inheritance",
                LatestReviewRoot = run.ReviewRoot!,
                Runs = [new ProjectRun { ReviewRoot = run.ReviewRoot!, CreatedAt = "2026-01-01T00:00:00Z" }]
            };
            var nextWorkspace = new ReviewWorkspaceService(store, CreateExtractor()).Load(nextRoot, nextProject);
            Assert(nextWorkspace.ImportedDecisions == 0
                   && string.IsNullOrWhiteSpace(nextWorkspace.Items.Single().Decision.Text),
                "A previous v6 decision with mismatched bound comparison evidence was inherited.");
        });

        WithFixture("SampleMod", modRoot =>
        {
            foreach (var (suffix, oldSource) in new[]
                     {
                         ("leading", " {SOURCE}"),
                         ("trailing", "{SOURCE} ")
                     })
            {
                var run = CreateSourceOnlyReview(modRoot, "reviews-whitespace-" + suffix);
                var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
                var relativeTarget = Path.GetRelativePath(
                    Path.Combine(run.ReviewRoot!, "Languages", "Korean"),
                    row.Target);
                var decision = ReviewDecisionFor(row, relativeTarget, "\uBC88\uC5ED \uC2DC\uC791");
                decision.SourceHash = string.Empty;
                decision.SourceText = oldSource.Replace("{SOURCE}", row.Source, StringComparison.Ordinal);
                SaveBoundReviewDocument(new AtomicJsonStore(), run.ReviewRoot!, new ReviewDecisionDocument
                {
                    Comparison = run.ComparisonFile!,
                    Items = [decision]
                });
                var workspaceService = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
                var workspace = workspaceService.Load(run.ReviewRoot!);
                var loaded = workspace.Items.Single(value => value.Row.Key == row.Key);
                Assert(loaded.Decision.SourceChanged
                       && loaded.EffectiveStatus == ReviewStatuses.Pending
                       && loaded.Decision.PreviousSourceText == decision.SourceText,
                    $"A {suffix}-whitespace source change was trimmed away.");
                workspaceService.Save(workspace);
                var applyResult = new ReviewApplyService(
                    new AtomicJsonStore(),
                    new LanguageFileService(),
                    CreateExtractor()).Apply(new ReviewApplyOptions
                {
                    ModRoot = modRoot,
                    ReviewRoot = run.ReviewRoot!,
                    SourceLanguageFolder = "English",
                    Overwrite = true
                });
                var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
                var rmkWorkspace = CreateRmkWorkspace(
                    fixtureRoot,
                    "Whitespace" + suffix,
                    out var entryRoot);
                var workbook = Path.Combine(entryRoot, "history.xlsx");
                var rmkResult = CreateRmkExportService().Export(new RmkExportOptions
                {
                    RmkWorkspaceRoot = rmkWorkspace,
                    RmkEntryRoot = entryRoot,
                    ReviewRoot = run.ReviewRoot!,
                    ModRoot = modRoot,
                    RmkLanguageFolderName = "Korean",
                    SourceLanguage = "English",
                    WorkbookPath = workbook,
                    Overwrite = true
                });
                Assert(applyResult.AppliedEntries == 0
                       && applyResult.WrittenFiles == 0
                       && applyResult.SkippedSourceChanged >= 1
                       && rmkResult.EligibleEntries == 0
                       && rmkResult.WrittenFiles == 0
                       && rmkResult.SkippedSourceChanged + rmkResult.SkippedStatus >= 1
                       && !File.Exists(workbook),
                    $"A {suffix}-whitespace source change produced local or RMK output (apply={applyResult.AppliedEntries}/{applyResult.WrittenFiles}/{applyResult.SkippedSourceChanged}, rmk={rmkResult.EligibleEntries}/{rmkResult.WrittenFiles}/{rmkResult.SkippedSourceChanged}, workbook={File.Exists(workbook)}).");
            }
        });

        WithTempRoot(root =>
        {
            var row = Row(root, "E-newline", "fixture.newline", "line one\nline two");
            WriteComparison(root, [row]);
            var decision = ReviewDecisionFor(
                row,
                Path.Combine("Keyed", "Fixture.xml"),
                "\uBC88\uC5ED");
            decision.SourceHash = string.Empty;
            decision.SourceText = "line one\r\nline two";
            SaveBoundReviewDocument(new AtomicJsonStore(), root, new ReviewDecisionDocument
            {
                Items = [decision]
            });
            var loaded = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor())
                .Load(root).Items.Single();
            Assert(!loaded.Decision.SourceChanged,
                "CRLF and LF source text was not treated as newline-equivalent.");
        });
    }

    private static void ReviewKeylessV6RoundTrip()
    {
        WithTempRoot(root =>
        {
            var reviewRoot = Path.Combine(root, "fresh-keyless");
            var row = Row(reviewRoot, "E-keyless-1", string.Empty, "fresh source");
            row.Kind = "DefInjected";
            row.DefClass = "ThingDef";
            row.Node = "FixtureDef.label";
            WriteComparison(reviewRoot, [row]);
            var service = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
            var workspace = service.Load(reviewRoot);
            var item = workspace.Items.Single();
            service.UpdateTranslation(workspace, item, "\uC0C8 \uBC88\uC5ED", "preserved note", editedByUser: true);
            service.SetStatus(workspace, item, ReviewStatuses.Approved);
            service.Save(workspace);

            var reloadedItem = service.Load(reviewRoot).Items.Single();
            var stored = new ReviewRepository(new AtomicJsonStore()).Load(reviewRoot);
            Assert(reloadedItem.Decision.Text == "\uC0C8 \uBC88\uC5ED"
                   && reloadedItem.Decision.Note == "preserved note"
                   && reloadedItem.EffectiveStatus == ReviewStatuses.Approved
                   && stored.Version == ReviewDecisionDocument.CurrentVersion
                   && ReviewComparisonDocument.IsSha256(stored.ComparisonSha256)
                   && stored.Items.Single().Target == Path.Combine("Keyed", "Fixture.xml")
                   && stored.Items.Single().DefClass == row.DefClass
                   && stored.Items.Single().Node == row.Node,
                "A fresh stable keyless decision did not survive a v6 save/reload round trip.");

            var legacyRoot = Path.Combine(root, "legacy-keyless-orphan");
            var legacyRow = Row(legacyRoot, "E-keyless-legacy", string.Empty, "current source");
            legacyRow.Kind = "DefInjected";
            legacyRow.DefClass = "ThingDef";
            legacyRow.Node = "Current.label";
            WriteComparison(legacyRoot, [legacyRow]);
            using var future = JsonDocument.Parse("{\"kept\":true}");
            var legacyDecision = new ReviewDecision
            {
                Id = legacyRow.Id,
                Status = ReviewStatuses.Approved,
                Text = "legacy translation",
                Note = "legacy note",
                SourceText = "legacy source",
                PreviousSourceText = "older source",
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["future"] = future.RootElement.Clone()
                }
            };
            File.WriteAllText(
                ReviewRepository.GetDecisionPath(legacyRoot),
                JsonSerializer.Serialize(new
                {
                    comparison = Path.Combine(legacyRoot, "_TranslationAudit", "fixture-comparison.json"),
                    items = new[] { legacyDecision }
                }),
                new UTF8Encoding(false));
            var legacyWorkspace = service.Load(legacyRoot);
            Assert(string.IsNullOrWhiteSpace(legacyWorkspace.Items.Single().Decision.Text),
                "A legacy ordinal keyless decision attached to a current row.");
            service.Save(legacyWorkspace);
            var migrated = new ReviewRepository(new AtomicJsonStore()).Load(legacyRoot);
            var orphan = migrated.QuarantinedItems.Single();
            Assert(migrated.Items.Count == 0
                   && orphan.Id == legacyDecision.Id
                   && orphan.Text == legacyDecision.Text
                   && orphan.Note == legacyDecision.Note
                   && orphan.Status == legacyDecision.Status
                   && orphan.SourceText == legacyDecision.SourceText
                   && orphan.PreviousSourceText == legacyDecision.PreviousSourceText
                   && orphan.ExtensionData?["future"].GetProperty("kept").GetBoolean() == true,
                "A legacy keyless orphan lost raw review fields during migration.");
            var secondRoundTrip = service.Load(legacyRoot);
            service.Save(secondRoundTrip);
            var secondStored = new ReviewRepository(new AtomicJsonStore()).Load(legacyRoot);
            Assert(secondStored.QuarantinedItems.Single().Text == legacyDecision.Text,
                "A quarantined keyless orphan was lost on the second round trip.");

            var duplicateRoot = Path.Combine(root, "duplicate-stable-keyless");
            var duplicateRow = Row(duplicateRoot, "E-keyless-duplicate", string.Empty, "source");
            duplicateRow.Kind = "DefInjected";
            duplicateRow.DefClass = "ThingDef";
            duplicateRow.Node = "Duplicate.label";
            WriteComparison(duplicateRoot, [duplicateRow]);
            var duplicatePath = Path.Combine(duplicateRoot, "_TranslationAudit", "fixture-comparison.json");
            var duplicateEvidence = ReviewComparisonDocument.LoadExact(duplicateRoot, duplicatePath).Evidence;
            var first = ReviewDecisionFor(
                duplicateRow,
                Path.Combine("Keyed", "Fixture.xml"),
                "first");
            var second = ReviewDecisionFor(
                duplicateRow,
                Path.Combine("Keyed", "Fixture.xml"),
                "second");
            AssertThrows<InvalidDataException>(() => new AtomicJsonStore().Write(
                ReviewRepository.GetDecisionPath(duplicateRoot),
                new ReviewDecisionDocument
                {
                    Version = ReviewDecisionDocument.CurrentVersion,
                    Comparison = duplicatePath,
                    ComparisonSha256 = duplicateEvidence.Sha256,
                    Items = [first, second]
                }));

            var currentRoot = Path.Combine(root, "later-keyed-same-id");
            var keyedRow = Row(currentRoot, row.Id, "new.key", row.Source);
            WriteComparison(currentRoot, [keyedRow]);
            var project = new TranslationProject
            {
                Id = "keyless-isolation",
                LatestReviewRoot = reviewRoot,
                Runs = [new ProjectRun { ReviewRoot = reviewRoot, CreatedAt = "2026-01-01T00:00:00Z" }]
            };
            var current = service.Load(currentRoot, project);
            Assert(current.ImportedDecisions == 0
                   && string.IsNullOrWhiteSpace(current.Items.Single().Decision.Text),
                "A later keyed row inherited a keyless translation through an ordinal ID.");
        });
    }

    private static void ReviewLegacyUnmatchedQuarantine()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            for (var version = 0; version <= 5; version++)
            {
                var reviewRoot = Path.Combine(fixtureRoot, $"legacy-unmatched-v{version}");
                var currentRow = Row(reviewRoot, $"E-current-{version}", $"current.key.{version}", "current source");
                WriteComparison(reviewRoot, [currentRow]);
                using var future = JsonDocument.Parse("{\"nested\":[1,2,3],\"kept\":true}");
                var unmatched = new ReviewDecision
                {
                    Id = $"legacy-id-{version}",
                    Key = $"legacy.unmatched.{version}",
                    Target = Path.Combine("Keyed", "Missing.xml"),
                    DefClass = "LegacyDef",
                    Node = "LegacyDef.label",
                    Status = ReviewStatuses.Approved,
                    Text = $"legacy translation {version}",
                    Note = $"legacy note {version}",
                    TranslationOrigin = "local",
                    TranslationUpdatedAt = "2026-01-02T03:04:05.0000000+09:00",
                    SourceHash = $"legacy-source-hash-{version}",
                    SourceText = $"legacy source {version}",
                    SourceChanged = true,
                    PreviousSourceText = $"older source {version}",
                    UpdatedAt = "2026-02-03T04:05:06.0000000+09:00",
                    ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["futureDecision"] = future.RootElement.Clone()
                    }
                };
                var decisionPath = ReviewRepository.GetDecisionPath(reviewRoot);
                var comparisonPath = Path.Combine(reviewRoot, "_TranslationAudit", "fixture-comparison.json");
                if (version == 0)
                {
                    File.WriteAllText(
                        decisionPath,
                        JsonSerializer.Serialize(new
                        {
                            comparison = comparisonPath,
                            items = new[] { unmatched }
                        }),
                        new UTF8Encoding(false));
                }
                else
                {
                    new AtomicJsonStore().Write(decisionPath, new ReviewDecisionDocument
                    {
                        Version = version,
                        Comparison = comparisonPath,
                        Items = [unmatched]
                    });
                }

                var service = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
                var firstLoad = service.Load(reviewRoot);
                Assert(string.IsNullOrWhiteSpace(firstLoad.Items.Single().Decision.Text),
                    $"Legacy v{version} unmatched keyed decision attached to a current row.");
                service.Save(firstLoad);
                var secondLoad = service.Load(reviewRoot);
                Assert(string.IsNullOrWhiteSpace(secondLoad.Items.Single().Decision.Text),
                    $"Legacy v{version} unmatched keyed decision attached after first migration save.");
                service.Save(secondLoad);
                var thirdLoad = service.Load(reviewRoot);
                Assert(string.IsNullOrWhiteSpace(thirdLoad.Items.Single().Decision.Text),
                    $"Legacy v{version} unmatched keyed decision attached after second migration save.");
                var stored = new ReviewRepository(new AtomicJsonStore()).Load(reviewRoot);
                Assert(stored.Version == ReviewDecisionDocument.CurrentVersion
                       && stored.Items.Count == 0
                       && stored.QuarantinedItems.Count == 1
                       && ReviewDecisionsEqual(stored.QuarantinedItems[0], unmatched),
                    $"Legacy v{version} unmatched keyed decision did not round-trip losslessly in quarantine.");

                var decisionBytes = File.ReadAllBytes(decisionPath);
                var decisionBackupBytes = File.Exists(decisionPath + ".bak")
                    ? File.ReadAllBytes(decisionPath + ".bak")
                    : null;
                var applyResult = new ReviewApplyService(
                    new AtomicJsonStore(),
                    new LanguageFileService(),
                    CreateExtractor()).Apply(new ReviewApplyOptions
                {
                    ModRoot = modRoot,
                    ReviewRoot = reviewRoot,
                    SourceLanguageFolder = "English",
                    Overwrite = true
                });
                var rmkWorkspace = CreateRmkWorkspace(
                    fixtureRoot,
                    $"LegacyUnmatched{version}",
                    out var entryRoot);
                var workbook = Path.Combine(entryRoot, "history.xlsx");
                RimWorldTranslatorRmkXlsxWriter.Write(
                    workbook,
                    [new RimWorldTranslatorRmkHistoryRow
                    {
                        Identifier = "Keyed+Existing.Sentinel",
                        ClassName = "Keyed",
                        Key = "Existing.Sentinel",
                        Source = "existing source",
                        Translation = "existing translation"
                    }],
                    "English");
                var workbookBytes = File.ReadAllBytes(workbook);
                var rmkResult = CreateRmkExportService().Export(new RmkExportOptions
                {
                    RmkWorkspaceRoot = rmkWorkspace,
                    RmkEntryRoot = entryRoot,
                    ReviewRoot = reviewRoot,
                    ModRoot = modRoot,
                    RmkLanguageFolderName = "Korean",
                    SourceLanguage = "English",
                    WorkbookPath = workbook,
                    Overwrite = true
                });
                Assert(applyResult.AppliedEntries == 0
                       && applyResult.WrittenFiles == 0
                       && rmkResult.EligibleEntries == 0
                       && rmkResult.WrittenFiles == 0
                       && File.ReadAllBytes(workbook).SequenceEqual(workbookBytes)
                       && File.ReadAllBytes(decisionPath).SequenceEqual(decisionBytes)
                       && (decisionBackupBytes is null
                           ? !File.Exists(decisionPath + ".bak")
                           : File.ReadAllBytes(decisionPath + ".bak").SequenceEqual(decisionBackupBytes)),
                    $"Legacy v{version} quarantined decision produced output or mutated decisions/workbook.");
            }

            var matchedRoot = Path.Combine(fixtureRoot, "legacy-versionless-matched");
            var matchedRow = Row(matchedRoot, "E-versionless-matched", "matched.key", "matched source");
            WriteComparison(matchedRoot, [matchedRow]);
            var matchedDecision = ReviewDecisionFor(
                matchedRow,
                Path.Combine("Keyed", "Fixture.xml"),
                "matched translation");
            matchedDecision.Note = "matched note";
            matchedDecision.Status = ReviewStatuses.Approved;
            var matchedComparison = Path.Combine(matchedRoot, "_TranslationAudit", "fixture-comparison.json");
            File.WriteAllText(
                ReviewRepository.GetDecisionPath(matchedRoot),
                JsonSerializer.Serialize(new
                {
                    comparison = matchedComparison,
                    items = new[] { matchedDecision }
                }),
                new UTF8Encoding(false));
            var matchedService = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
            var matchedWorkspace = matchedService.Load(matchedRoot);
            var matchedItem = matchedWorkspace.Items.Single();
            Assert(matchedItem.Decision.Text == matchedDecision.Text
                   && matchedItem.Decision.Note == matchedDecision.Note
                   && matchedItem.EffectiveStatus == ReviewStatuses.Pending
                   && matchedItem.Decision.SourceChanged
                   && SourceVerificationUnavailable(matchedItem.Decision),
                "A versionless stable target+key decision was not preserved and demoted.");
            matchedService.Save(matchedWorkspace);
            var matchedStored = new ReviewRepository(new AtomicJsonStore()).Load(matchedRoot);
            Assert(matchedStored.Items.Count == 1 && matchedStored.QuarantinedItems.Count == 0,
                "A versionless stable target+key decision was quarantined instead of migrated active.");
        });
    }

    private static bool ReviewDecisionsEqual(ReviewDecision left, ReviewDecision right) =>
        left.Id == right.Id
        && left.Key == right.Key
        && left.Target == right.Target
        && left.DefClass == right.DefClass
        && left.Node == right.Node
        && left.Status == right.Status
        && left.Text == right.Text
        && left.Note == right.Note
        && left.TranslationOrigin == right.TranslationOrigin
        && left.TranslationUpdatedAt == right.TranslationUpdatedAt
        && left.SourceHash == right.SourceHash
        && left.SourceText == right.SourceText
        && left.SourceChanged == right.SourceChanged
        && left.PreviousSourceText == right.PreviousSourceText
        && left.UpdatedAt == right.UpdatedAt
        && JsonSerializer.Serialize(left.ExtensionData) == JsonSerializer.Serialize(right.ExtensionData);

    private static void ApplyIdentitySwapInjection()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-identity-swap-injection");
            var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "\uBC88\uC5ED \uC2DC\uC791")]);
            var relativeTarget = Path.GetRelativePath(
                Path.Combine(run.ReviewRoot!, "Languages", "Korean"),
                row.Target);
            var target = Path.Combine(modRoot, "Languages", "Korean", relativeTarget);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(
                target,
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><LanguageData><{row.Key}>before</{row.Key}></LanguageData>",
                new UTF8Encoding(false));
            File.WriteAllText(target + ".bak", "backup-before", new UTF8Encoding(false));
            var targetBytes = File.ReadAllBytes(target);
            var backupBytes = File.ReadAllBytes(target + ".bak");
            var displacedRoot = modRoot + "-snapshot-original";
            var service = new ReviewApplyService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor());
            service.AfterModRootSnapshotCapturedTestHook = () =>
            {
                Directory.Move(modRoot, displacedRoot);
                CopyDirectory(displacedRoot, modRoot);
            };
            AssertThrows<InvalidDataException>(() => service.Apply(new ReviewApplyOptions
            {
                ModRoot = modRoot,
                ReviewRoot = run.ReviewRoot!,
                SourceLanguageFolder = "English",
                Overwrite = true
            }));
            Assert(File.ReadAllBytes(target).SequenceEqual(targetBytes)
                   && File.ReadAllBytes(target + ".bak").SequenceEqual(backupBytes)
                   && File.ReadAllBytes(Path.Combine(displacedRoot, "Languages", "Korean", relativeTarget)).SequenceEqual(targetBytes)
                   && File.ReadAllBytes(Path.Combine(displacedRoot, "Languages", "Korean", relativeTarget) + ".bak").SequenceEqual(backupBytes)
                   && TransactionSnapshots(modRoot).Length == 0,
                "A mod-root identity swap reached XML/backup writes before the final physical recheck.");
        });
    }

    private static void ApplyPhysicalWorkshopBoundary()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-physical-workshop-boundary");
            var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "\uBC88\uC5ED \uC2DC\uC791")]);
            var service = new ReviewApplyService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor());
            ReviewApplyOptions Options(string root) => new()
            {
                ModRoot = root,
                ReviewRoot = run.ReviewRoot!,
                SourceLanguageFolder = "English",
                Overwrite = true,
                DryRun = true
            };
            var normal = service.Apply(Options(modRoot));
            Assert(normal.AppliedEntries == 1,
                "A normal canonical local mod failed the physical write-boundary preview.");

            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workshopContainer = Path.Combine(
                fixtureRoot,
                "VeryLongSteamLibraryNameForCanonicalization",
                "steamapps",
                "workshop",
                "content",
                "294100");
            var workshop = Path.Combine(workshopContainer, "123456789012345678");
            Directory.CreateDirectory(Path.Combine(workshop, "Languages", "Korean"));
            var sentinel = Path.Combine(workshop, "Languages", "Korean", "keep.txt");
            File.WriteAllText(sentinel, "unchanged", new UTF8Encoding(false));
            AssertThrows<InvalidOperationException>(() => service.Apply(Options(workshop)));

            var aliasContainer = Path.Combine(fixtureRoot, "WorkshopParentAlias");
            try
            {
                Directory.CreateSymbolicLink(aliasContainer, workshopContainer);
                AssertThrows<InvalidDataException>(() =>
                    service.Apply(Options(Path.Combine(aliasContainer, Path.GetFileName(workshop)))));
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or PlatformNotSupportedException)
            {
                Console.WriteLine(
                    $"[INFO] Apply parent-link fixture unavailable ({exception.GetType().Name}); canonical Workshop boundary remained exercised.");
            }

            var shortWorkshop = TryGetShortPath(workshop);
            if (!string.IsNullOrWhiteSpace(shortWorkshop)
                && !shortWorkshop.Equals(workshop, StringComparison.OrdinalIgnoreCase))
            {
                AssertThrows<InvalidOperationException>(() => service.Apply(Options(shortWorkshop)));
            }
            else
            {
                Console.WriteLine("[INFO] 8.3 short paths are unavailable; canonical long Workshop path remained exercised.");
            }
            Assert(File.ReadAllText(sentinel) == "unchanged"
                   && Directory.EnumerateFiles(Path.Combine(workshop, "Languages", "Korean"))
                       .SequenceEqual([sentinel], StringComparer.OrdinalIgnoreCase),
                "A denied canonical, parent-link, or short/long Workshop target was modified.");
        });
    }

    private static void RmkEligibilityCounts()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-rmk-eligibility");
            var row = run.Rows.Single(value => value.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "\uBC88\uC5ED \uC2DC\uC791")]);
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "EligibilityEntry", out var entryRoot);
            var languageRoot = Path.Combine(entryRoot, "Languages", "Korean", "Keyed");
            Directory.CreateDirectory(languageRoot);
            var firstPath = Path.Combine(languageRoot, "First.xml");
            File.WriteAllText(
                firstPath,
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><LanguageData><{row.Key}>existing</{row.Key}></LanguageData>",
                new UTF8Encoding(false));
            var firstBytes = File.ReadAllBytes(firstPath);
            var workbook = Path.Combine(entryRoot, "history.xlsx");
            var decisionPath = ReviewRepository.GetDecisionPath(run.ReviewRoot!);
            var decisionBytes = File.ReadAllBytes(decisionPath);
            var service = CreateRmkExportService();
            RmkExportOptions Options(bool overwrite) => new()
            {
                RmkWorkspaceRoot = workspaceRoot,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                RmkLanguageFolderName = "Korean",
                SourceLanguage = "English",
                WorkbookPath = workbook,
                Overwrite = overwrite
            };

            var excluded = service.Export(Options(overwrite: false));
            Assert(excluded.EligibleEntries == 0
                   && excluded.UpdatedExisting == 0
                   && excluded.AddedNew == 0
                   && excluded.WrittenFiles == 0
                   && !File.Exists(workbook)
                   && File.ReadAllBytes(firstPath).SequenceEqual(firstBytes)
                   && File.ReadAllBytes(decisionPath).SequenceEqual(decisionBytes),
                "Overwrite=false over-reported eligibility or wrote RMK output.");

            var secondPath = Path.Combine(languageRoot, "Second.xml");
            File.WriteAllText(
                secondPath,
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><LanguageData><{row.Key}>other</{row.Key}></LanguageData>",
                new UTF8Encoding(false));
            var secondBytes = File.ReadAllBytes(secondPath);
            var ambiguous = service.Export(Options(overwrite: true));
            Assert(ambiguous.EligibleEntries == 0
                   && ambiguous.UpdatedExisting == 0
                   && ambiguous.AddedNew == 0
                   && ambiguous.WrittenFiles == 0
                   && ambiguous.SkippedAmbiguous >= 1
                   && !File.Exists(workbook)
                   && File.ReadAllBytes(firstPath).SequenceEqual(firstBytes)
                   && File.ReadAllBytes(secondPath).SequenceEqual(secondBytes)
                   && File.ReadAllBytes(decisionPath).SequenceEqual(decisionBytes),
                "An ambiguous RMK target over-reported eligibility or wrote output.");
        });
    }

    private static ReviewDecision ReviewDecisionFor(ReviewComparisonRow row, string target, string text) => new()
    {
        Id = row.Id,
        Key = row.Key,
        Target = target,
        DefClass = row.DefClass,
        Node = row.Node,
        Status = ReviewStatuses.Approved,
        Text = text,
        SourceText = row.Source,
        SourceHash = StableIdentity.Sha256(row.Source)
    };

    private static bool SourceVerificationUnavailable(ReviewDecision decision) =>
        decision.ExtensionData is not null
        && decision.ExtensionData.Any(property =>
            property.Key.Equals("sourceVerification", StringComparison.OrdinalIgnoreCase)
            && property.Value.ValueKind == JsonValueKind.String
            && property.Value.GetString()?.Equals("unavailable", StringComparison.OrdinalIgnoreCase) == true);

    private static string TryGetShortPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        var buffer = new char[32768];
        var length = GetShortPathName(path, buffer, buffer.Length);
        return length > 0 && length < buffer.Length
            ? new string(buffer, 0, checked((int)length))
            : string.Empty;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, int bufferLength);

    private static string[] TransactionSnapshots(string root) =>
        Directory.EnumerateFiles(root, "*.transaction.bak", SearchOption.AllDirectories).ToArray();
}
