using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void DiscoverySteamAndLoadFolders()
    {
        WithTempRoot(root =>
        {
            var primary = Path.Combine(root, "Steam");
            var library = Path.Combine(root, "OtherLibrary");
            Directory.CreateDirectory(Path.Combine(primary, "steamapps"));
            Directory.CreateDirectory(library);
            File.WriteAllText(Path.Combine(primary, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\"\n{{\n  \"1\" {{ \"path\" \"{library.Replace("\\", "\\\\")}\" }}\n}}\n");
            var mod = Path.Combine(library, "steamapps", "workshop", "content", "294100", "1234567890");
            var versionRoot = Path.Combine(mod, "1.6");
            Directory.CreateDirectory(Path.Combine(mod, "About"));
            Directory.CreateDirectory(Path.Combine(versionRoot, "Defs"));
            Directory.CreateDirectory(Path.Combine(versionRoot, "Languages", "English", "Keyed"));
            Directory.CreateDirectory(Path.Combine(versionRoot, "Languages", "ChineseSimplified", "Keyed"));
            File.WriteAllText(Path.Combine(mod, "About", "About.xml"),
                "<ModMetaData><name>Fixture Workshop Mod</name><packageId>fixture.workshop</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(mod, "LoadFolders.xml"),
                "<loadFolders><v1.5><li>1.5</li></v1.5><v1.6><li>1.6</li></v1.6></loadFolders>");
            File.WriteAllText(Path.Combine(versionRoot, "Languages", "English", "Keyed", "A.xml"), "<LanguageData><A>Hello</A></LanguageData>");
            File.WriteAllText(Path.Combine(versionRoot, "Languages", "ChineseSimplified", "Keyed", "A.xml"), "<LanguageData><A>你好</A></LanguageData>");

            var service = new RimWorldModDiscoveryService();
            var roots = service.GetSteamLibraryRoots([primary]);
            Assert(roots.Contains(Path.GetFullPath(library), StringComparer.OrdinalIgnoreCase), "Steam VDF library was not discovered.");
            var found = service.Discover([primary]);
            var info = found.Single();
            Assert(info.Name == "Fixture Workshop Mod" && info.PackageId == "fixture.workshop", "About.xml metadata was not loaded.");
            Assert(info.Path == Path.GetFullPath(versionRoot), "Highest usable LoadFolders.xml content root was not selected.");
            Assert(info.WorkshopId == "1234567890", "Workshop id was not parsed.");
            Assert(info.Display.EndsWith("[W:1234567890 / 1.6]", StringComparison.Ordinal),
                "The versioned Workshop display tag must use a readable UI separator instead of a path separator.");
            var languages = service.GetSourceLanguageOptions(info.Path);
            Assert(languages.Count == 2 && languages[0].Folder == "English", "Source language choices were not ranked or counted.");
        });
    }

    private static void ReviewSourceChangeInheritance()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var initial = CreateSourceOnlyReview(modRoot, "reviews-inheritance-initial");
            var initialRow = initial.Rows.Single(row => row.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(initial, [(initialRow, "번역 시작")]);
            var project = new TranslationProject
            {
                Id = "fixture-project",
                Name = "Fixture",
                ModRoot = modRoot,
                LatestReviewRoot = initial.ReviewRoot!,
                Runs = [new ProjectRun { ReviewRoot = initial.ReviewRoot!, CreatedAt = DateTimeOffset.Now.AddMinutes(-1).ToString("O") }]
            };

            var englishPath = Path.Combine(modRoot, "Languages", "English", "Keyed", "SampleKeys.xml");
            File.WriteAllText(englishPath,
                File.ReadAllText(englishPath).Replace("Translate now", "Translate immediately", StringComparison.Ordinal),
                new System.Text.UTF8Encoding(false));
            var updated = CreateSourceOnlyReview(modRoot, "reviews-inheritance-updated");
            var service = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
            var workspace = service.Load(updated.ReviewRoot!, project);
            var item = workspace.Items.Single(value => value.Row.Key == "CodexTranslator.SampleButton");
            Assert(item.Decision.Text == "번역 시작", "Previous translation was not inherited.");
            Assert(item.Decision.SourceChanged && item.EffectiveStatus == ReviewStatuses.Pending, "Updated source was not demoted to changed/pending.");
            Assert(item.Decision.PreviousSourceText == "Translate now", "Previous source snapshot was not preserved.");
            Assert(item.Row.Source == "Translate immediately", "Current source was not loaded.");
            var accessibleItemText = item.ToString();
            Assert(accessibleItemText.Contains("Translate immediately", StringComparison.Ordinal)
                   && accessibleItemText.Contains("변경됨", StringComparison.Ordinal)
                   && accessibleItemText.Contains("번역 시작", StringComparison.Ordinal),
                "Owner-drawn review item text is not available to accessibility clients.");
            Assert(workspace.ImportedDecisions >= 1 && workspace.ChangedSources >= 1, "Inheritance counters changed.");

            service.Save(workspace);
            var reloaded = service.Load(updated.ReviewRoot!, project);
            var reloadedItem = reloaded.Items.Single(value => value.Row.Key == "CodexTranslator.SampleButton");
            Assert(reloadedItem.Decision.Text == "번역 시작" && reloadedItem.Decision.SourceChanged, "Saved inherited state did not round-trip.");

            service.SetStatus(reloaded, reloadedItem, ReviewStatuses.Approved);
            service.Save(reloaded);
            Assert(!reloadedItem.Decision.SourceChanged
                   && reloadedItem.Decision.PreviousSourceText == "Translate now",
                "Approving the first source change lost its historical source.");
            project.LatestReviewRoot = updated.ReviewRoot!;
            project.Runs.Add(new ProjectRun
            {
                ReviewRoot = updated.ReviewRoot!,
                CreatedAt = DateTimeOffset.Now.ToString("O")
            });
            File.WriteAllText(englishPath,
                File.ReadAllText(englishPath).Replace("Translate immediately", "Translate finally", StringComparison.Ordinal),
                new System.Text.UTF8Encoding(false));
            var changedAgain = CreateSourceOnlyReview(modRoot, "reviews-inheritance-changed-again");
            var changedAgainWorkspace = service.Load(changedAgain.ReviewRoot!, project);
            var changedAgainItem = changedAgainWorkspace.Items.Single(value => value.Row.Key == "CodexTranslator.SampleButton");
            Assert(changedAgainItem.Decision.Text == "번역 시작"
                   && changedAgainItem.Decision.SourceChanged
                   && changedAgainItem.EffectiveStatus == ReviewStatuses.Pending
                   && changedAgainItem.Decision.PreviousSourceText == "Translate immediately"
                   && changedAgainItem.Row.Source == "Translate finally",
                "A second source change retained the older A source instead of the approved B source.");
        });
    }
}
