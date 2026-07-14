using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Validation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static readonly string[] LegacyIdentifierOnlyRemovedHeaders = ["B1", "C1", "D1"];
    private static readonly string[] LegacyClassNodeOnlyRemovedHeaders = ["A1", "D1"];

    private static void Phase05XmlAndReadOnlyRoundTrip()
    {
        WithTempRoot(root =>
        {
            var languagePath = Path.Combine(root, "Languages", "Korean", "Keyed", "Fixture.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(languagePath)!);
            var originalText = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
                + "<?fixture keep?>\r\n"
                + "<LanguageData>\r\n"
                + "  <!-- keep-comment -->\r\n"
                + "  <z_key> Z </z_key>\r\n"
                + "  <a_key>line1\r\nline2 {0} $TOKEN$</a_key>\r\n"
                + "</LanguageData>\r\n";
            File.WriteAllBytes(languagePath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(originalText)]);
            var originalBytes = File.ReadAllBytes(languagePath);
            var originalHash = Sha256File(languagePath);
            var service = new LanguageFileService();

            var opened = service.Read(languagePath);
            Assert(opened.Count == 2 && Sha256File(languagePath) == originalHash,
                "Read-only LanguageData open changed the source bytes.");

            var result = service.Write(
                languagePath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["a_key"] = "translated\nvalue {0} $TOKEN$",
                    ["m_key"] = "새 값",
                    ["unicode_key"] = "보존 😀 𠀀 제거\u0001\uD800끝"
                },
                overwrite: true);
            Assert(result.Applied == 3 && result.SkippedExisting == 0,
                "LanguageData update counts were incorrect.");
            Assert(File.ReadAllBytes(languagePath + ".bak").SequenceEqual(originalBytes),
                "LanguageData backup did not preserve the exact pre-write bytes.");

            var outputBytes = File.ReadAllBytes(languagePath);
            Assert(outputBytes.Length >= 3
                   && outputBytes[0] == 0xEF && outputBytes[1] == 0xBB && outputBytes[2] == 0xBF,
                "LanguageData UTF-8 BOM was not preserved.");
            var output = Encoding.UTF8.GetString(outputBytes, 3, outputBytes.Length - 3);
            Assert(output.Contains("<?fixture keep?>", StringComparison.Ordinal)
                   && output.Contains("<!-- keep-comment -->", StringComparison.Ordinal),
                "LanguageData processing instructions or comments were lost.");
            Assert(output.IndexOf("<z_key>", StringComparison.Ordinal)
                   < output.IndexOf("<a_key>", StringComparison.Ordinal)
                   && output.IndexOf("<a_key>", StringComparison.Ordinal)
                   < output.IndexOf("<m_key>", StringComparison.Ordinal),
                "LanguageData element order was not preserved while adding a key.");
            Assert(output.Contains("\r\n", StringComparison.Ordinal)
                   && !output.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'),
                "LanguageData CRLF convention was not preserved.");
            var reopened = service.Read(languagePath);
            Assert(reopened["z_key"] == " Z "
                   && reopened["a_key"] == "translated\nvalue {0} $TOKEN$"
                   && reopened["m_key"] == "새 값"
                   && reopened["unicode_key"] == "보존 😀 𠀀 제거끝",
                "LanguageData semantic round trip changed a preserved or updated value.");
            Assert(LanguageFileService.RemoveInvalidXmlCharacters("😀𠀀") == "😀𠀀"
                   && LanguageFileService.RemoveInvalidXmlCharacters("A\u0001\uD800B\uDC00C") == "ABC",
                "XML sanitization did not preserve supplementary Unicode or reject invalid UTF-16/XML characters.");

            var dtdPath = Path.Combine(root, "dtd.xml");
            File.WriteAllText(
                dtdPath,
                "<!DOCTYPE LanguageData [<!ENTITY secret SYSTEM \"file:///synthetic-do-not-read\">]><LanguageData><key>&secret;</key></LanguageData>",
                new UTF8Encoding(false));
            var dtdHash = Sha256File(dtdPath);
            AssertThrows<XmlException>(() => service.Read(dtdPath));
            Assert(Sha256File(dtdPath) == dtdHash, "Rejected DTD XML was modified.");

            var lfPath = Path.Combine(
                root,
                "경로 with space (fixture)",
                "nested-segment-abcdefghijklmnopqrstuvwxyz",
                "no-bom-lf.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(lfPath)!);
            File.WriteAllText(
                lfPath,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<LanguageData>\n  <key>value</key>\n</LanguageData>\n",
                new UTF8Encoding(false));
            var lfOriginal = File.ReadAllBytes(lfPath);
            _ = service.Read(lfPath);
            service.Write(lfPath, new Dictionary<string, string> { ["key"] = "새 값" }, overwrite: true);
            var lfOutput = File.ReadAllBytes(lfPath);
            Assert(!(lfOutput.Length >= 3
                     && lfOutput[0] == 0xEF && lfOutput[1] == 0xBB && lfOutput[2] == 0xBF)
                   && !Encoding.UTF8.GetString(lfOutput).Contains('\r')
                   && File.ReadAllBytes(lfPath + ".bak").SequenceEqual(lfOriginal),
                "No-BOM LF XML or Unicode/space path compatibility was not preserved.");

            var readOnlyPath = Path.Combine(root, "read-only.xml");
            File.Copy(lfPath, readOnlyPath);
            var readOnlyHash = Sha256File(readOnlyPath);
            File.SetAttributes(readOnlyPath, File.GetAttributes(readOnlyPath) | FileAttributes.ReadOnly);
            try
            {
                AssertThrows<UnauthorizedAccessException>(() => service.Write(
                    readOnlyPath,
                    new Dictionary<string, string> { ["key"] = "차단" },
                    overwrite: true));
                Assert(Sha256File(readOnlyPath) == readOnlyHash,
                    "Rejected read-only LanguageData file was modified.");
            }
            finally
            {
                File.SetAttributes(readOnlyPath, File.GetAttributes(readOnlyPath) & ~FileAttributes.ReadOnly);
            }

            var mediumPath = Path.Combine(root, "medium-500.xml");
            var mediumUpdates = Enumerable.Range(0, 500).ToDictionary(
                index => $"fixture.key.{index:D3}",
                index => index == 499
                    ? new string('가', 128 * 1024) + "\n{0} $TOKEN$"
                    : $"합성 값 {index:D3} {{0}}",
                StringComparer.Ordinal);
            var mediumResult = service.Write(mediumPath, mediumUpdates, overwrite: true);
            var mediumRoundTrip = service.Read(mediumPath);
            Assert(mediumResult.Applied == 500
                   && mediumRoundTrip.Count == 500
                   && mediumRoundTrip["fixture.key.499"].Length > 128 * 1024,
                "Medium/long-string LanguageData functional round trip failed.");

            var truncatedPath = Path.Combine(root, "truncated.xml");
            File.WriteAllText(truncatedPath, "<LanguageData><key>truncated", new UTF8Encoding(false));
            var truncatedHash = Sha256File(truncatedPath);
            AssertThrows<XmlException>(() => service.Read(truncatedPath));
            Assert(Sha256File(truncatedPath) == truncatedHash, "Rejected truncated XML was modified.");

            var equivalentNewlines = TranslationValidator.Validate("first\r\nsecond {0}", "첫째\n둘째 {0}");
            var deletedNewline = TranslationValidator.Validate("first\nsecond {0}", "첫째 둘째 {0}");
            var addedNewline = TranslationValidator.Validate("first second {0}", "첫째\n둘째 {0}");
            Assert(equivalentNewlines.IsSafe
                   && !deletedNewline.IsSafe && deletedNewline.MissingTokens.Contains("\n")
                   && !addedNewline.IsSafe && addedNewline.UnexpectedTokens.Contains("\n"),
                "Actual line-break preservation did not treat CRLF/LF as equivalent and additions/removals as unsafe.");

            var patchMod = Path.Combine(root, "patch-mod");
            Directory.CreateDirectory(Path.Combine(patchMod, "Defs"));
            Directory.CreateDirectory(Path.Combine(patchMod, "Patches"));
            File.WriteAllText(
                Path.Combine(patchMod, "Defs", "Fixture.xml"),
                "<Defs><ThingDef><defName>Fixture</defName><label>visible def</label></ThingDef></Defs>",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(patchMod, "Patches", "Unsafe.xml"),
                "<Patch><Operation Class=\"PatchOperationAdd\"><value><ThingDef><label>must not translate</label></ThingDef></value></Operation></Patch>",
                new UTF8Encoding(false));
            var patchExtraction = CreateExtractor().Extract(patchMod, includePatches: true);
            Assert(patchExtraction.Entries.Any(entry => entry.Text == "visible def")
                   && patchExtraction.Entries.All(entry => entry.Text != "must not translate")
                   && patchExtraction.Warnings.Any(warning => warning.Contains("disabled", StringComparison.OrdinalIgnoreCase)),
                "Patches were not excluded fail-closed while normal Def extraction remained available.");

            VerifyLegacyReviewOpenIsReadOnly(root);
        });
    }

    private static void VerifyLegacyReviewOpenIsReadOnly(string root)
    {
        var reviewRoot = Path.Combine(root, "legacy-review");
        var row = Row(reviewRoot, "E000001", "fixture.key", "source text");
        WriteComparison(reviewRoot, [row]);
        var comparison = ReviewWorkspaceService.FindComparisonFile(reviewRoot);
        var decisionPath = ReviewRepository.GetDecisionPath(reviewRoot);
        new AtomicJsonStore().Write(decisionPath, new ReviewDecisionDocument
        {
            Version = 5,
            ReviewRoot = reviewRoot,
            Comparison = comparison,
            Items =
            [
                new ReviewDecision
                {
                    Id = row.Id,
                    Key = row.Key,
                    Target = Path.Combine("Keyed", "Fixture.xml"),
                    Status = ReviewStatuses.Approved,
                    Text = "번역",
                    SourceText = row.Source,
                    SourceHash = RimWorldAiTranslator.Core.Utilities.StableIdentity.Sha256(row.Source)
                }
            ]
        });
        var before = Sha256File(decisionPath);
        var reviewService = new ReviewWorkspaceService(new AtomicJsonStore(), CreateExtractor());
        var workspace = reviewService.Load(reviewRoot);
        Assert(workspace.MigrationPending && !workspace.Dirty,
            "Legacy review normalization was not separated from user edits.");
        using (var coordinator = new ReviewSaveCoordinator(reviewService))
            coordinator.SaveAsync(workspace).GetAwaiter().GetResult();
        Assert(Sha256File(decisionPath) == before,
            "Read-only legacy review open/close rewrote the decision store.");
        reviewService.Save(workspace);
        Assert(!workspace.MigrationPending && Sha256File(decisionPath) != before,
            "Explicit legacy review save did not persist and clear the migration state.");
    }

    private static void Phase05RmkPackageRoundTrip()
    {
        WithTempRoot(root =>
        {
            var workbook = Path.Combine(root, "history.xlsx");
            var originalRow = new RimWorldTranslatorRmkHistoryRow
            {
                Identifier = "ThingDef+Fixture.label",
                ClassName = "ThingDef",
                Key = "Fixture.label",
                RequiredMods = " required.mod \r\n second.mod ",
                Source = " source line 1\r\nsource line 2 ",
                Translation = " 번역 1\r\n번역 2 "
            };
            RimWorldTranslatorRmkXlsxWriter.Write(workbook, [originalRow], "English");
            AddRmkPreservationSentinels(workbook);
            var sourceHash = Sha256File(workbook);
            var packageHashes = ZipEntryHashes(workbook);

            var opened = RimWorldTranslatorRmkXlsxReader.Read(workbook);
            string initialWorksheet;
            using (var initialArchive = ZipFile.OpenRead(workbook))
                initialWorksheet = ReadZipEntry(initialArchive, "xl/worksheets/sheet1.xml");
            Assert(Sha256File(workbook) == sourceHash && opened.Rows.Count == 1,
                "Read-only RMK workbook open changed the package bytes.");
            Assert(opened.Rows[0].RequiredMods == originalRow.RequiredMods
                   && opened.Rows[0].Source == originalRow.Source
                   && opened.Rows[0].Translation == originalRow.Translation,
                "RMK reader trimmed or normalized user cell text. "
                + $"required={JsonSerializer.Serialize(opened.Rows[0].RequiredMods)} "
                + $"source={JsonSerializer.Serialize(opened.Rows[0].Source)} "
                + $"translation={JsonSerializer.Serialize(opened.Rows[0].Translation)} "
                + $"crEntity={initialWorksheet.Contains("&#xD;", StringComparison.Ordinal)} "
                + $"lfEntity={initialWorksheet.Contains("&#xA;", StringComparison.Ordinal)}");

            var updatedRow = new RimWorldTranslatorRmkHistoryRow
            {
                Identifier = originalRow.Identifier,
                ClassName = originalRow.ClassName,
                Key = originalRow.Key,
                RequiredMods = " required.mod \r\n changed.mod ",
                Source = " changed source\r\nsecond line ",
                Translation = " 변경 번역\r\n둘째 줄 "
            };
            var newRow = new RimWorldTranslatorRmkHistoryRow
            {
                Identifier = "ThingDef+Fixture.description",
                ClassName = "ThingDef",
                Key = "Fixture.description",
                RequiredMods = "dependency.mod",
                Source = "description",
                Translation = "설명"
            };
            RimWorldTranslatorRmkXlsxWriter.Write(workbook, [updatedRow, newRow], "English");

            Assert(File.Exists(workbook + ".bak") && Sha256File(workbook + ".bak") == sourceHash,
                "RMK workbook backup was not the exact pre-write package.");
            var afterHashes = ZipEntryHashes(workbook);
            foreach (var pair in packageHashes.Where(pair => pair.Key != "xl/worksheets/sheet1.xml"))
            {
                Assert(afterHashes.TryGetValue(pair.Key, out var after) && after == pair.Value,
                    $"RMK package part changed unexpectedly: {pair.Key}");
            }
            VerifyRmkPreservationSentinels(workbook);
            var roundTrip = RimWorldTranslatorRmkXlsxReader.Read(workbook);
            var updated = roundTrip.Map[updatedRow.Identifier];
            Assert(roundTrip.Rows.Count == 2
                   && updated.RequiredMods == updatedRow.RequiredMods
                   && updated.Source == updatedRow.Source
                   && updated.Translation == updatedRow.Translation
                   && roundTrip.Map.ContainsKey(newRow.Identifier),
                "RMK stable-identifier update/new-row round trip lost data.");

            var beforeRejectedDuplicate = Sha256File(workbook);
            AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxWriter.Write(
                workbook,
                [updatedRow, new RimWorldTranslatorRmkHistoryRow
                {
                    Identifier = updatedRow.Identifier.ToUpperInvariant(),
                    ClassName = "ThingDef",
                    Key = "duplicate",
                    Source = "duplicate",
                    Translation = "중복"
                }],
                "English"));
            Assert(Sha256File(workbook) == beforeRejectedDuplicate,
                "Rejected duplicate RMK identifiers changed the workbook.");

            Exception? lockedWriteError = null;
            using (File.Open(workbook, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    RimWorldTranslatorRmkXlsxWriter.Write(workbook, [updatedRow, newRow], "English");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    lockedWriteError = exception;
                }
            }
            Assert(lockedWriteError is not null
                   && Sha256File(workbook) == beforeRejectedDuplicate
                   && !Directory.EnumerateFiles(root, "history.xlsx.tmp-*", SearchOption.TopDirectoryOnly).Any(),
                "Locked RMK workbook handling did not fail without changing the source.");

            foreach (var legacyVariant in new[]
                     {
                         (Name: "identifier-only", RemovedHeaders: LegacyIdentifierOnlyRemovedHeaders),
                         (Name: "class-node-only", RemovedHeaders: LegacyClassNodeOnlyRemovedHeaders)
                     })
            {
                var legacyWorkbook = Path.Combine(root, $"legacy-{legacyVariant.Name}.xlsx");
                File.Copy(workbook, legacyWorkbook);
                RewriteWorksheet(legacyWorkbook, document =>
                {
                    var header = document.Descendants().Single(node =>
                        node.Name.LocalName == "row" && (string?)node.Attribute("r") == "1");
                    foreach (var cell in header.Elements().Where(node =>
                                 legacyVariant.RemovedHeaders.Contains((string?)node.Attribute("r"), StringComparer.Ordinal)).ToArray())
                    {
                        cell.Remove();
                    }
                });
                var readableLegacy = RimWorldTranslatorRmkXlsxReader.Read(legacyWorkbook);
                Assert(readableLegacy.Rows.Count == 2,
                    $"Reader rejected supported legacy RMK headers: {legacyVariant.Name}");
                RimWorldTranslatorRmkXlsxWriter.Write(legacyWorkbook, [updatedRow, newRow], "English");
                var writtenLegacy = RimWorldTranslatorRmkXlsxReader.Read(legacyWorkbook);
                Assert(writtenLegacy.Rows.Count == 2
                       && writtenLegacy.Map[updatedRow.Identifier].RequiredMods == updatedRow.RequiredMods
                       && writtenLegacy.Map[newRow.Identifier].Translation == newRow.Translation,
                    $"Writer could not upgrade a readable legacy RMK header without losing data: {legacyVariant.Name}");
                VerifyRmkPreservationSentinels(legacyWorkbook);
            }

            var headerless = Path.Combine(root, "headerless.xlsx");
            File.Copy(workbook, headerless);
            RewriteWorksheet(headerless, document =>
            {
                foreach (var text in document.Descendants().Where(node => node.Name.LocalName == "t"))
                    text.Value = text.Value.Replace("[Source string]", "[Not a source]", StringComparison.Ordinal)
                        .Replace("[Translation]", "[Not a translation]", StringComparison.Ordinal);
            });
            var headerlessHash = Sha256File(headerless);
            AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxReader.Read(headerless));
            Assert(Sha256File(headerless) == headerlessHash, "Rejected headerless workbook was modified.");

            var splitHeader = Path.Combine(root, "split-header.xlsx");
            File.Copy(workbook, splitHeader);
            RewriteWorksheet(splitHeader, document =>
            {
                var sheetData = document.Descendants().Single(node => node.Name.LocalName == "sheetData");
                var header = sheetData.Elements().Single(node => node.Name.LocalName == "row" && (string?)node.Attribute("r") == "1");
                var translationCell = header.Elements().Single(node => (string?)node.Attribute("r") == "F1");
                translationCell.Remove();
                var splitRow = new XElement(header.Name, new XAttribute("r", "10"));
                AddInlineCell(splitRow, "F10", "Korean (한국어) [Translation]");
                sheetData.Add(splitRow);
            });
            var splitHeaderHash = Sha256File(splitHeader);
            AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxReader.Read(splitHeader));
            Assert(Sha256File(splitHeader) == splitHeaderHash,
                "Partial headers split across rows were combined or modified.");

            var dtdWorkbook = Path.Combine(root, "dtd.xlsx");
            File.Copy(workbook, dtdWorkbook);
            RewriteWorksheetRaw(dtdWorkbook, xml =>
            {
                var declarationEnd = xml.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                    ? xml.IndexOf("?>", StringComparison.Ordinal) + 2
                    : 0;
                return "<!DOCTYPE worksheet [<!ENTITY synthetic \"blocked\">]>" + xml[declarationEnd..];
            });
            AssertThrows<XmlException>(() => RimWorldTranslatorRmkXlsxReader.Read(dtdWorkbook));

            var truncatedWorkbook = Path.Combine(root, "truncated.xlsx");
            File.WriteAllBytes(truncatedWorkbook, [0x50, 0x4B, 0x03, 0x04, 0x00]);
            var truncatedWorkbookHash = Sha256File(truncatedWorkbook);
            AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxReader.Read(truncatedWorkbook));
            Assert(Sha256File(truncatedWorkbook) == truncatedWorkbookHash,
                "Rejected truncated workbook was modified.");

            var largeWorkbook = Path.Combine(root, "history-5000.xlsx");
            var largeRows = Enumerable.Range(0, 5_000).Select(index => new RimWorldTranslatorRmkHistoryRow
            {
                Identifier = $"ThingDef+Synthetic{index:D4}.label",
                ClassName = "ThingDef",
                Key = $"Synthetic{index:D4}.label",
                RequiredMods = index % 3 == 0 ? "dependency.mod" : string.Empty,
                Source = $"source {index:D4} {{0}}",
                Translation = $"번역 {index:D4} {{0}}"
            }).ToArray();
            RimWorldTranslatorRmkXlsxWriter.Write(largeWorkbook, largeRows, "English");
            var largeRoundTrip = RimWorldTranslatorRmkXlsxReader.Read(largeWorkbook);
            Assert(largeRoundTrip.Rows.Count == 5_000
                   && largeRoundTrip.Map["ThingDef+Synthetic4999.label"].Translation == "번역 4999 {0}",
                "5,000-row RMK functional round trip failed.");
        });
    }

    private static void AddRmkPreservationSentinels(string workbook)
    {
        RewriteWorksheet(workbook, document =>
        {
            XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var root = document.Root ?? throw new InvalidDataException("Synthetic worksheet root is missing.");
            root.SetAttributeValue(XNamespace.Xmlns + "r", relationships.NamespaceName);
            var rows = root.Descendants().Where(node => node.Name.LocalName == "row").ToArray();
            root.AddFirst(new XComment("WORKSHEET-COMMENT-SENTINEL"));
            root.AddFirst(new XProcessingInstruction(
                "phase07-preserve",
                "marker=\"WORKSHEET-PI-SENTINEL\""));
            var identifierCell = rows.Single(row => (string?)row.Attribute("r") == "2")
                .Elements()
                .Single(node => (string?)node.Attribute("r") == "A2");
            identifierCell.RemoveNodes();
            identifierCell.Add(
                new XElement(spreadsheet + "is",
                    new XElement(spreadsheet + "r",
                        new XAttribute("data-sentinel", "RICH-RUN-SENTINEL"),
                        new XElement(spreadsheet + "rPr", new XElement(spreadsheet + "b")),
                        new XElement(spreadsheet + "t", "ThingDef+")),
                    new XElement(spreadsheet + "r",
                        new XElement(spreadsheet + "t", "Fixture.label")),
                    new XElement(spreadsheet + "rPh",
                        new XAttribute("sb", "0"),
                        new XAttribute("eb", "8"),
                        new XElement(spreadsheet + "t", "PHONETIC-METADATA-SENTINEL")),
                    new XElement(spreadsheet + "phoneticPr",
                        new XAttribute("fontId", "0"),
                        new XAttribute("type", "noConversion"))),
                new XElement(spreadsheet + "extLst",
                    new XElement(spreadsheet + "ext", new XAttribute("uri", "CELL-METADATA-SENTINEL"))));
            AddInlineCell(rows.Single(row => (string?)row.Attribute("r") == "1"), "G1", "Extra Column");
            AddInlineCell(rows.Single(row => (string?)row.Attribute("r") == "2"), "G2", "EXTRA-SENTINEL");
            foreach (var cell in rows[1].Elements().Where(node => (string?)node.Attribute("r") is "E2" or "F2"))
                cell.SetAttributeValue("s", "7");
            root.Add(new XElement(spreadsheet + "legacyDrawing", new XAttribute(relationships + "id", "rIdVml")));
        });
        using var archive = ZipFile.Open(workbook, ZipArchiveMode.Update);
        ReplaceZipEntry(archive, "xl/workbook.xml", content => content.Replace(
            "<sheets>",
            "<sheets><sheet name=\"Metadata\" sheetId=\"2\" r:id=\"rIdMetadata\"/>",
            StringComparison.Ordinal));
        ReplaceZipEntry(archive, "[Content_Types].xml", content => content.Replace(
            "</Types>",
            "<Default Extension=\"vml\" ContentType=\"application/vnd.openxmlformats-officedocument.vmlDrawing\"/>"
            + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
            + "<Override PartName=\"/xl/comments1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml\"/>"
            + "<Override PartName=\"/xl/worksheets/metadata.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
            + "</Types>",
            StringComparison.Ordinal));
        ReplaceZipEntry(archive, "xl/_rels/workbook.xml.rels", content => content.Replace(
            "</Relationships>",
            "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
            + "<Relationship Id=\"rIdMetadata\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/metadata.xml\"/>"
            + "</Relationships>",
            StringComparison.Ordinal));
        AddZipEntry(
            archive,
            "xl/worksheets/metadata.xml",
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>METADATA-SHEET-SENTINEL</t></is></c></row></sheetData></worksheet>");
        AddZipEntry(
            archive,
            "xl/styles.xml",
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + "<fonts count=\"1\"><font/></fonts><fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>"
            + "<borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"8\">"
            + string.Concat(Enumerable.Repeat("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>", 8))
            + "</cellXfs></styleSheet>");
        AddZipEntry(archive, "xl/comments1.xml", "<comments xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><authors><author>synthetic</author></authors><commentList><comment ref=\"E2\" authorId=\"0\"><text><t>COMMENT-SENTINEL</t></text></comment></commentList></comments>");
        AddZipEntry(archive, "xl/drawings/vmlDrawing1.vml", "<xml xmlns:v=\"urn:schemas-microsoft-com:vml\"><v:shape id=\"VML-SENTINEL\"/></xml>");
        AddZipEntry(archive, "xl/worksheets/_rels/sheet1.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdComment\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments\" Target=\"../comments1.xml\"/><Relationship Id=\"rIdVml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/vmlDrawing\" Target=\"../drawings/vmlDrawing1.vml\"/></Relationships>");
        AddZipEntry(archive, "customXml/item1.xml", "<metadata><sentinel>CUSTOM-METADATA</sentinel></metadata>");
    }

    private static void VerifyRmkPreservationSentinels(string workbook)
    {
        using var archive = ZipFile.OpenRead(workbook);
        var worksheet = ReadZipEntry(archive, "xl/worksheets/sheet1.xml");
        Assert(worksheet.Contains("EXTRA-SENTINEL", StringComparison.Ordinal)
               && worksheet.Contains("legacyDrawing", StringComparison.Ordinal)
               && worksheet.Contains("s=\"7\"", StringComparison.Ordinal)
               && worksheet.Contains("RICH-RUN-SENTINEL", StringComparison.Ordinal)
               && worksheet.Contains("PHONETIC-METADATA-SENTINEL", StringComparison.Ordinal)
               && worksheet.Contains("CELL-METADATA-SENTINEL", StringComparison.Ordinal)
               && worksheet.Contains("WORKSHEET-COMMENT-SENTINEL", StringComparison.Ordinal)
               && worksheet.Contains("WORKSHEET-PI-SENTINEL", StringComparison.Ordinal),
            "RMK worksheet extra column, rich text, metadata, comment/PI, relationship, or style reference was lost.");
        Assert(ReadZipEntry(archive, "xl/comments1.xml").Contains("COMMENT-SENTINEL", StringComparison.Ordinal)
               && ReadZipEntry(archive, "xl/drawings/vmlDrawing1.vml").Contains("VML-SENTINEL", StringComparison.Ordinal)
               && ReadZipEntry(archive, "customXml/item1.xml").Contains("CUSTOM-METADATA", StringComparison.Ordinal),
            "RMK comments, VML, or custom metadata package parts were lost.");
    }

    private static void AddInlineCell(XElement row, string reference, string value)
    {
        var ns = row.Name.Namespace;
        var text = new XElement(ns + "t", value);
        text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        row.Add(new XElement(ns + "c",
            new XAttribute("r", reference),
            new XAttribute("t", "inlineStr"),
            new XElement(ns + "is", text)));
    }

    private static void RewriteWorksheet(string workbook, Action<XDocument> edit) =>
        RewriteWorksheetRaw(workbook, xml =>
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            edit(document);
            var marker = "PHASE05CR" + Guid.NewGuid().ToString("N");
            var changed = document.DescendantNodes().OfType<XText>()
                .Where(text => text.Value.Contains('\r'))
                .Select(text => (Node: text, Original: text.Value))
                .ToArray();
            foreach (var pair in changed)
                pair.Node.Value = pair.Original.Replace("\r", marker, StringComparison.Ordinal);
            try
            {
                using var writer = new Utf8StringWriter();
                document.Save(writer, SaveOptions.DisableFormatting);
                return writer.ToString().Replace(marker, "&#xD;", StringComparison.Ordinal);
            }
            finally
            {
                foreach (var pair in changed) pair.Node.Value = pair.Original;
            }
        });

    private static void RewriteWorksheetRaw(string workbook, Func<string, string> edit)
    {
        using var archive = ZipFile.Open(workbook, ZipArchiveMode.Update);
        const string name = "xl/worksheets/sheet1.xml";
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("Synthetic worksheet entry is missing.");
        string xml;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, leaveOpen: false)) xml = reader.ReadToEnd();
        entry.Delete();
        AddZipEntry(archive, name, edit(xml));
    }

    private static void AddZipEntry(ZipArchive archive, string name, string content)
    {
        archive.GetEntry(name)?.Delete();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void ReplaceZipEntry(ZipArchive archive, string name, Func<string, string> transform)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Synthetic package entry is missing: {name}");
        string content;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, leaveOpen: false))
            content = reader.ReadToEnd();
        entry.Delete();
        AddZipEntry(archive, name, transform(content));
    }

    private static Dictionary<string, string> ZipEntryHashes(string workbook)
    {
        using var archive = ZipFile.OpenRead(workbook);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var stream = entry.Open();
                return Convert.ToHexString(SHA256.HashData(stream));
            },
            StringComparer.Ordinal);
    }

    private static string ReadZipEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Synthetic package entry is missing: {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
