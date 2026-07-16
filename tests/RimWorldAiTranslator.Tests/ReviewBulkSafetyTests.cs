using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void Phase10SafeDuplicateSourceBulk()
    {
        WithTempRoot(root =>
        {
            var reviewRoot = Path.Combine(root, "review");
            const string sharedSource = "Launch {0}";
            var source = Row(reviewRoot, "E-source", "fixture.source", sharedSource);
            var safeTarget = Row(reviewRoot, "E-safe", "fixture.safe", sharedSource);
            var unsafeTarget = Row(reviewRoot, "E-unsafe", "Fixture.defName", sharedSource);
            unsafeTarget.Kind = "DefInjected";
            unsafeTarget.DefClass = "ThingDef";
            unsafeTarget.Node = "Fixture.defName";
            unsafeTarget.Field = "defName";
            unsafeTarget.Target = Path.Combine(
                reviewRoot,
                "Languages",
                "Korean",
                "DefInjected",
                "ThingDef",
                "Fixture.xml");
            var unrelated = Row(reviewRoot, "E-unrelated", "fixture.unrelated", "Different {0}");
            var service = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
            var sourceItem = NewItem(source, 0, string.Empty, ReviewStatuses.Pending);
            var safeItem = NewItem(safeTarget, 1, string.Empty, ReviewStatuses.Pending);
            var unsafeItem = NewItem(unsafeTarget, 2, "보존 {0}", ReviewStatuses.Approved);
            unsafeItem.Decision.SourceChanged = true;
            unsafeItem.Decision.PreviousSourceText = "Previous unsafe source";
            var unrelatedItem = NewItem(unrelated, 3, "다른 번역 {0}", ReviewStatuses.Approved);
            var workspace = new ReviewWorkspace(
                reviewRoot,
                Path.Combine(reviewRoot, "_TranslationAudit", "synthetic-comparison.json"),
                [sourceItem, safeItem, unsafeItem, unrelatedItem],
                extensionData: null,
                quarantinedItems: [],
                new ReviewComparisonEvidence("synthetic-comparison.json", "synthetic"));

            service.UpdateTranslation(workspace, sourceItem, sharedSource, string.Empty, editedByUser: true);
            var rejected = service.ApplyToDuplicateSources(workspace, sourceItem);
            Assert(rejected == 0
                   && string.IsNullOrEmpty(safeItem.Decision.Text)
                   && unsafeItem.Decision.Text == "보존 {0}"
                   && unrelatedItem.Decision.Text == "다른 번역 {0}",
                "An unsafe source translation was propagated by the identical-source bulk action.");

            service.UpdateTranslation(workspace, sourceItem, "실행 {0}", string.Empty, editedByUser: true);

            var changed = service.ApplyToDuplicateSources(workspace, sourceItem);
            Assert(changed == 1
                   && safeItem.Decision.Text == "실행 {0}"
                   && safeItem.EffectiveStatus == ReviewStatuses.Translated
                   && safeItem.Decision.TranslationOrigin == "local",
                "The safe identical-source target was not updated exactly once.");
            Assert(unsafeItem.Decision.Text == "보존 {0}"
                   && unsafeItem.Decision.Status == ReviewStatuses.Approved
                   && unsafeItem.Decision.SourceChanged
                   && unsafeItem.Decision.PreviousSourceText == "Previous unsafe source",
                "The identical-source bulk action changed a structurally forbidden target.");
            Assert(unrelatedItem.Decision.Text == "다른 번역 {0}"
                   && unrelatedItem.Decision.Status == ReviewStatuses.Approved,
                "The identical-source bulk action changed an unrelated row.");
            Assert(workspace.Dirty && workspace.Revision > 0,
                "The accepted safe bulk update did not mark the workspace for durable save.");
        });
    }

    private static ReviewItem NewItem(
        ReviewComparisonRow row,
        int index,
        string text,
        string status)
    {
        var languageRoot = Path.Combine(Path.GetDirectoryName(row.Target)!, "..");
        var decision = new ReviewDecision
        {
            Id = row.Id,
            Key = row.Key,
            Target = Path.GetRelativePath(Path.GetFullPath(languageRoot), row.Target),
            DefClass = row.DefClass,
            Node = row.Node,
            Status = status,
            Text = text,
            SourceText = row.Source
        };
        return new ReviewItem(row, decision, decision.Target, index);
    }
}
