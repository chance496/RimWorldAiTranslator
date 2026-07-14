using System.Text;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void OperationPlanFingerprintBinding()
    {
        ReviewApplyPlanFingerprintUnchanged();
        ReviewApplyPlanFingerprintDecisionDrift();
        ReviewApplyPlanFingerprintOutputDrift();
        ReviewApplyPlanFingerprintSourceDrift();
        RmkPlanFingerprintUnchanged();
        RmkPlanFingerprintDecisionDrift();
        RmkPlanFingerprintWorkbookDrift();
        RmkPlanFingerprintPathDrift();
    }

    private static void ReviewApplyPlanFingerprintUnchanged()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-plan-apply-unchanged");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            const string translation = "계획 고정 번역";
            SaveReviewDecisions(run, [(row, translation)]);
            var targetPath = PlanTargetPath(
                run,
                row,
                Path.Combine(modRoot, "Languages", "Korean"));
            var service = new ReviewApplyService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor());

            var preview = service.Apply(ReviewApplyPlanOptions(run, modRoot, dryRun: true));
            AssertPlanFingerprint(preview.PlanFingerprint, "ReviewApply DryRun");
            Assert(preview.SafeCandidateRows == 1,
                "ReviewApply DryRun did not retain its single safe candidate row.");
            Assert(!File.Exists(targetPath), "ReviewApply DryRun wrote its target.");

            var applied = service.Apply(ReviewApplyPlanOptions(
                run,
                modRoot,
                dryRun: false,
                expectedPlanFingerprint: preview.PlanFingerprint));
            Assert(applied.PlanFingerprint.Equals(preview.PlanFingerprint, StringComparison.Ordinal),
                "An unchanged ReviewApply execution returned a different plan fingerprint.");
            Assert(applied.SafeCandidateRows == 1
                   && applied.AppliedEntries == 1
                   && applied.WrittenFiles == 1,
                "An unchanged ReviewApply execution did not perform its planned write.");
            var written = new LanguageFileService().Read(targetPath);
            Assert(written.TryGetValue(row.Key, out var value)
                   && value.Equals(translation, StringComparison.Ordinal),
                "An unchanged ReviewApply execution wrote the wrong translation.");
        });
    }

    private static void ReviewApplyPlanFingerprintDecisionDrift()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-plan-apply-decision-drift");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "미리보기 번역")]);
            var targetPath = PlanTargetPath(
                run,
                row,
                Path.Combine(modRoot, "Languages", "Korean"));
            var service = new ReviewApplyService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor());
            var preview = service.Apply(ReviewApplyPlanOptions(run, modRoot, dryRun: true));
            AssertPlanFingerprint(preview.PlanFingerprint, "ReviewApply decision preview");

            SaveReviewDecisions(run, [(row, "미리보기 이후 변경된 번역")]);
            AssertPlanMismatch(
                () => service.Apply(ReviewApplyPlanOptions(
                    run,
                    modRoot,
                    dryRun: false,
                    expectedPlanFingerprint: preview.PlanFingerprint)),
                "ReviewApply decision drift");
            Assert(!File.Exists(targetPath),
                "ReviewApply wrote a target after its reviewed text changed following preview.");
        });
    }

    private static void ReviewApplyPlanFingerprintOutputDrift()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-plan-apply-output-drift");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "출력 기준선 시험 번역")]);
            var targetPath = PlanTargetPath(
                run,
                row,
                Path.Combine(modRoot, "Languages", "Korean"));
            var service = new ReviewApplyService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor());
            var preview = service.Apply(ReviewApplyPlanOptions(run, modRoot, dryRun: true));
            AssertPlanFingerprint(preview.PlanFingerprint, "ReviewApply output preview");
            Assert(!File.Exists(targetPath), "ReviewApply output-drift fixture unexpectedly had a target.");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            const string userBaseline =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<LanguageData><Phase10.UserValue>preview 이후 사용자 저장</Phase10.UserValue></LanguageData>\n";
            File.WriteAllText(targetPath, userBaseline, new UTF8Encoding(false));
            var baselineBytes = File.ReadAllBytes(targetPath);

            AssertPlanMismatch(
                () => service.Apply(ReviewApplyPlanOptions(
                    run,
                    modRoot,
                    dryRun: false,
                    expectedPlanFingerprint: preview.PlanFingerprint)),
                "ReviewApply output baseline drift");
            Assert(File.ReadAllBytes(targetPath).SequenceEqual(baselineBytes),
                "ReviewApply changed the user's output baseline while rejecting a stale preview.");
            Assert(!File.Exists(targetPath + ".bak"),
                "ReviewApply entered its write transaction before rejecting a changed output baseline.");
        });
    }

    private static void RmkPlanFingerprintUnchanged()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-plan-rmk-unchanged");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            const string translation = "RMK 계획 고정 번역";
            SaveReviewDecisions(run, [(row, translation)]);
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "PlanUnchanged", out var entryRoot);
            var workbookPath = Path.Combine(entryRoot, "plan-history.xlsx");
            var targetPath = PlanTargetPath(
                run,
                row,
                Path.Combine(entryRoot, "Languages", "Korean"));
            var service = CreateRmkExportService();

            var preview = service.Export(RmkPlanOptions(
                run,
                modRoot,
                workspaceRoot,
                entryRoot,
                workbookPath,
                dryRun: true));
            AssertPlanFingerprint(preview.PlanFingerprint, "RMK DryRun");
            Assert(!File.Exists(workbookPath) && !File.Exists(targetPath),
                "RMK DryRun wrote a workbook or language target.");

            var exported = service.Export(RmkPlanOptions(
                run,
                modRoot,
                workspaceRoot,
                entryRoot,
                workbookPath,
                dryRun: false,
                expectedPlanFingerprint: preview.PlanFingerprint));
            Assert(exported.PlanFingerprint.Equals(preview.PlanFingerprint, StringComparison.Ordinal),
                "An unchanged RMK execution returned a different plan fingerprint.");
            Assert(exported.EligibleEntries == 1 && exported.WrittenFiles == 1,
                "An unchanged RMK execution did not perform its planned write.");
            Assert(File.Exists(workbookPath), "An unchanged RMK execution did not write its workbook.");
            var written = new LanguageFileService().Read(targetPath);
            Assert(written.TryGetValue(row.Key, out var value)
                   && value.Equals(translation, StringComparison.Ordinal),
                "An unchanged RMK execution wrote the wrong translation.");
        });
    }

    private static void ReviewApplyPlanFingerprintSourceDrift()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-plan-apply-source-drift");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "원문 기준선 시험 번역")]);
            var targetPath = PlanTargetPath(
                run,
                row,
                Path.Combine(modRoot, "Languages", "Korean"));
            var sourcePath = Path.Combine(modRoot, "Languages", "English", "Keyed", "SampleKeys.xml");
            var service = new ReviewApplyService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor());
            var preview = service.Apply(ReviewApplyPlanOptions(run, modRoot, dryRun: true));
            AssertPlanFingerprint(preview.PlanFingerprint, "ReviewApply source preview");

            var originalSource = File.ReadAllText(sourcePath);
            var changedSource = originalSource.Replace(
                "Translate now",
                "Translate after plan preview",
                StringComparison.Ordinal);
            Assert(!changedSource.Equals(originalSource, StringComparison.Ordinal),
                "The synthetic ReviewApply source mutation did not change its fixture.");
            File.WriteAllText(sourcePath, changedSource, new UTF8Encoding(false));
            var changedSourceBytes = File.ReadAllBytes(sourcePath);

            AssertPlanMismatch(
                () => service.Apply(ReviewApplyPlanOptions(
                    run,
                    modRoot,
                    dryRun: false,
                    expectedPlanFingerprint: preview.PlanFingerprint)),
                "ReviewApply source drift");
            Assert(!File.Exists(targetPath),
                "ReviewApply wrote a target after its source changed following preview.");
            Assert(File.ReadAllBytes(sourcePath).SequenceEqual(changedSourceBytes),
                "ReviewApply changed the user's updated source while rejecting a stale preview.");
        });
    }

    private static void RmkPlanFingerprintDecisionDrift()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-plan-rmk-decision-drift");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "RMK 미리보기 번역")]);
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "PlanDecisionDrift", out var entryRoot);
            var workbookPath = Path.Combine(entryRoot, "plan-history.xlsx");
            var targetPath = PlanTargetPath(
                run,
                row,
                Path.Combine(entryRoot, "Languages", "Korean"));
            var service = CreateRmkExportService();
            var preview = service.Export(RmkPlanOptions(
                run,
                modRoot,
                workspaceRoot,
                entryRoot,
                workbookPath,
                dryRun: true));
            AssertPlanFingerprint(preview.PlanFingerprint, "RMK decision preview");

            SaveReviewDecisions(run, [(row, "RMK 미리보기 이후 변경된 번역")]);
            AssertPlanMismatch(
                () => service.Export(RmkPlanOptions(
                    run,
                    modRoot,
                    workspaceRoot,
                    entryRoot,
                    workbookPath,
                    dryRun: false,
                    expectedPlanFingerprint: preview.PlanFingerprint)),
                "RMK decision drift");
            Assert(!File.Exists(workbookPath) && !File.Exists(targetPath),
                "RMK wrote a workbook or target after its reviewed text changed following preview.");
        });
    }

    private static void RmkPlanFingerprintWorkbookDrift()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-plan-rmk-workbook-drift");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "RMK workbook 기준선 시험 번역")]);
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "PlanWorkbookDrift", out var entryRoot);
            var workbookPath = Path.Combine(entryRoot, "plan-history.xlsx");
            var targetPath = PlanTargetPath(
                run,
                row,
                Path.Combine(entryRoot, "Languages", "Korean"));
            var service = CreateRmkExportService();
            var preview = service.Export(RmkPlanOptions(
                run,
                modRoot,
                workspaceRoot,
                entryRoot,
                workbookPath,
                dryRun: true));
            AssertPlanFingerprint(preview.PlanFingerprint, "RMK workbook preview");
            Assert(!File.Exists(workbookPath), "RMK workbook-drift fixture unexpectedly had a workbook.");

            RimWorldTranslatorRmkXlsxWriter.Write(
                workbookPath,
                [new RimWorldTranslatorRmkHistoryRow
                {
                    Identifier = "Keyed+Phase10.UserBaseline",
                    ClassName = "Keyed",
                    Key = "Phase10.UserBaseline",
                    Source = "preview 이후 사용자 원문",
                    Translation = "preview 이후 사용자 번역"
                }],
                "English");
            var baselineBytes = File.ReadAllBytes(workbookPath);

            AssertPlanMismatch(
                () => service.Export(RmkPlanOptions(
                    run,
                    modRoot,
                    workspaceRoot,
                    entryRoot,
                    workbookPath,
                    dryRun: false,
                    expectedPlanFingerprint: preview.PlanFingerprint)),
                "RMK workbook baseline drift");
            Assert(File.ReadAllBytes(workbookPath).SequenceEqual(baselineBytes),
                "RMK changed the user's workbook baseline while rejecting a stale preview.");
            Assert(!File.Exists(workbookPath + ".bak") && !File.Exists(targetPath),
                "RMK entered its write transaction before rejecting a changed workbook baseline.");
        });
    }

    private static void RmkPlanFingerprintPathDrift()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-plan-rmk-path-drift");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "RMK 경로 고정 시험 번역")]);
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "PlanPathDrift", out var entryRoot);
            var previewWorkbookPath = Path.Combine(entryRoot, "approved-plan-history.xlsx");
            var alternateWorkbookPath = Path.Combine(entryRoot, "alternate-user-history.xlsx");
            var targetPath = PlanTargetPath(
                run,
                row,
                Path.Combine(entryRoot, "Languages", "Korean"));
            var service = CreateRmkExportService();
            var preview = service.Export(RmkPlanOptions(
                run,
                modRoot,
                workspaceRoot,
                entryRoot,
                previewWorkbookPath,
                dryRun: true));
            AssertPlanFingerprint(preview.PlanFingerprint, "RMK path preview");

            RimWorldTranslatorRmkXlsxWriter.Write(
                alternateWorkbookPath,
                [new RimWorldTranslatorRmkHistoryRow
                {
                    Identifier = "Keyed+Phase10.AlternatePath",
                    ClassName = "Keyed",
                    Key = "Phase10.AlternatePath",
                    Source = "사용자 대체 workbook 원문",
                    Translation = "사용자 대체 workbook 번역"
                }],
                "English");
            var alternateWorkbookBytes = File.ReadAllBytes(alternateWorkbookPath);

            AssertPlanMismatch(
                () => service.Export(RmkPlanOptions(
                    run,
                    modRoot,
                    workspaceRoot,
                    entryRoot,
                    alternateWorkbookPath,
                    dryRun: false,
                    expectedPlanFingerprint: preview.PlanFingerprint)),
                "RMK workbook path drift");
            Assert(!File.Exists(previewWorkbookPath),
                "RMK wrote the workbook path from an expired preview.");
            Assert(File.ReadAllBytes(alternateWorkbookPath).SequenceEqual(alternateWorkbookBytes),
                "RMK changed an existing alternate workbook while rejecting a stale path.");
            Assert(!File.Exists(alternateWorkbookPath + ".bak") && !File.Exists(targetPath),
                "RMK entered its write transaction before rejecting a changed workbook path.");
        });
    }

    private static ReviewApplyOptions ReviewApplyPlanOptions(
        TranslationRunResult run,
        string modRoot,
        bool dryRun,
        string? expectedPlanFingerprint = null) => new()
    {
        ModRoot = modRoot,
        ReviewRoot = run.ReviewRoot!,
        SourceKind = "Local",
        SourceLanguageFolder = "English",
        LanguageFolderName = "Korean",
        ApplyStatus = ReviewApplyStatus.ApprovedOnly,
        Overwrite = true,
        DryRun = dryRun,
        ExpectedPlanFingerprint = expectedPlanFingerprint
    };

    private static RmkExportOptions RmkPlanOptions(
        TranslationRunResult run,
        string modRoot,
        string workspaceRoot,
        string entryRoot,
        string workbookPath,
        bool dryRun,
        string? expectedPlanFingerprint = null) => new()
    {
        RmkWorkspaceRoot = workspaceRoot,
        RmkEntryRoot = entryRoot,
        ReviewRoot = run.ReviewRoot!,
        ModRoot = modRoot,
        ReviewLanguageFolderName = "Korean",
        RmkLanguageFolderName = "Korean",
        SourceLanguage = "English",
        WorkbookPath = workbookPath,
        ApplyStatus = ReviewApplyStatus.ApprovedOnly,
        Overwrite = true,
        DryRun = dryRun,
        ExpectedPlanFingerprint = expectedPlanFingerprint
    };

    private static string PlanTargetPath(
        TranslationRunResult run,
        ReviewComparisonRow row,
        string outputLanguageRoot)
    {
        var reviewLanguageRoot = Path.Combine(run.ReviewRoot!, "Languages", "Korean");
        var relativeTarget = Path.GetRelativePath(reviewLanguageRoot, row.Target);
        Assert(!Path.IsPathRooted(relativeTarget)
               && !relativeTarget.Equals("..", StringComparison.Ordinal)
               && !relativeTarget.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "The synthetic plan target escaped its review language root.");
        return Path.Combine(outputLanguageRoot, relativeTarget);
    }

    private static void AssertPlanFingerprint(string value, string scenario)
    {
        Assert(value.Length == 64 && value.All(character =>
                character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F'),
            $"{scenario} did not return a 64-character hexadecimal plan fingerprint.");
    }

    private static void AssertPlanMismatch(Action action, string scenario)
    {
        var exception = CaptureException<InvalidOperationException>(action);
        Assert(exception.Message.Contains("preview", StringComparison.OrdinalIgnoreCase)
               && exception.Message.Contains("changed", StringComparison.OrdinalIgnoreCase),
            $"{scenario} did not fail specifically as an expired plan preview.");
    }
}
