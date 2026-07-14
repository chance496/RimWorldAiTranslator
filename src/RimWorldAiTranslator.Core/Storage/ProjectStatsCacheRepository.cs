using System.Text.Json.Serialization;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

public sealed class ProjectStatsCacheDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("entries")]
    public List<ProjectStatsCacheEntry> Entries { get; set; } = [];
}

public sealed class ProjectStatsCacheEntry
{
    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("stamp")]
    public string Stamp { get; set; } = string.Empty;

    [JsonPropertyName("stats")]
    public ProjectStatsCacheValue Stats { get; set; } = new();
}

public sealed class ProjectStatsCacheValue
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("pending")]
    public int Pending { get; set; }

    [JsonPropertyName("translated")]
    public int Translated { get; set; }

    [JsonPropertyName("approved")]
    public int Approved { get; set; }

    [JsonPropertyName("updated")]
    public int Updated { get; set; }

    [JsonPropertyName("warnings")]
    public int Warnings { get; set; }

    public ReviewWorkspaceStats ToStats() => new(Total, Pending, Translated, Approved, Updated, Warnings);

    public static ProjectStatsCacheValue FromStats(ReviewWorkspaceStats stats) => new()
    {
        Total = stats.Total,
        Pending = stats.Pending,
        Translated = stats.Translated,
        Approved = stats.Approved,
        Updated = stats.Updated,
        Warnings = stats.Warnings
    };
}

public sealed class ProjectStatsCacheRepository
{
    private readonly AtomicJsonStore store;
    private readonly AppDataPaths paths;

    public ProjectStatsCacheRepository(AtomicJsonStore store, AppDataPaths paths)
    {
        this.store = store;
        this.paths = paths;
    }

    public ProjectStatsCacheDocument Load(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!store.Exists(paths.ProjectStats)) return new ProjectStatsCacheDocument();
        var document = store.Read<ProjectStatsCacheDocument>(
                           paths.ProjectStats,
                           cancellationToken: cancellationToken)
                       ?? new ProjectStatsCacheDocument();
        cancellationToken.ThrowIfCancellationRequested();
        if (document.Version != 1 || document.Entries is null) return new ProjectStatsCacheDocument();
        document.Entries = document.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ProjectId) && !string.IsNullOrWhiteSpace(entry.Stamp))
            .GroupBy(entry => entry.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        return document;
    }

    public void Save(ProjectStatsCacheDocument document)
    {
        document.Version = 1;
        document.Entries = document.Entries.OrderBy(entry => entry.ProjectId, StringComparer.OrdinalIgnoreCase).ToList();
        store.Write(paths.ProjectStats, document);
    }

    public static string CreateStamp(string reviewRoot)
    {
        if (string.IsNullOrWhiteSpace(reviewRoot)
            || PathSafety.IsNetworkPath(reviewRoot)
            || !Directory.Exists(reviewRoot))
        {
            return string.Empty;
        }
        string comparison;
        try { comparison = ReviewWorkspaceService.FindComparisonFile(reviewRoot); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Project statistics stamp failed closed ({exception.GetType().Name}).");
            return string.Empty;
        }
        return string.Join('|', Stamp(comparison), Stamp(ReviewRepository.GetDecisionPath(reviewRoot)));
    }

    private static string Stamp(string path)
    {
        if (!File.Exists(path)) return "missing";
        var file = new FileInfo(path);
        return $"{file.LastWriteTimeUtc.Ticks}:{file.Length}";
    }
}
