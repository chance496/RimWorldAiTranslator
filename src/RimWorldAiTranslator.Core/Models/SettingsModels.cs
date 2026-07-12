using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWorldAiTranslator.Core.Models;

public sealed class AppSettingsDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 4;

    [JsonPropertyName("themeMode")]
    public string ThemeMode { get; set; } = "System";

    [JsonPropertyName("designPreset")]
    public string DesignPreset { get; set; } = "Professional";

    [JsonPropertyName("textSize")]
    public int TextSize { get; set; } = 10;

    [JsonPropertyName("highContrast")]
    public bool HighContrast { get; set; }

    [JsonPropertyName("autoSave")]
    public bool AutoSave { get; set; } = true;

    [JsonPropertyName("rmkWorkspaceRoot")]
    public string RmkWorkspaceRoot { get; set; } = string.Empty;

    [JsonPropertyName("rmkUseExisting")]
    public bool RmkUseExisting { get; set; } = true;

    [JsonPropertyName("customGlossaryPath")]
    public string CustomGlossaryPath { get; set; } = string.Empty;

    [JsonPropertyName("apiProviderId")]
    public string ApiProviderId { get; set; } = "Cerebras";

    [JsonPropertyName("apiProviders")]
    public Dictionary<string, ApiProviderSettings> ApiProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ApiProviderSettings
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.1;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
