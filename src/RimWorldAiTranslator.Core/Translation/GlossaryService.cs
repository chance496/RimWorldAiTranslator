using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Translation;

public sealed class GlossaryTerm
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("ko")]
    public string Korean { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 1000;

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = string.Empty;

    [JsonIgnore]
    public bool AlwaysInclude { get; set; }

    [JsonIgnore]
    public string Category { get; set; } = string.Empty;
}

public sealed class GlossaryDocument
{
    [JsonPropertyName("terms")]
    public List<GlossaryTerm> Terms { get; set; } = [];
}

public sealed class GlossaryService
{
    private readonly List<GlossaryTerm> always = [];
    private readonly List<IndexedTerm> generated = [];
    private readonly Dictionary<string, List<IndexedTerm>> prefixIndex = new(StringComparer.Ordinal);

    public IReadOnlyList<GlossaryTerm> Load(string generatedPath, string? curatedPath, bool useCurated)
    {
        var all = new List<GlossaryTerm>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var officialSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in Import(generatedPath, false, "official"))
        {
            if (seen.Add($"{term.Source}|{term.Korean}"))
            {
                all.Add(term);
                officialSources.Add(term.Source);
            }
        }

        if (useCurated && !string.IsNullOrWhiteSpace(curatedPath))
        {
            foreach (var term in Import(curatedPath, true, "curated"))
            {
                if (!officialSources.Contains(term.Source) && seen.Add($"{term.Source}|{term.Korean}"))
                {
                    all.Add(term);
                }
            }
        }

        BuildIndex(all);
        return all;
    }

    public IReadOnlyList<GlossaryTerm> Select(IReadOnlyList<SourceEntry> batch, int maxAlways, int maxGenerated)
    {
        var selected = new List<GlossaryTerm>();
        var selectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in always.Take(maxAlways))
        {
            if (selectedSources.Add(term.Source)) selected.Add(term);
        }

        if (batch.Count == 0 || maxGenerated <= 0)
        {
            return selected;
        }

        var text = string.Join('\n', batch.Select(entry => entry.Text)).ToLowerInvariant();
        var matchedOrders = new HashSet<int>();
        for (var index = 0; index <= text.Length - 3; index++)
        {
            var prefix = text.Substring(index, 3);
            if (!prefixIndex.TryGetValue(prefix, out var matches)) continue;
            foreach (var match in matches)
            {
                if (!selectedSources.Contains(match.Term.Source) && text.Contains(match.SearchSource, StringComparison.Ordinal))
                {
                    matchedOrders.Add(match.Order);
                }
            }
        }

        foreach (var term in generated
            .Where(term => matchedOrders.Contains(term.Order))
            .Select(term => term.Term)
            .OrderBy(term => term.Priority)
            .ThenByDescending(term => term.Count)
            .ThenByDescending(term => term.Source.Length)
            .Take(maxGenerated))
        {
            if (selectedSources.Add(term.Source)) selected.Add(term);
        }
        return selected;
    }

    public static string ToPrompt(IEnumerable<GlossaryTerm> terms) => string.Join('\n', terms.Select(term =>
        string.IsNullOrWhiteSpace(term.Note)
            ? $"- {term.Source} => {term.Korean}"
            : $"- {term.Source} => {term.Korean} ({term.Note})"));

    private IEnumerable<GlossaryTerm> Import(string? path, bool alwaysInclude, string category)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            yield break;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".txt" or ".tsv" or ".csv")
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
                string source;
                string korean;
                var note = string.Empty;
                var mapping = Regex.Match(line, "^\\s*(.+?)\\s*(?:=>|=)\\s*(.+?)\\s*$", RegexOptions.CultureInvariant);
                if (mapping.Success)
                {
                    source = mapping.Groups[1].Value.Trim();
                    korean = mapping.Groups[2].Value.Trim();
                }
                else
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    source = parts[0].Trim();
                    korean = parts[1].Trim();
                    if (parts.Length >= 3) note = parts[2].Trim();
                }
                if (source.Length == 0 || korean.Length == 0) continue;
                yield return new GlossaryTerm
                {
                    Source = source,
                    Korean = korean,
                    Note = note,
                    Priority = 1000,
                    Count = 1,
                    Origin = "text-glossary",
                    AlwaysInclude = alwaysInclude,
                    Category = category
                };
            }
            yield break;
        }

        var document = JsonSerializer.Deserialize<GlossaryDocument>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new GlossaryDocument();
        foreach (var term in document.Terms.Where(term => !string.IsNullOrWhiteSpace(term.Source) && !string.IsNullOrWhiteSpace(term.Korean)))
        {
            term.AlwaysInclude = alwaysInclude;
            term.Category = category;
            yield return term;
        }
    }

    private void BuildIndex(IEnumerable<GlossaryTerm> terms)
    {
        always.Clear();
        generated.Clear();
        prefixIndex.Clear();
        var order = 0;
        foreach (var term in terms)
        {
            var search = term.Source.Trim().ToLowerInvariant();
            if (term.AlwaysInclude)
            {
                always.Add(term);
                order++;
                continue;
            }
            if (search.Length < 3)
            {
                order++;
                continue;
            }
            var indexed = new IndexedTerm(term, search, order++);
            generated.Add(indexed);
            var prefix = search[..3];
            if (!prefixIndex.TryGetValue(prefix, out var entries))
            {
                entries = [];
                prefixIndex[prefix] = entries;
            }
            entries.Add(indexed);
        }
    }

    private sealed record IndexedTerm(GlossaryTerm Term, string SearchSource, int Order);
}
