using System.IO.Compression;
using System.Text.Json;
using RimWorldAiTranslator.Core.Diagnostics;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void DiagnosticsPrivacy()
    {
        WithTempRoot(root =>
        {
            const string sourceSecret = "PRIVATE-SOURCE-CONTENT";
            const string keySecret = "Private.Localization.Key";
            var apiSecret = "csk-" + "diagnostic-secret-do-not-export";
            var paths = new AppDataPaths(Path.Combine(root, "appdata"));
            paths.EnsureExists();
            var review = Path.Combine(paths.Reviews, "run");
            Directory.CreateDirectory(review);
            new AtomicJsonStore().Write(ReviewRepository.GetDecisionPath(review), new ReviewDecisionDocument
            {
                Items =
                [
                    new ReviewDecision
                    {
                        Id = "one",
                        Key = keySecret,
                        Text = "비공개 번역",
                        SourceText = sourceSecret,
                        Status = ReviewStatuses.Translated,
                        TranslationOrigin = "local",
                        SourceChanged = true
                    }
                ]
            });
            var settings = new AppSettingsDocument
            {
                ApiProviderId = "Cerebras",
                RmkWorkspaceRoot = Path.Combine(root, "private-rmk"),
                RmkUseExisting = true,
                ApiProviders = new Dictionary<string, ApiProviderSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Cerebras"] = new()
                    {
                        Url = "https://api.cerebras.ai/v1/chat/completions",
                        Model = "gemma-4-31b",
                        Temperature = 0.1
                    }
                }
            };
            var projects = new ProjectStoreDocument
            {
                Projects =
                [
                    new TranslationProject
                    {
                        Id = "project",
                        Name = "Private Mod Name",
                        ModRoot = Path.Combine(root, "private-mod"),
                        SourceLanguageFolder = "English",
                        LatestReviewRoot = review
                    }
                ]
            };
            var product = Path.Combine(root, "product");
            Directory.CreateDirectory(product);
            File.WriteAllBytes(Path.Combine(product, "RimWorldAiTranslator.exe"), [1, 2, 3, 4]);
            var output = Path.Combine(root, "diagnostics.zip");
            var service = new DiagnosticBundleService();
            var result = service.Create(new DiagnosticBundleOptions(
                output, paths, settings, projects, product,
                [$"Bearer {apiSecret}", "HTTP 429 rate limit", "malformed JSON response"]));
            Assert(result.Entries == 6 && File.Exists(output), "Diagnostic bundle was not created.");
            using (var archive = ZipFile.OpenRead(output))
            {
                Assert(archive.Entries.Count == 6, "Diagnostic entry count changed.");
                var all = string.Join('\n', archive.Entries.Select(entry =>
                {
                    using var reader = new StreamReader(entry.Open());
                    return reader.ReadToEnd();
                }));
                foreach (var privateValue in new[] { sourceSecret, keySecret, apiSecret, root, "Private Mod Name", "비공개 번역" })
                    Assert(!all.Contains(privateValue, StringComparison.OrdinalIgnoreCase), "Diagnostic bundle leaked private content: " + privateValue);
                using var errorsEntry = archive.GetEntry("errors-summary.json")!.Open();
                using var errors = JsonDocument.Parse(errorsEntry);
                Assert(errors.RootElement.GetProperty("rateLimit").GetInt32() == 1, "Rate-limit classification changed.");
                Assert(errors.RootElement.GetProperty("json").GetInt32() == 1, "JSON classification changed.");
            }
            service.Create(new DiagnosticBundleOptions(output, paths, settings, projects, product, [], Force: true));
            Assert(File.Exists(output + ".bak"), "Forced diagnostic replacement did not preserve a backup.");
        });
    }

    private static void ProjectStatsCacheInvalidation()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(root);
            paths.EnsureExists();
            var review = Path.Combine(paths.Reviews, "run");
            var audit = Path.Combine(review, "_TranslationAudit");
            Directory.CreateDirectory(audit);
            File.WriteAllText(Path.Combine(audit, "fixture-comparison.json"), "[]");
            var decisionPath = ReviewRepository.GetDecisionPath(review);
            new AtomicJsonStore().Write(
                ReviewRepository.GetDecisionPath(review),
                new ReviewDecisionDocument());
            var firstStamp = ProjectStatsCacheRepository.CreateStamp(review);
            var repository = new ProjectStatsCacheRepository(new AtomicJsonStore(), paths);
            repository.Save(new ProjectStatsCacheDocument
            {
                Entries =
                [
                    new ProjectStatsCacheEntry
                    {
                        ProjectId = "fixture",
                        Stamp = firstStamp,
                        Stats = ProjectStatsCacheValue.FromStats(new ReviewWorkspaceStats(10, 2, 3, 4, 1, 0))
                    }
                ]
            });
            var loaded = repository.Load();
            Assert(loaded.Entries.Single().Stats.ToStats().Approved == 4, "Project statistics cache did not round-trip.");
            File.AppendAllText(decisionPath, " ");
            var secondStamp = ProjectStatsCacheRepository.CreateStamp(review);
            Assert(!firstStamp.Equals(secondStamp, StringComparison.Ordinal), "Decision changes did not invalidate the project statistics stamp.");
        });
    }

    private static void RmkAutoDiscovery()
    {
        WithTempRoot(root =>
        {
            var steam = Path.Combine(root, "SteamLibrary");
            var subscription = Path.Combine(steam, "steamapps", "workshop", "content", "294100", RmkWorkspaceService.WorkshopId);
            Directory.CreateDirectory(Path.Combine(subscription, "Data"));
            File.WriteAllText(Path.Combine(subscription, "ModList.tsv"), "");
            var localMods = Path.Combine(steam, "steamapps", "common", "RimWorld", "Mods");
            var workspace = Path.Combine(localMods, "RMK");
            Directory.CreateDirectory(Path.Combine(workspace, "Data"));
            Directory.CreateDirectory(Path.Combine(workspace, ".git"));
            File.WriteAllText(Path.Combine(workspace, ".git", "HEAD"), "ref: refs/heads/bus\n", new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(workspace, "ModList.tsv"), "");
            var service = new RmkWorkspaceService();
            Assert(service.FindSubscriptionRoot([steam]).Equals(Path.GetFullPath(subscription), StringComparison.OrdinalIgnoreCase), "RMK subscription was not found.");
            Assert(service.FindWritableWorkspace([localMods]).Equals(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase), "RMK writable workspace was not found.");
        });
    }
}
