using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Review;

public sealed class ReviewSourceSnapshot
{
    private readonly IReadOnlyDictionary<string, string> sourceByIdentity;
    private readonly IReadOnlySet<string> ambiguousIdentities;
    private readonly IReadOnlyList<string> sourceFiles;

    private ReviewSourceSnapshot(
        IReadOnlyDictionary<string, string> sourceByIdentity,
        IReadOnlySet<string> ambiguousIdentities,
        IReadOnlyList<string> sourceFiles)
    {
        this.sourceByIdentity = sourceByIdentity;
        this.ambiguousIdentities = ambiguousIdentities;
        this.sourceFiles = sourceFiles;
    }

    public IReadOnlyList<string> SourceFiles => sourceFiles;

    public static ReviewSourceSnapshot Capture(
        SourceExtractor extractor,
        string modRoot,
        string sourceLanguageFolder,
        CancellationToken cancellationToken = default)
    {
        var extraction = extractor.Extract(modRoot, sourceLanguageFolder, cancellationToken: cancellationToken);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in extraction.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = entry.Identity;
            if (string.IsNullOrWhiteSpace(identity) || ambiguous.Contains(identity)) continue;
            if (!sources.TryAdd(identity, ReviewSafety.Normalize(entry.Text)))
            {
                sources.Remove(identity);
                ambiguous.Add(identity);
            }
        }
        var files = extraction.Entries
            .Select(entry => Path.GetFullPath(entry.SourceFile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ReviewSourceSnapshot(sources, ambiguous, files);
    }

    public bool Matches(ReviewComparisonRow row)
    {
        var localizationNamespace = row.Kind.Equals("Keyed", StringComparison.OrdinalIgnoreCase)
            ? "Keyed"
            : row.DefClass;
        var identity = SourceExtractor.GetLocalizationIdentity(localizationNamespace, row.Key);
        return !string.IsNullOrWhiteSpace(identity)
            && !ambiguousIdentities.Contains(identity)
            && sourceByIdentity.TryGetValue(identity, out var current)
            && current.Equals(ReviewSafety.Normalize(row.Source), StringComparison.Ordinal);
    }

    public bool HasSameEvidence(ReviewSourceSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return sourceByIdentity.Count == other.sourceByIdentity.Count
            && sourceByIdentity.All(pair =>
                other.sourceByIdentity.TryGetValue(pair.Key, out var value)
                && value.Equals(pair.Value, StringComparison.Ordinal))
            && ambiguousIdentities.SetEquals(other.ambiguousIdentities)
            && sourceFiles.SequenceEqual(other.sourceFiles, StringComparer.OrdinalIgnoreCase);
    }
}
