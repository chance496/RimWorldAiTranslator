using System.Text.Json.Serialization;

namespace RimWorldAiTranslator.Core.Models;

public sealed class SourceEntry
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("Text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("Kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("TypeName")]
    public string TypeName { get; set; } = string.Empty;

    [JsonPropertyName("TargetRelativePath")]
    public string TargetRelativePath { get; set; } = string.Empty;

    [JsonPropertyName("SourceFile")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("Field")]
    public string Field { get; set; } = string.Empty;

    [JsonIgnore]
    public string LocalizationNamespace => Kind.Equals("Keyed", StringComparison.OrdinalIgnoreCase) ? "Keyed" : TypeName;

    [JsonIgnore]
    public string Identity => string.IsNullOrWhiteSpace(LocalizationNamespace) || string.IsNullOrWhiteSpace(Key)
        ? string.Empty
        : $"namespace:{LocalizationNamespace.Trim()}|key:{Key.Trim()}";
}

public sealed class SkippedSourceEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("defClass")]
    public string DefClass { get; set; } = string.Empty;

    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed record SourceLanguageRoot(string Name, string Path, int Rank);

public sealed record SourceExtractionResult(
    IReadOnlyList<SourceEntry> Entries,
    IReadOnlyList<SkippedSourceEntry> SkippedInternalIdentifiers,
    IReadOnlyList<SourceLanguageRoot> DetectedLanguages,
    IReadOnlyList<string> Warnings);

public sealed record ExistingLanguageEntry(string Text, string RelativePath);
