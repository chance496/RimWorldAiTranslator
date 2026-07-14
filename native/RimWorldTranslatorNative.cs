using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("RimWorld AI Translator Native")]
[assembly: AssemblyDescription("Native compatibility helpers for RimWorld AI Translator")]
[assembly: AssemblyProduct("RimWorld AI Translator")]
[assembly: AssemblyCompany("chance496")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyInformationalVersion("1.0.1-rc.1")]
[assembly: AssemblyCopyright("Copyright (c) 2026 wjdck")]

public static class RimWorldTranslatorNativeMethods
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

}

[StructLayout(LayoutKind.Sequential)]
public struct RimWorldTranslatorCaptureRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

public static class RimWorldTranslatorCaptureMethods
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RimWorldTranslatorCaptureRect value, int valueSize);
}

public sealed class RimWorldTranslatorRowRuntimeCache
{
    public string Identity { get; set; }
    public string RelativeTarget { get; set; }
    public string SourceFingerprint { get; set; }
    public object? Decision { get; set; }
    public object? DefContext { get; set; }
    public string SearchKey { get; set; }
    public string SearchText { get; set; }
    public string SearchDefClass { get; set; }
    public string SearchNode { get; set; }
    public string SearchAll { get; set; }
    public string SourcePreview { get; set; }
    public string DefaultPreview { get; set; }

    public RimWorldTranslatorRowRuntimeCache()
    {
        Identity = String.Empty;
        RelativeTarget = String.Empty;
        SourceFingerprint = String.Empty;
        SearchKey = String.Empty;
        SearchText = String.Empty;
        SearchDefClass = String.Empty;
        SearchNode = String.Empty;
        SearchAll = String.Empty;
        SourcePreview = String.Empty;
        DefaultPreview = String.Empty;
    }
}

public sealed class RimWorldTranslatorRowRuntimeCacheStore
{
    private ConditionalWeakTable<object, RimWorldTranslatorRowRuntimeCache> entries =
        new ConditionalWeakTable<object, RimWorldTranslatorRowRuntimeCache>();

    public RimWorldTranslatorRowRuntimeCache Get(object row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return entries.GetValue(row, delegate(object key) { return new RimWorldTranslatorRowRuntimeCache(); });
    }

    public void Reset()
    {
        entries = new ConditionalWeakTable<object, RimWorldTranslatorRowRuntimeCache>();
    }
}

public sealed class RimWorldTranslatorRmkHistoryRow
{
    public string Identifier { get; set; } = String.Empty;
    public string ClassName { get; set; } = String.Empty;
    public string Key { get; set; } = String.Empty;
    public string RequiredMods { get; set; } = String.Empty;
    public string Source { get; set; } = String.Empty;
    public string Translation { get; set; } = String.Empty;
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
    public string Key { get; set; } = String.Empty;
    public string Text { get; set; } = String.Empty;
    public string TypeName { get; set; } = String.Empty;
    public string Field { get; set; } = String.Empty;
}

public sealed class RimWorldTranslatorDefReadResult
{
    public List<RimWorldTranslatorRawSourceEntry> Entries { get; private set; }
    public List<RimWorldTranslatorRawSourceEntry> Excluded { get; private set; }

    public RimWorldTranslatorDefReadResult()
    {
        Entries = new List<RimWorldTranslatorRawSourceEntry>();
        Excluded = new List<RimWorldTranslatorRawSourceEntry>();
    }
}

public sealed class RimWorldTranslatorValidationIssues
{
    public string[] MissingTokens { get; set; } = Array.Empty<string>();
    public string[] UnexpectedTokens { get; set; } = Array.Empty<string>();
    public string[] TokenCountMismatches { get; set; } = Array.Empty<string>();
    public bool GrammarPrefixMoved { get; set; }
}

public static class RimWorldTranslatorValidation
{
    private static readonly Regex ProtectedTokenRegex = new Regex(
        @"(\r\n|\r|\n|\\r\\n|\\[nrt]|\{[^}\r\n]+\}|\[[A-Za-z0-9_.:;'"" -]+\]|</?[A-Za-z][^>\r\n]*>|\$[A-Za-z_][A-Za-z0-9_]*\$?|%[0-9.]*[sdif]|\b[A-Z]{2,}_[A-Z0-9_]+\b|\b[A-Za-z][A-Za-z0-9_]*->)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GrammarPrefixRegex = new Regex(
        @"^\s*([A-Za-z][A-Za-z0-9_]*->)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InvalidParticleRegex = new Regex(
        "(\\uC740\\(\\uB294\\)|\\uB294\\(\\uC740\\)|\\uC774\\(\\uAC00\\)|\\uAC00\\(\\uC774\\)|\\uC744\\(\\uB97C\\)|\\uB97C\\(\\uC744\\)|\\uACFC\\(\\uC640\\)|\\uC640\\(\\uACFC\\)|\\uC73C\\uB85C\\(\\uB85C\\)|\\uB85C\\(\\uC73C\\uB85C\\))|(?:\\[[^\\]\\r\\n]+\\]|\\{[^}\\r\\n]+\\}|\\$[A-Za-z_][A-Za-z0-9_]*\\$?)(?:\\uC73C\\uB85C|\\uC740|\\uB294|\\uC774|\\uAC00|\\uC744|\\uB97C|\\uACFC|\\uC640|\\uB85C)(?=$|[\\s.,!?\\u2026:;\\uFF0C\\u3002\\uFF01\\uFF1F\\u3001])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PathologicalNewlinesRegex = new Regex(
        @"(\r?\n\s*){8,}|(\\u000a\s*){8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NewlineRegex = new Regex(
        @"\r?\n",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Dictionary<string, int> GetProtectedTokenCounts(string? text)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string normalized = (text ?? String.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (Match match in ProtectedTokenRegex.Matches(normalized))
        {
            int count;
            if (counts.TryGetValue(match.Value, out count)) counts[match.Value] = count + 1;
            else counts[match.Value] = 1;
        }
        return counts;
    }

    public static RimWorldTranslatorValidationIssues GetTokenPreservationIssues(string? source, string? target)
    {
        Dictionary<string, int> sourceCounts = GetProtectedTokenCounts(source);
        Dictionary<string, int> targetCounts = GetProtectedTokenCounts(target);
        List<string> missing = new List<string>();
        List<string> unexpected = new List<string>();
        List<string> countMismatches = new List<string>();
        foreach (KeyValuePair<string, int> pair in sourceCounts)
        {
            int targetCount;
            if (!targetCounts.TryGetValue(pair.Key, out targetCount)) targetCount = 0;
            if (targetCount < pair.Value) missing.Add(pair.Key);
            if (targetCount != pair.Value) countMismatches.Add(pair.Key + " (" + pair.Value.ToString(CultureInfo.InvariantCulture) + "->" + targetCount.ToString(CultureInfo.InvariantCulture) + ")");
        }
        foreach (KeyValuePair<string, int> pair in targetCounts)
        {
            int sourceCount;
            if (!sourceCounts.TryGetValue(pair.Key, out sourceCount)) sourceCount = 0;
            if (pair.Value > sourceCount) unexpected.Add(pair.Key);
        }
        bool grammarPrefixMoved = false;
        Match grammarPrefix = GrammarPrefixRegex.Match(source ?? String.Empty);
        if (grammarPrefix.Success && !Regex.IsMatch(target ?? String.Empty, @"^\s*" + Regex.Escape(grammarPrefix.Groups[1].Value), RegexOptions.CultureInvariant))
        {
            grammarPrefixMoved = true;
            if (!missing.Contains(grammarPrefix.Groups[1].Value)) missing.Add(grammarPrefix.Groups[1].Value);
        }
        return new RimWorldTranslatorValidationIssues {
            MissingTokens = missing.ToArray(),
            UnexpectedTokens = unexpected.ToArray(),
            TokenCountMismatches = countMismatches.ToArray(),
            GrammarPrefixMoved = grammarPrefixMoved
        };
    }

    public static string[] GetInvalidKoreanParticleNotations(string? text)
    {
        if (String.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        List<string> result = new List<string>();
        foreach (Match match in InvalidParticleRegex.Matches(text))
        {
            if (seen.Add(match.Value)) result.Add(match.Value);
        }
        return result.ToArray();
    }

    public static bool IsPathologicalTranslation(string? text)
    {
        if (String.IsNullOrEmpty(text)) return false;
        if (PathologicalNewlinesRegex.IsMatch(text)) return true;
        return NewlineRegex.Matches(text).Count >= 20 && text.Length < 4000;
    }
}

public static class RimWorldTranslatorRmkXlsxReader
{
    private const long Mebibyte = 1024 * 1024;
    private const long XmlFileLimit = 64 * Mebibyte;
    private const long WorkbookXmlLimit = 16 * Mebibyte;
    private const long SharedStringsLimit = 64 * Mebibyte;
    internal const long MaximumWorksheetBytes = 64 * Mebibyte;
    private const long WorksheetLimit = MaximumWorksheetBytes;
    private const long MaximumWorkbookBytes = 256 * Mebibyte;
    private const long MaximumArchiveEntryBytes = 256 * Mebibyte;
    private const long MaximumAggregateUncompressedBytes = 512 * Mebibyte;
    private const long MaximumCompressionRatio = 100;
    private const int MaximumArchiveEntries = 4096;
    private const int MaximumArchiveEntryNameLength = 1024;
    private const int MaximumCentralDirectoryBytes = 16 * 1024 * 1024;
    private const int MaximumXmlDepth = 256;
    private const long MaximumXmlNodes = 2_000_000;
    private const long MaximumSourceXmlNodes = 500_000;
    private const int MaximumRawSourceEntries = 250_000;
    private const long MaximumRawSourceCharacters = 64L * Mebibyte;
    private const int MaximumSourceKeyCharacters = 16_384;
    private const int MaximumSourceSegmentCharacters = 1_024;
    private const int MaximumSourceTextCharacters = 1_048_576;
    private const int MaximumDefTraversalDepth = 256;
    internal const int MaximumHistoryRows = 100_000;
    internal const int MaximumCellCharacters = 32_767;
    internal const int MaximumExcelRows = 1_048_576;
    private const int MaximumWorksheetRows = MaximumHistoryRows + 10;
    private const int MaximumSharedStrings = 250_000;
    private const int MaximumWorksheetCells = 1_000_000;
    private const int MaximumCellsPerRow = 16_384;
    private const int EndOfCentralDirectoryMinimumBytes = 22;
    private const int MaximumZipCommentBytes = UInt16.MaxValue;
    private const UInt32 EndOfCentralDirectorySignature = 0x06054b50;
    private const UInt32 CentralDirectoryFileHeaderSignature = 0x02014b50;
    private const UInt32 Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
    private const UInt16 Zip64ExtraFieldIdentifier = 0x0001;
    private static readonly HashSet<string> CriticalArchiveEntries = new HashSet<string>(new string[] {
        "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
        "xl/_rels/workbook.xml.rels", "xl/sharedStrings.xml"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WorksheetRelationshipTypes = new HashSet<string>(new string[] {
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
        "http://purl.oclc.org/ooxml/officeDocument/relationships/worksheet"
    }, StringComparer.Ordinal);
    private static readonly HashSet<string> SpreadsheetXmlRelationshipTypeTokens = new HashSet<string>(new string[] {
        "calcChain", "chartsheet", "comments", "drawing", "pivotCacheDefinition",
        "pivotTable", "sharedStrings", "styles", "table", "theme", "vmlDrawing", "worksheet"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ActivePackagePartPrefixes = new string[] {
        "xl/activex/", "xl/ctrlprops/", "xl/dialogsheets/", "xl/embeddings/",
        "xl/externallinks/", "xl/macrosheets/", "xl/querytables/", "xl/webextensions/",
        "customui/", "webextensions/"
    };
    private static readonly string[] ExecutablePackageExtensions = new string[] {
        ".b" + "at", ".c" + "md", ".com", ".dll", ".exe", ".hta", ".js", ".jse", ".msi",
        ".msp", ".p" + "s1", ".scr", ".vbe", ".vbs", ".wsf", ".wsh"
    };
    private static readonly string[] ActiveRelationshipTypeTokens = new string[] {
        "activexcontrol", "activexcontrolbinary", "attachedtemplate", "connections",
        "control", "ctrlprop", "customui", "externallink", "externallinkpath",
        "dialogsheet", "xldialogsheet", "xlmacrosheet", "xlintlmacrosheet",
        "oleobject", "package", "querytable", "vbadata", "vbaproject",
        "vbaprojectsignature", "vbaprojectsignatureagile", "webextension",
        "webextensiontaskpanes"
    };
    private static readonly string[] ActiveContentTypeMarkers = new string[] {
        "activex", "connections+xml", "dialogsheet+xml", "externallink+xml",
        "intlmacrosheet+xml", "macroenabled", "macrosheet+xml", "oleobject",
        "querytable+xml", "vba", "webextension"
    };
    private static readonly HashSet<string> FormulaBearingElementNames = new HashSet<string>(new string[] {
        "f", "formula", "formula1", "formula2", "definedName",
        "calculatedColumnFormula", "totalsRowFormula"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExternalReferenceAttributeNames = new HashSet<string>(new string[] {
        "action", "codebase", "data", "href", "src"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly char[] UnsafeArchiveEntryNameCharacters = new char[] { '<', '>', ':', '"', '|', '?', '*' };
    private static readonly object DefFieldRulesLock = new object();
    private static HashSet<string> TranslatableDefFields = new HashSet<string>(new string[] {
        "label", "labelshort", "description", "jobstring", "reportstring",
        "deathmessage", "deathmessagefemale", "deathmessagemale",
        "pawnsplural", "leadertitle", "arrivedletter", "customlabel",
        "gizmolabel", "gizmodescription", "commandlabel", "commanddescription",
        "letterlabel", "lettertext", "header", "headertip", "summary",
        "formatstring", "formatstringunfinalized", "fixedname", "reason"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedDefSegments = new HashSet<string>(new string[] {
        "defname", "parentname", "classname", "class", "thingclass", "workerclass",
        "compclass", "hediffclass", "thoughtclass", "abilityclass", "worldobjectclass",
        "nodeclass", "texpath", "texname", "graphicpath", "shader", "sound", "sounddef",
        "iconpath", "packageid", "xpath", "operation", "colorchannel", "rendernode",
        "rendertree", "rendertreedef", "bodypart", "bodypartdef", "bodytype", "headtype",
        "racedef", "thingdef", "pawnkinddef", "jobdef", "statdef", "skilldef", "hediffdef",
        "genedef", "tagdef", "debuglabel"
    }, StringComparer.OrdinalIgnoreCase);

    public static void LoadDefFieldRules(string? path)
    {
        if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using FileStream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > 1048576) throw new InvalidDataException("Def field rule file is too large: " + path);
        using StreamReader reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        while (reader.ReadLine() is { } rawLine)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] columns = line.Split('\t', 2);
            if (columns.Length != 2) continue;
            string field = columns[1].Trim();
            if (!Regex.IsMatch(field, "^[A-Za-z_][A-Za-z0-9_]*$")) continue;
            if (columns[0].Equals("allow", StringComparison.OrdinalIgnoreCase)) allowed.Add(field);
            else if (columns[0].Equals("deny", StringComparison.OrdinalIgnoreCase)) denied.Add(field);
        }
        if (allowed.Count == 0) throw new InvalidDataException("Def field rule file did not contain any allow rules: " + path);
        lock (DefFieldRulesLock)
        {
            TranslatableDefFields = allowed;
            foreach (string field in denied) ExcludedDefSegments.Add(field);
        }
    }

    private sealed class PackageContentTypeMap
    {
        internal Dictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string> Overrides { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal string? Resolve(string partName)
        {
            string? value;
            if (Overrides.TryGetValue(partName, out value)) return value;
            int slash = partName.LastIndexOf('/');
            int dot = partName.LastIndexOf('.');
            if (dot <= slash || dot == partName.Length - 1) return null;
            return Defaults.TryGetValue(partName.Substring(dot + 1), out value) ? value : null;
        }
    }

    private static XmlReaderSettings CreateReaderSettings(
        long maximumLength,
        bool closeInput = true,
        bool ignoreComments = true,
        bool ignoreProcessingInstructions = true)
    {
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.DtdProcessing = DtdProcessing.Prohibit;
        settings.XmlResolver = null;
        settings.MaxCharactersFromEntities = 1024;
        settings.MaxCharactersInDocument = maximumLength;
        settings.IgnoreComments = ignoreComments;
        settings.IgnoreProcessingInstructions = ignoreProcessingInstructions;
        settings.CloseInput = closeInput;
        return settings;
    }

    private static void ValidateXmlStructure(
        Stream stream,
        long maximumLength,
        string description,
        long maximumNodes = MaximumXmlNodes)
    {
        long nodes = 0;
        using (XmlReader reader = XmlReader.Create(stream, CreateReaderSettings(maximumLength, false)))
        {
            while (reader.Read())
            {
                nodes++;
                if (nodes > maximumNodes)
                    throw new InvalidDataException(description + " exceeds the XML node-count limit.");
                if (reader.Depth > MaximumXmlDepth)
                    throw new InvalidDataException(description + " exceeds the XML depth limit.");
            }
        }
    }

    private static void ValidateXmlEntry(ZipArchiveEntry entry, long maximumLength)
    {
        if (entry.Length > maximumLength)
            throw new InvalidDataException("XLSX XML entry is too large: " + entry.FullName);
        using (Stream validationStream = entry.Open())
            ValidateXmlStructure(validationStream, maximumLength, "XLSX XML entry");
    }

    private static XDocument LoadXml(ZipArchiveEntry entry, long maximumLength)
    {
        return LoadXml(entry, maximumLength, LoadOptions.None);
    }

    private static XDocument LoadXml(ZipArchiveEntry entry, long maximumLength, LoadOptions loadOptions)
    {
        ValidateXmlEntry(entry, maximumLength);
        using (Stream stream = entry.Open())
        using (XmlReader reader = XmlReader.Create(stream, CreateReaderSettings(maximumLength, false)))
        {
            return XDocument.Load(reader, loadOptions);
        }
    }

    internal static XDocument LoadWorksheetDocument(ZipArchiveEntry entry)
    {
        ValidateXmlEntry(entry, WorksheetLimit);
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(
            stream,
            CreateReaderSettings(
                WorksheetLimit,
                closeInput: false,
                ignoreComments: false,
                ignoreProcessingInstructions: false));
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static XDocument LoadXmlFile(string path, long maximumLength)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > maximumLength)
                throw new InvalidDataException("XML file is too large: " + path);
            ValidateXmlStructure(stream, maximumLength, "XML file", MaximumSourceXmlNodes);
            stream.Position = 0;
            using (XmlReader reader = XmlReader.Create(stream, CreateReaderSettings(maximumLength, false)))
                return XDocument.Load(reader, LoadOptions.None);
        }
    }

    private static XmlReader OpenEntryReader(
        ZipArchiveEntry entry,
        long maximumLength,
        bool observeProcessingInstructions = false)
    {
        ValidateXmlEntry(entry, maximumLength);
        Stream stream = entry.Open();
        try
        {
            return XmlReader.Create(
                stream,
                CreateReaderSettings(
                    maximumLength,
                    ignoreProcessingInstructions: !observeProcessingInstructions));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool ValidKeySegment(string value)
    {
        return !String.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), "^[A-Za-z_][A-Za-z0-9_.-]*$");
    }

    private static string DirectChildText(XElement node, string name)
    {
        foreach (XElement child in node.Elements())
        {
            if (child.Name.LocalName != name) continue;
            string value = child.Value;
            if (value.Length > MaximumSourceTextCharacters)
                throw new InvalidDataException("Def XML metadata text exceeds the per-value limit.");
            return value.Trim();
        }
        return String.Empty;
    }

    private static string NormalizeTranslationHandle(string? value, bool useSimpleTypeName)
    {
        if (String.IsNullOrWhiteSpace(value)) return String.Empty;
        if (value.Length > MaximumSourceSegmentCharacters)
            throw new InvalidDataException("Def XML list handle exceeds the safe segment-length limit.");
        string handle = value.Trim();
        if (useSimpleTypeName)
        {
            int separator = Math.Max(handle.LastIndexOf('.'), handle.LastIndexOf('+'));
            if (separator >= 0 && separator + 1 < handle.Length) handle = handle.Substring(separator + 1);
        }
        handle = Regex.Replace(handle, "\\{.*?\\}", String.Empty);
        handle = handle.Replace(' ', '_').Replace('\n', '_').Replace("\r", String.Empty).Replace('\t', '_');
        handle = handle.Replace(".", String.Empty).Replace("-", String.Empty);
        handle = Regex.Replace(handle, "[^A-Za-z0-9_-]", String.Empty);
        handle = Regex.Replace(handle, "_+", "_").Trim('_');
        if (handle.Length > 0 && handle.All(Char.IsDigit)) handle = "_" + handle;
        return handle;
    }

    private static string ListItemHandle(XElement node, string parentPath)
    {
        string parent = (parentPath ?? String.Empty).ToLowerInvariant();
        string field = String.Empty;
        bool typeValue = false;
        if (parent == "comps")
        {
            field = DirectChildText(node, "compClass");
            typeValue = true;
        }
        else if (parent == "verbs")
        {
            field = DirectChildText(node, "verbClass");
            typeValue = true;
        }
        else if (parent == "hediffgivers") field = DirectChildText(node, "hediff");
        else if (parent == "scenparts" || parent == "parts") field = DirectChildText(node, "def");
        else if (parent == "stages" || parent == "lifestages" || parent == "tools")
        {
            field = DirectChildText(node, "label");
            if (field.Length == 0) field = DirectChildText(node, "customLabel");
        }
        return NormalizeTranslationHandle(field, typeValue);
    }

    private static bool ExcludedDefPath(IList<string> path)
    {
        foreach (string segment in path)
            if (ExcludedDefSegments.Contains(segment)) return true;
        return false;
    }

    private static bool TranslatableDefPath(IList<string> path)
    {
        if (path.Count == 0) return false;
        string leaf = path[path.Count - 1].ToLowerInvariant();
        string full = String.Join(".", path.ToArray()).ToLowerInvariant();
        if (TranslatableDefFields.Contains(leaf)) return true;
        if (leaf == "text" && Regex.IsMatch(full, "(letter|message|scenario|quest|dialog|help|tip|inspect)")) return true;
        if (leaf == "slateref" && Regex.IsMatch(full, "(letter|text|label|description|inspect|string)")) return true;
        if (leaf == "li" && Regex.IsMatch(full, "(rulesstrings|tagsstrings)")) return true;
        return false;
    }

    private static bool ContextuallyExcludedDefPath(IList<string> path, string typeName)
    {
        if (path.Count == 0) return false;
        string leaf = path[path.Count - 1].ToLowerInvariant();
        string full = String.Join(".", path.ToArray()).ToLowerInvariant();
        if (!String.IsNullOrWhiteSpace(typeName) && typeName.Contains("PawnRenderTreeDef", StringComparison.OrdinalIgnoreCase))
            return true;
        if (leaf == "name" && (full.Contains("alienrace.", StringComparison.Ordinal) ||
            Regex.IsMatch(full, "(^|\\.)(colorchannels|bodyaddons|powermodes)(\\.|$)")))
            return true;
        if (Regex.IsMatch(full, "(^|\\.)(graphicpaths?|rendernodes?|rendertree)(\\.|$)"))
            return true;
        return false;
    }

    private static void AddDefLeaves(
        XElement node,
        List<string> path,
        string defName,
        string typeName,
        List<RimWorldTranslatorRawSourceEntry> entries,
        List<RimWorldTranslatorRawSourceEntry> excluded,
        int traversalDepth,
        int pathCharacters,
        ref long rawCharacters)
    {
        if (traversalDepth > MaximumDefTraversalDepth)
            throw new InvalidDataException("Def XML exceeds the safe traversal-depth limit.");

        List<XElement> children = node.Elements().ToList();
        if (children.Count == 0)
        {
            bool isExcluded = ExcludedDefPath(path);
            bool isTranslatable = TranslatableDefPath(path);
            bool isContextuallyExcluded = !isExcluded && !isTranslatable && ContextuallyExcludedDefPath(path, typeName);
            if (!isExcluded && !isTranslatable && !isContextuallyExcluded) return;
            if (entries.Count + excluded.Count >= MaximumRawSourceEntries)
                throw new InvalidDataException("Def XML exceeds the raw source-entry limit.");
            string key = defName + "." + String.Join(".", path.ToArray());
            string text = node.Value;
            string field = path[path.Count - 1];
            ValidateRawSourceValue(key, MaximumSourceKeyCharacters, "key");
            ValidateRawSourceValue(text, MaximumSourceTextCharacters, "text");
            ReserveRawSourceCharacters(ref rawCharacters, key, text, typeName, field);
            RimWorldTranslatorRawSourceEntry entry = new RimWorldTranslatorRawSourceEntry {
                Key = key,
                Text = text,
                TypeName = typeName,
                Field = field
            };
            if (isExcluded)
            {
                excluded.Add(entry);
                return;
            }
            if (isTranslatable)
            {
                entries.Add(entry);
                return;
            }
            if (isContextuallyExcluded) excluded.Add(entry);
            return;
        }

        string parentPath = path.Count > 0 ? path[path.Count - 1] : "li";
        Dictionary<string, int> handleTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (XElement child in children.Where(c => c.Name.LocalName == "li"))
        {
            string handle = ListItemHandle(child, parentPath);
            if (handle.Length == 0) continue;
            if (!handleTotals.ContainsKey(handle)) handleTotals[handle] = 0;
            handleTotals[handle]++;
        }
        Dictionary<string, int> handleIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        int listIndex = 0;
        foreach (XElement child in children)
        {
            string name = child.Name.LocalName;
            string segment = name;
            if (name == "li")
            {
                string handle = ListItemHandle(child, parentPath);
                if (handle.Length == 0)
                    segment = listIndex.ToString(CultureInfo.InvariantCulture);
                else if (handleTotals[handle] > 1)
                {
                    int handleIndex;
                    if (!handleIndexes.TryGetValue(handle, out handleIndex)) handleIndex = 0;
                    segment = handle + "-" + handleIndex.ToString(CultureInfo.InvariantCulture);
                    handleIndexes[handle] = handleIndex + 1;
                }
                else segment = handle;
                listIndex++;
            }
            ValidateRawSourceValue(segment, MaximumSourceSegmentCharacters, "path segment");
            int childPathCharacters = checked(pathCharacters + 1 + segment.Length);
            if (childPathCharacters > MaximumSourceKeyCharacters - defName.Length - 1)
                throw new InvalidDataException("Def XML translation key exceeds the safe length limit.");
            List<string> childPath = new List<string>(path);
            childPath.Add(segment);
            AddDefLeaves(
                child,
                childPath,
                defName,
                typeName,
                entries,
                excluded,
                traversalDepth + 1,
                childPathCharacters,
                ref rawCharacters);
        }
    }

    private static void ValidateRawSourceValue(string value, int maximumCharacters, string description)
    {
        if (value.Length > maximumCharacters)
            throw new InvalidDataException("Source XML " + description + " exceeds the safe length limit.");
    }

    private static void ReserveRawSourceCharacters(ref long current, params string[] values)
    {
        long added = 0;
        foreach (string value in values) added = checked(added + value.Length);
        if (added > MaximumRawSourceCharacters - current)
            throw new InvalidDataException("Source XML exceeds the aggregate raw text limit.");
        current += added;
    }

    public static List<RimWorldTranslatorRawSourceEntry> ReadLanguageData(string path)
    {
        List<RimWorldTranslatorRawSourceEntry> entries = new List<RimWorldTranslatorRawSourceEntry>();
        long rawCharacters = 0;
        XDocument document = LoadXmlFile(path, XmlFileLimit);
        XElement? root = document.Root;
        if (root == null || root.Name.LocalName != "LanguageData") return entries;
        foreach (XElement child in root.Elements())
        {
            if (entries.Count >= MaximumRawSourceEntries)
                throw new InvalidDataException("LanguageData XML exceeds the raw source-entry limit.");
            string key = child.Name.LocalName;
            string text = child.Value;
            ValidateRawSourceValue(key, MaximumSourceKeyCharacters, "key");
            ValidateRawSourceValue(text, MaximumSourceTextCharacters, "text");
            int separator = key.LastIndexOf('.');
            string field = separator >= 0 ? key.Substring(separator + 1) : key;
            ReserveRawSourceCharacters(ref rawCharacters, key, text, field);
            entries.Add(new RimWorldTranslatorRawSourceEntry {
                Key = key,
                Text = text,
                TypeName = String.Empty,
                Field = field
            });
        }
        return entries;
    }

    public static RimWorldTranslatorDefReadResult ReadDefsDetailed(string path)
    {
        RimWorldTranslatorDefReadResult result = new RimWorldTranslatorDefReadResult();
        long rawCharacters = 0;
        XDocument document = LoadXmlFile(path, XmlFileLimit);
        XElement? root = document.Root;
        if (root == null || root.Name.LocalName != "Defs") return result;
        foreach (XElement def in root.Elements())
        {
            string defName = DirectChildText(def, "defName");
            ValidateRawSourceValue(defName, MaximumSourceSegmentCharacters, "defName");
            if (!ValidKeySegment(defName)) continue;
            string typeName = def.Name.LocalName;
            ValidateRawSourceValue(typeName, MaximumSourceSegmentCharacters, "Def type");
            foreach (XElement child in def.Elements())
            {
                if (child.Name.LocalName == "defName") continue;
                ValidateRawSourceValue(child.Name.LocalName, MaximumSourceSegmentCharacters, "path segment");
                List<string> pathSegments = new List<string>();
                pathSegments.Add(child.Name.LocalName);
                AddDefLeaves(
                    child,
                    pathSegments,
                    defName.Trim(),
                    typeName,
                    result.Entries,
                    result.Excluded,
                    0,
                    child.Name.LocalName.Length,
                    ref rawCharacters);
            }
        }
        return result;
    }

    public static List<RimWorldTranslatorRawSourceEntry> ReadDefs(string path)
    {
        return ReadDefsDetailed(path).Entries;
    }

    private static int ColumnIndex(string? reference)
    {
        int value = 0;
        bool found = false;
        if (String.IsNullOrEmpty(reference)) return -1;
        foreach (char raw in reference)
        {
            char character = Char.ToUpperInvariant(raw);
            if (character < 'A' || character > 'Z') break;
            if (value >= MaximumCellsPerRow) return MaximumCellsPerRow;
            value = (value * 26) + (character - 'A' + 1);
            found = true;
        }
        return found ? value - 1 : -1;
    }

    private static int ParseExcelRowNumber(string value, string description)
    {
        int result;
        if (value.Length == 0
            || !Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            || result < 1
            || result > MaximumExcelRows)
        {
            throw new InvalidDataException(description + " is outside Excel's supported row range.");
        }
        return result;
    }

    private static int? ValidateWorksheetRowReference(string? reference)
    {
        if (String.IsNullOrWhiteSpace(reference)) return null;
        return ParseExcelRowNumber(reference, "RMK workbook row reference");
    }

    private static void ValidateCellRowReference(string? reference, int? expectedRow)
    {
        if (String.IsNullOrWhiteSpace(reference)) return;
        int index = 0;
        while (index < reference.Length)
        {
            char character = Char.ToUpperInvariant(reference[index]);
            if (character < 'A' || character > 'Z') break;
            index++;
        }
        if (index == 0 || index == reference.Length)
            throw new InvalidDataException("RMK workbook cell reference is invalid.");
        int rowNumber = ParseExcelRowNumber(reference.Substring(index), "RMK workbook cell reference");
        if (expectedRow.HasValue && expectedRow.Value != rowNumber)
            throw new InvalidDataException("RMK workbook cell reference does not match its row.");
    }

    private static string ReadTextElements(XmlReader parent, string elementName)
    {
        StringBuilder value = new StringBuilder();
        int phoneticDepth = -1;
        using (XmlReader subtree = parent.ReadSubtree())
        {
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.EndElement
                    && phoneticDepth >= 0
                    && subtree.Depth == phoneticDepth)
                {
                    phoneticDepth = -1;
                    continue;
                }
                if (subtree.NodeType != XmlNodeType.Element) continue;
                if (subtree.LocalName == "rPh")
                {
                    if (!subtree.IsEmptyElement) phoneticDepth = subtree.Depth;
                    continue;
                }
                if (phoneticDepth < 0 && subtree.LocalName == elementName)
                    AppendBoundedCellText(value, ReadBoundedElementText(subtree, MaximumCellCharacters));
            }
        }
        return value.ToString();
    }

    private static string ReadBoundedElementText(XmlReader reader, int maximumCharacters)
    {
        if (reader.IsEmptyElement) return String.Empty;
        int elementDepth = reader.Depth;
        StringBuilder value = new StringBuilder(Math.Min(maximumCharacters, 256));
        char[] buffer = new char[4096];
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == elementDepth) break;
            if (reader.NodeType != XmlNodeType.Text
                && reader.NodeType != XmlNodeType.CDATA
                && reader.NodeType != XmlNodeType.Whitespace
                && reader.NodeType != XmlNodeType.SignificantWhitespace)
                continue;
            int read;
            while ((read = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
            {
                if (value.Length > maximumCharacters - read)
                    throw new InvalidDataException("RMK workbook cell text exceeds the supported Excel limit.");
                value.Append(buffer, 0, read);
            }
        }
        return value.ToString();
    }

    private static void AppendBoundedCellText(StringBuilder target, string value)
    {
        if (target.Length > MaximumCellCharacters - value.Length)
            throw new InvalidDataException("RMK workbook cell text exceeds the supported Excel limit.");
        target.Append(value);
    }

    private static string ReadCell(XmlReader cell, IList<string> sharedStrings)
    {
        string type = cell.GetAttribute("t") ?? String.Empty;
        string rawValue = String.Empty;
        StringBuilder? inlineValue = null;
        int phoneticDepth = -1;
        using (XmlReader subtree = cell.ReadSubtree())
        {
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.EndElement
                    && phoneticDepth >= 0
                    && subtree.Depth == phoneticDepth)
                {
                    phoneticDepth = -1;
                    continue;
                }
                if (subtree.NodeType != XmlNodeType.Element) continue;
                if (subtree.LocalName == "rPh")
                {
                    if (!subtree.IsEmptyElement) phoneticDepth = subtree.Depth;
                    continue;
                }
                if (subtree.LocalName == "v")
                {
                    rawValue = ReadBoundedElementText(subtree, MaximumCellCharacters);
                }
                else if (subtree.LocalName == "t" && type == "inlineStr" && phoneticDepth < 0)
                {
                    if (inlineValue == null) inlineValue = new StringBuilder();
                    AppendBoundedCellText(inlineValue, ReadBoundedElementText(subtree, MaximumCellCharacters));
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
        int cellsSeen = 0;
        int? expectedRow = ValidateWorksheetRowReference(row.GetAttribute("r"));
        using (XmlReader subtree = row.ReadSubtree())
        {
            while (subtree.Read())
            {
                if (subtree.NodeType != XmlNodeType.Element || subtree.LocalName != "c") continue;
                if (++cellsSeen > MaximumCellsPerRow)
                    throw new InvalidDataException("RMK workbook row exceeds the Excel column limit.");
                string? reference = subtree.GetAttribute("r");
                ValidateCellRowReference(reference, expectedRow);
                int index = ColumnIndex(reference);
                if (index >= MaximumCellsPerRow)
                    throw new InvalidDataException("RMK workbook cell reference exceeds the Excel column limit.");
                if (index >= 0 && !values.TryAdd(index, ReadCell(subtree, sharedStrings)))
                    throw new InvalidDataException("RMK workbook row contains a duplicate cell reference.");
            }
        }
        return values;
    }

    private static string GetValue(Dictionary<int, string> values, int index, bool trim = true)
    {
        string? value;
        string result = index >= 0 && values.TryGetValue(index, out value) ? value ?? String.Empty : String.Empty;
        return trim ? result.Trim() : result;
    }

    internal static ZipArchive OpenValidatedWorkbook(string workbookPath)
    {
        FileStream stream = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length <= 0 || stream.Length > MaximumWorkbookBytes)
                throw new InvalidDataException("RMK workbook size is outside the allowed range.");
            ValidateCentralDirectory(stream);
            stream.Position = 0;
            ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8);
            try
            {
                ValidateArchiveEntries(archive);
                return archive;
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ValidateCentralDirectory(FileStream stream)
    {
        long length = stream.Length;
        int tailLength = checked((int)Math.Min(
            length,
            EndOfCentralDirectoryMinimumBytes + MaximumZipCommentBytes));
        byte[] tail = new byte[tailLength];
        stream.Position = length - tailLength;
        int totalRead = 0;
        while (totalRead < tail.Length)
        {
            int read = stream.Read(tail, totalRead, tail.Length - totalRead);
            if (read == 0) throw new InvalidDataException("RMK workbook ZIP directory is truncated.");
            totalRead += read;
        }

        int endRecordOffset = -1;
        for (int offset = tail.Length - EndOfCentralDirectoryMinimumBytes; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset, sizeof(UInt32)))
                != EndOfCentralDirectorySignature)
                continue;
            UInt16 commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 20, sizeof(UInt16)));
            if (offset + EndOfCentralDirectoryMinimumBytes + commentLength != tail.Length) continue;
            endRecordOffset = offset;
            break;
        }
        if (endRecordOffset < 0)
            throw new InvalidDataException("RMK workbook has no bounded ZIP end record.");

        ReadOnlySpan<byte> endRecord = tail.AsSpan(endRecordOffset, EndOfCentralDirectoryMinimumBytes);
        UInt16 diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[4..]);
        UInt16 directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[6..]);
        UInt16 entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[8..]);
        UInt16 totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[10..]);
        UInt32 directorySize = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[12..]);
        UInt32 directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[16..]);
        if (diskNumber != 0
            || directoryDisk != 0
            || entriesOnDisk != totalEntries
            || entriesOnDisk == UInt16.MaxValue
            || directorySize == UInt32.MaxValue
            || directoryOffset == UInt32.MaxValue)
            throw new InvalidDataException("RMK workbook uses unsupported split-disk or Zip64 metadata.");
        if (totalEntries == 0 || totalEntries > MaximumArchiveEntries)
            throw new InvalidDataException("RMK workbook ZIP entry count is outside the allowed range.");
        if (directorySize == 0 || directorySize > MaximumCentralDirectoryBytes)
            throw new InvalidDataException("RMK workbook central directory exceeds its size limit.");

        long absoluteEndRecordOffset = checked(length - tailLength + endRecordOffset);
        long absoluteDirectoryEnd = checked((long)directoryOffset + directorySize);
        if (absoluteDirectoryEnd != absoluteEndRecordOffset)
            throw new InvalidDataException("RMK workbook central-directory bounds are invalid.");
        if (absoluteEndRecordOffset >= 20)
        {
            byte[] signature = new byte[sizeof(UInt32)];
            stream.Position = absoluteEndRecordOffset - 20;
            int read = stream.Read(signature, 0, signature.Length);
            if (read != signature.Length)
                throw new InvalidDataException("RMK workbook Zip64 locator check was truncated.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(signature) == Zip64EndOfCentralDirectoryLocatorSignature)
                throw new InvalidDataException("RMK workbook uses unsupported Zip64 metadata.");
        }
        ValidateCentralDirectoryEntries(stream, directoryOffset, directorySize, totalEntries);
    }

    private static void ValidateCentralDirectoryEntries(
        FileStream stream,
        UInt32 directoryOffset,
        UInt32 directorySize,
        UInt16 totalEntries)
    {
        byte[] bytes = new byte[checked((int)directorySize)];
        stream.Position = directoryOffset;
        stream.ReadExactly(bytes);
        int offset = 0;
        for (int index = 0; index < totalEntries; index++)
        {
            const int fixedHeaderBytes = 46;
            if (offset > bytes.Length - fixedHeaderBytes
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(UInt32)))
                != CentralDirectoryFileHeaderSignature)
            {
                throw new InvalidDataException("RMK workbook central directory has an invalid file header.");
            }

            UInt32 compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 20, sizeof(UInt32)));
            UInt32 uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 24, sizeof(UInt32)));
            UInt16 nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(offset + 28, sizeof(UInt16)));
            UInt16 extraLength = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(offset + 30, sizeof(UInt16)));
            UInt16 commentLength = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(offset + 32, sizeof(UInt16)));
            UInt16 diskStart = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(offset + 34, sizeof(UInt16)));
            UInt32 localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 42, sizeof(UInt32)));
            if (compressedSize == UInt32.MaxValue
                || uncompressedSize == UInt32.MaxValue
                || diskStart == UInt16.MaxValue
                || diskStart != 0
                || localHeaderOffset == UInt32.MaxValue)
            {
                throw new InvalidDataException(
                    "RMK workbook central directory uses unsupported Zip64 or split-disk metadata.");
            }

            int entryBytes = checked(fixedHeaderBytes + nameLength + extraLength + commentLength);
            if (entryBytes > bytes.Length - offset)
                throw new InvalidDataException("RMK workbook central-directory entry exceeds its bounded record.");
            int extraOffset = offset + fixedHeaderBytes + nameLength;
            ValidateNoZip64ExtraField(bytes.AsSpan(extraOffset, extraLength));
            offset += entryBytes;
        }
        if (offset != bytes.Length)
            throw new InvalidDataException("RMK workbook central directory has trailing or missing records.");
    }

    private static void ValidateNoZip64ExtraField(ReadOnlySpan<byte> extraFields)
    {
        int offset = 0;
        while (offset < extraFields.Length)
        {
            if (offset > extraFields.Length - 4)
                throw new InvalidDataException("RMK workbook has a truncated central-directory extra field.");
            UInt16 identifier = BinaryPrimitives.ReadUInt16LittleEndian(extraFields.Slice(offset));
            UInt16 length = BinaryPrimitives.ReadUInt16LittleEndian(extraFields.Slice(offset + 2));
            offset += 4;
            if (length > extraFields.Length - offset)
                throw new InvalidDataException("RMK workbook has an out-of-bounds central-directory extra field.");
            if (identifier == Zip64ExtraFieldIdentifier)
                throw new InvalidDataException("RMK workbook uses an unsupported Zip64 extra field.");
            offset += length;
        }
    }

    private static void ValidateArchiveEntries(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("RMK workbook ZIP entry count is outside the allowed range.");

        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregate = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ValidateArchiveEntryName(entry.FullName);
            ValidateInactivePackagePart(entry.FullName);
            if (!names.Add(entry.FullName))
            {
                string kind = CriticalArchiveEntries.Contains(entry.FullName) ? "critical " : String.Empty;
                throw new InvalidDataException("RMK workbook contains a duplicate " + kind + "package entry.");
            }

            long maximum = MaximumEntryLength(entry.FullName);
            long uncompressed = entry.Length;
            long compressed = entry.CompressedLength;
            if (uncompressed < 0 || uncompressed > maximum)
                throw new InvalidDataException("RMK workbook package entry exceeds its size limit: " + entry.FullName);
            if (compressed < 0 || (uncompressed > 0 && compressed == 0))
                throw new InvalidDataException("RMK workbook package entry has an invalid compressed size: " + entry.FullName);
            if (compressed > 0 && uncompressed > compressed * MaximumCompressionRatio)
                throw new InvalidDataException("RMK workbook package entry exceeds the compression-ratio limit: " + entry.FullName);
            aggregate = checked(aggregate + uncompressed);
            if (aggregate > MaximumAggregateUncompressedBytes)
                throw new InvalidDataException("RMK workbook exceeds the aggregate uncompressed-size limit.");
        }
        ZipArchiveEntry? contentTypes = archive.GetEntry("[Content_Types].xml");
        if (contentTypes == null)
            throw new InvalidDataException("RMK workbook content-type metadata was not found.");
        PackageContentTypeMap contentTypeMap = ValidateContentTypes(contentTypes);
        HashSet<string> relationshipXmlParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> relationshipSpreadsheetParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries.Where(delegate(ZipArchiveEntry item) {
                     return item.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
                 }))
        {
            ValidateRelationshipEntry(entry, relationshipXmlParts, relationshipSpreadsheetParts);
        }

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string? contentType = contentTypeMap.Resolve(entry.FullName);
            bool xmlLike = IsXmlLikePart(entry.FullName, contentType)
                || relationshipXmlParts.Contains(entry.FullName);
            if (!xmlLike) continue;
            bool spreadsheetMarkup = relationshipSpreadsheetParts.Contains(entry.FullName)
                || IsSpreadsheetMarkupPart(entry.FullName, contentType);
            bool vml = entry.FullName.EndsWith(".vml", StringComparison.OrdinalIgnoreCase)
                || (contentType != null && contentType.Contains("vml", StringComparison.OrdinalIgnoreCase));
            long maximumLength = spreadsheetMarkup || vml
                ? Math.Min(MaximumEntryLength(entry.FullName), WorksheetLimit)
                : MaximumEntryLength(entry.FullName);
            ValidatePassiveXmlPart(entry, maximumLength, spreadsheetMarkup || vml);
        }
    }

    private static long MaximumEntryLength(string name)
    {
        if (name.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
            || name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
            return WorkbookXmlLimit;
        if (name.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase))
            return SharedStringsLimit;
        if (name.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return WorksheetLimit;
        return MaximumArchiveEntryBytes;
    }

    private static void ValidateInactivePackagePart(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower == "xl/connections.xml"
            || lower == "xl/vbaproject.bin"
            || ActivePackagePartPrefixes.Any(delegate(string prefix) { return lower.StartsWith(prefix, StringComparison.Ordinal); })
            || ExecutablePackageExtensions.Any(delegate(string extension) { return lower.EndsWith(extension, StringComparison.Ordinal); }))
        {
            throw new InvalidDataException("RMK workbook contains an active or external-data package part: " + name);
        }
    }

    private static void ValidateRelationshipEntry(
        ZipArchiveEntry entry,
        HashSet<string> relationshipXmlParts,
        HashSet<string> relationshipSpreadsheetParts)
    {
        XDocument relationships = LoadXml(entry, WorkbookXmlLimit);
        foreach (XElement relation in relationships.Descendants().Where(delegate(XElement item) { return item.Name.LocalName == "Relationship"; }))
        {
            string relationshipType = ((string?)relation.Attribute("Type") ?? String.Empty).Trim();
            if (relationshipType.Length == 0)
                throw new InvalidDataException("RMK workbook relationship type is missing.");
            string decodedType;
            try { decodedType = Uri.UnescapeDataString(relationshipType); }
            catch (UriFormatException exception)
            {
                throw new InvalidDataException("RMK workbook relationship type is malformed.", exception);
            }
            int typeSeparator = decodedType.LastIndexOf('/');
            string typeToken = decodedType.Substring(typeSeparator + 1);
            if (ActiveRelationshipTypeTokens.Contains(typeToken, StringComparer.OrdinalIgnoreCase)
                || typeToken.Contains("activex", StringComparison.OrdinalIgnoreCase)
                || typeToken.Contains("dialogsheet", StringComparison.OrdinalIgnoreCase)
                || typeToken.Contains("macrosheet", StringComparison.OrdinalIgnoreCase)
                || typeToken.StartsWith("vbaproject", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("RMK workbook contains an active or external-data relationship type.");
            }

            string targetMode = ((string?)relation.Attribute("TargetMode") ?? String.Empty).Trim();
            if (targetMode.Equals("External", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RMK workbook relationships must not use external targets.");
            if (targetMode.Length > 0 && !targetMode.Equals("Internal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RMK workbook relationship TargetMode is invalid.");

            string target = ((string?)relation.Attribute("Target") ?? String.Empty).Trim();
            if (target.Length == 0 || target.StartsWith('#')) continue;
            string decoded;
            try { decoded = Uri.UnescapeDataString(target); }
            catch (UriFormatException exception)
            {
                throw new InvalidDataException("RMK workbook relationship target is malformed.", exception);
            }
            if (decoded.Contains('\\')
                || decoded.Contains(':')
                || decoded.StartsWith("//", StringComparison.Ordinal))
                throw new InvalidDataException("RMK workbook relationship target is not an internal package path.");

            string resolvedTarget = ResolveRelationshipTarget(entry.FullName, decoded);
            if (WorksheetRelationshipTypes.Contains(decodedType)
                || SpreadsheetXmlRelationshipTypeTokens.Contains(typeToken))
            {
                relationshipXmlParts.Add(resolvedTarget);
                relationshipSpreadsheetParts.Add(resolvedTarget);
            }
        }
    }

    private static PackageContentTypeMap ValidateContentTypes(ZipArchiveEntry entry)
    {
        XDocument contentTypes = LoadXml(entry, WorkbookXmlLimit);
        PackageContentTypeMap result = new PackageContentTypeMap();
        foreach (XElement declaration in contentTypes.Descendants().Where(delegate(XElement item) {
                     return item.Name.LocalName == "Default" || item.Name.LocalName == "Override";
                 }))
        {
            string contentType = ((string?)declaration.Attribute("ContentType") ?? String.Empty).Trim();
            if (contentType.Length == 0 || contentType.Length > 1024 || contentType.Any(Char.IsControl))
                throw new InvalidDataException("RMK workbook content-type declaration is incomplete.");
            if (ActiveContentTypeMarkers.Any(delegate(string marker) {
                    return contentType.Contains(marker, StringComparison.OrdinalIgnoreCase);
                }))
            {
                throw new InvalidDataException("RMK workbook declares an active or external-data content type.");
            }

            if (declaration.Name.LocalName == "Default")
            {
                string extension = ((string?)declaration.Attribute("Extension") ?? String.Empty).Trim();
                if (extension.Length == 0
                    || extension.Length > 64
                    || extension.StartsWith('.')
                    || extension.Any(delegate(char value) {
                        return !Char.IsLetterOrDigit(value) && value != '-' && value != '_';
                    })
                    || !result.Defaults.TryAdd(extension, contentType))
                {
                    throw new InvalidDataException("RMK workbook content-type extension declaration is invalid or duplicated.");
                }
            }
            else
            {
                string rawPartName = ((string?)declaration.Attribute("PartName") ?? String.Empty).Trim();
                if (!rawPartName.StartsWith('/') || rawPartName.StartsWith("//", StringComparison.Ordinal))
                    throw new InvalidDataException("RMK workbook content-type part declaration is invalid.");
                string decodedPartName;
                try { decodedPartName = Uri.UnescapeDataString(rawPartName.Substring(1)); }
                catch (UriFormatException exception)
                {
                    throw new InvalidDataException("RMK workbook content-type part declaration is malformed.", exception);
                }
                if (decodedPartName.Contains('#') || decodedPartName.Contains('?') || decodedPartName.EndsWith('/'))
                    throw new InvalidDataException("RMK workbook content-type part declaration is invalid.");
                ValidateArchiveEntryName(decodedPartName);
                if (!result.Overrides.TryAdd(decodedPartName, contentType))
                    throw new InvalidDataException("RMK workbook content-type part declaration is duplicated.");
            }
        }
        return result;
    }

    private static bool IsXmlLikePart(string partName, string? contentType)
    {
        if (partName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            || partName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
            || partName.EndsWith(".vml", StringComparison.OrdinalIgnoreCase))
            return true;
        if (String.IsNullOrWhiteSpace(contentType)) return false;
        string mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("vml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpreadsheetMarkupPart(string partName, string? contentType)
    {
        if (partName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) return true;
        return contentType != null
            && (contentType.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("ms-excel", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFormulaBearingMarkupName(string localName)
    {
        return FormulaBearingElementNames.Contains(localName)
            || localName.StartsWith("fmla", StringComparison.OrdinalIgnoreCase)
            || localName.Contains("formula", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPassiveFormulaDisplayAttribute(
        string elementLocalName,
        string elementNamespace,
        string attributeLocalName)
    {
        return elementLocalName.Equals("sheetView", StringComparison.OrdinalIgnoreCase)
            && elementNamespace.Equals(
                "http://schemas.openxmlformats.org/spreadsheetml/2006/main",
                StringComparison.Ordinal)
            && attributeLocalName.Equals("showFormulas", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExternalReference(string value)
    {
        string decoded;
        try { decoded = Uri.UnescapeDataString(value).Trim(); }
        catch (UriFormatException) { return true; }
        if (decoded.Length == 0 || decoded.StartsWith('#')) return false;
        if (decoded.StartsWith("//", StringComparison.Ordinal)
            || decoded.StartsWith("\\\\", StringComparison.Ordinal))
            return true;
        int colon = decoded.IndexOf(':');
        if (colon <= 0 || !Char.IsLetter(decoded[0])) return false;
        for (int index = 1; index < colon; index++)
        {
            char character = decoded[index];
            if (!Char.IsLetterOrDigit(character) && character != '+' && character != '-' && character != '.')
                return false;
        }
        return true;
    }

    private static void ValidatePassiveXmlPart(
        ZipArchiveEntry entry,
        long maximumLength,
        bool rejectFormulaMarkup)
    {
        using XmlReader reader = OpenEntryReader(entry, maximumLength, observeProcessingInstructions: true);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.ProcessingInstruction
                && reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("RMK workbook XML parts must not contain external stylesheet processing instructions.");
            }
            if (reader.NodeType != XmlNodeType.Element) continue;
            string elementLocalName = reader.LocalName;
            string elementNamespace = reader.NamespaceURI;
            if (rejectFormulaMarkup && IsFormulaBearingMarkupName(elementLocalName))
            {
                throw new InvalidDataException(
                    "RMK workbooks containing formula-bearing spreadsheet content are not accepted because formulas can execute external actions when opened.");
            }
            if (!reader.HasAttributes) continue;
            while (reader.MoveToNextAttribute())
            {
                if (rejectFormulaMarkup
                    && IsFormulaBearingMarkupName(reader.LocalName)
                    && !IsPassiveFormulaDisplayAttribute(
                        elementLocalName,
                        elementNamespace,
                        reader.LocalName))
                {
                    throw new InvalidDataException(
                        "RMK workbooks containing formula-bearing spreadsheet content are not accepted because formulas can execute external actions when opened.");
                }
                if (ExternalReferenceAttributeNames.Contains(reader.LocalName)
                    && LooksLikeExternalReference(reader.Value))
                {
                    throw new InvalidDataException("RMK workbook XML parts must not contain external active-content references.");
                }
            }
            reader.MoveToElement();
        }
    }

    private static string ResolveRelationshipTarget(string relationshipName, string target)
    {
        string baseDirectory = GetRelationshipBaseDirectory(relationshipName);
        string combined = target.StartsWith('/')
            ? target.Substring(1)
            : baseDirectory + "/" + target;
        List<string> segments = new List<string>();
        foreach (string segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new InvalidDataException("RMK workbook relationship escapes the package root.");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        if (segments.Count == 0)
            throw new InvalidDataException("RMK workbook relationship target is invalid.");
        string resolved = String.Join("/", segments);
        ValidateArchiveEntryName(resolved);
        return resolved;
    }

    private static string GetRelationshipBaseDirectory(string relationshipName)
    {
        const string marker = "/_rels/";
        int markerIndex = relationshipName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return markerIndex < 0 ? String.Empty : relationshipName.Substring(0, markerIndex);
    }

    private static void ValidateArchiveEntryName(string name)
    {
        if (String.IsNullOrWhiteSpace(name)
            || name.Length > MaximumArchiveEntryNameLength
            || name.StartsWith('/')
            || name.Contains('\\')
            || name.Any(Char.IsControl))
            throw new InvalidDataException("RMK workbook contains an unsafe package entry name.");

        string path = name.EndsWith('/') ? name[..^1] : name;
        if (path.Length == 0) throw new InvalidDataException("RMK workbook contains an unsafe package entry name.");
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0
                || segment == "."
                || segment == ".."
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || ContainsUnsafeArchiveEntryNameCharacter(segment)
                || IsReservedWindowsName(segment))
                throw new InvalidDataException("RMK workbook contains an unsafe package entry name.");
        }
    }

    private static bool ContainsUnsafeArchiveEntryNameCharacter(string value)
    {
        foreach (char character in value)
            foreach (char unsafeCharacter in UnsafeArchiveEntryNameCharacters)
                if (character == unsafeCharacter) return true;
        return false;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        string trimmed = segment.TrimEnd(' ', '.');
        int extension = trimmed.IndexOf('.');
        string stem = (extension >= 0 ? trimmed.Substring(0, extension) : trimmed).ToUpperInvariant();
        if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CLOCK$") return true;
        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
            return stem[3] >= '1' && stem[3] <= '9';
        return false;
    }

    internal static string FindWorksheetEntry(ZipArchive archive)
    {
        ValidateArchiveEntries(archive);
        return FindWorksheetEntry(archive, ReadSharedStrings(archive));
    }

    private static string FindWorksheetEntry(ZipArchive archive, IList<string> sharedStrings)
    {
        ZipArchiveEntry? workbookEntry = archive.GetEntry("xl/workbook.xml");
        if (workbookEntry == null) throw new InvalidDataException("Workbook XML entry was not found.");
        ZipArchiveEntry? relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (relationshipsEntry == null) throw new InvalidDataException("Workbook relationships entry was not found.");

        XDocument workbook = LoadXml(workbookEntry, WorkbookXmlLimit);
        XDocument relationships = LoadXml(relationshipsEntry, WorkbookXmlLimit);
        Dictionary<string, XElement> relations = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement relation in relationships.Descendants().Where(delegate(XElement item) { return item.Name.LocalName == "Relationship"; }))
        {
            string targetMode = (string?)relation.Attribute("TargetMode") ?? String.Empty;
            if (targetMode.Equals("External", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Workbook relationships must not use external targets.");
            if (targetMode.Length > 0 && !targetMode.Equals("Internal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Workbook relationship TargetMode is invalid.");
            string id = (string?)relation.Attribute("Id") ?? String.Empty;
            if (String.IsNullOrWhiteSpace(id)) continue;
            if (!relations.TryAdd(id, relation))
                throw new InvalidDataException("Workbook relationships contain a duplicate Id: " + id);
        }
        List<string> matches = new List<string>();
        foreach (XElement sheet in workbook.Descendants().Where(delegate(XElement item) { return item.Name.LocalName == "sheet"; }))
        {
            XAttribute? relationAttribute = sheet.Attributes().FirstOrDefault(delegate(XAttribute item) { return item.Name.LocalName == "id"; });
            string relationId = relationAttribute == null ? String.Empty : relationAttribute.Value;
            XElement? relation;
            relations.TryGetValue(relationId, out relation);
            if (relation == null)
                throw new InvalidDataException("Workbook sheet relationship was not found: " + relationId);
            string relationType = (string?)relation.Attribute("Type") ?? String.Empty;
            if (!WorksheetRelationshipTypes.Contains(relationType))
                throw new InvalidDataException("Workbook sheet relationship type is invalid.");
            string target = ((string?)relation.Attribute("Target") ?? String.Empty).Replace('\\', '/');
            if (target.Length > 0 && target.IndexOf(':') < 0)
            {
                string candidate = ResolveRelationshipTarget("xl/_rels/workbook.xml.rels", target);
                ZipArchiveEntry? entry = archive.GetEntry(candidate);
                if (entry != null && HasRmkHeader(entry, sharedStrings)) matches.Add(candidate);
            }
        }
        if (matches.Count == 0) throw new InvalidDataException("Workbook does not contain an RMK translation worksheet.");
        if (matches.Count > 1) throw new InvalidDataException("Workbook contains multiple RMK translation worksheets.");
        return matches[0];
    }

    private static bool HasRmkHeader(ZipArchiveEntry worksheetEntry, IList<string> sharedStrings)
    {
        int matches = 0;
        int rowsSeen = 0;
        using (XmlReader reader = OpenEntryReader(worksheetEntry, WorksheetLimit))
        {
            while (reader.Read() && rowsSeen < 10)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
                rowsSeen++;
                Dictionary<int, string> values = ReadRow(reader, sharedStrings);
                int identifierColumn = -1;
                int classColumn = -1;
                int nodeColumn = -1;
                int requiredModsColumn = -1;
                int sourceColumn = -1;
                int translationColumn = -1;
                RimWorldTranslatorRmkHistoryData header = new RimWorldTranslatorRmkHistoryData();
                DetectHeader(values, header, ref identifierColumn, ref classColumn, ref nodeColumn, ref requiredModsColumn, ref sourceColumn, ref translationColumn);
                if (sourceColumn >= 0 && translationColumn >= 0 && (identifierColumn >= 0 || (classColumn >= 0 && nodeColumn >= 0)))
                    matches++;
            }
        }
        if (matches > 1) throw new InvalidDataException("Workbook worksheet contains multiple RMK header rows: " + worksheetEntry.FullName);
        return matches == 1;
    }

    internal static List<string> ReadSharedStrings(ZipArchive archive)
    {
        List<string> values = new List<string>();
        ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return values;
        XmlReader reader = OpenEntryReader(entry, SharedStringsLimit);
        using (reader)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                {
                    if (values.Count >= MaximumSharedStrings)
                        throw new InvalidDataException("RMK workbook exceeds the shared-string count limit.");
                    values.Add(ReadTextElements(reader, "t"));
                }
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
            if (lower.StartsWith("class+node", StringComparison.Ordinal) || lower.Contains("identifier (key", StringComparison.Ordinal))
                identifierColumn = pair.Key;
            else if (lower.StartsWith("class ", StringComparison.Ordinal) || lower.StartsWith("class[", StringComparison.Ordinal))
                classColumn = pair.Key;
            else if (lower.StartsWith("node ", StringComparison.Ordinal) || lower.StartsWith("node[", StringComparison.Ordinal))
                nodeColumn = pair.Key;
            else if (lower.StartsWith("required mods", StringComparison.Ordinal))
                requiredModsColumn = pair.Key;

            if (lower.Contains("[source string]", StringComparison.Ordinal))
            {
                sourceColumn = pair.Key;
                int bracket = header.IndexOf('[');
                result.SourceLanguage = bracket > 0 ? header.Substring(0, bracket).Trim() : String.Empty;
            }
            if (lower.Contains("[translation]", StringComparison.Ordinal)) translationColumn = pair.Key;
        }
    }

    public static RimWorldTranslatorRmkHistoryData Read(string workbookPath)
    {
        RimWorldTranslatorRmkHistoryData result = new RimWorldTranslatorRmkHistoryData();
        using (ZipArchive archive = OpenValidatedWorkbook(workbookPath))
        {
            List<string> sharedStrings = ReadSharedStrings(archive);
            string sheetEntryName = FindWorksheetEntry(archive, sharedStrings);

            int identifierColumn = -1;
            int classColumn = -1;
            int nodeColumn = -1;
            int requiredModsColumn = -1;
            int sourceColumn = -1;
            int translationColumn = -1;
            bool headerFound = false;
            int rowIndex = -1;
            int worksheetRows = 0;
            int worksheetCells = 0;

            ZipArchiveEntry? worksheetEntry = archive.GetEntry(sheetEntryName);
            if (worksheetEntry == null) throw new InvalidDataException("Workbook worksheet entry was not found.");
            XmlReader reader = OpenEntryReader(worksheetEntry, WorksheetLimit);
            using (reader)
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
                    if (++worksheetRows > MaximumWorksheetRows)
                        throw new InvalidDataException("RMK workbook exceeds the history-row limit.");
                    rowIndex++;
                    Dictionary<int, string> values = ReadRow(reader, sharedStrings);
                    worksheetCells = checked(worksheetCells + values.Count);
                    if (worksheetCells > MaximumWorksheetCells)
                        throw new InvalidDataException("RMK workbook exceeds the worksheet-cell limit.");
                    if (!headerFound)
                    {
                        if (rowIndex >= 10) throw new InvalidDataException("Workbook does not use the RMK translation columns.");
                        int rowIdentifierColumn = -1;
                        int rowClassColumn = -1;
                        int rowNodeColumn = -1;
                        int rowRequiredModsColumn = -1;
                        int rowSourceColumn = -1;
                        int rowTranslationColumn = -1;
                        RimWorldTranslatorRmkHistoryData header = new RimWorldTranslatorRmkHistoryData();
                        DetectHeader(values, header, ref rowIdentifierColumn, ref rowClassColumn, ref rowNodeColumn, ref rowRequiredModsColumn, ref rowSourceColumn, ref rowTranslationColumn);
                        headerFound = rowSourceColumn >= 0 && rowTranslationColumn >= 0
                            && (rowIdentifierColumn >= 0 || (rowClassColumn >= 0 && rowNodeColumn >= 0));
                        if (headerFound)
                        {
                            identifierColumn = rowIdentifierColumn;
                            classColumn = rowClassColumn;
                            nodeColumn = rowNodeColumn;
                            requiredModsColumn = rowRequiredModsColumn;
                            sourceColumn = rowSourceColumn;
                            translationColumn = rowTranslationColumn;
                            result.SourceLanguage = header.SourceLanguage;
                        }
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
                        RequiredMods = GetValue(values, requiredModsColumn, false),
                        Source = GetValue(values, sourceColumn, false),
                        Translation = GetValue(values, translationColumn, false)
                    };
                    result.Rows.Add(historyRow);
                    result.Map.TryAdd(identifier, historyRow);
                }
            }
            if (!headerFound) throw new InvalidDataException("Workbook does not use the RMK translation columns.");
        }
        return result;
    }
}

public sealed class RimWorldTranslatorPreparedFileEvidence
{
    internal RimWorldTranslatorPreparedFileEvidence(
        uint volumeSerialNumber,
        ulong fileIndex,
        long length,
        long lastWriteTimeUtcTicks,
        string sha256Hex)
    {
        VolumeSerialNumber = volumeSerialNumber;
        FileIndex = fileIndex;
        Length = length;
        LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        Sha256Hex = sha256Hex;
    }

    public uint VolumeSerialNumber { get; }
    public ulong FileIndex { get; }
    public long Length { get; }
    public long LastWriteTimeUtcTicks { get; }
    public string Sha256Hex { get; }
}

public static class RimWorldTranslatorRmkXlsxWriter
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int MaximumSourceLanguageCharacters = 256;
    private const long MaximumOutputTextCharacters = 16L * 1024 * 1024;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    internal static Action<string>? PreparedArchiveCreatedTestHook { get; set; }

    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private sealed class PreparedWorkbookState
    {
        internal PreparedWorkbookState(
            string outputPath,
            List<RimWorldTranslatorRmkHistoryRow> rows,
            string sourceLanguage,
            CreatedFileIdentity createdIdentity)
        {
            OutputPath = outputPath;
            Rows = rows;
            SourceLanguage = sourceLanguage;
            CreatedIdentity = createdIdentity;
        }

        internal string OutputPath { get; }
        internal List<RimWorldTranslatorRmkHistoryRow> Rows { get; }
        internal string SourceLanguage { get; }
        internal CreatedFileIdentity CreatedIdentity { get; }
    }

    private sealed class CreatedFileIdentity
    {
        internal CreatedFileIdentity(uint volumeSerialNumber, ulong fileIndex)
        {
            VolumeSerialNumber = volumeSerialNumber;
            FileIndex = fileIndex;
        }

        internal uint VolumeSerialNumber { get; }
        internal ulong FileIndex { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private sealed class BoundedWorksheetStream : Stream
    {
        private readonly Stream? destination;
        private readonly long maximumBytes;
        private long bytesWritten;

        internal BoundedWorksheetStream(Stream? destination, long maximumBytes)
        {
            this.destination = destination;
            this.maximumBytes = maximumBytes;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => bytesWritten;
        public override long Position
        {
            get => bytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => destination?.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
                throw new ArgumentException("The requested buffer range is invalid.", nameof(count));
            Reserve(count);
            destination?.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            destination?.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Reserve(1);
            destination?.WriteByte(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) destination?.Dispose();
            base.Dispose(disposing);
        }

        private void Reserve(int count)
        {
            if (bytesWritten > maximumBytes - count)
                throw new InvalidDataException("RMK output exceeds the worksheet serialization-size limit.");
            bytesWritten += count;
        }
    }

    private static string CleanText(string? value)
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

    private static int DecimalDigits(int value)
    {
        int digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }
        return digits;
    }

    private static void AddTextBudget(
        string? value,
        ref long aggregateCharacters,
        ref long estimatedEscapedBytes)
    {
        if (String.IsNullOrEmpty(value)) return;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (Char.IsHighSurrogate(current)
                && index + 1 < value.Length
                && Char.IsLowSurrogate(value[index + 1]))
            {
                aggregateCharacters = checked(aggregateCharacters + 2);
                estimatedEscapedBytes = checked(estimatedEscapedBytes + 4);
                index++;
                continue;
            }
            if (Char.IsSurrogate(current) || !XmlConvert.IsXmlChar(current)) continue;
            aggregateCharacters = checked(aggregateCharacters + 1);
            if (current == '&' || current == '<' || current == '>' || current == '\'' || current == '"')
                estimatedEscapedBytes = checked(estimatedEscapedBytes + 6);
            else if (current == '\r' || current == '\n')
                estimatedEscapedBytes = checked(estimatedEscapedBytes + 5);
            else if (current <= 0x7f)
                estimatedEscapedBytes = checked(estimatedEscapedBytes + 1);
            else if (current <= 0x7ff)
                estimatedEscapedBytes = checked(estimatedEscapedBytes + 2);
            else
                estimatedEscapedBytes = checked(estimatedEscapedBytes + 3);
        }
    }

    private static void ValidateOutputBudget(
        IList<RimWorldTranslatorRmkHistoryRow> rows,
        string sourceLanguage)
    {
        long aggregateCharacters = 0;
        long estimatedWorksheetBytes = 4096;
        string[] headers = new string[] {
            "Class+Node [(Identifier (Key)]",
            "Class [Not chosen]",
            "Node [Not chosen]",
            "Required Mods [Not chosen]",
            sourceLanguage + " [Source string]",
            "Korean (한국어) [Translation]"
        };

        estimatedWorksheetBytes = checked(estimatedWorksheetBytes + 48 + (6L * (72 + DecimalDigits(1))));
        foreach (string header in headers)
            AddTextBudget(header, ref aggregateCharacters, ref estimatedWorksheetBytes);

        for (int index = 0; index < rows.Count; index++)
        {
            int rowNumber = index + 2;
            int digits = DecimalDigits(rowNumber);
            estimatedWorksheetBytes = checked(estimatedWorksheetBytes + 48 + (6L * (72 + digits)));
            RimWorldTranslatorRmkHistoryRow row = rows[index];
            foreach (string value in new string[] {
                         row.Identifier, row.ClassName, row.Key, row.RequiredMods, row.Source, row.Translation
                     })
            {
                AddTextBudget(value, ref aggregateCharacters, ref estimatedWorksheetBytes);
                if (aggregateCharacters > MaximumOutputTextCharacters)
                    throw new InvalidDataException("RMK output exceeds the aggregate text-character limit.");
                if (estimatedWorksheetBytes > RimWorldTranslatorRmkXlsxReader.MaximumWorksheetBytes)
                    throw new InvalidDataException("RMK output exceeds the estimated worksheet-size limit.");
            }
        }
        if (aggregateCharacters > MaximumOutputTextCharacters)
            throw new InvalidDataException("RMK output exceeds the aggregate text-character limit.");
        if (estimatedWorksheetBytes > RimWorldTranslatorRmkXlsxReader.MaximumWorksheetBytes)
            throw new InvalidDataException("RMK output exceeds the estimated worksheet-size limit.");
    }

    private static XmlWriter CreateXmlWriter(Stream stream)
    {
        XmlWriterSettings settings = new XmlWriterSettings();
        settings.Encoding = Utf8NoBom;
        settings.Indent = false;
        settings.NewLineHandling = NewLineHandling.Entitize;
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
        string escaped = SecurityElement.Escape(CleanText(value)) ?? String.Empty;
        writer.WriteRaw(escaped.Replace("\r", "&#xD;", StringComparison.Ordinal).Replace("\n", "&#xA;", StringComparison.Ordinal));
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteWorksheet(ZipArchive archive, IList<RimWorldTranslatorRmkHistoryRow> rows, string sourceLanguage)
    {
        ZipArchiveEntry entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using (Stream stream = entry.Open())
        using (BoundedWorksheetStream boundedStream = new BoundedWorksheetStream(
                   stream,
                   RimWorldTranslatorRmkXlsxReader.MaximumWorksheetBytes))
        using (XmlWriter writer = CreateXmlWriter(boundedStream))
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

    private static CreatedFileIdentity? WriteWorkbookArchive(
        string outputPath,
        IList<RimWorldTranslatorRmkHistoryRow> rows,
        string sourceLanguage,
        bool requireMissingOutput = false)
    {
        CreatedFileIdentity? createdIdentity = null;
        using (FileStream? output = requireMissingOutput
                   ? new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
                   : null)
        using (ZipArchive archive = output == null
                   ? ZipFile.Open(outputPath, ZipArchiveMode.Create)
                   : new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
        {
            if (requireMissingOutput)
            {
                createdIdentity = CaptureCreatedFileIdentity(
                    output!.SafeFileHandle,
                    outputPath);
                PreparedArchiveCreatedTestHook?.Invoke(outputPath);
            }
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
        return createdIdentity;
    }

    private static XDocument LoadWorksheetDocument(ZipArchiveEntry entry)
    {
        return RimWorldTranslatorRmkXlsxReader.LoadWorksheetDocument(entry);
    }

    private static void SaveWorksheetDocument(Stream output, XDocument document)
    {
        using (BoundedWorksheetStream preflight = new BoundedWorksheetStream(
                   destination: null,
                   maximumBytes: RimWorldTranslatorRmkXlsxReader.MaximumWorksheetBytes))
        using (XmlWriter writer = CreateXmlWriter(preflight))
            document.Save(writer);

        using BoundedWorksheetStream boundedOutput = new BoundedWorksheetStream(
            output,
            RimWorldTranslatorRmkXlsxReader.MaximumWorksheetBytes);
        using XmlWriter outputWriter = CreateXmlWriter(boundedOutput);
        document.Save(outputWriter);
    }

    private static int GetColumnIndex(string? reference)
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
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);
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
        XAttribute? value = row.Attribute("r");
        if (value == null)
        {
            if (fallback < 1 || fallback > RimWorldTranslatorRmkXlsxReader.MaximumExcelRows)
                throw new InvalidDataException("Workbook row fallback is outside Excel's supported row range.");
            return fallback;
        }
        if (!Int32.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            || result < 1
            || result > RimWorldTranslatorRmkXlsxReader.MaximumExcelRows)
        {
            throw new InvalidDataException("Workbook row reference is outside Excel's supported row range.");
        }
        return result;
    }

    private static string GetCellValue(XElement cell, IList<string> sharedStrings)
    {
        string type = (string?)cell.Attribute("t") ?? String.Empty;
        if (type == "inlineStr")
            return String.Concat(cell.Descendants()
                .Where(delegate(XElement node) {
                    return node.Name.LocalName == "t"
                        && !node.Ancestors().Any(delegate(XElement ancestor) { return ancestor.Name.LocalName == "rPh"; });
                })
                .Select(delegate(XElement node) { return node.Value; }));
        XElement? valueNode = cell.Elements().FirstOrDefault(delegate(XElement node) { return node.Name.LocalName == "v"; });
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
            int column = GetColumnIndex((string?)cell.Attribute("r"));
            if (column < 0) column = sequentialColumn;
            result[column] = GetCellValue(cell, sharedStrings);
            sequentialColumn = column + 1;
        }
        return result;
    }

    private static string GetValue(Dictionary<int, string> values, int column)
    {
        string? value;
        return column >= 0 && values.TryGetValue(column, out value) ? value ?? String.Empty : String.Empty;
    }

    private static void SetCellValue(
        XElement row,
        int column,
        int rowNumber,
        string value,
        IList<string> sharedStrings)
    {
        XNamespace ns = row.Name.Namespace;
        XElement? cell = row.Elements().FirstOrDefault(delegate(XElement candidate) {
            return candidate.Name.LocalName == "c" && GetColumnIndex((string?)candidate.Attribute("r")) == column;
        });
        string reference = GetColumnName(column) + rowNumber.ToString(CultureInfo.InvariantCulture);
        string cleanedValue = CleanText(value);
        if (cell != null && String.Equals(GetCellValue(cell, sharedStrings), cleanedValue, StringComparison.Ordinal))
        {
            if (!String.Equals((string?)cell.Attribute("r"), reference, StringComparison.Ordinal))
                cell.SetAttributeValue("r", reference);
            return;
        }
        if (cell == null)
        {
            cell = new XElement(ns + "c");
            XElement? next = row.Elements().FirstOrDefault(delegate(XElement candidate) {
                return candidate.Name.LocalName == "c" && GetColumnIndex((string?)candidate.Attribute("r")) > column;
            });
            if (next == null) row.Add(cell); else next.AddBeforeSelf(cell);
        }
        cell.SetAttributeValue("r", reference);
        cell.SetAttributeValue("t", "inlineStr");
        cell.RemoveNodes();
        XElement text = new XElement(ns + "t", cleanedValue);
        text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        cell.Add(new XElement(ns + "is", text));
    }

    private static void MergeWorksheet(XDocument document, IList<string> sharedStrings, IList<RimWorldTranslatorRmkHistoryRow> rows, string sourceLanguage)
    {
        XElement? worksheet = document.Root;
        if (worksheet == null || worksheet.Name.LocalName != "worksheet") throw new InvalidDataException("Workbook worksheet XML is invalid.");
        XNamespace ns = worksheet.Name.Namespace;
        XElement? sheetData = worksheet.Elements().FirstOrDefault(delegate(XElement node) { return node.Name.LocalName == "sheetData"; });
        if (sheetData == null) throw new InvalidDataException("Workbook sheetData was not found.");

        int identifierColumn = -1;
        int classColumn = -1;
        int nodeColumn = -1;
        int requiredModsColumn = -1;
        int sourceColumn = -1;
        int translationColumn = -1;
        XElement? headerRow = null;
        int headerRowNumber = 1;
        int nextAvailableColumn = sheetData.Descendants()
            .Where(delegate(XElement node) { return node.Name.LocalName == "c"; })
            .Select(delegate(XElement cell) { return GetColumnIndex((string?)cell.Attribute("r")); })
            .Where(delegate(int column) { return column >= 0; })
            .DefaultIfEmpty(-1)
            .Max() + 1;
        foreach (XElement row in sheetData.Elements().Where(delegate(XElement node) { return node.Name.LocalName == "row"; }).Take(10))
        {
            Dictionary<int, string> values = GetRowValues(row, sharedStrings);
            int rowIdentifierColumn = -1;
            int rowClassColumn = -1;
            int rowNodeColumn = -1;
            int rowRequiredModsColumn = -1;
            int rowSourceColumn = -1;
            int rowTranslationColumn = -1;
            foreach (KeyValuePair<int, string> pair in values)
            {
                string header = (pair.Value ?? String.Empty).Trim();
                string lower = header.ToLowerInvariant();
                if (lower.StartsWith("class+node", StringComparison.Ordinal) || lower.Contains("identifier (key", StringComparison.Ordinal)) rowIdentifierColumn = pair.Key;
                else if (lower.StartsWith("class ", StringComparison.Ordinal) || lower.StartsWith("class[", StringComparison.Ordinal)) rowClassColumn = pair.Key;
                else if (lower.StartsWith("node ", StringComparison.Ordinal) || lower.StartsWith("node[", StringComparison.Ordinal)) rowNodeColumn = pair.Key;
                else if (lower.StartsWith("required mods", StringComparison.Ordinal)) rowRequiredModsColumn = pair.Key;
                if (lower.Contains("[source string]", StringComparison.Ordinal)) rowSourceColumn = pair.Key;
                if (lower.Contains("[translation]", StringComparison.Ordinal)) rowTranslationColumn = pair.Key;
            }
            if (rowSourceColumn >= 0 && rowTranslationColumn >= 0
                && (rowIdentifierColumn >= 0 || (rowClassColumn >= 0 && rowNodeColumn >= 0)))
            {
                if (headerRow != null) throw new InvalidDataException("Workbook worksheet contains multiple RMK header rows.");
                if (rowIdentifierColumn < 0) rowIdentifierColumn = nextAvailableColumn++;
                if (rowClassColumn < 0) rowClassColumn = nextAvailableColumn++;
                if (rowNodeColumn < 0) rowNodeColumn = nextAvailableColumn++;
                if (rowRequiredModsColumn < 0) rowRequiredModsColumn = nextAvailableColumn++;
                if (nextAvailableColumn > 16384) throw new InvalidDataException("Workbook has no available columns for the RMK compatibility headers.");
                headerRow = row;
                headerRowNumber = GetRowNumber(row, 1);
                identifierColumn = rowIdentifierColumn;
                classColumn = rowClassColumn;
                nodeColumn = rowNodeColumn;
                requiredModsColumn = rowRequiredModsColumn;
                sourceColumn = rowSourceColumn;
                translationColumn = rowTranslationColumn;
            }
        }
        if (headerRow == null) throw new InvalidDataException("Workbook does not use the RMK translation columns.");

        SetCellValue(headerRow, identifierColumn, headerRowNumber, "Class+Node [(Identifier (Key)]", sharedStrings);
        SetCellValue(headerRow, classColumn, headerRowNumber, "Class [Not chosen]", sharedStrings);
        SetCellValue(headerRow, nodeColumn, headerRowNumber, "Node [Not chosen]", sharedStrings);
        SetCellValue(headerRow, requiredModsColumn, headerRowNumber, "Required Mods [Not chosen]", sharedStrings);
        SetCellValue(headerRow, sourceColumn, headerRowNumber, CleanText(sourceLanguage) + " [Source string]", sharedStrings);
        SetCellValue(headerRow, translationColumn, headerRowNumber, "Korean (한국어) [Translation]", sharedStrings);

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
            XElement? row;
            int rowNumber;
            if (existingRows.TryGetValue(desired.Identifier, out row) && row != null)
            {
                rowNumber = GetRowNumber(row, maxRow + 1);
                if (rowNumber > maxRow) maxRow = rowNumber;
            }
            else
            {
                if (maxRow >= RimWorldTranslatorRmkXlsxReader.MaximumExcelRows)
                    throw new InvalidDataException("RMK output cannot append a row beyond Excel's supported row range.");
                rowNumber = ++maxRow;
                row = new XElement(ns + "row", new XAttribute("r", rowNumber.ToString(CultureInfo.InvariantCulture)));
                sheetData.Add(row);
                existingRows[desired.Identifier] = row;
            }
            SetCellValue(row, identifierColumn, rowNumber, desired.Identifier, sharedStrings);
            SetCellValue(row, classColumn, rowNumber, desired.ClassName, sharedStrings);
            SetCellValue(row, nodeColumn, rowNumber, desired.Key, sharedStrings);
            SetCellValue(row, requiredModsColumn, rowNumber, desired.RequiredMods, sharedStrings);
            SetCellValue(row, sourceColumn, rowNumber, desired.Source, sharedStrings);
            SetCellValue(row, translationColumn, rowNumber, desired.Translation, sharedStrings);
        }

        int maxColumn = Math.Max(translationColumn, Math.Max(sourceColumn, Math.Max(requiredModsColumn, Math.Max(nodeColumn, Math.Max(classColumn, identifierColumn)))));
        foreach (XElement cell in sheetData.Descendants().Where(delegate(XElement node) { return node.Name.LocalName == "c"; }))
        {
            int column = GetColumnIndex((string?)cell.Attribute("r"));
            if (column > maxColumn) maxColumn = column;
        }
        XElement? dimension = worksheet.Elements().FirstOrDefault(delegate(XElement node) { return node.Name.LocalName == "dimension"; });
        if (dimension == null)
        {
            dimension = new XElement(ns + "dimension");
            worksheet.AddFirst(dimension);
        }
        dimension.SetAttributeValue("ref", "A1:" + GetColumnName(maxColumn) + maxRow.ToString(CultureInfo.InvariantCulture));
        XElement? autoFilter = worksheet.Elements().FirstOrDefault(delegate(XElement node) { return node.Name.LocalName == "autoFilter"; });
        if (autoFilter != null)
        {
            int filterStartColumn = identifierColumn;
            int filterEndColumn = translationColumn;
            string filterReference = (string?)autoFilter.Attribute("ref") ?? String.Empty;
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

    private static CreatedFileIdentity? UpdateWorkbookArchive(
        string existingPath,
        string outputPath,
        IList<RimWorldTranslatorRmkHistoryRow> rows,
        string sourceLanguage,
        bool overwriteOutput = true)
    {
        File.Copy(existingPath, outputPath, overwriteOutput);
        CreatedFileIdentity? createdIdentity = null;
        if (!overwriteOutput)
        {
            createdIdentity = CaptureCreatedFileIdentity(outputPath);
            PreparedArchiveCreatedTestHook?.Invoke(outputPath);
        }
        _ = RimWorldTranslatorRmkXlsxReader.Read(outputPath);
        List<string> sharedStrings;
        string worksheetName;
        using (ZipArchive readArchive = RimWorldTranslatorRmkXlsxReader.OpenValidatedWorkbook(outputPath))
        {
            sharedStrings = RimWorldTranslatorRmkXlsxReader.ReadSharedStrings(readArchive);
            worksheetName = RimWorldTranslatorRmkXlsxReader.FindWorksheetEntry(readArchive);
        }
        using (ZipArchive archive = ZipFile.Open(outputPath, ZipArchiveMode.Update))
        {
            if (String.IsNullOrWhiteSpace(worksheetName)) throw new InvalidDataException("Workbook worksheet was not found.");
            ZipArchiveEntry? oldEntry = archive.GetEntry(worksheetName);
            if (oldEntry == null) throw new InvalidDataException("Workbook worksheet entry was not found.");
            XDocument document = LoadWorksheetDocument(oldEntry);
            MergeWorksheet(document, sharedStrings, rows, sourceLanguage);
            oldEntry.Delete();
            ZipArchiveEntry newEntry = archive.CreateEntry(worksheetName, CompressionLevel.Optimal);
            using (Stream stream = newEntry.Open()) SaveWorksheetDocument(stream, document);
        }
        return createdIdentity;
    }

    private static void FlushAndValidateWorkbook(
        string workbookPath,
        IList<RimWorldTranslatorRmkHistoryRow> expectedRows,
        string expectedSourceLanguage)
    {
        using (FileStream stream = new FileStream(workbookPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            stream.Flush(true);

        ValidateWorkbookContents(
            workbookPath,
            expectedRows,
            expectedSourceLanguage,
            requireExactRowCount: false);
    }

    private static void ValidateWorkbookContents(
        string workbookPath,
        IList<RimWorldTranslatorRmkHistoryRow> expectedRows,
        string expectedSourceLanguage,
        bool requireExactRowCount)
    {
        RimWorldTranslatorRmkHistoryData reopened = RimWorldTranslatorRmkXlsxReader.Read(workbookPath);
        if (!String.Equals(reopened.SourceLanguage, expectedSourceLanguage, StringComparison.Ordinal))
            throw new InvalidDataException("Temporary RMK workbook source-language header validation failed.");
        if (requireExactRowCount && reopened.Rows.Count != expectedRows.Count)
            throw new InvalidDataException("Temporary RMK workbook row-count validation failed.");
        foreach (RimWorldTranslatorRmkHistoryRow expected in expectedRows)
        {
            RimWorldTranslatorRmkHistoryRow? actual;
            string expectedIdentifier = CleanText(expected.Identifier).Trim();
            string expectedKey = CleanText(expected.Key).Trim();
            if (expectedKey.Length == 0)
            {
                int separator = expectedIdentifier.IndexOf('+');
                if (separator >= 0 && separator + 1 < expectedIdentifier.Length)
                    expectedKey = expectedIdentifier.Substring(separator + 1);
            }
            if (!reopened.Map.TryGetValue(expectedIdentifier, out actual)
                || actual == null
                || !String.Equals(actual.ClassName, CleanText(expected.ClassName).Trim(), StringComparison.Ordinal)
                || !String.Equals(actual.Key, expectedKey, StringComparison.Ordinal)
                || !String.Equals(actual.RequiredMods, CleanText(expected.RequiredMods), StringComparison.Ordinal)
                || !String.Equals(actual.Source, CleanText(expected.Source), StringComparison.Ordinal)
                || !String.Equals(actual.Translation, CleanText(expected.Translation), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Temporary RMK workbook row validation failed for identifier: " + expectedIdentifier);
            }
        }
    }

    public static void WritePrepared(
        string? sourceWorkbookPath,
        string? preparedPath,
        IEnumerable<RimWorldTranslatorRmkHistoryRow>? sourceRows,
        string? sourceLanguage)
    {
        _ = WritePreparedCore(sourceWorkbookPath, preparedPath, sourceRows, sourceLanguage);
    }

    public static RimWorldTranslatorPreparedFileEvidence WritePreparedWithEvidence(
        string? sourceWorkbookPath,
        string? preparedPath,
        IEnumerable<RimWorldTranslatorRmkHistoryRow>? sourceRows,
        string? sourceLanguage)
    {
        PreparedWorkbookState prepared = WritePreparedCore(
            sourceWorkbookPath,
            preparedPath,
            sourceRows,
            sourceLanguage);
        return ValidatePinnedPreparedWorkbook(prepared);
    }

    private static PreparedWorkbookState WritePreparedCore(
        string? sourceWorkbookPath,
        string? preparedPath,
        IEnumerable<RimWorldTranslatorRmkHistoryRow>? sourceRows,
        string? sourceLanguage)
    {
        if (String.IsNullOrWhiteSpace(preparedPath))
            throw new ArgumentException("Prepared workbook path is required.", nameof(preparedPath));
        string outputPath = Path.GetFullPath(preparedPath);
        string? directory = Path.GetDirectoryName(outputPath);
        if (String.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidDataException("Prepared workbook directory is invalid.");
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new IOException("Prepared workbook output must be a missing reserved leaf.");

        string? sourcePath = null;
        if (!String.IsNullOrWhiteSpace(sourceWorkbookPath))
        {
            sourcePath = Path.GetFullPath(sourceWorkbookPath);
            if (!File.Exists(sourcePath) || Directory.Exists(sourcePath))
                throw new FileNotFoundException("RMK source workbook is missing.", sourcePath);
            if (!String.Equals(Path.GetExtension(sourcePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Source workbook path must use the .xlsx extension.", nameof(sourceWorkbookPath));
        }

        string effectiveSourceLanguage;
        if (String.IsNullOrEmpty(sourceLanguage)) effectiveSourceLanguage = "English";
        else
        {
            if (sourceLanguage.Length > MaximumSourceLanguageCharacters)
                throw new InvalidDataException("RMK output source language exceeds its character limit.");
            effectiveSourceLanguage = String.IsNullOrWhiteSpace(sourceLanguage)
                ? "English"
                : CleanText(sourceLanguage.Trim());
        }
        List<RimWorldTranslatorRmkHistoryRow> rows = sourceRows == null
            ? new List<RimWorldTranslatorRmkHistoryRow>()
            : sourceRows.Where(delegate(RimWorldTranslatorRmkHistoryRow row)
                {
                    return row != null && !String.IsNullOrWhiteSpace(row.Identifier);
                })
                .Take(RimWorldTranslatorRmkXlsxReader.MaximumHistoryRows + 1)
                .ToList();
        if (rows.Count > RimWorldTranslatorRmkXlsxReader.MaximumHistoryRows)
            throw new InvalidDataException("RMK output exceeds the history-row limit.");
        foreach (RimWorldTranslatorRmkHistoryRow row in rows)
        {
            foreach (string value in new string[]
                     {
                         row.Identifier, row.ClassName, row.Key, row.RequiredMods, row.Source, row.Translation
                     })
            {
                if ((value ?? String.Empty).Length > RimWorldTranslatorRmkXlsxReader.MaximumCellCharacters)
                    throw new InvalidDataException("RMK output contains a value that exceeds the Excel cell limit.");
            }
        }
        ValidateOutputBudget(rows, effectiveSourceLanguage);
        string? duplicateIdentifier = rows
            .GroupBy(delegate(RimWorldTranslatorRmkHistoryRow row)
                {
                    return CleanText(row.Identifier).Trim();
                }, StringComparer.OrdinalIgnoreCase)
            .Where(delegate(IGrouping<string, RimWorldTranslatorRmkHistoryRow> group)
                {
                    return group.Count() > 1;
                })
            .Select(delegate(IGrouping<string, RimWorldTranslatorRmkHistoryRow> group)
                {
                    return group.Key;
                })
            .FirstOrDefault();
        if (!String.IsNullOrEmpty(duplicateIdentifier))
            throw new InvalidDataException("RMK output contains a duplicate identifier: " + duplicateIdentifier);

        CreatedFileIdentity? createdIdentity = sourcePath is null
            ? WriteWorkbookArchive(
                outputPath,
                rows,
                effectiveSourceLanguage,
                requireMissingOutput: true)
            : UpdateWorkbookArchive(
                sourcePath,
                outputPath,
                rows,
                effectiveSourceLanguage,
                overwriteOutput: false);
        if (createdIdentity is null)
            throw new InvalidDataException(
                "The prepared RMK workbook creation identity was not captured.");
        FlushAndValidateWorkbook(outputPath, rows, effectiveSourceLanguage);
        return new PreparedWorkbookState(
            outputPath,
            rows,
            effectiveSourceLanguage,
            createdIdentity);
    }

    private static CreatedFileIdentity CaptureCreatedFileIdentity(string path)
    {
        using (SafeFileHandle handle = OpenPreparedFile(
                   path,
                   GenericRead | FileReadAttributes,
                   FileShareRead,
                   IntPtr.Zero,
                   OpenExisting,
                   FileFlagOpenReparsePoint,
                   IntPtr.Zero))
        {
            if (handle.IsInvalid)
                throw new IOException("The newly prepared RMK workbook could not be identity-pinned.");
            return CaptureCreatedFileIdentity(handle, path);
        }
    }

    private static CreatedFileIdentity CaptureCreatedFileIdentity(
        SafeFileHandle handle,
        string expectedPath)
    {
        NativeByHandleFileInformation information;
        if (!GetFileInformationByHandle(handle, out information))
            throw new IOException("The newly prepared RMK workbook identity could not be read.");
        const uint rejectedAttributes =
            (uint)(FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint);
        ulong fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        if ((information.FileAttributes & rejectedAttributes) != 0
            || information.NumberOfLinks != 1
            || fileIndex == 0)
        {
            throw new InvalidDataException(
                "The newly prepared RMK workbook is not a unique regular file.");
        }
        string canonical = Path.GetFullPath(GetFinalPath(handle));
        if (!canonical.Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The newly prepared RMK workbook resolved outside its reserved path.");
        return new CreatedFileIdentity(information.VolumeSerialNumber, fileIndex);
    }

    private static RimWorldTranslatorPreparedFileEvidence ValidatePinnedPreparedWorkbook(
        PreparedWorkbookState prepared)
    {
        using (SafeFileHandle handle = OpenPreparedFile(
                   prepared.OutputPath,
                   GenericRead | FileReadAttributes,
                   FileShareRead,
                   IntPtr.Zero,
                   OpenExisting,
                   FileFlagOpenReparsePoint,
                   IntPtr.Zero))
        {
            if (handle.IsInvalid)
                throw new IOException("The validated RMK workbook could not be identity-pinned.");
            NativeByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
                throw new IOException("The validated RMK workbook identity could not be read.");

            const uint rejectedAttributes =
                (uint)(FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint);
            ulong fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            long length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
            if ((information.FileAttributes & rejectedAttributes) != 0
                || information.NumberOfLinks != 1
                || fileIndex == 0
                || length < 0
                || information.VolumeSerialNumber != prepared.CreatedIdentity.VolumeSerialNumber
                || fileIndex != prepared.CreatedIdentity.FileIndex)
            {
                throw new InvalidDataException(
                    "The validated RMK workbook no longer matches its unique creation identity.");
            }

            string canonical = Path.GetFullPath(GetFinalPath(handle));
            if (!canonical.Equals(prepared.OutputPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The validated RMK workbook resolved outside its reserved prepared path.");

            ValidateWorkbookContents(
                prepared.OutputPath,
                prepared.Rows,
                prepared.SourceLanguage,
                requireExactRowCount: true);

            using (FileStream stream = new FileStream(
                       handle,
                       FileAccess.Read,
                       64 * 1024,
                       false))
            {
                if (stream.Length != length)
                    throw new InvalidDataException(
                        "The pinned RMK workbook length changed during validation.");
                byte[] hash = SHA256.HashData(stream);
                long fileTime = ((long)information.LastWriteTime.HighDateTime << 32)
                                | information.LastWriteTime.LowDateTime;
                return new RimWorldTranslatorPreparedFileEvidence(
                    information.VolumeSerialNumber,
                    fileIndex,
                    length,
                    DateTime.FromFileTimeUtc(fileTime).Ticks,
                    BitConverter.ToString(hash).Replace("-", String.Empty, StringComparison.Ordinal));
            }
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        int capacity = 512;
        while (capacity <= 32 * 1024)
        {
            char[] buffer = new char[capacity];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)capacity, 0);
            if (length == 0)
                throw new IOException("The validated RMK workbook canonical path could not be read.");
            if (length < capacity)
            {
                string value = new string(buffer, 0, checked((int)length));
                if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    return string.Concat(@"\\", value.AsSpan(8));
                if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    return value[4..];
                return value;
            }
            capacity = checked((int)length + 1);
        }
        throw new IOException("The validated RMK workbook canonical path is too long.");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern SafeFileHandle OpenPreparedFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out NativeByHandleFileInformation information);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathCharacters,
        uint flags);

    public static void Write(string? workbookPath, IEnumerable<RimWorldTranslatorRmkHistoryRow>? sourceRows, string? sourceLanguage)
    {
        if (String.IsNullOrWhiteSpace(workbookPath)) throw new ArgumentException("Workbook path is required.", nameof(workbookPath));
        string fullPath = Path.GetFullPath(workbookPath);
        if (!String.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Workbook path must use the .xlsx extension.", nameof(workbookPath));
        string? directory = Path.GetDirectoryName(fullPath);
        if (String.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("Workbook directory is invalid.");
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("Refusing to replace a read-only RMK workbook: " + fullPath);
        string effectiveSourceLanguage;
        if (String.IsNullOrEmpty(sourceLanguage))
        {
            effectiveSourceLanguage = "English";
        }
        else
        {
            if (sourceLanguage.Length > MaximumSourceLanguageCharacters)
                throw new InvalidDataException("RMK output source language exceeds its character limit.");
            effectiveSourceLanguage = String.IsNullOrWhiteSpace(sourceLanguage)
                ? "English"
                : CleanText(sourceLanguage.Trim());
        }
        List<RimWorldTranslatorRmkHistoryRow> rows = sourceRows == null
            ? new List<RimWorldTranslatorRmkHistoryRow>()
            : sourceRows.Where(delegate(RimWorldTranslatorRmkHistoryRow row) { return row != null && !String.IsNullOrWhiteSpace(row.Identifier); })
                .Take(RimWorldTranslatorRmkXlsxReader.MaximumHistoryRows + 1)
                .ToList();
        if (rows.Count > RimWorldTranslatorRmkXlsxReader.MaximumHistoryRows)
            throw new InvalidDataException("RMK output exceeds the history-row limit.");
        foreach (RimWorldTranslatorRmkHistoryRow row in rows)
        {
            foreach (string value in new string[] {
                         row.Identifier, row.ClassName, row.Key, row.RequiredMods, row.Source, row.Translation
                     })
                if ((value ?? String.Empty).Length > RimWorldTranslatorRmkXlsxReader.MaximumCellCharacters)
                    throw new InvalidDataException("RMK output contains a value that exceeds the Excel cell limit.");
        }
        ValidateOutputBudget(rows, effectiveSourceLanguage);
        string? duplicateIdentifier = rows
            .GroupBy(delegate(RimWorldTranslatorRmkHistoryRow row) { return CleanText(row.Identifier).Trim(); }, StringComparer.OrdinalIgnoreCase)
            .Where(delegate(IGrouping<string, RimWorldTranslatorRmkHistoryRow> group) { return group.Count() > 1; })
            .Select(delegate(IGrouping<string, RimWorldTranslatorRmkHistoryRow> group) { return group.Key; })
            .FirstOrDefault();
        if (!String.IsNullOrEmpty(duplicateIdentifier))
            throw new InvalidDataException("RMK output contains a duplicate identifier: " + duplicateIdentifier);
        Directory.CreateDirectory(directory);
        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        string backupPath = fullPath + ".bak";
        try
        {
            if (File.Exists(fullPath))
                _ = UpdateWorkbookArchive(fullPath, temporaryPath, rows, effectiveSourceLanguage);
            else
                _ = WriteWorkbookArchive(temporaryPath, rows, effectiveSourceLanguage);
            FlushAndValidateWorkbook(temporaryPath, rows, effectiveSourceLanguage);
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, backupPath, true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
