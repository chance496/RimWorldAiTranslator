namespace RimWorldAiTranslator.Core.Review;

public sealed record TranslationMemoryEntry(
    string Identity,
    string Source,
    string Translation,
    string Origin,
    string Status,
    string Target,
    string UpdatedAt,
    bool SourceChanged,
    bool SafeToApply);

public sealed record TranslationMemorySuggestion(
    string Text,
    string Origin,
    string Status,
    string Target,
    string Identity,
    string UpdatedAt,
    int Rank,
    long UpdatedTicks);

public static class TranslationMemoryService
{
    public static IReadOnlyList<TranslationMemorySuggestion> Select(
        IEnumerable<TranslationMemoryEntry> entries,
        string source,
        string excludeIdentity = "",
        int maximum = 5)
    {
        var sourceKey = Normalize(source);
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return [];
        }

        return entries
            .Where(entry => Normalize(entry.Source) == sourceKey)
            .Where(entry => string.IsNullOrEmpty(excludeIdentity) || entry.Identity != excludeIdentity)
            .Where(entry => !entry.SourceChanged && entry.SafeToApply)
            .Where(entry => entry.Status.Equals("approved", StringComparison.OrdinalIgnoreCase)
                || entry.Status.Equals("translated", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new TranslationMemorySuggestion(
                Normalize(entry.Translation),
                entry.Origin,
                entry.Status.ToLowerInvariant(),
                entry.Target,
                entry.Identity,
                entry.UpdatedAt,
                Rank(entry.Status, entry.Origin),
                DateTimeOffset.TryParse(entry.UpdatedAt, out var parsed) ? parsed.UtcTicks : 0))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Text) && entry.Text != sourceKey)
            .OrderByDescending(entry => entry.Rank)
            .ThenByDescending(entry => entry.UpdatedTicks)
            .ThenBy(entry => entry.Identity, StringComparer.Ordinal)
            .DistinctBy(entry => entry.Text, StringComparer.Ordinal)
            .Take(Math.Clamp(maximum, 1, 20))
            .ToArray();
    }

    public static string Normalize(string? source) => (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static int Rank(string status, string origin)
    {
        var statusRank = status.ToLowerInvariant() switch { "approved" => 400, "translated" => 200, _ => 0 };
        var originRank = origin.ToLowerInvariant() switch
        {
            "local" => 90,
            "rmk" => 80,
            "existing" => 70,
            "mod" => 60,
            "ai" => 40,
            _ => 10
        };
        return statusRank + originRank;
    }
}
