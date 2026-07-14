using System.Formats.Tar;
using System.Text;
using System.Text.Json;

namespace RimWorldAiTranslator.GlossaryTool;

internal static class SelfTest
{
    public static int Run(CancellationToken cancellationToken)
    {
        using var fixture = TemporaryDirectory.Create();
        var dataRoot = Path.Combine(fixture.Path, "official-data");
        var rmkRoot = Path.Combine(fixture.Path, "rmk");
        var workshopRoot = Path.Combine(fixture.Path, "workshop");
        CreateOfficialFixture(dataRoot);
        CreateRmkFixture(rmkRoot, workshopRoot);

        var firstJson = Path.Combine(fixture.Path, "first.json");
        var firstCsv = Path.Combine(fixture.Path, "first.csv");
        var options = new BuildOptions
        {
            RimWorldDataRoot = dataRoot,
            RmkRoot = rmkRoot,
            WorkshopRoot = workshopRoot,
            OutputPath = firstJson,
            ConflictPath = firstCsv,
            GameVersion = "1.6",
            IncludeRmk = true,
            MaxSourceChars = 80,
            MinRmkOccurrences = 1
        };
        options.Validate();
        var first = Build(options, cancellationToken);
        AtomicOutput.CommitPair(firstJson, first.Json, firstCsv, first.Csv, cancellationToken);

        var secondOptions = options with
        {
            OutputPath = Path.Combine(fixture.Path, "second.json"),
            ConflictPath = Path.Combine(fixture.Path, "second.csv")
        };
        secondOptions.Validate();
        var second = Build(secondOptions, cancellationToken);
        Assert(first.Json.SequenceEqual(second.Json), "deterministic JSON");
        Assert(first.Csv.SequenceEqual(second.Csv), "deterministic CSV");
        Assert(first.Result.Document.Terms.Any(term => term.Source == "Hello" && term.Korean == "안녕"), "official pair");
        var workbench = first.Result.Document.Terms.Single(term => term.Source.Equals("Workbench", StringComparison.OrdinalIgnoreCase)
            && term.Korean == "작업대");
        Assert(workbench.Count == 2 && workbench.Keys.Count == 2, "RMK Keyed and DefInjected pairs");
        Assert(!first.Result.Document.Terms.Any(term => term.Source == "Tokenless" || term.Source == "Stale Version"),
            "target token and stale version exclusion");
        Assert(first.Result.Conflicts.Any(conflict => conflict.Source == "Color"), "conflict report");

        AssertPrivacy(first.Json, fixture.Path);
        AssertTraversalRejected(fixture.Path, cancellationToken);
        AssertDtdRejected(fixture.Path, cancellationToken);
        AssertRollback(fixture.Path, first.Json, first.Csv, cancellationToken);
        AssertBackupCollisionRejected(fixture.Path, first.Json, first.Csv, cancellationToken);
        AssertReadOnlyOutputRejected(options);
        AssertCsvFormulaNeutralized();

        Console.WriteLine("self-test: PASS");
        return 0;
    }

    private static (GlossaryBuildResult Result, byte[] Json, byte[] Csv) Build(BuildOptions options, CancellationToken cancellationToken)
    {
        var result = new GlossaryBuilder(options).Build(cancellationToken);
        return (result, OutputFormatter.FormatJson(result), OutputFormatter.FormatConflictsCsv(result.Conflicts));
    }

    private static void CreateOfficialFixture(string dataRoot)
    {
        var coreLanguages = Path.Combine(dataRoot, "Core", "Languages");
        var royaltyLanguages = Path.Combine(dataRoot, "Royalty", "Languages");
        WriteText(Path.Combine(coreLanguages, "English", "Keyed", "Core.xml"),
            "<LanguageData><Greeting>Hello</Greeting><Material>Steel</Material><ColorName>Color</ColorName><Unsafe>{name}</Unsafe><Tokenless>Tokenless</Tokenless></LanguageData>");
        WriteText(Path.Combine(coreLanguages, "English", "Keyed", "Duplicate.xml"),
            "<LanguageData><Greeting>Hello</Greeting></LanguageData>");
        WriteTar(Path.Combine(coreLanguages, "Korean-fixture.tar"),
            ("Keyed/Core.xml", "<LanguageData><Greeting>안녕</Greeting><Material>강철</Material><ColorName>색상</ColorName><Unsafe>이름</Unsafe><Tokenless>토큰 {unexpected}</Tokenless></LanguageData>"));
        WriteText(Path.Combine(royaltyLanguages, "English", "Keyed", "Royalty.xml"),
            "<LanguageData><RoyalTitle>Empire</RoyalTitle><ColorName>Color</ColorName></LanguageData>");
        WriteTar(Path.Combine(royaltyLanguages, "Korean-fixture.tar"),
            ("Keyed/Royalty.xml", "<LanguageData><RoyalTitle>제국</RoyalTitle><ColorName>빛깔</ColorName></LanguageData>"));
    }

    private static void CreateRmkFixture(string rmkRoot, string workshopRoot)
    {
        WriteText(Path.Combine(rmkRoot, "ModList.tsv"), "123456\tSynthetic Mod\tData\tsynthetic.mod\n");
        var korean = Path.Combine(rmkRoot, "Data", "Synthetic Mod - 123456", "1.6", "Languages", "Korean (한국어)");
        WriteText(Path.Combine(korean, "Keyed", "Rmk.xml"),
            "<LanguageData><Rmk.Workbench>작업대</Rmk.Workbench></LanguageData>");
        WriteText(Path.Combine(korean, "DefInjected", "ThingDef", "Rmk.xml"),
            "<LanguageData><SyntheticBench.label>작업대</SyntheticBench.label></LanguageData>");

        var source = Path.Combine(workshopRoot, "123456", "1.6");
        WriteText(Path.Combine(source, "Languages", "English", "Keyed", "Rmk.xml"),
            "<LanguageData><Rmk.Workbench>Workbench</Rmk.Workbench></LanguageData>");
        WriteText(Path.Combine(source, "Defs", "Things.xml"),
            "<Defs><ThingDef><defName>SyntheticBench</defName><label>Workbench</label><texPath>UI/Forbidden</texPath></ThingDef></Defs>");
        WriteText(Path.Combine(rmkRoot, "Data", "Synthetic Mod - 123456", "1.5", "Languages", "Korean (한국어)", "Keyed", "Old.xml"),
            "<LanguageData><Old.Version>오래된 버전</Old.Version></LanguageData>");
        WriteText(Path.Combine(workshopRoot, "123456", "1.5", "Languages", "English", "Keyed", "Old.xml"),
            "<LanguageData><Old.Version>Stale Version</Old.Version></LanguageData>");
    }

    private static void AssertPrivacy(byte[] json, string tempRoot)
    {
        var text = new UTF8Encoding(false, true).GetString(json);
        Assert(!text.Contains(tempRoot, StringComparison.OrdinalIgnoreCase), "serialized source path");
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement);
        return;

        static void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject()) Walk(property.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(item);
                    break;
                case JsonValueKind.String:
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value))
                        throw new GlossaryToolException("Self-test failed: rooted path was serialized.");
                    break;
            }
        }
    }

    private static void AssertTraversalRejected(string root, CancellationToken cancellationToken)
    {
        var archive = Path.Combine(root, "traversal.tar");
        WriteTar(archive, ("../escape.xml", "<LanguageData><Bad>나쁨</Bad></LanguageData>"));
        var destination = Path.Combine(root, "traversal-output");
        var rejected = false;
        try { SafeTarExtractor.Extract(archive, destination, cancellationToken); }
        catch (InputDataException) { rejected = true; }
        Assert(rejected, "tar traversal rejection");
        Assert(!File.Exists(Path.Combine(root, "escape.xml")), "tar traversal containment");
    }

    private static void AssertDtdRejected(string root, CancellationToken cancellationToken)
    {
        var languageRoot = Path.Combine(root, "dtd", "Keyed");
        WriteText(Path.Combine(languageRoot, "Bad.xml"),
            "<!DOCTYPE LanguageData [<!ENTITY xxe SYSTEM 'file:///forbidden'>]><LanguageData><Bad>&xxe;</Bad></LanguageData>");
        var rejected = false;
        try { _ = LanguageSources.ReadLanguageRoot(Path.GetDirectoryName(languageRoot)!, cancellationToken); }
        catch (InputDataException) { rejected = true; }
        Assert(rejected, "DTD rejection");
    }

    private static void AssertRollback(string root, byte[] newJson, byte[] newCsv, CancellationToken cancellationToken)
    {
        var jsonPath = Path.Combine(root, "rollback.json");
        var csvPath = Path.Combine(root, "rollback.csv");
        const string oldJson = "{\"old\":true}\n";
        const string oldCsv = "old-csv\n";
        const string priorJsonBackup = "prior-json-backup\n";
        const string priorCsvBackup = "prior-csv-backup\n";
        WriteText(jsonPath, oldJson);
        WriteText(csvPath, oldCsv);
        WriteText(jsonPath + ".bak", priorJsonBackup);
        WriteText(csvPath + ".bak", priorCsvBackup);
        var failed = false;
        try
        {
            AtomicOutput.CommitPair(jsonPath, newJson, csvPath, newCsv, cancellationToken,
                () => throw new InjectedFailureException());
        }
        catch (InjectedFailureException) { failed = true; }
        Assert(failed, "injected failure");
        Assert(File.ReadAllText(jsonPath, Encoding.UTF8) == oldJson, "JSON rollback");
        Assert(File.ReadAllText(csvPath, Encoding.UTF8) == oldCsv, "CSV rollback");
        Assert(File.ReadAllText(jsonPath + ".bak", Encoding.UTF8) == priorJsonBackup, "JSON prior backup rollback");
        Assert(File.ReadAllText(csvPath + ".bak", Encoding.UTF8) == priorCsvBackup, "CSV prior backup rollback");
    }

    private static void AssertBackupCollisionRejected(string root, byte[] json, byte[] csv, CancellationToken cancellationToken)
    {
        var jsonPath = Path.Combine(root, "collision.json");
        var csvPath = jsonPath + ".bak";
        WriteText(jsonPath, "original-json\n");
        WriteText(csvPath, "original-csv\n");
        var rejected = false;
        try { AtomicOutput.CommitPair(jsonPath, json, csvPath, csv, cancellationToken); }
        catch (GlossaryToolException) { rejected = true; }
        Assert(rejected, "backup namespace collision rejection");
        Assert(File.ReadAllText(jsonPath, Encoding.UTF8) == "original-json\n", "collision JSON preservation");
        Assert(File.ReadAllText(csvPath, Encoding.UTF8) == "original-csv\n", "collision CSV preservation");
    }

    private static void AssertReadOnlyOutputRejected(BuildOptions options)
    {
        var protectedInput = Path.Combine(options.RimWorldDataRoot, "protected-output.json");
        var rejected = false;
        try { (options with { OutputPath = protectedInput, ConflictPath = protectedInput + ".csv" }).Validate(); }
        catch (GlossaryToolException) { rejected = true; }
        Assert(rejected && !File.Exists(protectedInput), "read-only input/output boundary");
    }

    private static void AssertCsvFormulaNeutralized()
    {
        var csv = Encoding.UTF8.GetString(OutputFormatter.FormatConflictsCsv(
            [new GlossaryConflict { Source = "=1+1", ChosenKorean = "+위험", ChosenPriority = 1, ChosenCount = 1, Alternatives = "@alt" }]));
        Assert(csv.Contains("\"'=1+1\"", StringComparison.Ordinal)
               && csv.Contains("\"'+위험\"", StringComparison.Ordinal)
               && csv.Contains("\"'@alt\"", StringComparison.Ordinal),
            "CSV formula neutralization");
    }

    private static void WriteTar(string path, params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var item in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(item.Content);
            using var data = new MemoryStream(bytes, writable: false);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, item.Name)
            {
                DataStream = data,
                ModificationTime = DateTimeOffset.UnixEpoch
            };
            writer.WriteEntry(entry);
        }
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new GlossaryToolException($"Self-test failed: {name}.");
    }

    private sealed class InjectedFailureException : Exception
    {
    }
}
