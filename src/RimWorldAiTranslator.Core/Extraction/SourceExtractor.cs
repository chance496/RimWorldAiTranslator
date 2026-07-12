using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Core.Extraction;

public sealed partial class SourceExtractor
{
    private static readonly string[] DefaultAllowedFields =
    [
        "label", "labelshort", "description", "jobstring", "reportstring", "deathmessage", "deathmessagefemale",
        "deathmessagemale", "pawnsplural", "leadertitle", "arrivedletter", "customlabel", "gizmolabel",
        "gizmodescription", "commandlabel", "commanddescription", "letterlabel", "lettertext", "header", "headertip",
        "summary", "formatstring", "formatstringunfinalized", "fixedname", "reason"
    ];

    private static readonly string[] DefaultDeniedFields =
    [
        "defname", "parentname", "classname", "class", "thingclass", "workerclass", "compclass", "hediffclass",
        "thoughtclass", "abilityclass", "worldobjectclass", "nodeclass", "texpath", "texname", "graphicpath",
        "shader", "sound", "sounddef", "iconpath", "packageid", "xpath", "operation", "colorchannel", "rendernode",
        "rendertree", "rendertreedef", "bodypart", "bodypartdef", "bodytype", "headtype", "racedef", "thingdef",
        "pawnkinddef", "jobdef", "statdef", "skilldef", "hediffdef", "genedef", "tagdef", "debuglabel"
    ];

    private readonly HashSet<string> allowedFields = new(DefaultAllowedFields, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> deniedFields = new(DefaultDeniedFields, StringComparer.OrdinalIgnoreCase);

    public SourceExtractor(string? defFieldRulesPath = null)
    {
        if (!string.IsNullOrWhiteSpace(defFieldRulesPath) && File.Exists(defFieldRulesPath))
        {
            LoadRules(defFieldRulesPath);
            RimWorldTranslatorRmkXlsxReader.LoadDefFieldRules(defFieldRulesPath);
        }
    }

    public SourceExtractionResult Extract(
        string modRoot,
        string sourceLanguageFolder = "Auto",
        string outputFilePrefix = "CodexAI",
        bool includePatches = false,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var skipped = new List<SkippedSourceEntry>();
        var entries = new List<SourceEntry>();
        var contentRoots = GetActiveContentRoots(modRoot, warnings);
        var languages = GetSourceLanguageRoots(contentRoots, sourceLanguageFolder);

        foreach (var language in languages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in EnumerateXmlFiles(language.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(language.Path, file);
                var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                var kind = parts.Length > 0 ? parts[0] : "Keyed";
                var typeName = kind.Equals("DefInjected", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 ? parts[1] : string.Empty;
                ImportLanguageFile(file, relative, kind, typeName, entries, skipped, warnings);
            }
        }

        if (includePatches)
        {
            warnings.Add("Patch XML translation is disabled because RimWorld patch conditions and list handles cannot be resolved safely outside the game. Defs and language files will still be processed.");
        }

        var seenDefsRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contentRoot in contentRoots)
        {
            var defsRoot = Path.Combine(contentRoot, "Defs");
            if (!Directory.Exists(defsRoot) || !seenDefsRoots.Add(Path.GetFullPath(defsRoot)))
            {
                continue;
            }

            foreach (var file in EnumerateXmlFiles(defsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImportDefsFile(file, outputFilePrefix, entries, skipped, warnings);
            }
        }

        return new SourceExtractionResult(entries, skipped, languages, warnings);
    }

    public IReadOnlyList<string> GetActiveContentRoots(string modRoot, ICollection<string>? warnings = null)
    {
        var modFull = PathSafety.Normalize(modRoot);
        var roots = new List<string> { modFull };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modFull };
        var loadFolders = Path.Combine(modFull, "LoadFolders.xml");
        if (!File.Exists(loadFolders))
        {
            return roots;
        }

        try
        {
            var document = SafeXml.Load(loadFolders, 4 * 1024 * 1024);
            var version = document.Root?.Elements()
                .Where(element => VersionNodeRegex().IsMatch(element.Name.LocalName))
                .OrderByDescending(element => GetVersionScore(element.Name.LocalName))
                .FirstOrDefault();
            if (version is null)
            {
                return roots;
            }

            foreach (var item in version.Elements().Where(element => element.Name.LocalName == "li"))
            {
                var relative = item.Value.Trim();
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                var candidate = relative is "/" or "\\" or "." ? modFull : Path.Combine(modFull, relative);
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                var candidateFull = PathSafety.Normalize(candidate);
                if (!candidateFull.Equals(modFull, StringComparison.OrdinalIgnoreCase)
                    && !PathSafety.IsStrictlyInside(candidateFull, modFull))
                {
                    continue;
                }

                if (seen.Add(candidateFull))
                {
                    roots.Add(candidateFull);
                }
            }
        }
        catch (Exception ex)
        {
            warnings?.Add($"Could not read LoadFolders.xml; using the selected mod root only. {ex.GetType().Name}: {ex.Message}");
        }

        return roots;
    }

    public IReadOnlyList<SourceLanguageRoot> GetSourceLanguageRoots(IEnumerable<string> contentRoots, string sourceLanguageFolder)
    {
        var roots = contentRoots.ToArray();
        if (!string.IsNullOrWhiteSpace(sourceLanguageFolder) && !sourceLanguageFolder.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = Path.IsPathRooted(sourceLanguageFolder)
                ? new[] { sourceLanguageFolder }
                : roots.SelectMany(root =>
                {
                    var languagesRoot = Path.Combine(root, "Languages");
                    if (!Directory.Exists(languagesRoot)) return Array.Empty<string>();
                    var exact = Path.Combine(languagesRoot, sourceLanguageFolder);
                    if (HasXml(exact)) return new[] { exact };
                    var match = Directory.EnumerateDirectories(languagesRoot, "*", SearchOption.TopDirectoryOnly)
                        .Where(path => Path.GetFileName(path).Equals(sourceLanguageFolder, StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(path).StartsWith(sourceLanguageFolder + " ", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    return match is null ? Array.Empty<string>() : new[] { match };
                });
            var result = candidates
                .Where(HasXml)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new SourceLanguageRoot(Path.GetFileName(path), path, -1))
                .ToArray();
            return result.Length > 0
                ? result
                : throw new InvalidDataException($"Source language folder has no XML files: {sourceLanguageFolder}");
        }

        var candidatesAuto = roots
            .Select(root => Path.Combine(root, "Languages"))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            .Where(path => !IsExcludedLanguage(Path.GetFileName(path)) && HasXml(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new SourceLanguageRoot(Path.GetFileName(path), path, GetLanguageRank(Path.GetFileName(path))))
            .ToArray();
        var best = candidatesAuto.OrderBy(candidate => candidate.Rank).ThenBy(candidate => candidate.Name, StringComparer.Ordinal).FirstOrDefault();
        return best is null
            ? []
            : candidatesAuto.Where(candidate => candidate.Name.Equals(best.Name, StringComparison.Ordinal)).ToArray();
    }

    public Dictionary<string, ExistingLanguageEntry> ReadExistingLanguageMap(string languageRoot, ICollection<string>? warnings = null)
    {
        var map = new Dictionary<string, ExistingLanguageEntry>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(languageRoot))
        {
            return map;
        }

        var fullRoot = PathSafety.Normalize(languageRoot);
        foreach (var file in EnumerateXmlFiles(fullRoot))
        {
            try
            {
                var relative = Path.GetRelativePath(fullRoot, file);
                var localizationNamespace = GetNamespaceFromRelativePath(relative);
                if (string.IsNullOrWhiteSpace(localizationNamespace))
                {
                    continue;
                }

                foreach (var raw in RimWorldTranslatorRmkXlsxReader.ReadLanguageData(file))
                {
                    var identity = GetLocalizationIdentity(localizationNamespace, raw.Key);
                    if (string.IsNullOrWhiteSpace(identity) || ambiguous.Contains(identity))
                    {
                        continue;
                    }

                    if (map.ContainsKey(identity))
                    {
                        map.Remove(identity);
                        ambiguous.Add(identity);
                        warnings?.Add($"Ignoring duplicated existing localization identity: {localizationNamespace} / {raw.Key}");
                    }
                    else
                    {
                        map[identity] = new ExistingLanguageEntry(NormalizeText(raw.Text), relative);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings?.Add($"Skipping unreadable existing language XML: {file}. {ex.GetType().Name}: {ex.Message}");
            }
        }

        return map;
    }

    public static string GetLocalizationIdentity(string localizationNamespace, string key) =>
        string.IsNullOrWhiteSpace(localizationNamespace) || string.IsNullOrWhiteSpace(key)
            ? string.Empty
            : $"namespace:{localizationNamespace.Trim()}|key:{key.Trim()}";

    public static string GetNamespaceFromRelativePath(string relativePath)
    {
        var parts = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }
        if (parts[0].Equals("Keyed", StringComparison.OrdinalIgnoreCase))
        {
            return "Keyed";
        }
        return parts[0].Equals("DefInjected", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private void ImportLanguageFile(
        string file,
        string targetRelativePath,
        string kind,
        string typeName,
        ICollection<SourceEntry> entries,
        ICollection<SkippedSourceEntry> skipped,
        ICollection<string> warnings)
    {
        IReadOnlyList<RimWorldTranslatorRawSourceEntry> rawEntries;
        try
        {
            rawEntries = RimWorldTranslatorRmkXlsxReader.ReadLanguageData(file);
        }
        catch (Exception ex)
        {
            warnings.Add($"Skipping unreadable language XML: {file}. {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (var raw in rawEntries)
        {
            AddCandidate(raw, file, targetRelativePath, kind, typeName, entries, skipped);
        }
    }

    private void ImportDefsFile(
        string file,
        string outputFilePrefix,
        ICollection<SourceEntry> entries,
        ICollection<SkippedSourceEntry> skipped,
        ICollection<string> warnings)
    {
        RimWorldTranslatorDefReadResult result;
        try
        {
            result = RimWorldTranslatorRmkXlsxReader.ReadDefsDetailed(file);
        }
        catch (Exception ex)
        {
            warnings.Add($"Skipping unreadable Def XML: {file}. {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (var raw in result.Excluded)
        {
            var text = NormalizeText(raw.Text);
            if (!HasHumanText(text) || LooksLikeCodeOrPath(text, raw.Field))
            {
                continue;
            }

            var target = Path.Combine("DefInjected", raw.TypeName, outputFilePrefix + ".xml");
            var reason = GetInternalIdentifierReason(raw.Key, "DefInjected", raw.TypeName, raw.Field)
                ?? $"RimWorld NoTranslate or runtime field '{raw.Field}'";
            skipped.Add(NewSkipped(raw.Key, text, raw.TypeName, raw.Field, file, target, reason));
        }

        foreach (var raw in result.Entries)
        {
            var target = Path.Combine("DefInjected", raw.TypeName, outputFilePrefix + ".xml");
            AddCandidate(raw, file, target, "DefInjected", raw.TypeName, entries, skipped);
        }
    }

    private void AddCandidate(
        RimWorldTranslatorRawSourceEntry raw,
        string sourceFile,
        string target,
        string kind,
        string typeName,
        ICollection<SourceEntry> entries,
        ICollection<SkippedSourceEntry> skipped)
    {
        var text = NormalizeText(raw.Text);
        if (!HasHumanText(text) || LooksLikeCodeOrPath(text, raw.Field))
        {
            return;
        }

        var reason = GetInternalIdentifierReason(raw.Key, kind, typeName, raw.Field);
        if (reason is not null)
        {
            skipped.Add(NewSkipped(raw.Key, text, typeName, raw.Field, sourceFile, target, reason));
            return;
        }

        entries.Add(new SourceEntry
        {
            Key = raw.Key,
            Text = text,
            Kind = kind,
            TypeName = typeName,
            TargetRelativePath = target,
            SourceFile = sourceFile,
            Field = raw.Field
        });
    }

    public string? GetInternalIdentifierReason(string key, string kind, string typeName, string field)
    {
        if (string.IsNullOrWhiteSpace(key) || !kind.Equals("DefInjected", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var keyLower = key.Trim().ToLowerInvariant();
        var typeLower = (typeName ?? string.Empty).Trim().ToLowerInvariant();
        var fieldLower = string.IsNullOrWhiteSpace(field) ? keyLower[(keyLower.LastIndexOf('.') + 1)..] : field.Trim().ToLowerInvariant();
        var displayField = DisplayFieldRegex().IsMatch(fieldLower);
        if (deniedFields.Contains(fieldLower)) return $"RimWorld NoTranslate field '{fieldLower}'";
        if (TechnicalFieldRegex().IsMatch(fieldLower)) return $"internal reference field '{fieldLower}'";
        if (keyLower.Contains(".alienrace.generalsettings.alienpartgenerator.colorchannels.", StringComparison.Ordinal)) return "AlienRace color-channel identifier";
        if (fieldLower == "name" && keyLower.Contains(".alienrace.", StringComparison.Ordinal)) return "AlienRace internal name";
        if (fieldLower == "name" && RuntimeListRegex().IsMatch(keyLower)) return "runtime list identifier";
        if (RenderPathRegex().IsMatch(keyLower) && !displayField) return "rendering or graphic-path identifier";
        if (typeLower.Contains("pawnrendertreedef", StringComparison.Ordinal) && !displayField) return "PawnRenderTreeDef internal identifier";
        return null;
    }

    private void LoadRules(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 1024 * 1024)
        {
            throw new InvalidDataException($"Def field rule file is too large: {path}");
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var match = RuleRegex().Match(raw);
            if (!match.Success)
            {
                continue;
            }

            if (match.Groups[1].Value.Equals("allow", StringComparison.OrdinalIgnoreCase))
            {
                allowed.Add(match.Groups[2].Value);
            }
            else
            {
                deniedFields.Add(match.Groups[2].Value);
            }
        }

        if (allowed.Count > 0)
        {
            allowedFields.Clear();
            allowedFields.UnionWith(allowed);
        }
    }

    private static SkippedSourceEntry NewSkipped(string key, string source, string defClass, string field, string sourceFile, string target, string reason) =>
        new()
        {
            Key = key,
            Source = source,
            Kind = "DefInjected",
            DefClass = defClass,
            Field = field,
            SourceFile = sourceFile,
            Target = target,
            Reason = reason
        };

    private static IEnumerable<string> EnumerateXmlFiles(string root) =>
        Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    private static bool HasXml(string path) => Directory.Exists(path) && Directory.EnumerateFiles(path, "*.xml", SearchOption.AllDirectories).Any();

    private static bool IsExcludedLanguage(string name) =>
        string.IsNullOrWhiteSpace(name)
        || name.StartsWith("Korean", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("한국", StringComparison.OrdinalIgnoreCase);

    private static int GetLanguageRank(string name) => name switch
    {
        "English" => 0,
        _ when name.StartsWith("ChineseSimplified", StringComparison.Ordinal) => 10,
        _ when name.StartsWith("ChineseTraditional", StringComparison.Ordinal) => 11,
        _ when name.StartsWith("Japanese", StringComparison.Ordinal) => 20,
        _ when name.StartsWith("Spanish", StringComparison.Ordinal) => 40,
        _ when name.StartsWith("French", StringComparison.Ordinal) => 41,
        _ when name.StartsWith("German", StringComparison.Ordinal) => 42,
        _ when name.StartsWith("Russian", StringComparison.Ordinal) => 43,
        _ => 100
    };

    private static int GetVersionScore(string name)
    {
        var score = 0;
        foreach (Match match in NumberRegex().Matches(name))
        {
            score = checked(score * 100 + int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        return score;
    }

    private static bool HasHumanText(string text) => !string.IsNullOrWhiteSpace(text) && LetterRegex().IsMatch(text);

    private bool LooksLikeCodeOrPath(string text, string field)
    {
        var value = text.Trim();
        if (NumericOnlyRegex().IsMatch(value) || AssetPathRegex().IsMatch(value) || SlashPathRegex().IsMatch(value) || BracedIdentifierRegex().IsMatch(value))
        {
            return true;
        }

        if (DottedIdentifierRegex().IsMatch(value))
        {
            var acronymSegments = value.Split('.').Count(segment => SingleUppercaseRegex().IsMatch(segment));
            if (acronymSegments >= 2)
            {
                return false;
            }
            return string.IsNullOrWhiteSpace(field) || !allowedFields.Contains(field);
        }

        return false;
    }

    private static string NormalizeText(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    [GeneratedRegex("^v\\d", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex VersionNodeRegex();
    [GeneratedRegex("\\d+", RegexOptions.CultureInvariant)] private static partial Regex NumberRegex();
    [GeneratedRegex("\\p{L}", RegexOptions.CultureInvariant)] private static partial Regex LetterRegex();
    [GeneratedRegex("^[\\d\\s.,:+\\-/%°]+$", RegexOptions.CultureInvariant)] private static partial Regex NumericOnlyRegex();
    [GeneratedRegex("^[A-Za-z0-9_./\\\\:-]+\\.(png|jpg|jpeg|dds|tga|wav|ogg|mp3|dll|asset|shader|xml)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex AssetPathRegex();
    [GeneratedRegex("^[A-Za-z0-9_./\\\\:-]+/[A-Za-z0-9_./\\\\:-]+$", RegexOptions.CultureInvariant)] private static partial Regex SlashPathRegex();
    [GeneratedRegex("^\\{[A-Za-z0-9_:.-]+\\}$", RegexOptions.CultureInvariant)] private static partial Regex BracedIdentifierRegex();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.]*\\.[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)] private static partial Regex DottedIdentifierRegex();
    [GeneratedRegex("^[A-Z]$", RegexOptions.CultureInvariant)] private static partial Regex SingleUppercaseRegex();
    [GeneratedRegex("^(label|labelshort|description|jobstring|reportstring|deathmessage|deathmessagefemale|deathmessagemale|letterlabel|lettertext|header|headertip|summary|formatstring|formatstringunfinalized|fixedname|reason|text|slateref)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex DisplayFieldRegex();
    [GeneratedRegex("^(defname|parentname|classname|class|thingclass|workerclass|compclass|hediffclass|thoughtclass|abilityclass|worldobjectclass|nodeclass|debuglabel|tagdef|texpath|texname|graphicpath|shader|sound|sounddef|iconpath|packageid|xpath|operation|colorchannel|rendernode|rendertree|rendertreedef|bodypart|bodypartdef|bodytype|headtype|racedef|thingdef|pawnkinddef|jobdef|statdef|skilldef|hediffdef|genedef)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex TechnicalFieldRegex();
    [GeneratedRegex("\\.(colorchannels|bodyaddons|powermodes)\\.", RegexOptions.CultureInvariant)] private static partial Regex RuntimeListRegex();
    [GeneratedRegex("\\.(graphicpaths?|rendernodes?|rendertree)\\.", RegexOptions.CultureInvariant)] private static partial Regex RenderPathRegex();
    [GeneratedRegex("^\\s*(allow|deny)\\t([A-Za-z_][A-Za-z0-9_]*)\\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex RuleRegex();
}
