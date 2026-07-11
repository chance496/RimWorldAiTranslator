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
    public string Source { get; set; }
    public string Translation { get; set; }
}

public sealed class RimWorldTranslatorRmkHistoryData
{
    public string SourceLanguage { get; set; }
    public Dictionary<string, RimWorldTranslatorRmkHistoryRow> Map { get; private set; }

    public RimWorldTranslatorRmkHistoryData()
    {
        SourceLanguage = String.Empty;
        Map = new Dictionary<string, RimWorldTranslatorRmkHistoryRow>(StringComparer.OrdinalIgnoreCase);
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

    private static string FindWorksheetEntry(ZipArchive archive)
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

    private static List<string> ReadSharedStrings(ZipArchive archive)
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
                        DetectHeader(values, result, ref identifierColumn, ref classColumn, ref nodeColumn, ref sourceColumn, ref translationColumn);
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
                    if (result.Map.ContainsKey(identifier)) continue;
                    result.Map.Add(identifier, new RimWorldTranslatorRmkHistoryRow {
                        Identifier = identifier,
                        ClassName = className,
                        Key = key,
                        Source = GetValue(values, sourceColumn),
                        Translation = GetValue(values, translationColumn)
                    });
                }
            }
        }
        return result;
    }
}
