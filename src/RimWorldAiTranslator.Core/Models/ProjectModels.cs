using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWorldAiTranslator.Core.Models;

public sealed class ProjectStoreDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("projects")]
    public List<TranslationProject> Projects { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TranslationProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("modRoot")]
    public string ModRoot { get; set; } = string.Empty;

    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("workshopId")]
    public string WorkshopId { get; set; } = string.Empty;

    [JsonPropertyName("sourceLanguageFolder")]
    public string SourceLanguageFolder { get; set; } = "Auto";

    [JsonPropertyName("latestReviewRoot")]
    public string LatestReviewRoot { get; set; } = string.Empty;

    [JsonPropertyName("latestReviewAt")]
    public string LatestReviewAt { get; set; } = string.Empty;

    [JsonPropertyName("lastAppliedAt")]
    public string LastAppliedAt { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("runs")]
    public List<ProjectRun> Runs { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ProjectRun
{
    [JsonPropertyName("reviewRoot")]
    public string ReviewRoot { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
