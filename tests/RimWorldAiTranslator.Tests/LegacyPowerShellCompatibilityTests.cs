using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void LegacyPowerShellProjectCompatibility()
    {
        WithTempRoot(root =>
        {
            var fixtureRoot = Path.Combine(root, "legacy-project");
            CopyDirectory(
                Path.Combine(RepositoryRoot(), "testdata", "LegacyPowerShellProject"),
                fixtureRoot);

            var appDataRoot = Path.Combine(fixtureRoot, "appdata");
            var paths = new AppDataPaths(appDataRoot);
            paths.EnsureExists();
            var modRoot = Path.Combine(fixtureRoot, "mod");
            var reviewRoot = Path.Combine(paths.Reviews, "legacy-review");
            var comparisonPath = Path.Combine(reviewRoot, "_TranslationAudit", "legacy-comparison.json");
            var decisionPath = ReviewRepository.GetDecisionPath(reviewRoot);
            var targetPath = Path.Combine(reviewRoot, "Languages", "Korean", "Keyed", "Legacy.xml");
            var rmkRoot = Path.Combine(fixtureRoot, "rmk");
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            Directory.CreateDirectory(rmkRoot);

            ReplaceLegacyFixtureTokens(
                fixtureRoot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["__MOD_ROOT__"] = modRoot,
                    ["__REVIEW_ROOT__"] = reviewRoot,
                    ["__COMPARISON__"] = comparisonPath,
                    ["__TARGET__"] = targetPath,
                    ["__RMK_ROOT__"] = rmkRoot
                });

            var workbook = Path.Combine(rmkRoot, "legacy-history.xlsx");
            CreateLegacyPowerShellWorkbook(workbook);
            var guardedFiles = Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, Sha256File, StringComparer.OrdinalIgnoreCase);
            var originalProjects = File.ReadAllBytes(paths.Projects);
            var originalDecisions = File.ReadAllBytes(decisionPath);

            var store = new AtomicJsonStore();
            var projectRepository = new ProjectRepository(store, paths);
            var projectService = new ProjectService(
                projectRepository,
                new RimWorldModDiscoveryService(),
                new ProjectCleanupService());
            var projects = projectService.Load();
            var project = projects.Projects.Single();
            Assert(projects.Version == 2
                   && project.SourceKind == "Folder"
                   && project.SourceLanguageFolder == "English"
                   && project.ExtensionData?.ContainsKey("legacyProjectMetadata") == true,
                "The PowerShell v2 project store was not loaded with legacy defaults and unknown fields intact.");

            using (var settingsRepository = new SettingsRepository(store, paths))
            {
                var settings = settingsRepository.Load();
                Assert(settings.Version == 3
                       && settings.CustomGlossaryPath == string.Empty
                       && settings.ApiProviders.ContainsKey("Custom")
                       && settings.ExtensionData?.ContainsKey("legacySettingsMetadata") == true,
                    "The PowerShell v3 settings store was not loaded with missing-field defaults and unknown fields intact.");
            }

            var workbookData = RimWorldTranslatorRmkXlsxReader.Read(workbook);
            Assert(workbookData.Rows.Count == 3
                   && workbookData.Map.Count == 2
                   && workbookData.Map["Keyed+Legacy.Message"].Translation == "안녕하세요 {0}",
                "A passive showFormulas setting, conditional-formatting formula, or stable PowerShell duplicate history row blocked the legacy RMK workbook.");

            var reviewService = new ReviewWorkspaceService(store, CreateExtractor(), paths.Reviews);
            var workspace = reviewService.Load(reviewRoot, project);
            Assert(workspace.MigrationPending
                   && !workspace.Dirty
                   && workspace.Items.Count == 1
                   && workspace.Items[0].Decision.Text == "안녕하세요 {0}"
                   && workspace.Items[0].Decision.Note == "PowerShell fixture note",
                "The PowerShell v5 review store did not open read-only with its translation and note intact.");

            var markerlessRoot = Path.Combine(root, "markerless-legacy-review");
            CopyDirectory(reviewRoot, markerlessRoot);
            File.Delete(Path.Combine(markerlessRoot, ".rimworld-ai-project.json"));
            ReplaceJsonPath(markerlessRoot, reviewRoot, markerlessRoot);
            var markerless = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor()).Load(markerlessRoot, project);
            Assert(markerless.Items.Count == 1 && markerless.MigrationPending,
                "A valid legacy review was blocked only because its ownership marker was missing.");

            var corruptReviewRoot = Path.Combine(root, "corrupt-review");
            CopyDirectory(reviewRoot, corruptReviewRoot);
            ReplaceJsonPath(corruptReviewRoot, reviewRoot, corruptReviewRoot);
            var corruptDecisionPath = ReviewRepository.GetDecisionPath(corruptReviewRoot);
            File.WriteAllText(corruptDecisionPath, "{\"version\":5,\"items\":[", new UTF8Encoding(false));
            var corruptDecisionHash = Sha256File(corruptDecisionPath);
            AssertThrows<InvalidDataException>(() =>
                new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor()).Load(corruptReviewRoot, project));
            Assert(Sha256File(corruptDecisionPath) == corruptDecisionHash,
                "A rejected corrupt review store was modified.");

            var corruptWorkbook = Path.Combine(root, "corrupt-history.xlsx");
            File.WriteAllBytes(corruptWorkbook, [0x50, 0x4B, 0x03, 0x04, 0x00]);
            var corruptWorkbookHash = Sha256File(corruptWorkbook);
            AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxReader.Read(corruptWorkbook));
            Assert(Sha256File(corruptWorkbook) == corruptWorkbookHash,
                "A rejected corrupt RMK workbook was modified.");

            var analysis = new TranslationEngine(RepositoryRoot(), CreateExtractor()).RunAsync(
                new TranslationEngineOptions
                {
                    ModRoot = modRoot,
                    SourceOnly = true,
                    ReviewOnly = true,
                    ReviewRoot = Path.Combine(root, "analysis-runs"),
                    SourceLanguageFolder = "English",
                    ReferenceSourceWorkbook = workbook
                }).GetAwaiter().GetResult();
            Assert(analysis.SourceEntries == 1
                   && analysis.ReviewEntries == 1
                   && analysis.Rows.Single().Key == "Legacy.Message",
                "Source analysis did not complete against the readable PowerShell project and RMK history.");

            var changedInputs = guardedFiles
                .Where(pair => !File.Exists(pair.Key) || Sha256File(pair.Key) != pair.Value)
                .Select(pair => Path.GetRelativePath(fixtureRoot, pair.Key))
                .ToArray();
            Assert(changedInputs.Length == 0,
                "Opening the PowerShell project, settings, review, or RMK history changed an original input file. "
                + $"Changed=[{string.Join(",", changedInputs)}]");

            reviewService.Save(workspace);
            Assert(File.ReadAllBytes(decisionPath + ".bak").SequenceEqual(originalDecisions),
                "Legacy review migration did not preserve an exact backup of the PowerShell decision store.");
            var migrated = new ReviewRepository(store).Load(reviewRoot, paths.Reviews);
            Assert(migrated.Version == ReviewDecisionDocument.CurrentVersion
                   && migrated.Items.Single().Text == "안녕하세요 {0}"
                   && migrated.Items.Single().Note == "PowerShell fixture note"
                   && migrated.Items.Single().ExtensionData?.ContainsKey("legacyDecisionMetadata") == true
                   && migrated.ExtensionData?.ContainsKey("legacyReviewMetadata") == true,
                "Legacy review migration lost user data or unknown extension fields.");
            var reopened = reviewService.Load(reviewRoot, project);
            Assert(!reopened.MigrationPending && reopened.Items.Single().Decision.Text == "안녕하세요 {0}",
                "The migrated review store did not reopen cleanly on restart. "
                + $"MigrationPending={reopened.MigrationPending}, Dirty={reopened.Dirty}, "
                + $"Imported={reopened.ImportedDecisions}, Changed={reopened.ChangedSources}, "
                + $"Status={reopened.Items.Single().Decision.Status}");

            var plan = projectService.GetRemovalPlan(project, [paths.Reviews]);
            Assert(plan.SafePaths.SequenceEqual([Path.GetFullPath(reviewRoot)])
                   && plan.MarkerErrors.Count == 0,
                "The PowerShell v1 ownership marker was not accepted within the pinned review boundary.");
            var removal = projectService.Remove(projects, project, plan);
            Assert(removal.ProjectRecordRemoved
                   && removal.CleanupFailures.Count == 0
                   && !Directory.Exists(reviewRoot)
                   && projects.Projects.Count == 0,
                "The copied PowerShell project could not be deleted safely.");
            Assert(File.ReadAllBytes(paths.Projects + ".bak").SequenceEqual(originalProjects)
                   && File.Exists(workbook)
                   && File.Exists(Path.Combine(modRoot, "Languages", "English", "Keyed", "Legacy.xml")),
                "Project deletion did not preserve its rollback copy or changed source/RMK inputs.");
        });
    }

    private static void CreateLegacyPowerShellWorkbook(string workbook)
    {
        RimWorldTranslatorRmkXlsxWriter.Write(
            workbook,
            [
                new RimWorldTranslatorRmkHistoryRow
                {
                    Identifier = "Keyed+Legacy.Message",
                    ClassName = "Keyed",
                    Key = "Legacy.Message",
                    Source = "Hello {0}",
                    Translation = "안녕하세요 {0}"
                },
                new RimWorldTranslatorRmkHistoryRow
                {
                    Identifier = "Keyed+Legacy.Other",
                    ClassName = "Keyed",
                    Key = "Legacy.Other",
                    Source = "Other",
                    Translation = "기타"
                }
            ],
            "English");
        RewriteWorksheet(workbook, document =>
        {
            var sheetView = document.Descendants().Single(node => node.Name.LocalName == "sheetView");
            sheetView.SetAttributeValue("showFormulas", "false");
            var sheetData = document.Descendants().Single(node => node.Name.LocalName == "sheetData");
            var duplicate = new XElement(sheetData.Elements().Single(node => (string?)node.Attribute("r") == "2"));
            duplicate.SetAttributeValue("r", "4");
            foreach (var cell in duplicate.Elements().Where(node => node.Name.LocalName == "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? string.Empty;
                cell.SetAttributeValue("r", reference.TrimEnd('2') + "4");
            }
            sheetData.Add(duplicate);
            var spreadsheet = sheetData.Name.Namespace;
            sheetData.AddAfterSelf(
                new XElement(
                    spreadsheet + "conditionalFormatting",
                    new XAttribute("sqref", "D2:D4"),
                    new XElement(
                        spreadsheet + "cfRule",
                        new XAttribute("type", "expression"),
                        new XAttribute("priority", "1"),
                        new XElement(spreadsheet + "formula", "LEN($D2)>0"))));
        });
    }

    private static void ReplaceLegacyFixtureTokens(
        string fixtureRoot,
        IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var path in Directory.EnumerateFiles(fixtureRoot, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            foreach (var pair in replacements)
                text = text.Replace(pair.Key, JsonStringContent(pair.Value), StringComparison.Ordinal);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }

    private static void ReplaceJsonPath(string root, string oldPath, string newPath)
    {
        var oldValue = JsonStringContent(oldPath);
        var newValue = JsonStringContent(newPath);
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path, Encoding.UTF8)
                .Replace(oldValue, newValue, StringComparison.Ordinal);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }

    private static string JsonStringContent(string value)
    {
        var serialized = JsonSerializer.Serialize(value);
        return serialized[1..^1];
    }
}
