using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;

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
    internal const long MaximumFileBytes = 16L * 1024 * 1024;
    internal const int MaximumTerms = 100_000;
    internal const int MaximumTermCharacters = 8_192;
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
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
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }
        if (PathSafety.IsNetworkPath(path))
            throw new InvalidDataException("Glossary files must use a local path.");
        if (!File.Exists(path)) yield break;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var bytes = BoundedFileReader.ReadAllBytes(path, MaximumFileBytes, "Glossary file");
        if (extension is ".txt" or ".tsv" or ".csv")
        {
            var text = Utf8Strict.GetString(bytes);
            using var reader = new StringReader(text);
            var imported = 0;
            while (reader.ReadLine() is { } raw)
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
                ValidateTermLength(source, korean, note);
                if (++imported > MaximumTerms)
                    throw new InvalidDataException($"Glossary contains more than {MaximumTerms:N0} terms.");
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

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = 64,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !TryGetProperty(document.RootElement, "terms", out var terms)
            || terms.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            yield break;
        }

        IReadOnlyList<JsonElement> elements = terms.ValueKind switch
        {
            JsonValueKind.Array => terms.EnumerateArray().ToArray(),
            JsonValueKind.Object => new[] { terms },
            _ => Array.Empty<JsonElement>()
        };
        if (elements.Count > MaximumTerms)
            throw new InvalidDataException($"Glossary contains more than {MaximumTerms:N0} terms.");
        foreach (var element in elements)
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var term = new GlossaryTerm
            {
                Source = ReadString(element, "source"),
                Korean = ReadString(element, "ko"),
                Note = ReadString(element, "note"),
                Priority = ReadInt32(element, "priority", 1000),
                Count = ReadInt32(element, "count", 1),
                Origin = ReadString(element, "origin")
            };
            if (string.IsNullOrWhiteSpace(term.Source) || string.IsNullOrWhiteSpace(term.Korean)) continue;
            ValidateTermLength(term.Source, term.Korean, term.Note);
            term.AlwaysInclude = alwaysInclude;
            term.Category = category;
            yield return term;
        }
    }

    private static void ValidateTermLength(string source, string korean, string note)
    {
        if (source.Length > MaximumTermCharacters
            || korean.Length > MaximumTermCharacters
            || note.Length > MaximumTermCharacters)
        {
            throw new InvalidDataException($"Glossary terms must not exceed {MaximumTermCharacters:N0} characters per field.");
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static int ReadInt32(JsonElement element, string name, int defaultValue)
    {
        if (!TryGetProperty(element, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return defaultValue;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }
        throw new JsonException($"Glossary property '{name}' must be an integer or null.");
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
