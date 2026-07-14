using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Utilities;

namespace RimWorldAiTranslator.Tests;

internal static class Phase05ArtifactValidationTests
{
    private static readonly UTF8Encoding StrictUtf8NoBom = new(false, true);

    public static void RunAll()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RimWorldAiTranslator-phase05-artifact-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var modRoot = Path.Combine(root, "SyntheticMod");
            CopyDirectory(Path.Combine(RepositoryRoot(), "testdata", "SampleMod"), modRoot);
            AddMultilineSource(modRoot);

            var reviewsRoot = Path.Combine(root, "reviews");
            var extractor = new SourceExtractor(Path.Combine(RepositoryRoot(), "rimworld-def-field-rules.txt"));
            var result = new TranslationEngine(RepositoryRoot(), extractor).RunAsync(new TranslationEngineOptions
            {
                ModRoot = modRoot,
                SourceOnly = true,
                ReviewOnly = true,
                ReviewRoot = reviewsRoot,
                SourceLanguageFolder = "Auto"
            }).GetAwaiter().GetResult();

            ValidateTranslationAudit(result);
            ValidatePreservedTranslation(root, reviewsRoot, result, extractor);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void ValidateTranslationAudit(TranslationRunResult result)
    {
        var reviewRoot = result.ReviewRoot
            ?? throw new InvalidOperationException("The synthetic review root was not created.");
        var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
        var expectedRows = result.Rows.Count;
        Assert(expectedRows == 8, $"The synthetic multiline fixture produced {expectedRows} rows instead of 8.");
        Assert(result.Rows.Single(row => row.Key == "Fixture.Multiline").Source.Contains('\n'),
            "The synthetic multiline source did not retain a newline for CSV coverage.");

        AssertJsonArrayCount(FindSingle(auditRoot, "-source.json"), expectedRows);
        AssertJsonArrayCount(FindSingle(auditRoot, "-skipped-internal-identifiers.json"), 1);
        AssertJsonArrayCount(FindSingle(auditRoot, "-translated.json"), 0);
        AssertJsonArrayCount(FindSingle(auditRoot, "-comparison.json"), expectedRows);
        AssertJsonArrayCount(FindSingle(auditRoot, "-token-warnings.json"), 0);
        AssertJsonArrayCount(FindSingle(auditRoot, "-progress.json"), 1);

        var records = ParseCsv(ReadStrictUtf8WithoutBom(FindSingle(auditRoot, "-comparison.csv")));
        Assert(records.Count == expectedRows + 1, "The comparison CSV row count does not match the comparison JSON.");
        Assert(records[0].Length == 31 && records[0][0] == "id" && records[0][30] == "safeToApply",
            "The comparison CSV header changed or has an invalid column count.");
        Assert(records.Skip(1).All(record => record.Length == records[0].Length),
            "A comparison CSV data row has an invalid column count.");
        Assert(records.Skip(1).Single(record => record[1] == "Fixture.Multiline")[7].Contains('\n'),
            "The multiline comparison CSV value did not round-trip through CSV parsing.");
    }

    private static void ValidatePreservedTranslation(
        string root,
        string reviewsRoot,
        TranslationRunResult result,
        SourceExtractor extractor)
    {
        var reviewRoot = result.ReviewRoot!;
        var workspaceService = new ReviewWorkspaceService(new AtomicJsonStore(), extractor);
        var workspace = workspaceService.Load(reviewRoot);
        var item = workspace.Items.Single(candidate => candidate.Row.Key == "Fixture.Multiline");
        workspaceService.UpdateTranslation(workspace, item, "보존 값\r\n둘째 줄", string.Empty, true);

        var service = new TranslationRunArtifactService(Path.Combine(root, "temp"), reviewsRoot);
        var path = service.CreatePreservedTranslations(workspace);
        using var document = ParseStrictJson(path);
        var jsonRoot = document.RootElement;
        Assert(jsonRoot.ValueKind == JsonValueKind.Object
               && jsonRoot.GetProperty("version").GetInt32() == 2
               && jsonRoot.GetProperty("items").GetArrayLength() == 1,
            "The preserved-translation JSON root or item count is invalid.");
        var preservedItem = jsonRoot.GetProperty("items")[0];
        Assert(preservedItem.GetProperty("text").GetString() == item.Decision.Text,
            "The preserved-translation JSON changed the normalized multiline translation.");
        Assert(!preservedItem.GetProperty("sourceChanged").GetBoolean()
               && preservedItem.GetProperty("sourceText").GetString() == item.Row.Source
               && preservedItem.GetProperty("previousSourceText").GetString() == string.Empty
               && preservedItem.GetProperty("sourceHash").GetString()
               == StableIdentity.Sha256(item.Row.Source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')),
            "The preserved-translation JSON lost its source-verification evidence.");
        AssertSubstitutedPreserveCleanupIsRejected(service, path, swap: true);
        service.DeletePreservedTranslations(path);

        item.Decision.SourceChanged = true;
        item.Decision.PreviousSourceText = "synthetic previous source";
        var changedPath = service.CreatePreservedTranslations(workspace);
        using var changedDocument = ParseStrictJson(changedPath);
        var changedItem = changedDocument.RootElement.GetProperty("items")[0];
        Assert(changedItem.GetProperty("sourceChanged").GetBoolean()
               && changedItem.GetProperty("sourceText").GetString() == item.Row.Source
               && changedItem.GetProperty("previousSourceText").GetString() == "synthetic previous source",
            "The preserved-translation JSON lost unresolved source-change history.");
        AssertSubstitutedPreserveCleanupIsRejected(service, changedPath, swap: false);
        var cleanupMutationBlocked = false;
        try
        {
            AtomicTemporaryFiles.BeforePinnedDeleteTestHook = candidate =>
            {
                if (!candidate.Equals(changedPath, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    File.WriteAllText(candidate, "forged-during-owned-cleanup", StrictUtf8NoBom);
                    throw new InvalidOperationException(
                        "The owned preserved-translation file remained writable during pinned deletion.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleanupMutationBlocked = true;
                }
            };
            service.DeletePreservedTranslations(changedPath);
        }
        finally
        {
            AtomicTemporaryFiles.BeforePinnedDeleteTestHook = null;
        }
        Assert(cleanupMutationBlocked && !File.Exists(changedPath),
            "Pinned preserved-translation cleanup did not block a final rewrite or complete deletion.");

        var unowned = Path.Combine(
            Path.GetDirectoryName(changedPath)!,
            $"preserve-{Guid.NewGuid():N}.json");
        File.WriteAllText(unowned, "unowned-winner", StrictUtf8NoBom);
        AssertThrows<InvalidDataException>(() => service.DeletePreservedTranslations(unowned));
        Assert(File.ReadAllText(unowned, Encoding.UTF8) == "unowned-winner",
            "A validly named but unregistered preservation file was deleted.");
    }

    private static void AssertSubstitutedPreserveCleanupIsRejected(
        TranslationRunArtifactService service,
        string path,
        bool swap)
    {
        var original = File.ReadAllBytes(path);
        var winner = Encoding.UTF8.GetBytes(swap ? "swap-cleanup-winner" : "rewrite-cleanup-winner");
        string? displaced = null;
        if (swap)
        {
            displaced = path + ".owned-original";
            File.Move(path, displaced);
        }
        File.WriteAllBytes(path, winner);

        AssertThrows<IOException>(() => service.DeletePreservedTranslations(path));
        Assert(File.ReadAllBytes(path).SequenceEqual(winner),
            "A substituted preservation winner was deleted by path-only cleanup.");
        if (displaced is not null)
        {
            Assert(File.ReadAllBytes(displaced).SequenceEqual(original),
                "The original preservation artifact was not retained after a cleanup namespace swap.");
            File.Delete(path);
            File.Move(displaced, path);
        }
        else
        {
            File.WriteAllBytes(path, original);
        }

        Assert(File.ReadAllBytes(path).SequenceEqual(original),
            "The owned preservation artifact could not be restored after the cleanup substitution test.");
    }

    private static void AssertJsonArrayCount(string path, int expectedCount)
    {
        using var document = ParseStrictJson(path);
        Assert(document.RootElement.ValueKind == JsonValueKind.Array,
            $"The JSON artifact root is not an array: {Path.GetFileName(path)}");
        Assert(document.RootElement.GetArrayLength() == expectedCount,
            $"The JSON artifact item count changed: {Path.GetFileName(path)}");
    }

    private static JsonDocument ParseStrictJson(string path) =>
        JsonDocument.Parse(ReadStrictUtf8WithoutBom(path));

    private static string ReadStrictUtf8WithoutBom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert(bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF,
            $"A text artifact contains an unexpected UTF-8 BOM: {Path.GetFileName(path)}");
        return StrictUtf8NoBom.GetString(bytes);
    }

    private static List<string[]> ParseCsv(string text)
    {
        var records = new List<string[]>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }
                continue;
            }
            if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                record.Add(field.ToString());
                field.Clear();
                records.Add(record.ToArray());
                record.Clear();
            }
            else
            {
                field.Append(character);
            }
        }
        Assert(!quoted, "The comparison CSV contains an unterminated quoted field.");
        if (record.Count > 0 || field.Length > 0)
        {
            record.Add(field.ToString());
            records.Add(record.ToArray());
        }
        return records;
    }

    private static void AddMultilineSource(string modRoot)
    {
        var path = Path.Combine(modRoot, "Languages", "English", "Keyed", "SampleKeys.xml");
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        document.Root!.Add(new XElement("Fixture.Multiline", "first\r\nsecond, \"quoted\""));
        document.Save(path);
    }

    private static string FindSingle(string directory, string suffix) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Single(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "RimWorldAiTranslator.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
