using System.Text.Json;
using System.Text.Json.Nodes;
using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Translation;

public static class TranslationPrompt
{
    public static string CreateSystem(string glossary, string? extraPrompt)
    {
        var extra = string.IsNullOrWhiteSpace(extraPrompt)
            ? string.Empty
            : $"\n\nAdditional user instructions:\n{extraPrompt}\n";
        return $$"""
            You translate RimWorld mod localization entries into natural Korean.
            Return only JSON matching this shape: {"translations":[{"id":"same id","text":"Korean translation"}]}.

            Rules:
            - Translate only the text value. Never translate ids, XML keys, defNames, file names, class names, or paths.
            - Preserve placeholders, grammar-rule prefixes, and markup exactly: {0}, {PAWN_nameDef}, [pawn_nameDef], r_logentry->, $variable, <color=...>, </color>, \n, %, and XML-like tags.
            - A grammar-rule prefix such as r_logentry-> must remain unchanged at the beginning of the translated value.
            - When a Korean particle follows a placeholder or dynamic noun, use RimWorld's automatic particle notation with the consonant-final form in parentheses first: (은)는, (이)가, (을)를, (과)와, (으)로.
            - Attach that notation directly to the placeholder, for example [lodgersLabelSingOrPluralDef](이)가. Never use reversed forms such as 은(는), 이(가), 을(를), 과(와), or 으로(로).
            - Keep label fields short, usually a noun phrase.
            - Use polite declarative Korean for descriptions and letters when appropriate.
            - Preserve meaningful line breaks, but never add padding blank lines or more than two consecutive \n escapes.
            - Do not output repeated \u000a escapes.
            - Keep RimWorld/DLC terms consistent with the glossary.
            - If a value is already a proper noun, keep the proper noun or transliterate naturally.
            - Do not add comments, explanations, markdown, or missing ids.

            Glossary:
            {{glossary}}{{extra}}
            """;
    }

    public static string CreateUserPayload(IReadOnlyList<SourceEntry> batch)
    {
        var entries = new JsonArray();
        foreach (var item in batch)
        {
            entries.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["key"] = item.Key,
                ["kind"] = item.Kind,
                ["defType"] = item.TypeName,
                ["field"] = item.Field,
                ["text"] = item.Text
            });
        }
        return new JsonObject { ["entries"] = entries }.ToJsonString();
    }

    public static JsonObject CreateRequestBody(
        TranslationEngineOptions options,
        string systemPrompt,
        string userPayload)
    {
        var body = new JsonObject
        {
            ["model"] = options.ProviderSettings.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPayload }
            },
            ["stream"] = false
        };
        if (options.ProviderSettings.Temperature >= 0)
        {
            body["temperature"] = options.ProviderSettings.Temperature;
            body["top_p"] = 0.9;
        }
        if (options.MaxCompletionTokens > 0 && options.CompletionTokenParameter != "none")
        {
            body[options.CompletionTokenParameter] = options.MaxCompletionTokens;
        }
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
        {
            body["reasoning_effort"] = options.ReasoningEffort.Trim();
        }

        var format = options.NoStructuredOutputs ? "JsonObject" : options.ResponseFormatMode;
        if (format == "JsonSchema")
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "rimworld_translation_batch",
                    ["strict"] = true,
                    ["schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("translations"),
                        ["properties"] = new JsonObject
                        {
                            ["translations"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["additionalProperties"] = false,
                                    ["required"] = new JsonArray("id", "text"),
                                    ["properties"] = new JsonObject
                                    {
                                        ["id"] = new JsonObject { ["type"] = "string" },
                                        ["text"] = new JsonObject { ["type"] = "string" }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }
        else if (format == "JsonObject")
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        return body;
    }

    public static Dictionary<string, string> ParseResponse(string responseJson)
    {
        using var response = JsonDocument.Parse(responseJson);
        var root = response.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Model response did not contain choices.");
        }
        var content = choices[0].GetProperty("message").GetProperty("content");
        var contentText = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(content.EnumerateArray().Select(part =>
                part.ValueKind == JsonValueKind.String
                    ? part.GetString()
                    : part.TryGetProperty("text", out var text) ? text.GetString() : string.Empty)),
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(contentText))
        {
            throw new InvalidDataException("Model response did not contain text content.");
        }

        var trimmed = contentText.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = trimmed.IndexOf('\n');
            trimmed = firstBreak >= 0 ? trimmed[(firstBreak + 1)..] : trimmed[3..];
            if (trimmed.EndsWith("```", StringComparison.Ordinal)) trimmed = trimmed[..^3].TrimEnd();
        }
        using var parsed = JsonDocument.Parse(trimmed);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parsed.RootElement.TryGetProperty("translations", out var translations))
        {
            foreach (var item in translations.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || !item.TryGetProperty("text", out var text)) continue;
                map[id.GetString() ?? string.Empty] = Normalize(text.GetString());
            }
        }
        else
        {
            foreach (var property in parsed.RootElement.EnumerateObject())
            {
                map[property.Name] = Normalize(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString());
            }
        }
        return map;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
}
