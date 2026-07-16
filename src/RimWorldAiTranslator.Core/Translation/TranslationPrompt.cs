using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Translation;

public static class TranslationPrompt
{
    public static string CreateSystem(string glossary, string? extraPrompt)
    {
        // The string below intentionally mirrors the Golden Master here-string byte-for-byte,
        // including its five single-space-indented rule lines and Windows line endings.
        var prompt = string.Join("\r\n",
        [
            "You translate RimWorld mod localization entries into natural Korean.",
            "Return only JSON matching this shape: {\"translations\":[{\"id\":\"same id\",\"text\":\"Korean translation\"}]}.",
            string.Empty,
            "Rules:",
            "- Translate only the text value. Never translate ids, XML keys, defNames, file names, class names, or paths.",
            " - Preserve placeholders, grammar-rule prefixes, and markup exactly: {0}, {PAWN_nameDef}, [pawn_nameDef], r_logentry->, $variable, <color=...>, </color>, \\n, %, and XML-like tags.",
            " - A grammar-rule prefix such as r_logentry-> must remain unchanged at the beginning of the translated value.",
            " - When a Korean particle follows a placeholder or dynamic noun, use RimWorld's automatic particle notation with the consonant-final form in parentheses first: (은)는, (이)가, (을)를, (과)와, (으)로.",
            " - Attach that notation directly to the placeholder, for example [lodgersLabelSingOrPluralDef](이)가. Never use reversed forms such as 은(는), 이(가), 을(를), 과(와), or 으로(로).",
            " - Keep label fields short, usually a noun phrase.",
            "- Use polite declarative Korean for descriptions and letters when appropriate.",
            "- Preserve meaningful line breaks, but never add padding blank lines or more than two consecutive \\n escapes.",
            "- Do not output repeated \\u000a escapes.",
            "- Keep RimWorld/DLC terms consistent with the glossary.",
            "- If a value is already a proper noun, keep the proper noun or transliterate naturally.",
            "- Do not add comments, explanations, markdown, or missing ids.",
            string.Empty,
            "Glossary:",
            glossary
        ]);
        return string.IsNullOrWhiteSpace(extraPrompt)
            ? prompt + "\r\n"
            : prompt + "\r\n\r\nAdditional user instructions:\r\n" + extraPrompt;
    }

    public static string CreateUserPayload(IReadOnlyList<SourceEntry> batch)
    {
        var builder = new StringBuilder();
        builder.Append("{\r\n    \"entries\":  [\r\n");
        for (var index = 0; index < batch.Count; index++)
        {
            if (index > 0) builder.Append(",\r\n");
            var item = batch[index];
            builder.Append("                    {\r\n");
            AppendProperty(builder, "id", item.Id, true);
            AppendProperty(builder, "key", item.Key, true);
            AppendProperty(builder, "kind", item.Kind, true);
            AppendProperty(builder, "defType", item.TypeName, true);
            AppendProperty(builder, "field", item.Field, true);
            AppendProperty(builder, "text", item.Text, false);
            builder.Append("                    }");
        }
        builder.Append("\r\n                ]\r\n}");
        return builder.ToString();
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
        var reasoningEffort = ResolveReasoningEffort(options);
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            body["reasoning_effort"] = reasoningEffort;
        }

        var format = ResolveResponseFormat(options);
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

    private static string ResolveReasoningEffort(TranslationEngineOptions options)
    {
        var configured = options.ReasoningEffort.Trim();
        if (!options.Provider.Id.Equals("Gemini", StringComparison.OrdinalIgnoreCase)) return configured;
        return string.IsNullOrWhiteSpace(configured) ? "low" : configured;
    }

    private static string ResolveResponseFormat(TranslationEngineOptions options)
    {
        if (options.NoStructuredOutputs) return "JsonObject";
        return options.ResponseFormatMode;
    }

    public static Dictionary<string, string> ParseResponse(
        string responseJson,
        IEnumerable<string>? expectedIds = null)
    {
        try
        {
            return ParseResponseCore(responseJson, expectedIds);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Provider response contained malformed JSON.", exception);
        }
    }

    private static Dictionary<string, string> ParseResponseCore(
        string responseJson,
        IEnumerable<string>? expectedIds)
    {
        using var response = JsonDocument.Parse(responseJson);
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Model response did not contain a choices array.");
        }
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object
            || !choice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
        {
            throw new InvalidDataException("Model response did not contain message content.");
        }

        var contentText = ReadContent(content);
        if (string.IsNullOrWhiteSpace(contentText))
            throw new InvalidDataException("Model response did not contain text content.");

        var trimmed = StripCodeFence(contentText);
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(trimmed);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Model content contained malformed JSON.", exception);
        }
        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Model content must be a JSON object.");

            var map = ParseTranslationMap(parsed.RootElement);
            ValidateExpectedIds(map, expectedIds);
            return map;
        }
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Model message content had an unsupported shape.");

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
                continue;
            }
            throw new InvalidDataException("Model message content array contained an unsupported part.");
        }
        return builder.ToString();
    }

    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstBreak = trimmed.IndexOf('\n');
        trimmed = firstBreak >= 0 ? trimmed[(firstBreak + 1)..] : trimmed[3..];
        if (trimmed.EndsWith("```", StringComparison.Ordinal)) trimmed = trimmed[..^3].TrimEnd();
        return trimmed;
    }

    private static Dictionary<string, string> ParseTranslationMap(JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("translations", out var translations))
        {
            if (translations.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Model translations must be an array.");
            foreach (var item in translations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString())
                    || !item.TryGetProperty("text", out var text)
                    || text.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Model translation entries require string id and text properties.");
                }
                map[id.GetString()!] = Normalize(text.GetString());
            }
        }
        else
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name)
                    || property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Model direct-map entries require non-empty ids and string values.");
                }
                map[property.Name] = Normalize(property.Value.GetString());
            }
        }
        return map;
    }

    private static void ValidateExpectedIds(
        IReadOnlyDictionary<string, string> map,
        IEnumerable<string>? expectedIds)
    {
        if (expectedIds is null) return;
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in expectedIds)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException("Translation batch contained an empty id.");
            expected.Add(id);
        }
        var missing = expected.Count(id => !map.ContainsKey(id));
        if (missing > 0)
            throw new InvalidDataException($"Model response omitted {missing} translation id(s).");
    }

    private static void AppendProperty(StringBuilder builder, string name, string? value, bool comma)
    {
        builder.Append("                        \"").Append(name).Append("\":  ");
        if (value is null) builder.Append("null");
        else AppendJsonString(builder, value);
        if (comma) builder.Append(',');
        builder.Append("\r\n");
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                case '<': builder.Append("\\u003c"); break;
                case '>': builder.Append("\\u003e"); break;
                case '&': builder.Append("\\u0026"); break;
                default:
                    if (character < ' ')
                        builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
}
