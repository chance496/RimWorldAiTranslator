using System.IO.Compression;
using System.Text;
using System.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static readonly string[] Phase07ValidationFormulaNames = ["formula1", "formula2"];
    private static readonly string[] Phase07TableFormulaNames = ["calculatedColumnFormula", "totalsRowFormula"];
    private static readonly char[] Phase07ExcelDataColumns = ['A', 'B', 'C', 'D', 'E', 'F'];

    private static void Phase07NativeActiveContentAndLimits()
    {
        WithTempRoot(root =>
        {
            var validWorkbook = Path.Combine(root, "native-content-baseline.xlsx");
            var baselineRow = new RimWorldTranslatorRmkHistoryRow
            {
                Identifier = "Keyed+Phase07.NativeContent",
                ClassName = "Keyed",
                Key = "Phase07.NativeContent",
                Source = "Synthetic source",
                Translation = "Synthetic translation"
            };
            RimWorldTranslatorRmkXlsxWriter.Write(
                validWorkbook,
                [baselineRow],
                "English");

            var secondaryExternalRelationship = CopyWorkbook(
                validWorkbook,
                root,
                "secondary-external-relationship.xlsx");
            using (var archive = ZipFile.Open(secondaryExternalRelationship, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry(
                    "xl/worksheets/_rels/sheet1.xml.rels",
                    CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="phase07" Type="https://outside.invalid/type" Target="https://outside.invalid/resource" TargetMode="External" />
                    </Relationships>
                    """);
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(secondaryExternalRelationship));

            var escapingRelationship = CopyWorkbook(validWorkbook, root, "escaping-relationship.xlsx");
            using (var archive = ZipFile.Open(escapingRelationship, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry(
                    "xl/worksheets/_rels/sheet1.xml.rels",
                    CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="phase07" Type="https://outside.invalid/type" Target="../../../outside.xml" />
                    </Relationships>
                    """);
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(escapingRelationship));

            var activePartWorkbook = CopyWorkbook(validWorkbook, root, "active-part.xlsx");
            using (var archive = ZipFile.Open(activePartWorkbook, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry("xl/connections.xml", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write("<connections xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" />");
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(activePartWorkbook));

            var activeRelationshipWorkbook = CopyWorkbook(validWorkbook, root, "active-relationship.xlsx");
            using (var archive = ZipFile.Open(activeRelationshipWorkbook, ZipArchiveMode.Update))
            {
                var relation = archive.CreateEntry(
                    "xl/worksheets/_rels/sheet1.xml.rels",
                    CompressionLevel.Optimal);
                using (var writer = new StreamWriter(relation.Open(), new UTF8Encoding(false), leaveOpen: false))
                {
                    writer.Write("""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                          <Relationship Id="phase07" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject" Target="../custom/phase07.dat" />
                        </Relationships>
                        """);
                }
                var payload = archive.CreateEntry("xl/custom/phase07.dat", CompressionLevel.NoCompression);
                using var payloadWriter = new StreamWriter(payload.Open(), new UTF8Encoding(false));
                payloadWriter.Write("synthetic inactive-looking payload");
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(activeRelationshipWorkbook));

            var activeContentTypeWorkbook = CopyWorkbook(validWorkbook, root, "active-content-type.xlsx");
            using (var archive = ZipFile.Open(activeContentTypeWorkbook, ZipArchiveMode.Update))
            {
                var payload = archive.CreateEntry("xl/custom/phase07.dat", CompressionLevel.NoCompression);
                using (var payloadWriter = new StreamWriter(payload.Open(), new UTF8Encoding(false), leaveOpen: false))
                    payloadWriter.Write("synthetic inactive-looking payload");
                ReplaceZipEntry(archive, "[Content_Types].xml", content => content.Replace(
                    "</Types>",
                    "<Override PartName=\"/xl/custom/phase07.dat\" ContentType=\"application/vnd.ms-office.vbaProject\" /></Types>",
                    StringComparison.Ordinal));
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(activeContentTypeWorkbook));

            var formulaWorkbook = CopyWorkbook(validWorkbook, root, "formula.xlsx");
            using (var archive = ZipFile.Open(formulaWorkbook, ZipArchiveMode.Update))
            {
                ReplaceZipEntry(archive, "xl/worksheets/sheet1.xml", content => content.Replace(
                    "<sheetData>",
                    "<sheetData><row r=\"100\"><c r=\"A100\"><f>WEBSERVICE(&quot;https://outside.invalid/&quot;)</f></c></row>",
                    StringComparison.Ordinal));
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(formulaWorkbook));
            AssertPhase07ActiveWorkbookRejectedAndPreserved(formulaWorkbook, "worksheet f");

            var relocatedWorksheet = CopyWorkbook(validWorkbook, root, "relocated-worksheet.xlsx");
            using (var archive = ZipFile.Open(relocatedWorksheet, ZipArchiveMode.Update))
            {
                const string originalName = "xl/worksheets/sheet1.xml";
                const string relocatedName = "xl/relocated/translation-sheet.data";
                var original = archive.GetEntry(originalName)
                    ?? throw new InvalidDataException("Synthetic worksheet entry is missing.");
                string worksheet;
                using (var reader = new StreamReader(original.Open(), Encoding.UTF8, true, leaveOpen: false))
                    worksheet = reader.ReadToEnd();
                original.Delete();
                var relocated = archive.CreateEntry(relocatedName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(relocated.Open(), new UTF8Encoding(false), leaveOpen: false))
                    writer.Write(worksheet);
                ReplaceZipEntry(archive, "[Content_Types].xml", content => content.Replace(
                    "/xl/worksheets/sheet1.xml",
                    "/xl/relocated/translation-sheet.data",
                    StringComparison.Ordinal));
                ReplaceZipEntry(archive, "xl/_rels/workbook.xml.rels", content => content.Replace(
                    "worksheets/sheet1.xml",
                    "relocated/translation-sheet.data",
                    StringComparison.Ordinal));
            }
            Assert(RimWorldTranslatorRmkXlsxReader.Read(relocatedWorksheet).Rows.Count == 1,
                "A safe worksheet relocated through OPC metadata was not readable.");

            var relocatedFormula = CopyWorkbook(relocatedWorksheet, root, "relocated-formula.xlsx");
            using (var archive = ZipFile.Open(relocatedFormula, ZipArchiveMode.Update))
            {
                ReplaceZipEntry(archive, "xl/relocated/translation-sheet.data", content => content.Replace(
                    "<sheetData>",
                    "<sheetData><row r=\"100\"><c r=\"A100\"><f>WEBSERVICE(&quot;https://outside.invalid/relocated&quot;)</f></c></row>",
                    StringComparison.Ordinal));
                ReplaceZipEntry(archive, "[Content_Types].xml", content => content.Replace(
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml",
                    "application/octet-stream",
                    StringComparison.Ordinal));
            }
            AssertPhase07ActiveWorkbookRejectedAndPreserved(
                relocatedFormula,
                "formula in a relationship-only relocated worksheet");

            string CreateTransformedWorkbook(
                string fileName,
                string entryName,
                string marker,
                string replacement)
            {
                var path = CopyWorkbook(validWorkbook, root, fileName);
                using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
                ReplaceZipEntry(archive, entryName, content => content.Replace(
                    marker,
                    replacement,
                    StringComparison.Ordinal));
                return path;
            }

            string CreateWorkbookWithPart(string fileName, string entryName, string content)
            {
                var path = CopyWorkbook(validWorkbook, root, fileName);
                using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
                return path;
            }

            var conditionalFormula = CreateTransformedWorkbook(
                "conditional-formula.xlsx",
                "xl/worksheets/sheet1.xml",
                "</sheetData>",
                "</sheetData><conditionalFormatting sqref=\"A1\"><cfRule type=\"expression\" priority=\"1\"><formula>WEBSERVICE(&quot;https://outside.invalid/conditional&quot;)</formula></cfRule></conditionalFormatting>");
            AssertPhase07ActiveWorkbookRejectedAndPreserved(conditionalFormula, "conditional-format formula");

            var contentTypeFormula = CreateWorkbookWithPart(
                "content-type-only-formula.xlsx",
                "custom/phase07-sheet.data",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\"><f>WEBSERVICE(&quot;https://outside.invalid/content-type&quot;)</f></c></row></sheetData></worksheet>");
            using (var archive = ZipFile.Open(contentTypeFormula, ZipArchiveMode.Update))
            {
                ReplaceZipEntry(archive, "[Content_Types].xml", content => content.Replace(
                    "</Types>",
                    "<Override PartName=\"/custom/phase07-sheet.data\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\" /></Types>",
                    StringComparison.Ordinal));
            }
            AssertPhase07ActiveWorkbookRejectedAndPreserved(
                contentTypeFormula,
                "formula in a content-type-only SpreadsheetML part");

            foreach (var formulaName in Phase07ValidationFormulaNames)
            {
                var validationFormula = CreateTransformedWorkbook(
                    $"data-validation-{formulaName}.xlsx",
                    "xl/worksheets/sheet1.xml",
                    "</sheetData>",
                    $"</sheetData><dataValidations count=\"1\"><dataValidation type=\"custom\" sqref=\"A1\"><{formulaName}>WEBSERVICE(&quot;https://outside.invalid/{formulaName}&quot;)</{formulaName}></dataValidation></dataValidations>");
                AssertPhase07ActiveWorkbookRejectedAndPreserved(validationFormula, formulaName);
            }

            var definedNameFormula = CreateTransformedWorkbook(
                "defined-name-formula.xlsx",
                "xl/workbook.xml",
                "</workbook>",
                "<definedNames><definedName name=\"Phase07ActiveName\">WEBSERVICE(&quot;https://outside.invalid/name&quot;)</definedName></definedNames></workbook>");
            AssertPhase07ActiveWorkbookRejectedAndPreserved(definedNameFormula, "definedName");

            foreach (var tableFormulaName in Phase07TableFormulaNames)
            {
                var tableFormula = CreateWorkbookWithPart(
                    $"table-{tableFormulaName}.xlsx",
                    $"xl/tables/phase07-{tableFormulaName}.xml",
                    $"<table xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" id=\"1\" name=\"Phase07\" displayName=\"Phase07\" ref=\"A1:A2\"><tableColumns count=\"1\"><tableColumn id=\"1\" name=\"Value\"><{tableFormulaName}>WEBSERVICE(&quot;https://outside.invalid/{tableFormulaName}&quot;)</{tableFormulaName}></tableColumn></tableColumns></table>");
                AssertPhase07ActiveWorkbookRejectedAndPreserved(tableFormula, tableFormulaName);
            }

            var vmlFormula = CreateWorkbookWithPart(
                "vml-formula.xlsx",
                "xl/drawings/phase07.vml",
                "<xml xmlns:x=\"urn:schemas-microsoft-com:office:excel\"><x:FmlaMacro>[0]!Phase07</x:FmlaMacro></xml>");
            AssertPhase07ActiveWorkbookRejectedAndPreserved(vmlFormula, "VML formula markup");

            var vmlFormulaAttribute = CreateWorkbookWithPart(
                "vml-formula-attribute.xlsx",
                "xl/drawings/phase07-attribute.vml",
                "<xml xmlns:v=\"urn:schemas-microsoft-com:vml\"><v:shape fmla=\"WEBSERVICE('https://outside.invalid/vml-attribute')\" /></xml>");
            AssertPhase07ActiveWorkbookRejectedAndPreserved(vmlFormulaAttribute, "VML formula attribute");

            var vmlExternalReference = CreateWorkbookWithPart(
                "vml-external-reference.xlsx",
                "xl/drawings/phase07-external.vml",
                "<xml xmlns:v=\"urn:schemas-microsoft-com:vml\"><v:shape href=\"https://outside.invalid/vml\" /></xml>");
            AssertPhase07ActiveWorkbookRejectedAndPreserved(vmlExternalReference, "VML external reference");

            var customXmlDtd = CreateWorkbookWithPart(
                "custom-xml-dtd.xlsx",
                "customXml/phase07.data",
                "<!DOCTYPE metadata [<!ENTITY phase07 \"blocked\">]><metadata>&phase07;</metadata>");
            using (var archive = ZipFile.Open(customXmlDtd, ZipArchiveMode.Update))
            {
                ReplaceZipEntry(archive, "[Content_Types].xml", content => content.Replace(
                    "</Types>",
                    "<Override PartName=\"/customXml/phase07.data\" ContentType=\"application/xml\" /></Types>",
                    StringComparison.Ordinal));
            }
            AssertThrows<XmlException>(() => RimWorldTranslatorRmkXlsxReader.Read(customXmlDtd));
            var customXmlDtdHash = Sha256File(customXmlDtd);
            AssertThrows<XmlException>(() => RimWorldTranslatorRmkXlsxWriter.Write(
                customXmlDtd,
                [baselineRow],
                "English"));
            Assert(Sha256File(customXmlDtd) == customXmlDtdHash
                   && !Directory.EnumerateFiles(root, "custom-xml-dtd.xlsx.tmp-*", SearchOption.TopDirectoryOnly).Any(),
                "Rejected custom XML DTD update changed or leaked the workbook.");

            var customXmlStylesheet = CreateWorkbookWithPart(
                "custom-xml-stylesheet.xlsx",
                "customXml/phase07-stylesheet.xml",
                "<?xml-stylesheet type=\"text/xsl\" href=\"https://outside.invalid/phase07.xsl\"?><metadata />");
            AssertPhase07ActiveWorkbookRejectedAndPreserved(
                customXmlStylesheet,
                "custom XML external stylesheet processing instruction");

            var customXmlFormulaMetadata = CreateWorkbookWithPart(
                "custom-xml-formula-metadata.xlsx",
                "customXml/phase07-formula.xml",
                "<metadata><formula>SAFE-METADATA-SENTINEL</formula></metadata>");
            Assert(RimWorldTranslatorRmkXlsxReader.Read(customXmlFormulaMetadata).Rows.Count == 1,
                "Passive custom XML metadata was rejected only because an element was named formula.");

            foreach (var (fileName, relationshipToken) in new[]
                     {
                         ("relocated-xl-macro-sheet.xlsx", "xlMacrosheet"),
                         ("relocated-xl-intl-macro-sheet.xlsx", "xlIntlMacrosheet"),
                         ("relocated-dialog-sheet.xlsx", "dialogsheet"),
                         ("relocated-xl-dialog-sheet.xlsx", "xlDialogsheet")
                     })
            {
                var relocated = CopyWorkbook(validWorkbook, root, fileName);
                using (var archive = ZipFile.Open(relocated, ZipArchiveMode.Update))
                {
                    var partName = $"xl/custom/{relationshipToken}.xml";
                    var part = archive.CreateEntry(partName, CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(part.Open(), new UTF8Encoding(false), leaveOpen: false))
                        writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData /></worksheet>");
                    ReplaceZipEntry(archive, "xl/_rels/workbook.xml.rels", content => content.Replace(
                        "</Relationships>",
                        $"<Relationship Id=\"phase07{relationshipToken}\" Type=\"http://schemas.microsoft.com/office/2006/relationships/{relationshipToken}\" Target=\"custom/{relationshipToken}.xml\" /></Relationships>",
                        StringComparison.Ordinal));
                }
                AssertPhase07ActiveWorkbookRejectedAndPreserved(relocated, relationshipToken + " relationship");
            }

            foreach (var (fileName, contentType) in new[]
                     {
                         ("relocated-macro-content-type.xlsx", "application/vnd.ms-excel.macrosheet+xml"),
                         ("relocated-intl-macro-content-type.xlsx", "application/vnd.ms-excel.intlmacrosheet+xml"),
                         ("relocated-dialog-content-type.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.dialogsheet+xml")
                     })
            {
                var relocated = CopyWorkbook(validWorkbook, root, fileName);
                using (var archive = ZipFile.Open(relocated, ZipArchiveMode.Update))
                {
                    var partName = "xl/custom/" + Path.GetFileNameWithoutExtension(fileName) + ".xml";
                    var part = archive.CreateEntry(partName, CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(part.Open(), new UTF8Encoding(false), leaveOpen: false))
                        writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData /></worksheet>");
                    ReplaceZipEntry(archive, "[Content_Types].xml", content => content.Replace(
                        "</Types>",
                        $"<Override PartName=\"/{partName}\" ContentType=\"{contentType}\" /></Types>",
                        StringComparison.Ordinal));
                }
                AssertPhase07ActiveWorkbookRejectedAndPreserved(relocated, contentType);
            }

            var oversizedOutput = Path.Combine(root, "oversized-output.xlsx");
            AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxWriter.Write(
                oversizedOutput,
                [
                    new RimWorldTranslatorRmkHistoryRow
                    {
                        Identifier = "Keyed+Phase07.Oversized",
                        ClassName = "Keyed",
                        Key = "Phase07.Oversized",
                        Source = new string('S', 32_768),
                        Translation = "Synthetic translation"
                    }
                ],
                "English"));
            Assert(!File.Exists(oversizedOutput),
                "Rejected oversized RMK output left a final workbook behind.");
            Assert(!Directory.EnumerateFiles(root, "oversized-output.xlsx.tmp-*", SearchOption.TopDirectoryOnly).Any(),
                "Rejected oversized RMK output left a temporary workbook behind.");

            string MoveBaselineDataRow(string fileName, int rowNumber)
            {
                var path = CopyWorkbook(validWorkbook, root, fileName);
                using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
                ReplaceZipEntry(archive, "xl/worksheets/sheet1.xml", content =>
                {
                    var moved = content.Replace(
                        "<row r=\"2\"",
                        $"<row r=\"{rowNumber}\"",
                        StringComparison.Ordinal);
                    foreach (var column in Phase07ExcelDataColumns)
                    {
                        moved = moved.Replace(
                            $"r=\"{column}2\"",
                            $"r=\"{column}{rowNumber}\"",
                            StringComparison.Ordinal);
                    }
                    return moved;
                });
                return path;
            }

            var outOfRangeRow = MoveBaselineDataRow("row-1048577.xlsx", 1_048_577);
            AssertPhase07ActiveWorkbookRejectedAndPreserved(outOfRangeRow, "out-of-range Excel row");

            var lastExcelRow = MoveBaselineDataRow("row-1048576.xlsx", 1_048_576);
            Assert(RimWorldTranslatorRmkXlsxReader.Read(lastExcelRow).Rows.Count == 1,
                "Excel's last supported row was rejected.");
            var lastExcelRowHash = Sha256File(lastExcelRow);
            AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxWriter.Write(
                lastExcelRow,
                [baselineRow, new RimWorldTranslatorRmkHistoryRow
                {
                    Identifier = "Keyed+Phase07.AppendOverflow",
                    ClassName = "Keyed",
                    Key = "Phase07.AppendOverflow",
                    Source = "Synthetic source",
                    Translation = "Synthetic translation"
                }],
                "English"));
            Assert(Sha256File(lastExcelRow) == lastExcelRowHash
                   && !Directory.EnumerateFiles(root, "row-1048576.xlsx.tmp-*", SearchOption.TopDirectoryOnly).Any(),
                "Rejected append beyond Excel's row range changed or leaked the workbook.");

            void AssertRejectedWriterBudgetClean(
                string fileName,
                IEnumerable<RimWorldTranslatorRmkHistoryRow> rows,
                string language)
            {
                var path = Path.Combine(root, fileName);
                AssertThrows<InvalidDataException>(() =>
                    RimWorldTranslatorRmkXlsxWriter.Write(path, rows, language));
                Assert(!File.Exists(path)
                       && !Directory.EnumerateFiles(root, fileName + ".tmp-*", SearchOption.TopDirectoryOnly).Any(),
                    $"Rejected writer budget left output artifacts: {fileName}");
            }

            AssertRejectedWriterBudgetClean(
                "source-language-limit.xlsx",
                [baselineRow],
                new string('L', 257));

            var aggregatePayload = new string('P', 32_700);
            var aggregateRows = Enumerable.Range(0, 110).Select(index => new RimWorldTranslatorRmkHistoryRow
            {
                Identifier = $"Keyed+Phase07.Aggregate{index:D3}",
                ClassName = aggregatePayload,
                Key = aggregatePayload,
                RequiredMods = aggregatePayload,
                Source = aggregatePayload,
                Translation = aggregatePayload
            }).ToArray();
            AssertRejectedWriterBudgetClean("aggregate-character-limit.xlsx", aggregateRows, "English");

            var expansionPayload = new string('&', 32_000);
            var expansionRows = Enumerable.Range(0, 85).Select(index => new RimWorldTranslatorRmkHistoryRow
            {
                Identifier = $"Keyed+Phase07.Expansion{index:D3}",
                ClassName = expansionPayload,
                Key = expansionPayload,
                RequiredMods = expansionPayload,
                Source = expansionPayload,
                Translation = expansionPayload
            }).ToArray();
            AssertRejectedWriterBudgetClean("estimated-size-limit.xlsx", expansionRows, "English");
        });

        WithTempRoot(root =>
        {
            var rawEntryLimitPath = Path.Combine(root, "raw-entry-limit.xml");
            var rawEntries = new StringBuilder(1_500_000);
            rawEntries.Append("<LanguageData>");
            for (var index = 0; index < 250_001; index++) rawEntries.Append("<p/>");
            rawEntries.Append("</LanguageData>");
            File.WriteAllText(rawEntryLimitPath, rawEntries.ToString(), new UTF8Encoding(false));
            var rawEntryError = CaptureException<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.ReadLanguageData(rawEntryLimitPath));
            Assert(rawEntryError.Message.Contains("source-entry", StringComparison.OrdinalIgnoreCase),
                "The native source XML raw-entry limit was not reached directly.");

            var nodeLimitPath = Path.Combine(root, "source-node-limit.xml");
            var nodes = new StringBuilder(3_000_000);
            nodes.Append("<LanguageData>");
            for (var index = 0; index < 500_001; index++) nodes.Append("<n/>");
            nodes.Append("</LanguageData>");
            File.WriteAllText(nodeLimitPath, nodes.ToString(), new UTF8Encoding(false));
            var nodeError = CaptureException<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.ReadLanguageData(nodeLimitPath));
            Assert(nodeError.Message.Contains("node-count", StringComparison.OrdinalIgnoreCase),
                "The native source XML node-count limit was not reached directly.");

            var textLimitPath = Path.Combine(root, "source-text-limit.xml");
            File.WriteAllText(
                textLimitPath,
                "<LanguageData><Phase07.Text>" + new string('T', 1_048_577) + "</Phase07.Text></LanguageData>",
                new UTF8Encoding(false));
            var textError = CaptureException<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.ReadLanguageData(textLimitPath));
            Assert(textError.Message.Contains("text", StringComparison.OrdinalIgnoreCase),
                "The native source XML per-value text limit was not reached directly.");
        });
    }

    private static void AssertPhase07ActiveWorkbookRejectedAndPreserved(
        string workbookPath,
        string activeSurface)
    {
        AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxReader.Read(workbookPath));
        var original = File.ReadAllBytes(workbookPath);
        AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxWriter.Write(
            workbookPath,
            [
                new RimWorldTranslatorRmkHistoryRow
                {
                    Identifier = "Keyed+Phase07.NativeContent",
                    ClassName = "Keyed",
                    Key = "Phase07.NativeContent",
                    Source = "Replacement source",
                    Translation = "Replacement translation"
                }
            ],
            "English"));
        Assert(File.ReadAllBytes(workbookPath).SequenceEqual(original),
            $"Rejected {activeSurface} RMK update changed the original workbook.");
        var directory = Path.GetDirectoryName(workbookPath)
            ?? throw new InvalidOperationException("The active-content workbook has no parent directory.");
        Assert(!Directory.EnumerateFiles(
                directory,
                Path.GetFileName(workbookPath) + ".tmp-*",
                SearchOption.TopDirectoryOnly).Any(),
            $"Rejected {activeSurface} RMK update left a temporary workbook behind.");
    }
}
