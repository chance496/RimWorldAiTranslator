using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWorldAiTranslator.GlossaryTool;

internal sealed class GlossaryDocument
{
    [JsonPropertyOrder(0), JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyOrder(1), JsonPropertyName("tool")]
    public string Tool { get; init; } = "RimWorldAiTranslator.GlossaryTool";

    [JsonPropertyOrder(2), JsonPropertyName("gameVersion")]
    public string GameVersion { get; init; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("sources")]
    public required GlossarySourceMetadata Sources { get; init; }

    [JsonPropertyOrder(4), JsonPropertyName("priorityOrder")]
    public required IReadOnlyList<GlossaryPriority> PriorityOrder { get; init; }

    [JsonPropertyOrder(5), JsonPropertyName("filters")]
    public required GlossaryFilterMetadata Filters { get; init; }

    [JsonPropertyOrder(6), JsonPropertyName("stats")]
    public required GlossaryStats Stats { get; init; }

    [JsonPropertyOrder(7), JsonPropertyName("terms")]
    public required IReadOnlyList<GlossaryTermOutput> Terms { get; init; }
}

internal sealed class GlossarySourceMetadata
{
    [JsonPropertyOrder(0), JsonPropertyName("official")]
    public bool Official { get; init; }

    [JsonPropertyOrder(1), JsonPropertyName("rmk")]
    public bool Rmk { get; init; }

    [JsonPropertyOrder(2), JsonPropertyName("discoveryRequested")]
    public bool DiscoveryRequested { get; init; }

    [JsonPropertyOrder(3), JsonPropertyName("categories")]
    public required IReadOnlyList<string> Categories { get; init; }

    [JsonPropertyOrder(4), JsonPropertyName("customDefFieldRules")]
    public bool CustomDefFieldRules { get; init; }
}

internal sealed class GlossaryPriority
{
    [JsonPropertyOrder(0), JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyOrder(1), JsonPropertyName("origin")]
    public required string Origin { get; init; }
}

internal sealed class GlossaryFilterMetadata
{
    [JsonPropertyOrder(0), JsonPropertyName("maxSourceChars")]
    public int MaxSourceChars { get; init; }

    [JsonPropertyOrder(1), JsonPropertyName("minRmkOccurrences")]
    public int MinRmkOccurrences { get; init; }

    [JsonPropertyOrder(2), JsonPropertyName("includeSentences")]
    public bool IncludeSentences { get; init; }

    [JsonPropertyOrder(3), JsonPropertyName("includePatches")]
    public bool IncludePatches { get; }

    [JsonPropertyOrder(4), JsonPropertyName("includeRmk")]
    public bool IncludeRmk { get; init; }
}

internal sealed class GlossaryStats
{
    [JsonPropertyOrder(0), JsonPropertyName("observations")]
    public int Observations { get; init; }

    [JsonPropertyOrder(1), JsonPropertyName("officialObservations")]
    public int OfficialObservations { get; init; }

    [JsonPropertyOrder(2), JsonPropertyName("rmkObservations")]
    public int RmkObservations { get; init; }

    [JsonPropertyOrder(3), JsonPropertyName("rmkScannedMods")]
    public int RmkScannedMods { get; init; }

    [JsonPropertyOrder(4), JsonPropertyName("rmkPairedMods")]
    public int RmkPairedMods { get; init; }

    [JsonPropertyOrder(5), JsonPropertyName("rmkMissingSourceMods")]
    public int RmkMissingSourceMods { get; init; }

    [JsonPropertyOrder(6), JsonPropertyName("rmkMissingKoreanMods")]
    public int RmkMissingKoreanMods { get; init; }

    [JsonPropertyOrder(7), JsonPropertyName("terms")]
    public int Terms { get; init; }

    [JsonPropertyOrder(8), JsonPropertyName("conflicts")]
    public int Conflicts { get; init; }
}

internal sealed class GlossaryTermOutput
{
    [JsonPropertyOrder(0), JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyOrder(1), JsonPropertyName("ko")]
    public required string Korean { get; init; }

    [JsonPropertyOrder(2), JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyOrder(3), JsonPropertyName("origin")]
    public required string Origin { get; init; }

    [JsonPropertyOrder(4), JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyOrder(5), JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyOrder(6), JsonPropertyName("keys")]
    public required IReadOnlyList<string> Keys { get; init; }

    [JsonPropertyOrder(7), JsonPropertyName("workshopIds")]
    public required IReadOnlyList<string> WorkshopIds { get; init; }

    [JsonPropertyOrder(8), JsonPropertyName("alternatives")]
    public required IReadOnlyList<GlossaryAlternative> Alternatives { get; init; }
}

internal sealed class GlossaryAlternative
{
    [JsonPropertyOrder(0), JsonPropertyName("ko")]
    public required string Korean { get; init; }

    [JsonPropertyOrder(1), JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyOrder(2), JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyOrder(3), JsonPropertyName("origins")]
    public required IReadOnlyList<string> Origins { get; init; }
}

internal sealed class GlossaryConflict
{
    public required string Source { get; init; }
    public required string ChosenKorean { get; init; }
    public int ChosenPriority { get; init; }
    public int ChosenCount { get; init; }
    public required string Alternatives { get; init; }
}

internal sealed record GlossaryBuildResult(GlossaryDocument Document, IReadOnlyList<GlossaryConflict> Conflicts);

internal static class OutputFormatter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null
    };

    public static byte[] FormatJson(GlossaryBuildResult result)
    {
        var text = JsonSerializer.Serialize(result.Document, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        return Utf8.GetBytes(text);
    }

    public static byte[] FormatConflictsCsv(IReadOnlyList<GlossaryConflict> conflicts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\"source\",\"chosenKo\",\"chosenPriority\",\"chosenCount\",\"alternatives\"");
        foreach (var conflict in conflicts.OrderBy(item => item.Source, StringComparer.Ordinal))
        {
            builder.Append(Csv(conflict.Source)).Append(',')
                .Append(Csv(conflict.ChosenKorean)).Append(',')
                .Append(Csv(conflict.ChosenPriority.ToString(System.Globalization.CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(conflict.ChosenCount.ToString(System.Globalization.CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(conflict.Alternatives)).Append('\n');
        }
        return Utf8.GetBytes(builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string Csv(string value)
    {
        var safe = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@' or '\t') safe = "'" + safe;
        return '"' + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
