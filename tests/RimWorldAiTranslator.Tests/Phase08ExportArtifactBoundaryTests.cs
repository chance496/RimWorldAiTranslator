using System.IO.Compression;
using System.Text;
using RimWorldAiTranslator.Core.Diagnostics;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Quality;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void Phase08ExportArtifactBoundaries()
    {
        WithTempRoot(root =>
        {
            ExerciseQualityReportCommitBoundary(root);
            ExerciseDiagnosticBundleCommitBoundary(root);
        });
    }

    private static void ExerciseQualityReportCommitBoundary(string root)
    {
        const string sourceSecret = "PRIVATE-QUALITY-SOURCE";
        const string translationSecret = "PRIVATE-QUALITY-TRANSLATION";
        var entries = new[]
        {
            new QualityEntry(
                0,
                "Private.Quality.Key",
                Path.Combine(root, "private-quality-target.xml"),
                "ThingDef",
                sourceSecret,
                translationSecret,
                string.Empty,
                "translated",
                false,
                true)
        };
        var issues = QualityService.FindIssues(entries);

        foreach (var mode in new[] { "rewrite", "swap" })
        {
            var target = Path.Combine(root, $"quality-boundary-{mode}.html");
            var original = Encoding.UTF8.GetBytes($"original-quality-{mode}");
            var forged = Encoding.UTF8.GetBytes($"forged-quality-{mode}");
            File.WriteAllBytes(target, original);
            var rewriteBlocked = false;
            IOException? failure = null;
            try
            {
                PathSafety.AfterAtomicFinalEvidenceReleasedTestHook = prepared =>
                {
                    if (!Path.GetFileName(prepared).Contains(Path.GetFileName(target), StringComparison.OrdinalIgnoreCase))
                        return;
                    if (mode == "swap")
                    {
                        File.Delete(prepared);
                        File.WriteAllBytes(prepared, forged);
                        return;
                    }

                    try
                    {
                        File.WriteAllBytes(prepared, forged);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        rewriteBlocked = true;
                    }
                };

                if (mode == "swap")
                {
                    failure = CaptureException<IOException>(() =>
                        QualityService.ExportHtml(target, entries, issues));
                }
                else
                {
                    var result = QualityService.ExportHtml(target, entries, issues);
                    Assert(result.BackupPath is not null, "A quality-report replacement did not report its backup.");
                }
            }
            finally
            {
                PathSafety.AfterAtomicFinalEvidenceReleasedTestHook = null;
            }

            if (mode == "rewrite")
            {
                Assert(rewriteBlocked,
                    "A prepared quality report remained writable while its final replacement evidence was pinned.");
                Assert(File.ReadAllBytes(target + ".bak").SequenceEqual(original),
                    "A successful quality-report commit did not preserve the original report in its backup.");
                var html = File.ReadAllText(target, Encoding.UTF8);
                Assert(html.Contains("이 보고서는 집계 수치만 포함합니다.", StringComparison.Ordinal)
                       && !html.Contains(sourceSecret, StringComparison.Ordinal)
                       && !html.Contains(translationSecret, StringComparison.Ordinal)
                       && !html.Contains(root, StringComparison.OrdinalIgnoreCase),
                    "A committed quality report failed its aggregate-only privacy contract.");
                continue;
            }

            Assert(File.ReadAllBytes(target).SequenceEqual(original),
                "A substituted quality report displaced the original canonical report.");
            AssertRejectedWinner(root, target, forged, failure!, "quality report");
        }
    }

    private static void ExerciseDiagnosticBundleCommitBoundary(string root)
    {
        const string sourceSecret = "PRIVATE-DIAGNOSTIC-SOURCE";
        var apiSecret = "csk-" + "private-diagnostic-api-key";
        var paths = new AppDataPaths(Path.Combine(root, "diagnostic-appdata"));
        paths.EnsureExists();
        var product = Path.Combine(root, "diagnostic-product");
        Directory.CreateDirectory(product);
        var service = new DiagnosticBundleService();
        var settings = new AppSettingsDocument
        {
            ApiProviders = new Dictionary<string, ApiProviderSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["OpenAI"] = new()
                {
                    Url = "https://provider.invalid/v1",
                    Model = "synthetic-model"
                }
            }
        };

        foreach (var mode in new[] { "rewrite", "swap" })
        {
            var target = Path.Combine(root, $"diagnostic-boundary-{mode}.zip");
            var original = Encoding.UTF8.GetBytes($"original-diagnostic-{mode}");
            var forged = Encoding.UTF8.GetBytes($"forged-diagnostic-{mode}");
            File.WriteAllBytes(target, original);
            var options = new DiagnosticBundleOptions(
                target,
                paths,
                settings,
                new ProjectStoreDocument(),
                product,
                [$"Bearer {apiSecret}", sourceSecret, root],
                Force: true);
            var rewriteBlocked = false;
            IOException? failure = null;
            try
            {
                PathSafety.AfterAtomicFinalEvidenceReleasedTestHook = prepared =>
                {
                    if (!Path.GetFileName(prepared).Contains(Path.GetFileName(target), StringComparison.OrdinalIgnoreCase))
                        return;
                    if (mode == "swap")
                    {
                        File.Delete(prepared);
                        File.WriteAllBytes(prepared, forged);
                        return;
                    }

                    try
                    {
                        File.WriteAllBytes(prepared, forged);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        rewriteBlocked = true;
                    }
                };

                if (mode == "swap")
                {
                    failure = CaptureException<IOException>(() => service.Create(options));
                }
                else
                {
                    var result = service.Create(options);
                    Assert(result.Entries == 6 && result.Bytes == new FileInfo(target).Length,
                        "The validated diagnostic bundle reported inconsistent output evidence.");
                }
            }
            finally
            {
                PathSafety.AfterAtomicFinalEvidenceReleasedTestHook = null;
            }

            if (mode == "rewrite")
            {
                Assert(rewriteBlocked,
                    "A prepared diagnostic bundle remained writable while its final replacement evidence was pinned.");
                Assert(File.ReadAllBytes(target + ".bak").SequenceEqual(original),
                    "A successful diagnostic replacement did not preserve the original bundle in its backup.");
                using var archive = ZipFile.OpenRead(target);
                var all = string.Join('\n', archive.Entries.Select(entry =>
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    return reader.ReadToEnd();
                }));
                Assert(archive.Entries.Count == 6
                       && !all.Contains(sourceSecret, StringComparison.Ordinal)
                       && !all.Contains(apiSecret, StringComparison.Ordinal)
                       && !all.Contains(root, StringComparison.OrdinalIgnoreCase),
                    "A committed diagnostic bundle leaked private source, key, or path content.");
                continue;
            }

            Assert(File.ReadAllBytes(target).SequenceEqual(original),
                "A substituted diagnostic bundle displaced the original canonical bundle.");
            AssertRejectedWinner(root, target, forged, failure!, "diagnostic bundle");
        }
    }

    private static void AssertRejectedWinner(
        string root,
        string target,
        byte[] forged,
        IOException failure,
        string artifactName)
    {
        var rejected = Directory.EnumerateFiles(
                root,
                $".{Path.GetFileName(target)}.*.rejected.tmp",
                SearchOption.TopDirectoryOnly)
            .Single();
        Assert(File.ReadAllBytes(rejected).SequenceEqual(forged),
            $"The substituted {artifactName} winner was not preserved in quarantine.");
        Assert(failure is ConcurrentLeafChangeException concurrent
               && concurrent.PreservedPaths.Contains(rejected, StringComparer.OrdinalIgnoreCase),
            $"The substituted {artifactName} winner was not reported as preserved evidence.");
    }
}
