using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void RmkExportTransaction()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-rmk-transaction");
            var defRow = run.Rows.Single(row => row.Key == "Codex_TestWorkbench.label");
            var keyedRow = run.Rows.Single(row => row.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(defRow, "시험 작업대")]);

            var root = Directory.GetParent(modRoot)!.FullName;
            var rmkRoot = Path.Combine(root, "RmkEntry");
            var defPath = Path.Combine(rmkRoot, "Languages", "Korean", "DefInjected", "ThingDef", "CodexAI.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(defPath)!);
            File.WriteAllText(defPath,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<LanguageData>\n  <Existing.Rmk>BEFORE</Existing.Rmk>\n</LanguageData>\n",
                new System.Text.UTF8Encoding(false));
            var originalDef = File.ReadAllBytes(defPath);
            var workbookPath = Path.Combine(rmkRoot, "history.xlsx");
            var service = CreateRmkExportService();

            var first = service.Export(new RmkExportOptions
            {
                RmkEntryRoot = rmkRoot,
                ReviewRoot = run.ReviewRoot!,
                RmkLanguageFolderName = "Korean",
                WorkbookPath = workbookPath,
                Overwrite = true
            });
            Assert(first.EligibleEntries == 1 && first.WrittenFiles == 1, "Initial RMK export count changed.");
            Assert(File.Exists(workbookPath), "RMK workbook was not created.");
            Assert(File.Exists(defPath + ".bak"), "RMK XML backup was not retained.");
            Assert(File.ReadAllBytes(defPath + ".bak").SequenceEqual(originalDef), "RMK XML backup did not preserve the prior file.");
            var defBeforeFailure = File.ReadAllBytes(defPath);
            var workbookBeforeFailure = File.ReadAllBytes(workbookPath);

            SaveReviewDecisions(run, [(defRow, "시험 작업대"), (keyedRow, "번역 시작")]);
            var blockedTarget = Path.Combine(rmkRoot, "Languages", "Korean", "Keyed", "SampleKeys.xml");
            Directory.CreateDirectory(blockedTarget);
            var error = CaptureException<IOException>(() => service.Export(new RmkExportOptions
            {
                RmkEntryRoot = rmkRoot,
                ReviewRoot = run.ReviewRoot!,
                RmkLanguageFolderName = "Korean",
                WorkbookPath = workbookPath,
                Overwrite = true
            }));
            Assert(error.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase), "RMK failure did not report rollback.");
            Assert(File.ReadAllBytes(defPath).SequenceEqual(defBeforeFailure), "RMK XML was not restored after failure.");
            Assert(File.ReadAllBytes(workbookPath).SequenceEqual(workbookBeforeFailure), "RMK workbook was not restored after failure.");
            Assert(Directory.Exists(blockedTarget), "RMK rollback modified the blocking directory.");
            Assert(!Directory.EnumerateFiles(rmkRoot, "*.transaction.bak", SearchOption.AllDirectories).Any(), "RMK transaction snapshots were left behind.");

            Directory.Delete(blockedTarget);
            var recovered = service.Export(new RmkExportOptions
            {
                RmkEntryRoot = rmkRoot,
                ReviewRoot = run.ReviewRoot!,
                RmkLanguageFolderName = "Korean",
                WorkbookPath = workbookPath,
                Overwrite = true
            });
            Assert(recovered.EligibleEntries == 2 && File.Exists(blockedTarget), "RMK export did not recover after rollback.");
            Assert(RimWorldTranslatorRmkXlsxReader.Read(workbookPath).Rows.Count >= 2, "RMK workbook is unreadable or incomplete.");
        });
    }

    private static void RmkSourceHistoryRoundTrip()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var initial = CreateSourceOnlyReview(modRoot, "reviews-rmk-history-initial");
            var initialRow = initial.Rows.Single(row => row.Key == "CodexTranslator.SampleButton");
            const string translation = "번역 시작";
            SaveReviewDecisions(initial, [(initialRow, translation)]);

            var root = Directory.GetParent(modRoot)!.FullName;
            var rmkRoot = Path.Combine(root, "RmkHistoryEntry");
            Directory.CreateDirectory(rmkRoot);
            var workbookPath = Path.Combine(rmkRoot, "history.xlsx");
            var service = CreateRmkExportService();
            service.Export(new RmkExportOptions
            {
                RmkEntryRoot = rmkRoot,
                ReviewRoot = initial.ReviewRoot!,
                RmkLanguageFolderName = "Korean",
                WorkbookPath = workbookPath,
                SourceLanguage = "English",
                Overwrite = true
            });

            var englishPath = Path.Combine(modRoot, "Languages", "English", "Keyed", "SampleKeys.xml");
            var english = File.ReadAllText(englishPath).Replace("Translate now", "Translate immediately", StringComparison.Ordinal);
            File.WriteAllText(englishPath, english, new System.Text.UTF8Encoding(false));
            var referenceRoot = Path.Combine(rmkRoot, "Languages", "Korean");
            var updated = new TranslationEngine(RepositoryRoot(), CreateExtractor()).RunAsync(new TranslationEngineOptions
            {
                ModRoot = modRoot,
                SourceOnly = true,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(root, "reviews-rmk-history-updated"),
                SourceLanguageFolder = "English",
                ReferenceLanguageRoots = [referenceRoot],
                ReferenceSourceWorkbook = workbookPath
            }).GetAwaiter().GetResult();
            var updatedRow = updated.Rows.Single(row => row.Key == "CodexTranslator.SampleButton");
            Assert(updatedRow.Existing == translation && updatedRow.ExistingOrigin == "rmk", "RMK translation origin was not restored.");
            Assert(updatedRow.RmkSourceChanged, "RMK source change was not detected.");
            Assert(updatedRow.RmkHistoricalSource == "Translate now", "Translation-time RMK source was overwritten.");
            Assert(updatedRow.RmkCurrentSource == "Translate immediately", "Current RMK source was not captured.");

            SaveReviewDecisions(updated, [(updatedRow, translation, ReviewStatuses.Pending, true, updatedRow.RmkHistoricalSource)]);
            service.Export(new RmkExportOptions
            {
                RmkEntryRoot = rmkRoot,
                ReviewRoot = updated.ReviewRoot!,
                RmkLanguageFolderName = "Korean",
                WorkbookPath = workbookPath,
                SourceLanguage = "English",
                Overwrite = true
            });

            var history = RimWorldTranslatorRmkXlsxReader.Read(workbookPath);
            var historyRow = history.Rows.Single(row => row.Identifier == updatedRow.RmkIdentifier);
            Assert(historyRow.Source == "Translate now", "Unreviewed changed source replaced translation-time RMK history.");
            Assert(historyRow.Translation == translation, "Unreviewed changed source replaced the RMK translation.");
            var rmkMap = new LanguageFileService().Read(Path.Combine(referenceRoot, "Keyed", "SampleKeys.xml"));
            Assert(rmkMap[updatedRow.Key] == translation, "Pending source change overwrote RMK XML.");
        });
    }

    private static void TranslationRmkLanguageMismatch()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var languages = Path.Combine(modRoot, "Languages");
            Directory.Move(Path.Combine(languages, "English"), Path.Combine(languages, "Spanish"));
            var root = Directory.GetParent(modRoot)!.FullName;
            var workbook = Path.Combine(root, "rmk-english-history.xlsx");
            RimWorldTranslatorRmkXlsxWriter.Write(workbook, [], "English");
            var result = new TranslationEngine(RepositoryRoot(), CreateExtractor()).RunAsync(new TranslationEngineOptions
            {
                ModRoot = modRoot,
                SourceOnly = true,
                ReviewOnly = true,
                ReviewRoot = Path.Combine(root, "reviews-rmk-language-mismatch"),
                SourceLanguageFolder = "Spanish",
                ReferenceSourceWorkbook = workbook
            }).GetAwaiter().GetResult();
            Assert(result.Rows.Count > 0, "A stale RMK source-language header blocked current source extraction.");
            Assert(result.Rows.All(row => !row.RmkSourceChanged), "Different source languages were compared as updates.");
        });
    }

    private static RmkExportService CreateRmkExportService() =>
        new(new AtomicJsonStore(), new LanguageFileService(), CreateExtractor());

    private static void SaveReviewDecisions(
        TranslationRunResult run,
        IEnumerable<(ReviewComparisonRow Row, string Text, string Status, bool SourceChanged, string PreviousSource)> items)
    {
        var languageRoot = Path.Combine(run.ReviewRoot!, "Languages", "Korean");
        var document = new ReviewDecisionDocument
        {
            ReviewRoot = run.ReviewRoot!,
            Comparison = run.ComparisonFile!,
            Items = items.Select(item => new ReviewDecision
            {
                Id = item.Row.Id,
                Key = item.Row.Key,
                Target = Path.GetRelativePath(languageRoot, item.Row.Target),
                Status = item.Status,
                Text = item.Text,
                SourceText = item.Row.Source,
                SourceChanged = item.SourceChanged,
                PreviousSourceText = item.PreviousSource
            }).ToList()
        };
        new ReviewRepository(new AtomicJsonStore()).Save(run.ReviewRoot!, document);
    }

    private static void SaveReviewDecisions(
        TranslationRunResult run,
        IEnumerable<(ReviewComparisonRow Row, string Text)> items) =>
        SaveReviewDecisions(run, items.Select(item => (item.Row, item.Text, ReviewStatuses.Approved, false, string.Empty)));

    private static T CaptureException<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            return ex;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
