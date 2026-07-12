using RimWorldAiTranslator.Core.Logging;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void SecurityLogRedaction()
    {
        WithTempRoot(root =>
        {
            using (var logger = new AppLogger(root))
            {
                logger.Error("Authorization=secret-value Bearer abcdefgh csk-1234567890 sk-abcdefghijk");
            }
            var text = File.ReadAllText(Directory.EnumerateFiles(root, "*.log").Single());
            Assert(!text.Contains("secret-value") && !text.Contains("csk-1234567890") && !text.Contains("sk-abcdefghijk"), "Logger retained a credential.");
            Assert(text.Contains("[REDACTED]"), "Logger did not mark redacted data.");
        });
    }

    private static void TranslationDirectOutputRollback()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var defPath = Path.Combine(modRoot, "Languages", "Korean", "DefInjected", "ThingDef", "CodexAI.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(defPath)!);
            File.WriteAllText(defPath,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<LanguageData>\n  <Existing.Rollback>BEFORE</Existing.Rollback>\n  <Codex_TestWorkbench.label>OLD_LABEL</Codex_TestWorkbench.label>\n</LanguageData>\n",
                new System.Text.UTF8Encoding(false));
            var before = File.ReadAllBytes(defPath);
            var blocked = Path.Combine(modRoot, "Languages", "Korean", "Keyed", "SampleKeys.xml");
            Directory.CreateDirectory(blocked);
            var handler = new FakeOpenAiHandler(0);
            var engine = new TranslationEngine(RepositoryRoot(), CreateExtractor(), apiClientFactory: keys => new TranslationApiClient(keys, handler));
            var options = withReviewOnlyFalse();
            var error = CaptureException<IOException>(() => engine.RunAsync(options).GetAwaiter().GetResult());
            Assert(error.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase), "Direct output failure did not report rollback.");
            Assert(File.ReadAllBytes(defPath).SequenceEqual(before), "Direct output rollback did not restore the first XML file.");
            Assert(Directory.Exists(blocked), "Direct output rollback modified the blocking directory.");
            Assert(!Directory.EnumerateFiles(Path.Combine(modRoot, "Languages", "Korean"), "*.transaction.bak", SearchOption.AllDirectories).Any(), "Direct output left transaction snapshots.");
            var progressPath = Directory.EnumerateFiles(Path.Combine(modRoot, "_TranslationAudit"), "*-progress.json").Single();
            using var progress = System.Text.Json.JsonDocument.Parse(File.ReadAllText(progressPath));
            Assert(!progress.RootElement[0].GetProperty("complete").GetBoolean(), "Failed direct output was marked complete.");

            TranslationEngineOptions withReviewOnlyFalse() => new()
            {
                ModRoot = modRoot,
                ApiKeys = ["test-key"],
                SourceLanguageFolder = "English",
                ReviewOnly = false,
                BatchSize = 40,
                MaxInputCharactersPerBatch = 100_000,
                MaxInputTokensPerBatch = 100_000,
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                MaxRetries = 1,
                Timeout = TimeSpan.FromSeconds(10),
                AllowInsecureLoopback = true,
                Overwrite = true,
                Provider = ApiProviderCatalog.Get("Custom"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Loopback Fixture",
                    Url = "http://127.0.0.1:12345/v1/chat/completions",
                    Model = "fixture-model",
                    Temperature = 0.1
                }
            };
        });
    }

    private static void RmkWorkspaceIndex()
    {
        WithTempRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "Data", "Category", "Fixture Mod - 123", "1.6", "Languages", "Korean (한국어)"));
            File.WriteAllText(Path.Combine(root, "ModList.tsv"), "123\tFixture Mod\tData/Category\tfixture.package\n");
            var yamlPath = Path.Combine(root, "Data", "Category", "Fixture Mod - 123", "1.6", "LoadFolders.Build.yaml");
            File.WriteAllText(yamlPath,
                "BuildRule:\n  Binding:\n    PackageID: [\"fixture.package\"]\n  Version:\n    Default: \"1.6\"\nMetadata:\n  WorkshopID: \"123\"\n  ModName: \"Fixture Mod\"\n");
            var unrelated = Path.Combine(root, "Data", "Unrelated - 999", "1.6");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "LoadFolders.Build.yaml"), "Metadata:\n  WorkshopID: \"999\"\n");
            var service = new RmkWorkspaceService();
            var project = new TranslationProject { Name = "Fixture Mod", WorkshopId = "123", PackageId = "fixture.package" };
            var targets = service.FindTargets(root, project);
            Assert(targets.Count == 1 && targets[0].Version == "1.6", "RMK indexed target was not resolved.");
            Assert(targets[0].LanguageRoot.EndsWith(Path.Combine("Languages", "Korean (한국어)"), StringComparison.Ordinal), "RMK Korean language root changed.");
            var missing = service.FindTargets(root, new TranslationProject { WorkshopId = "777", PackageId = "missing.package" });
            Assert(missing.Count == 0, "RMK searched unrelated Data entries despite a usable index miss.");

            var created = service.CreateTarget(root, new TranslationProject
            {
                Name = "New Fixture",
                WorkshopId = "777",
                PackageId = "new.fixture"
            }, "1.6");
            Assert(File.Exists(created.YamlPath) && Directory.Exists(created.LanguageRoot), "RMK target creation did not create its YAML and Korean folder.");
            Assert(created.Root.StartsWith(Path.Combine(root, "Data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "RMK target escaped the Data root.");
        });
    }
}
