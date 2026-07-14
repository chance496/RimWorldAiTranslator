using System.Text;
using System.Text.Json;

namespace RimWorldAiTranslator.Core.Storage;

internal static class ValidatedArtifactFile
{
    private const int MaximumValidatedArtifactBytes = 128 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8NoBom = new(false, true);

    public static void ValidateJson(
        string path,
        string expectedJson,
        Action<JsonElement>? validateSemantics = null)
    {
        var actualJson = ReadStrictUtf8WithoutBom(path);
        try
        {
            using var expected = JsonDocument.Parse(expectedJson);
            using var actual = JsonDocument.Parse(actualJson);
            if (actual.RootElement.ValueKind != expected.RootElement.ValueKind)
                throw new InvalidDataException("The flushed JSON artifact has an unexpected root type.");

            var expectedCount = GetContainerCount(expected.RootElement);
            if (expectedCount is not null && GetContainerCount(actual.RootElement) != expectedCount)
                throw new InvalidDataException("The flushed JSON artifact has an unexpected root item count.");
            if (!actualJson.Equals(expectedJson, StringComparison.Ordinal))
                throw new InvalidDataException("The flushed JSON artifact does not match the serialized payload.");

            validateSemantics?.Invoke(actual.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The flushed JSON artifact is invalid.", exception);
        }
    }

    public static void ValidateCsv(string path, IReadOnlyList<string[]> expectedRecords)
    {
        var records = ParseCsv(ReadStrictUtf8WithoutBom(path));
        if (records.Count != expectedRecords.Count)
            throw new InvalidDataException("The flushed CSV artifact has an unexpected row count.");

        for (var rowIndex = 0; rowIndex < expectedRecords.Count; rowIndex++)
        {
            var expected = expectedRecords[rowIndex];
            var actual = records[rowIndex];
            if (actual.Length != expected.Length)
                throw new InvalidDataException($"The flushed CSV artifact has an unexpected column count at row {rowIndex + 1}.");
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
                throw new InvalidDataException($"The flushed CSV artifact does not match the expected values at row {rowIndex + 1}.");
        }
    }

    private static int? GetContainerCount(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.GetArrayLength(),
        JsonValueKind.Object => element.EnumerateObject().Count(),
        _ => null
    };

    private static string ReadStrictUtf8WithoutBom(string path)
    {
        var bytes = BoundedFileReader.ReadAllBytes(path, MaximumValidatedArtifactBytes, "flushed text artifact");
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new InvalidDataException("The flushed text artifact unexpectedly contains a UTF-8 BOM.");
        try
        {
            return StrictUtf8NoBom.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The flushed text artifact is not valid UTF-8.", exception);
        }
    }

    private static List<string[]> ParseCsv(string text)
    {
        var records = new List<string[]>();
        if (text.Length == 0) return records;

        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quotedFieldClosed = false;
        var fieldStarted = false;

        void CompleteField()
        {
            record.Add(field.ToString());
            field.Clear();
            quotedFieldClosed = false;
            fieldStarted = false;
        }

        void CompleteRecord()
        {
            CompleteField();
            records.Add(record.ToArray());
            record.Clear();
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character != '"')
                {
                    field.Append(character);
                    continue;
                }
                if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                    continue;
                }
                inQuotes = false;
                quotedFieldClosed = true;
                continue;
            }

            if (quotedFieldClosed && character is not ',' and not '\r' and not '\n')
                throw new InvalidDataException("The flushed CSV artifact has characters after a closing quote.");

            switch (character)
            {
                case '"':
                    if (fieldStarted)
                        throw new InvalidDataException("The flushed CSV artifact contains an unexpected quote.");
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    CompleteField();
                    break;
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                    CompleteRecord();
                    break;
                case '\n':
                    CompleteRecord();
                    break;
                default:
                    field.Append(character);
                    fieldStarted = true;
                    break;
            }
        }

        if (inQuotes)
            throw new InvalidDataException("The flushed CSV artifact has an unterminated quoted field.");
        if (record.Count > 0 || fieldStarted || quotedFieldClosed || field.Length > 0)
            CompleteRecord();
        return records;
    }
}
