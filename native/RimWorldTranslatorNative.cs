using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

public static class RimWorldTranslatorNativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
}

public sealed class RimWorldTranslatorRmkHistoryRow
{
    public string Identifier { get; set; }
    public string ClassName { get; set; }
    public string Key { get; set; }
    public string RequiredMods { get; set; }
    public string Source { get; set; }
    public string Translation { get; set; }
}

public sealed class RimWorldTranslatorRmkHistoryData
{
    public string SourceLanguage { get; set; }
    public Dictionary<string, RimWorldTranslatorRmkHistoryRow> Map { get; private set; }
    public List<RimWorldTranslatorRmkHistoryRow> Rows { get; private set; }

    public RimWorldTranslatorRmkHistoryData()
    {
        SourceLanguage = String.Empty;
        Map = new Dictionary<string, RimWorldTranslatorRmkHistoryRow>(StringComparer.OrdinalIgnoreCase);
        Rows = new List<RimWorldTranslatorRmkHistoryRow>();
    }
}

public sealed class RimWorldTranslatorRawSourceEntry
{
    public string Key { get; set; }
    public string Text { get; set; }
    public string TypeName { get; set; }
    public string Field { get; set; }
}

public static class RimWorldTranslatorRmkXlsxReader
{
    private const long XmlFileLimit = 134217728;
    private const long WorkbookXmlLimit = 16777216;
    private const long SharedStringsLimit = 268435456;
    private const long WorksheetLimit = 536870912;

    private static XmlReaderSettings CreateReaderSettings(long maximumLength)
    {
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.DtdProcessing = DtdProcessing.Prohibit;
        settings.XmlResolver = null;
        settings.MaxCharactersFromEntities = 1024;
        settings.MaxCharactersInDocument = maximumLength;
        settings.IgnoreComments = true;
        settings.IgnoreProcessingInstructions = true;
        return settings;
    }

    private static XDocument LoadXml(ZipArchiveEntry entry, long maximumLength)
    {
        if (entry == null) return null;
        if (entry.Length > maximumLength)
            throw new InvalidDataException("XLSX XML entry is too large: " + entry.FullName);

        using (Stream stream = entry.Open())
        using (XmlReader reader = XmlReader.Create(stream, CreateReaderSettings(maximumLength)))
        {
            return XDocument.Load(reader, LoadOptions.None);
        }
    }

    private static XDocument LoadXmlFile(string path, long maximumLength)
    {
        FileInfo info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("XML file was not found.", path);
        if (info.Length > maximumLength)
            throw new InvalidDataException("XML file is too large: " + path);

        using (XmlReader reader = XmlReader.Create(path, CreateReaderSettings(maximumLength)))
        {
            return XDocument.Load(reader, LoadOptions.None);
        }
    }

    private static XmlReader OpenEntryReader(ZipArchiveEntry entry, long maximumLength)
    {
        if (entry == null) return null;
        if (entry.Length > maximumLength)
            throw new InvalidDataException("XLSX XML entry is too large: " + entry.FullName);
        return XmlReader.Create(entry.Open(), CreateReaderSettings(maximumLength));
    }

    private static bool ValidKeySegment(string value)
    {
        return !String.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), "^[A-Za-z_][A-Za-z0-9_.-]*$");
    }

    private static string DirectChildText(XElement node, string name)
    {
        foreach (XElement child in node.Elements())
        {
            if (child.Name.LocalName == name) return child.Value.Trim();
        }
        return String.Empty;
    }

    private static string ListItemSegment(XElement node, int index)
    {
        string[] names = new string[] { "id", "defName", "key", "name" };
        foreach (string name in names)
        {
            string value = DirectChildText(node, name);
            if (ValidKeySegment(value)) return value.Trim();
        }
        return index.ToString(CultureInfo.InvariantCulture);
    }

    private static bool ExcludedDefPath(IList<string> path)
    {
        string full = String.Join(".", path.ToArray()).ToLowerInvariant();
        string[] excluded = new string[] {
            "defname", "parentname", "classname", "thingclass", "workerclass",
            "compclass", "hediffclass", "thoughtclass", "abilityclass",
            "texpath", "texname", "graphicpath", "shader", "sound", "iconpath",
            "modextension", "li.class", "packageid", "xpath", "operation"
        };
        foreach (string item in excluded)
        {
            if (full == item ||
                full.StartsWith(item + ".", StringComparison.Ordinal) ||
                full.EndsWith("." + item, StringComparison.Ordinal) ||
                full.IndexOf("." + item + ".", StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }

    private static bool TranslatableDefPath(IList<string> path)
    {
        if (path.Count == 0) return false;
        string leaf = path[path.Count - 1].ToLowerInvariant();
        string full = String.Join(".", path.ToArray()).ToLowerInvariant();
        string[] exact = new string[] {
            "label", "labelshort", "description", "jobstring", "reportstring",
            "deathmessage", "deathmessagefemale", "deathmessagemale",
            "pawnsplural", "leadertitle", "arrivedletter", "customlabel",
            "gizmolabel", "gizmodescription", "commandlabel", "commanddescription",
            "letterlabel", "lettertext", "header", "headertip", "summary",
            "formatstring", "formatstringunfinalized", "fixedname", "reason"
        };
        if (Array.IndexOf(exact, leaf) >= 0) return true;
        if (leaf == "text" && Regex.IsMatch(full, "(letter|message|scenario|quest|dialog|help|tip|inspect)")) return true;
        if (leaf == "slateref" && Regex.IsMatch(full, "(letter|text|label|description|inspect|string)")) return true;
        if (leaf == "li" && Regex.IsMatch(full, "(rulesstrings|tagsstrings)")) return true;
        return false;
    }

    private static void AddDefLeaves(
        XElement node,
        List<string> path,
        string defName,
        string typeName,
        List<RimWorldTranslatorRawSourceEntry> entries)
    {
        List<XElement> children = node.Elements().ToList();
        if (children.Count == 0)
        {
            if (ExcludedDefPath(path) || !TranslatableDefPath(path)) return;
            entries.Add(new RimWorldTranslatorRawSourceEntry {
                Key = defName + "." + String.Join(".", path.ToArray()),
                Text = node.Value,
                TypeName = typeName,
                Field = path[path.Count - 1]
            });
            return;
        }

        Dictionary<string, int> listIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement child in children)
        {
            string name = child.Name.LocalName;
            string segment = name;
            if (name == "li")
            {
                string parent = path.Count > 0 ? path[path.Count - 1] : "li";
                int index;
                if (!listIndexes.TryGetValue(parent, out index)) index = 0;
                segment = ListItemSegment(child, index);
                listIndexes[parent] = index + 1;
            }
            List<string> childPath = new List<string>(path);
            childPath.Add(segment);
            AddDefLeaves(child, childPath, defName, typeName, entries);
        }
    }

    public static List<RimWorldTranslatorRawSourceEntry> ReadLanguageData(string path)
    {
        List<RimWorldTranslatorRawSourceEntry> entries = new List<RimWorldTranslatorRawSourceEntry>();
        XDocument document = LoadXmlFile(path, XmlFileLimit);
        XElement root = document.Root;
        if (root == null || root.Name.LocalName != "LanguageData") return entries;
        foreach (XElement child in root.Elements())
        {
            string key = child.Name.LocalName;
            int separator = key.LastIndexOf('.');
            entries.Add(new RimWorldTranslatorRawSourceEntry {
                Key = key,
                Text = child.Value,
                TypeName = String.Empty,
                Field = separator >= 0 ? key.Substring(separator + 1) : key
            });
        }
        return entries;
    }

    public static List<RimWorldTranslatorRawSourceEntry> ReadDefs(string path)
    {
        List<RimWorldTranslatorRawSourceEntry> entries = new List<RimWorldTranslatorRawSourceEntry>();
        XDocument document = LoadXmlFile(path, XmlFileLimit);
        XElement root = document.Root;
        if (root == null || root.Name.LocalName != "Defs") return entries;
        foreach (XElement def in root.Elements())
        {
            string defName = DirectChildText(def, "defName");
            if (!ValidKeySegment(defName)) continue;
            string typeName = def.Name.LocalName;
            foreach (XElement child in def.Elements())
            {
                if (child.Name.LocalName == "defName") continue;
                List<string> pathSegments = new List<string>();
                pathSegments.Add(child.Name.LocalName);
                AddDefLeaves(child, pathSegments, defName.Trim(), typeName, entries);
            }
        }
        return entries;
    }

    private static int ColumnIndex(string reference)
    {
        int value = 0;
        bool found = false;
        if (String.IsNullOrEmpty(reference)) return -1;
        foreach (char raw in reference)
        {
            char character = Char.ToUpperInvariant(raw);
            if (character < 'A' || character > 'Z') break;
            value = (value * 26) + (character - 'A' + 1);
            found = true;
        }
        return found ? value - 1 : -1;
    }

    private static string ReadTextElements(XmlReader parent, string elementName)
    {
        StringBuilder value = new StringBuilder();
        using (XmlReader subtree = parent.ReadSubtree())
        {
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == elementName)
                    value.Append(subtree.ReadElementContentAsString());
            }
        }
        return value.ToString();
    }

    private static string ReadCell(XmlReader cell, IList<string> sharedStrings)
    {
        string type = cell.GetAttribute("t") ?? String.Empty;
        string rawValue = String.Empty;
        StringBuilder inlineValue = null;
        using (XmlReader subtree = cell.ReadSubtree())
        {
            while (subtree.Read())
            {
                if (subtree.NodeType != XmlNodeType.Element) continue;
                if (subtree.LocalName == "v")
                {
                    rawValue = subtree.ReadElementContentAsString();
                }
                else if (subtree.LocalName == "t" && type == "inlineStr")
                {
                    if (inlineValue == null) inlineValue = new StringBuilder();
                    inlineValue.Append(subtree.ReadElementContentAsString());
                }
            }
        }

        if (type == "inlineStr") return inlineValue == null ? String.Empty : inlineValue.ToString();
        int index;
        if (type == "s" && Int32.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0 && index < sharedStrings.Count)
            return sharedStrings[index] ?? String.Empty;
        return rawValue;
    }

    private static Dictionary<int, string> ReadRow(XmlReader row, IList<string> sharedStrings)
    {
        Dictionary<int, string> values = new Dictionary<int, string>();
        using (XmlReader subtree = row.ReadSubtree())
        {
            while (subtree.Read())
            {
                if (subtree.NodeType != XmlNodeType.Element || subtree.LocalName != "c") continue;
                int index = ColumnIndex(subtree.GetAttribute("r"));
                if (index >= 0) values[index] = ReadCell(subtree, sharedStrings);
            }
        }
        return values;
    }

    private static string GetValue(Dictionary<int, string> values, int index)
    {
        string value;
        return index >= 0 && values.TryGetValue(index, out value) ? (value ?? String.Empty).Trim() : String.Empty;
    }

    internal static string FindWorksheetEntry(ZipArchive archive)
    {
        XDocument workbook = LoadXml(archive.GetEntry("xl/workbook.xml"), WorkbookXmlLimit);
        XDocument relationships = LoadXml(archive.GetEntry("xl/_rels/workbook.xml.rels"), WorkbookXmlLimit);
        if (workbook != null && relationships != null)
        {
            XElement sheet = workbook.Descendants().FirstOrDefault(delegate(XElement item) { return item.Name.LocalName == "sheet"; });
            if (sheet != null)
            {
                XAttribute relationAttribute = sheet.Attributes().FirstOrDefault(delegate(XAttribute item) { return item.Name.LocalName == "id"; });
                string relationId = relationAttribute == null ? String.Empty : relationAttribute.Value;
                XElement relation = relationships.Descendants().FirstOrDefault(delegate(XElement item) {
                    return item.Name.LocalName == "Relationship" && String.Equals((string)item.Attribute("Id"), relationId, StringComparison.Ordinal);
                });
                string target = relation == null ? String.Empty : ((string)relation.Attribute("Target") ?? String.Empty).Replace('\\', '/').TrimStart('/');
                if (target.Length > 0 && target.IndexOf(':') < 0 && !target.Split('/').Contains(".."))
                {
                    string candidate = target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
                    if (archive.GetEntry(candidate) != null) return candidate;
                }
            }
        }

        ZipArchiveEntry fallback = archive.Entries
            .Where(delegate(ZipArchiveEntry item) { return item.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase); })
            .OrderBy(delegate(ZipArchiveEntry item) { return item.FullName; }, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return fallback == null ? String.Empty : fallback.FullName;
    }

    internal static List<string> ReadSharedStrings(ZipArchive archive)
    {
        List<string> values = new List<string>();
        XmlReader reader = OpenEntryReader(archive.GetEntry("xl/sharedStrings.xml"), SharedStringsLimit);
        if (reader == null) return values;
        using (reader)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                    values.Add(ReadTextElements(reader, "t"));
            }
        }
        return values;
    }

    private static void DetectHeader(
        Dictionary<int, string> values,
        RimWorldTranslatorRmkHistoryData result,
        ref int identifierColumn,
        ref int classColumn,
        ref int nodeColumn,
        ref int requiredModsColumn,
        ref int sourceColumn,
        ref int translationColumn)
    {
        foreach (KeyValuePair<int, string> pair in values)
        {
            string header = (pair.Value ?? String.Empty).Trim();
            string lower = header.ToLowerInvariant();
            if (lower.StartsWith("class+node", StringComparison.Ordinal) || lower.IndexOf("identifier (key", StringComparison.Ordinal) >= 0)
                identifierColumn = pair.Key;
            else if (lower.StartsWith("class ", StringComparison.Ordinal) || lower.StartsWith("class[", StringComparison.Ordinal))
                classColumn = pair.Key;
            else if (lower.StartsWith("node ", StringComparison.Ordinal) || lower.StartsWith("node[", StringComparison.Ordinal))
                nodeColumn = pair.Key;
            else if (lower.StartsWith("required mods", StringComparison.Ordinal))
                requiredModsColumn = pair.Key;

            if (lower.IndexOf("[source string]", StringComparison.Ordinal) >= 0)
            {
                sourceColumn = pair.Key;
                int bracket = header.IndexOf('[');
                result.SourceLanguage = bracket > 0 ? header.Substring(0, bracket).Trim() : String.Empty;
            }
            if (lower.IndexOf("[translation]", StringComparison.Ordinal) >= 0) translationColumn = pair.Key;
        }
    }

    public static RimWorldTranslatorRmkHistoryData Read(string workbookPath)
    {
        RimWorldTranslatorRmkHistoryData result = new RimWorldTranslatorRmkHistoryData();
        FileInfo workbookInfo = new FileInfo(workbookPath);
        if (!workbookInfo.Exists) throw new FileNotFoundException("RMK workbook was not found.", workbookPath);
        if (workbookInfo.Length > 268435456) throw new InvalidDataException("RMK workbook is too large: " + workbookPath);

        using (ZipArchive archive = ZipFile.OpenRead(workbookPath))
        {
            List<string> sharedStrings = ReadSharedStrings(archive);
            string sheetEntryName = FindWorksheetEntry(archive);
            if (sheetEntryName.Length == 0) return result;

            int identifierColumn = -1;
            int classColumn = -1;
            int nodeColumn = -1;
            int requiredModsColumn = -1;
            int sourceColumn = -1;
            int translationColumn = -1;
            bool headerFound = false;
            int rowIndex = -1;

            XmlReader reader = OpenEntryReader(archive.GetEntry(sheetEntryName), WorksheetLimit);
            if (reader == null) return result;
            using (reader)
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
                    rowIndex++;
                    Dictionary<int, string> values = ReadRow(reader, sharedStrings);
                    if (!headerFound)
                    {
                        if (rowIndex >= 10) return result;
                        DetectHeader(values, result, ref identifierColumn, ref classColumn, ref nodeColumn, ref requiredModsColumn, ref sourceColumn, ref translationColumn);
                        headerFound = sourceColumn >= 0 && translationColumn >= 0 && (identifierColumn >= 0 || (classColumn >= 0 && nodeColumn >= 0));
                        continue;
                    }

                    string className = GetValue(values, classColumn);
                    string key = GetValue(values, nodeColumn);
                    string identifier = GetValue(values, identifierColumn);
                    if (identifier.Length == 0 && className.Length > 0 && key.Length > 0) identifier = className + "+" + key;
                    if (identifier.Length == 0) continue;
                    if (key.Length == 0)
                    {
                        int separator = identifier.IndexOf('+');
                        if (separator >= 0 && separator + 1 < identifier.Length) key = identifier.Substring(separator + 1);
                    }
                    RimWorldTranslatorRmkHistoryRow historyRow = new RimWorldTranslatorRmkHistoryRow {
                        Identifier = identifier,
                        ClassName = className,
                        Key = key,
                        RequiredMods = GetValue(values, requiredModsColumn),
                        Source = GetValue(values, sourceColumn),
                        Translation = GetValue(values, translationColumn)
                    };
                    result.Rows.Add(historyRow);
                    if (!result.Map.ContainsKey(identifier)) result.Map.Add(identifier, historyRow);
                }
            }
        }
        return result;
    }
}

public static class RimWorldTranslatorRmkXlsxWriter
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private static string CleanText(string value)
    {
        if (String.IsNullOrEmpty(value)) return String.Empty;
        StringBuilder builder = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (Char.IsHighSurrogate(current) && index + 1 < value.Length && Char.IsLowSurrogate(value[index + 1]))
            {
                builder.Append(current);
                builder.Append(value[++index]);
            }
            else if (!Char.IsSurrogate(current) && XmlConvert.IsXmlChar(current))
            {
                builder.Append(current);
            }
        }
        return builder.ToString();
    }

    private static XmlWriter CreateXmlWriter(Stream stream)
    {
        XmlWriterSettings settings = new XmlWriterSettings();
        settings.Encoding = Utf8NoBom;
        settings.Indent = false;
        settings.CloseOutput = false;
        return XmlWriter.Create(stream, settings);
    }

    private static void WriteStaticEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using (Stream stream = entry.Open())
        using (StreamWriter writer = new StreamWriter(stream, Utf8NoBom))
        {
            writer.Write(content);
        }
    }

    private static void WriteTextCell(XmlWriter writer, string reference, string value)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is", SpreadsheetNamespace);
        writer.WriteStartElement("t", SpreadsheetNamespace);
        writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
        writer.WriteString(CleanText(value));
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteWorksheet(ZipArchive archive, IList<RimWorldTranslatorRmkHistoryRow> rows, string sourceLanguage)
    {
        ZipArchiveEntry entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using (Stream stream = entry.Open())
        using (XmlWriter writer = CreateXmlWriter(stream))
        {
            int lastRow = Math.Max(1, rows.Count + 1);
            writer.WriteStartDocument(true);
            writer.WriteStartElement("worksheet", SpreadsheetNamespace);
            writer.WriteStartElement("dimension", SpreadsheetNamespace);
            writer.WriteAttributeString("ref", "A1:F" + lastRow.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteStartElement("sheetViews", SpreadsheetNamespace);
            writer.WriteStartElement("sheetView", SpreadsheetNamespace);
            writer.WriteAttributeString("workbookViewId", "0");
            writer.WriteStartElement("pane", SpreadsheetNamespace);
            writer.WriteAttributeString("ySplit", "1");
            writer.WriteAttributeString("topLeftCell", "A2");
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("cols", SpreadsheetNamespace);
            double[] widths = new double[] { 42, 24, 52, 28, 72, 72 };
            for (int column = 0; column < widths.Length; column++)
            {
                writer.WriteStartElement("col", SpreadsheetNamespace);
                writer.WriteAttributeString("min", (column + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("max", (column + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("width", widths[column].ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("sheetData", SpreadsheetNamespace);
            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", "1");
            string[] headers = new string[] {
                "Class+Node [(Identifier (Key)]",
                "Class [Not chosen]",
                "Node [Not chosen]",
                "Required Mods [Not chosen]",
                CleanText(sourceLanguage) + " [Source string]",
                "Korean (한국어) [Translation]"
            };
            for (int column = 0; column < headers.Length; column++)
                WriteTextCell(writer, ((char)('A' + column)).ToString() + "1", headers[column]);
            writer.WriteEndElement();

            for (int index = 0; index < rows.Count; index++)
            {
                RimWorldTranslatorRmkHistoryRow row = rows[index];
                int rowNumber = index + 2;
                string suffix = rowNumber.ToString(CultureInfo.InvariantCulture);
                writer.WriteStartElement("row", SpreadsheetNamespace);
                writer.WriteAttributeString("r", suffix);
                WriteTextCell(writer, "A" + suffix, row.Identifier);
                WriteTextCell(writer, "B" + suffix, row.ClassName);
                WriteTextCell(writer, "C" + suffix, row.Key);
                WriteTextCell(writer, "D" + suffix, row.RequiredMods);
                WriteTextCell(writer, "E" + suffix, row.Source);
                WriteTextCell(writer, "F" + suffix, row.Translation);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("autoFilter", SpreadsheetNamespace);
            writer.WriteAttributeString("ref", "A1:F" + lastRow.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
    }

    private static void WriteWorkbookArchive(string outputPath, IList<RimWorldTranslatorRmkHistoryRow> rows, string sourceLanguage)
    {
        using (ZipArchive archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            WriteStaticEntry(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "</Types>");
            WriteStaticEntry(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            WriteStaticEntry(archive, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"" + SpreadsheetNamespace + "\" xmlns:r=\"" + RelationshipsNamespace + "\">" +
                "<sheets><sheet name=\"Translations\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            WriteStaticEntry(archive, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");
            WriteWorksheet(archive, rows, sourceLanguage);
        }
    }

    private static XDocument LoadWorksheetDocument(ZipArchiveEntry entry)
    {
        if (entry == null) throw new InvalidDataException("Workbook worksheet was not found.");
        if (entry.Length > 536870912) throw new InvalidDataException("Workbook worksheet is too large.");
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.DtdProcessing = DtdProcessing.Prohibit;
        settings.XmlResolver = null;
        settings.MaxCharactersFromEntities = 1024;
        settings.MaxCharactersInDocument = 536870912;
        using (Stream stream = entry.Open())
        using (XmlReader reader = XmlReader.Create(stream, settings))
        {
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
    }

    private static int GetColumnIndex(string reference)
    {
        if (String.IsNullOrWhiteSpace(reference)) return -1;
        int result = 0;
        int count = 0;
        foreach (char character in reference)
        {
            if (!Char.IsLetter(character)) break;
            result = checked(result * 26 + (Char.ToUpperInvariant(character) - 'A' + 1));
            count++;
        }
        return count == 0 ? -1 : result - 1;
    }

    private static string GetColumnName(int columnIndex)
    {
        if (columnIndex < 0) throw new ArgumentOutOfRangeException("columnIndex");
        StringBuilder builder = new StringBuilder();
        int value = columnIndex + 1;
        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + (value % 26)));
            value /= 26;
        }
        return builder.ToString();
    }

    private static int GetRowNumber(XElement row, int fallback)
    {
        int result;
        XAttribute value = row.Attribute("r");
        return value != null && Int32.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
            ? result
            : fallback;
    }

    private static string GetCellValue(XElement cell, IList<string> sharedStrings)
    {
        string type = (string)cell.Attribute("t") ?? String.Empty;
        if (type == "inlineStr")
            return String.Concat(cell.Descendants().Where(delegate(XElement node) { return node.Name.LocalName == "t"; }).Select(delegate(XElement node) { return node.Value; }));
        XElement valueNode = cell.Elements().FirstOrDefault(delegate(XElement node) { return node.Name.LocalName == "v"; });
        string value = valueNode == null ? String.Empty : valueNode.Value;
        if (type == "s")
        {
            int index;
            if (Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < sharedStrings.Count)
                return sharedStrings[index];
        }
        return value;
    }

    private static Dictionary<int, string> GetRowValues(XElement row, IList<string> sharedStrings)
    {
        Dictionary<int, string> result = new Dictionary<int, string>();
        int sequentialColumn = 0;
        foreach (XElement cell in row.Elements().Where(delegate(XElement node) { return node.Name.LocalName == "c"; }))
        {
            int column = GetColumnIndex((string)cell.Attribute("r"));
            if (column < 0) column = sequentialColumn;
            result[column] = GetCellValue(cell, sharedStrings);
            sequentialColumn = column + 1;
        }
        return result;
    }

    private static string GetValue(Dictionary<int, string> values, int column)
    {
        string value;
        return column >= 0 && values.TryGetValue(column, out value) ? value ?? String.Empty : String.Empty;
    }

    private static void SetCellValue(XElement row, int column, int rowNumber, string value)
    {
        XNamespace ns = row.Name.Namespace;
        XElement cell = row.Elements().FirstOrDefault(delegate(XElement candidate) {
            return candidate.Name.LocalName == "c" && GetColumnIndex((string)candidate.Attribute("r")) == column;
        });
        if (cell == null)
        {
            cell = new XElement(ns + "c");
            XElement next = row.Elements().FirstOrDefault(delegate(XElement candidate) {
                return candidate.Name.LocalName == "c" && GetColumnIndex((string)candidate.Attribute("r")) > column;
            });
            if (next == null) row.Add(cell); else next.AddBeforeSelf(cell);
        }
        cell.SetAttributeValue("r", GetColumnName(column) + rowNumber.ToString(CultureInfo.InvariantCulture));
        cell.SetAttributeValue("t", "inlineStr");
        cell.RemoveNodes();
        XElement text = new XElement(ns + "t", CleanText(value));
        text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        cell.Add(new XElement(ns + "is", text));
    }

    private static void MergeWorksheet(XDocument document, IList<string> sharedStrings, IList<RimWorldTranslatorRmkHistoryRow> rows, string sourceLanguage)
    {
        XElement worksheet = document.Root;
        if (worksheet == null || worksheet.Name.LocalName != "worksheet") throw new InvalidDataException("Workbook worksheet XML is invalid.");
        XNamespace ns = worksheet.Name.Namespace;
        XElement sheetData = worksheet.Elements().FirstOrDefault(delegate(XElement node) { return node.Name.LocalName == "sheetData"; });
        if (sheetData == null) throw new InvalidDataException("Workbook sheetData was not found.");

        int identifierColumn = -1;
        int classColumn = -1;
        int nodeColumn = -1;
        int requiredModsColumn = -1;
        int sourceColumn = -1;
        int translationColumn = -1;
        XElement headerRow = null;
        int headerRowNumber = 1;
        foreach (XElement row in sheetData.Elements().Where(delegate(XElement node) { return node.Name.LocalName == "row"; }).Take(10))
        {
            Dictionary<int, string> values = GetRowValues(row, sharedStrings);
            foreach (KeyValuePair<int, string> pair in values)
            {
                string header = (pair.Value ?? String.Empty).Trim();
                string lower = header.ToLowerInvariant();
                if (lower.StartsWith("class+node", StringComparison.Ordinal) || lower.IndexOf("identifier (key", StringComparison.Ordinal) >= 0) identifierColumn = pair.Key;
                else if (lower.StartsWith("class ", StringComparison.Ordinal) || lower.StartsWith("class[", StringComparison.Ordinal)) classColumn = pair.Key;
                else if (lower.StartsWith("node ", StringComparison.Ordinal) || lower.StartsWith("node[", StringComparison.Ordinal)) nodeColumn = pair.Key;
                else if (lower.StartsWith("required mods", StringComparison.Ordinal)) requiredModsColumn = pair.Key;
                if (lower.IndexOf("[source string]", StringComparison.Ordinal) >= 0) sourceColumn = pair.Key;
                if (lower.IndexOf("[translation]", StringComparison.Ordinal) >= 0) translationColumn = pair.Key;
            }
            if (sourceColumn >= 0 && translationColumn >= 0 && identifierColumn >= 0 && classColumn >= 0 && nodeColumn >= 0 && requiredModsColumn >= 0)
            {
                headerRow = row;
                headerRowNumber = GetRowNumber(row, 1);
                break;
            }
        }
        if (headerRow == null) throw new InvalidDataException("Workbook does not use the RMK translation columns.");

        SetCellValue(headerRow, identifierColumn, headerRowNumber, "Class+Node [(Identifier (Key)]");
        SetCellValue(headerRow, classColumn, headerRowNumber, "Class [Not chosen]");
        SetCellValue(headerRow, nodeColumn, headerRowNumber, "Node [Not chosen]");
        SetCellValue(headerRow, requiredModsColumn, headerRowNumber, "Required Mods [Not chosen]");
        SetCellValue(headerRow, sourceColumn, headerRowNumber, CleanText(sourceLanguage) + " [Source string]");
        SetCellValue(headerRow, translationColumn, headerRowNumber, "Korean (한국어) [Translation]");

        Dictionary<string, XElement> existingRows = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        int maxRow = headerRowNumber;
        foreach (XElement row in sheetData.Elements().Where(delegate(XElement node) { return node.Name.LocalName == "row"; }))
        {
            int rowNumber = GetRowNumber(row, maxRow + 1);
            if (rowNumber > maxRow) maxRow = rowNumber;
            if (Object.ReferenceEquals(row, headerRow)) continue;
            Dictionary<int, string> values = GetRowValues(row, sharedStrings);
            string identifier = GetValue(values, identifierColumn);
            if (identifier.Length == 0)
            {
                string className = GetValue(values, classColumn);
                string key = GetValue(values, nodeColumn);
                if (className.Length > 0 && key.Length > 0) identifier = className + "+" + key;
            }
            if (identifier.Length > 0 && !existingRows.ContainsKey(identifier)) existingRows.Add(identifier, row);
        }

        foreach (RimWorldTranslatorRmkHistoryRow desired in rows)
        {
            XElement row;
            int rowNumber;
            if (existingRows.TryGetValue(desired.Identifier, out row))
            {
                rowNumber = GetRowNumber(row, maxRow + 1);
                if (rowNumber > maxRow) maxRow = rowNumber;
            }
            else
            {
                rowNumber = ++maxRow;
                row = new XElement(ns + "row", new XAttribute("r", rowNumber.ToString(CultureInfo.InvariantCulture)));
                sheetData.Add(row);
                existingRows[desired.Identifier] = row;
            }
            SetCellValue(row, identifierColumn, rowNumber, desired.Identifier);
            SetCellValue(row, classColumn, rowNumber, desired.ClassName);
            SetCellValue(row, nodeColumn, rowNumber, desired.Key);
            SetCellValue(row, requiredModsColumn, rowNumber, desired.RequiredMods);
            SetCellValue(row, sourceColumn, rowNumber, desired.Source);
            SetCellValue(row, translationColumn, rowNumber, desired.Translation);
        }

        int maxColumn = Math.Max(translationColumn, Math.Max(sourceColumn, Math.Max(requiredModsColumn, Math.Max(nodeColumn, Math.Max(classColumn, identifierColumn)))));
        foreach (XElement cell in sheetData.Descendants().Where(delegate(XElement node) { return node.Name.LocalName == "c"; }))
        {
            int column = GetColumnIndex((string)cell.Attribute("r"));
            if (column > maxColumn) maxColumn = column;
        }
        XElement dimension = worksheet.Elements().FirstOrDefault(delegate(XElement node) { return node.Name.LocalName == "dimension"; });
        if (dimension == null)
        {
            dimension = new XElement(ns + "dimension");
            worksheet.AddFirst(dimension);
        }
        dimension.SetAttributeValue("ref", "A1:" + GetColumnName(maxColumn) + maxRow.ToString(CultureInfo.InvariantCulture));
        XElement autoFilter = worksheet.Elements().FirstOrDefault(delegate(XElement node) { return node.Name.LocalName == "autoFilter"; });
        if (autoFilter != null)
        {
            int filterStartColumn = identifierColumn;
            int filterEndColumn = translationColumn;
            string filterReference = (string)autoFilter.Attribute("ref") ?? String.Empty;
            string[] filterParts = filterReference.Split(':');
            if (filterParts.Length > 0)
            {
                int parsedStart = GetColumnIndex(filterParts[0]);
                if (parsedStart >= 0) filterStartColumn = parsedStart;
                int parsedEnd = GetColumnIndex(filterParts[filterParts.Length - 1]);
                if (parsedEnd > filterEndColumn) filterEndColumn = parsedEnd;
            }
            autoFilter.SetAttributeValue("ref", GetColumnName(filterStartColumn) + headerRowNumber.ToString(CultureInfo.InvariantCulture) + ":" + GetColumnName(filterEndColumn) + maxRow.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void UpdateWorkbookArchive(string existingPath, string outputPath, IList<RimWorldTranslatorRmkHistoryRow> rows, string sourceLanguage)
    {
        File.Copy(existingPath, outputPath, true);
        using (ZipArchive archive = ZipFile.Open(outputPath, ZipArchiveMode.Update))
        {
            List<string> sharedStrings = RimWorldTranslatorRmkXlsxReader.ReadSharedStrings(archive);
            string worksheetName = RimWorldTranslatorRmkXlsxReader.FindWorksheetEntry(archive);
            if (String.IsNullOrWhiteSpace(worksheetName)) throw new InvalidDataException("Workbook worksheet was not found.");
            ZipArchiveEntry oldEntry = archive.GetEntry(worksheetName);
            XDocument document = LoadWorksheetDocument(oldEntry);
            MergeWorksheet(document, sharedStrings, rows, sourceLanguage);
            oldEntry.Delete();
            ZipArchiveEntry newEntry = archive.CreateEntry(worksheetName, CompressionLevel.Optimal);
            using (Stream stream = newEntry.Open())
            using (XmlWriter writer = CreateXmlWriter(stream))
            {
                document.Save(writer);
            }
        }
    }

    public static void Write(string workbookPath, IEnumerable<RimWorldTranslatorRmkHistoryRow> sourceRows, string sourceLanguage)
    {
        if (String.IsNullOrWhiteSpace(workbookPath)) throw new ArgumentException("Workbook path is required.", "workbookPath");
        string fullPath = Path.GetFullPath(workbookPath);
        if (!String.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Workbook path must use the .xlsx extension.", "workbookPath");
        string directory = Path.GetDirectoryName(fullPath);
        if (String.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("Workbook directory is invalid.");
        Directory.CreateDirectory(directory);
        string effectiveSourceLanguage = String.IsNullOrWhiteSpace(sourceLanguage) ? "English" : CleanText(sourceLanguage.Trim());
        List<RimWorldTranslatorRmkHistoryRow> rows = sourceRows == null
            ? new List<RimWorldTranslatorRmkHistoryRow>()
            : sourceRows.Where(delegate(RimWorldTranslatorRmkHistoryRow row) { return row != null && !String.IsNullOrWhiteSpace(row.Identifier); }).ToList();
        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        string backupPath = temporaryPath + ".bak";
        try
        {
            if (File.Exists(fullPath)) UpdateWorkbookArchive(fullPath, temporaryPath, rows, effectiveSourceLanguage);
            else WriteWorkbookArchive(temporaryPath, rows, effectiveSourceLanguage);
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, backupPath, true);
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}
