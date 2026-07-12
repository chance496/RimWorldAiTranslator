using System.Text.Json;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Projects;

public sealed record ProjectCleanupPlan(
    IReadOnlyList<string> SafePaths,
    IReadOnlyList<string> UnsafePaths,
    IReadOnlyList<string> MarkerErrors);

public sealed class ProjectCleanupService
{
    public ProjectCleanupPlan BuildPlan(TranslationProject project, IEnumerable<string> reviewRoots)
    {
        var roots = reviewRoots.Where(value => !string.IsNullOrWhiteSpace(value)).Select(PathSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var safe = new List<string>();
        var unsafePaths = new List<string>();
        var markerErrors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recorded = project.Runs.Select(run => run.ReviewRoot).Prepend(project.LatestReviewRoot);

        foreach (var path in recorded.Where(Directory.Exists))
        {
            string full;
            try
            {
                full = PathSafety.Normalize(path);
            }
            catch
            {
                unsafePaths.Add(path);
                continue;
            }

            if (!seen.Add(full))
            {
                continue;
            }

            var owned = GetAppOwnedReviewDirectory(full, roots, project.ModRoot);
            if (owned is null)
            {
                unsafePaths.Add(full);
            }
            else
            {
                safe.Add(owned);
            }
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var markerPath = Path.Combine(directory, ".rimworld-ai-project.json");
                if (!File.Exists(markerPath))
                {
                    continue;
                }

                try
                {
                    using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
                    if (!marker.RootElement.TryGetProperty("projectId", out var id)
                        || !string.Equals(id.GetString(), project.Id, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var owned = GetAppOwnedReviewDirectory(directory, roots, project.ModRoot);
                    if (owned is not null && seen.Add(owned))
                    {
                        safe.Add(owned);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    markerErrors.Add($"{directory} : {ex.GetType().Name}");
                }
            }
        }

        return new ProjectCleanupPlan(safe, unsafePaths, markerErrors);
    }

    public IReadOnlyList<string> Remove(TranslationProject project, IEnumerable<string> reviewRoots, IEnumerable<string> paths)
    {
        var roots = reviewRoots.Where(value => !string.IsNullOrWhiteSpace(value)).Select(PathSafety.Normalize).ToArray();
        var failures = new List<string>();
        foreach (var path in paths)
        {
            var verified = GetAppOwnedReviewDirectory(path, roots, project.ModRoot);
            if (verified is null)
            {
                failures.Add($"Safety boundary check failed: {path}");
                continue;
            }

            try
            {
                Directory.Delete(verified, true);
            }
            catch (Exception ex)
            {
                failures.Add($"{verified} : {ex.Message}");
            }
        }

        return failures;
    }

    public static void WriteOwnershipMarker(TranslationProject project, string reviewRoot)
    {
        Directory.CreateDirectory(reviewRoot);
        var marker = JsonSerializer.Serialize(new
        {
            version = 1,
            projectId = project.Id,
            modRoot = project.ModRoot,
            createdAt = DateTimeOffset.Now.ToString("O")
        });
        File.WriteAllText(Path.Combine(reviewRoot, ".rimworld-ai-project.json"), marker, new System.Text.UTF8Encoding(false));
    }

    private static string? GetAppOwnedReviewDirectory(string path, IEnumerable<string> reviewRoots, string modRoot)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        string full;
        try
        {
            full = PathSafety.Normalize(path);
        }
        catch
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(modRoot))
        {
            var modFull = PathSafety.Normalize(modRoot);
            if (full.Equals(modFull, StringComparison.OrdinalIgnoreCase) || PathSafety.IsStrictlyInside(full, modFull))
            {
                return null;
            }
        }

        foreach (var root in reviewRoots)
        {
            if (!PathSafety.IsStrictlyInside(full, root))
            {
                continue;
            }

            return PathSafety.ContainsReparsePoint(full, root) ? null : full;
        }

        return null;
    }
}
