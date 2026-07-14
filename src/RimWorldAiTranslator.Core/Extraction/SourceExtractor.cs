using System.Text;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Core.Extraction;

public sealed partial class SourceExtractor
{
    private const int MaximumEnumeratedDirectories = 25_000;
    private const int MaximumEnumeratedXmlFiles = 100_000;
    private const int DefaultMaximumExtractedRecords = 250_000;
    private const long DefaultMaximumExtractedCharacters = 64L * 1024 * 1024;
    private const long DefaultMaximumScannedXmlBytes = 512L * 1024 * 1024;
    private const int DefaultMaximumImmediateLanguageDirectories = 1_024;
    private const int DefaultMaximumContentRoots = 256;
    private const int DefaultMaximumLanguageDiscoveryEntries = 100_000;
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
    private readonly int maximumExtractedRecords;
    private readonly long maximumExtractedCharacters;
    private readonly long maximumScannedXmlBytes;
    private readonly int maximumImmediateLanguageDirectories;
    private readonly int maximumContentRoots;

    public SourceExtractor(string? defFieldRulesPath = null)
        : this(
            defFieldRulesPath,
            DefaultMaximumExtractedRecords,
            DefaultMaximumExtractedCharacters,
            DefaultMaximumScannedXmlBytes,
            DefaultMaximumImmediateLanguageDirectories,
            DefaultMaximumContentRoots)
    {
    }

    internal SourceExtractor(
        string? defFieldRulesPath,
        int maximumExtractedRecords,
        long maximumExtractedCharacters,
        long maximumScannedXmlBytes,
        int maximumImmediateLanguageDirectories = DefaultMaximumImmediateLanguageDirectories,
        int maximumContentRoots = DefaultMaximumContentRoots)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtractedRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtractedCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumScannedXmlBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumImmediateLanguageDirectories);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumContentRoots);
        this.maximumExtractedRecords = maximumExtractedRecords;
        this.maximumExtractedCharacters = maximumExtractedCharacters;
        this.maximumScannedXmlBytes = maximumScannedXmlBytes;
        this.maximumImmediateLanguageDirectories = maximumImmediateLanguageDirectories;
        this.maximumContentRoots = maximumContentRoots;
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
        if (PathSafety.IsNetworkPath(modRoot))
            throw new InvalidDataException("Source extraction requires a local mod path.");
        var warnings = new List<string>();
        var skipped = new List<SkippedSourceEntry>();
        var entries = new List<SourceEntry>();
        var budget = new ExtractionBudget(
            maximumExtractedRecords,
            maximumExtractedCharacters,
            maximumScannedXmlBytes);
        var contentRoots = GetActiveContentRoots(modRoot, warnings);
        var languages = GetSourceLanguageRoots(contentRoots, sourceLanguageFolder, cancellationToken);

        foreach (var language in languages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trustedContentRoot = FindTrustedContainer(language.Path, contentRoots)
                ?? throw new InvalidDataException("A source-language root resolves outside the selected mod.");
            foreach (var file in EnumerateXmlFiles(language.Path, trustedContentRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(language.Path, file);
                var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                var kind = parts.Length > 0 ? parts[0] : "Keyed";
                var typeName = kind.Equals("DefInjected", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 ? parts[1] : string.Empty;
                budget.ReserveXmlFile(file);
                ImportLanguageFile(file, relative, kind, typeName, entries, skipped, warnings, budget);
            }
        }

        if (includePatches)
        {
            warnings.Add("Patch XML translation is disabled because RimWorld patch conditions and list handles cannot be resolved safely outside the game. Defs and language files will still be processed.");
        }

        var seenDefsRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contentRoot in contentRoots)
        {
            var requestedDefsRoot = Path.Combine(contentRoot, "Defs");
            if (!TryGetTrustedChildDirectory(requestedDefsRoot, contentRoot, out var defsRoot)
                || !seenDefsRoots.Add(defsRoot))
            {
                continue;
            }

            foreach (var file in EnumerateXmlFiles(defsRoot, contentRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.ReserveXmlFile(file);
                ImportDefsFile(file, outputFilePrefix, entries, skipped, warnings, budget);
            }
        }

        return new SourceExtractionResult(entries, skipped, languages, warnings);
    }

    public IReadOnlyList<string> GetActiveContentRoots(string modRoot, ICollection<string>? warnings = null)
    {
        if (PathSafety.IsNetworkPath(modRoot))
            throw new InvalidDataException("Source discovery requires a local mod path.");
        var modFull = PathSafety.GetCanonicalExistingDirectory(modRoot);
        var roots = new List<string> { modFull };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modFull };
        var loadFolders = Path.Combine(modFull, "LoadFolders.xml");
        if (!File.Exists(loadFolders))
        {
            return roots;
        }

        var contentRootLimitExceeded = false;
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

            var examinedEntries = 0;
            foreach (var item in version.Elements().Where(element => element.Name.LocalName == "li"))
            {
                if (++examinedEntries > maximumContentRoots)
                {
                    contentRootLimitExceeded = true;
                    throw new InvalidDataException(
                        $"LoadFolders.xml contains more than {maximumContentRoots:N0} content-root candidates.");
                }
                var relative = item.Value.Trim();
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                if (Path.IsPathRooted(relative))
                {
                    continue;
                }

                var candidate = relative is "/" or "\\" or "." ? modFull : Path.Combine(modFull, relative);
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                var candidateFull = PathSafety.GetCanonicalExistingDirectory(candidate);
                if (!candidateFull.Equals(modFull, StringComparison.OrdinalIgnoreCase)
                    && !PathSafety.IsStrictlyInside(candidateFull, modFull))
                {
                    continue;
                }

                if (seen.Add(candidateFull))
                {
                    if (roots.Count >= maximumContentRoots)
                    {
                        contentRootLimitExceeded = true;
                        throw new InvalidDataException(
                            $"LoadFolders.xml resolves to more than {maximumContentRoots:N0} content roots.");
                    }
                    roots.Add(candidateFull);
                }
            }
        }
        catch (Exception ex)
        {
            if (contentRootLimitExceeded) throw;
            warnings?.Add($"Could not read LoadFolders.xml; using the selected mod root only ({SafeExceptionType(ex)}).");
        }

        return roots;
    }

    public IReadOnlyList<SourceLanguageRoot> GetSourceLanguageRoots(
        IEnumerable<string> contentRoots,
        string sourceLanguageFolder,
        CancellationToken cancellationToken = default)
    {
        var roots = new List<string>(Math.Min(maximumContentRoots, 16));
        foreach (var root in contentRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PathSafety.IsNetworkPath(root))
                throw new InvalidDataException("Source language discovery requires local content roots.");
            if (roots.Count >= maximumContentRoots)
            {
                throw new InvalidDataException(
                    $"Source language discovery contains more than {maximumContentRoots:N0} content roots.");
            }
            var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(root);
            if (PathSafety.IsNetworkPath(canonicalRoot))
                throw new InvalidDataException("Source language discovery requires local content roots.");
            roots.Add(canonicalRoot);
        }
        var languageDirectoryBudget = new ImmediateDirectoryBudget(maximumImmediateLanguageDirectories);
        var xmlDiscoveryBudget = new XmlDiscoveryBudget(DefaultMaximumLanguageDiscoveryEntries);
        if (!string.IsNullOrWhiteSpace(sourceLanguageFolder) && !sourceLanguageFolder.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            ValidateLanguageFolderName(sourceLanguageFolder);
            var candidates = new List<(string Path, string TrustedRoot, bool XmlVerified)>();
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetTrustedChildDirectory(
                        Path.Combine(root, "Languages"),
                        root,
                        out var languagesRoot)) continue;
                var exact = Path.Combine(languagesRoot, sourceLanguageFolder);
                if (TryGetTrustedChildDirectory(exact, languagesRoot, out var exactRoot)
                    && HasXml(exactRoot, languagesRoot, xmlDiscoveryBudget, cancellationToken))
                {
                    candidates.Add((exactRoot, languagesRoot, true));
                    continue;
                }
                var match = EnumerateSafeChildDirectories(languagesRoot, languageDirectoryBudget)
                    .Where(path => Path.GetFileName(path).Equals(sourceLanguageFolder, StringComparison.OrdinalIgnoreCase)
                                   || Path.GetFileName(path).StartsWith(sourceLanguageFolder + " ", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (match is not null) candidates.Add((match, languagesRoot, false));
            }
            var result = candidates
                .Where(candidate => candidate.XmlVerified
                                    || HasXml(
                                        candidate.Path,
                                        candidate.TrustedRoot,
                                        xmlDiscoveryBudget,
                                        cancellationToken))
                .Select(candidate => Path.GetFullPath(candidate.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new SourceLanguageRoot(Path.GetFileName(path), path, -1))
                .ToArray();
            return result.Length > 0
                ? result
                : throw new InvalidDataException($"Source language folder has no XML files: {sourceLanguageFolder}");
        }

        var safeAutoCandidates = new List<string>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetTrustedChildDirectory(
                    Path.Combine(root, "Languages"),
                    root,
                    out var languagesRoot)) continue;
            foreach (var path in EnumerateSafeChildDirectories(languagesRoot, languageDirectoryBudget))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsExcludedLanguage(Path.GetFileName(path))
                    && HasXml(path, languagesRoot, xmlDiscoveryBudget, cancellationToken))
                {
                    safeAutoCandidates.Add(path);
                }
            }
        }
        var candidatesAuto = safeAutoCandidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new SourceLanguageRoot(Path.GetFileName(path), path, GetLanguageRank(Path.GetFileName(path))))
            .ToArray();
        var best = candidatesAuto.OrderBy(candidate => candidate.Rank).ThenBy(candidate => candidate.Name, StringComparer.Ordinal).FirstOrDefault();
        return best is null
            ? []
            : candidatesAuto.Where(candidate => candidate.Name.Equals(best.Name, StringComparison.Ordinal)).ToArray();
    }

    public Dictionary<string, ExistingLanguageEntry> ReadExistingLanguageMap(
        string languageRoot,
        ICollection<string>? warnings = null,
        CancellationToken cancellationToken = default)
    {
        if (PathSafety.IsNetworkPath(languageRoot))
            throw new InvalidDataException("Existing language inputs must use a local path.");
        var map = new Dictionary<string, ExistingLanguageEntry>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var budget = new ExtractionBudget(
            maximumExtractedRecords,
            maximumExtractedCharacters,
            maximumScannedXmlBytes);
        if (!Directory.Exists(languageRoot))
        {
            return map;
        }

        var fullRoot = PathSafety.Normalize(languageRoot);
        foreach (var file in EnumerateXmlFiles(
                     fullRoot,
                     trustedRoot: null,
                     cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ReserveXmlFile(file);
            IReadOnlyList<RimWorldTranslatorRawSourceEntry> rows;
            try
            {
                var relative = Path.GetRelativePath(fullRoot, file);
                var localizationNamespace = GetNamespaceFromRelativePath(relative);
                if (string.IsNullOrWhiteSpace(localizationNamespace))
                {
                    continue;
                }
                rows = RimWorldTranslatorRmkXlsxReader.ReadLanguageData(file);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings?.Add($"Skipping an unreadable existing language XML file ({SafeExceptionType(ex)}).");
                continue;
            }

            var relativePath = Path.GetRelativePath(fullRoot, file);
            var entryNamespace = GetNamespaceFromRelativePath(relativePath);
            foreach (var raw in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.ReserveRecord(raw.Key, raw.Text, relativePath);
                var identity = GetLocalizationIdentity(entryNamespace, raw.Key);
                if (string.IsNullOrWhiteSpace(identity) || ambiguous.Contains(identity)) continue;
                if (map.Remove(identity))
                {
                    ambiguous.Add(identity);
                    warnings?.Add("Ignoring a duplicated existing localization identity.");
                }
                else
                {
                    map[identity] = new ExistingLanguageEntry(NormalizeText(raw.Text), relativePath);
                }
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
        ICollection<string> warnings,
        ExtractionBudget budget)
    {
        IReadOnlyList<RimWorldTranslatorRawSourceEntry> rawEntries;
        try
        {
            rawEntries = RimWorldTranslatorRmkXlsxReader.ReadLanguageData(file);
        }
        catch (Exception ex)
        {
            warnings.Add($"Skipping an unreadable language XML file ({SafeExceptionType(ex)}).");
            return;
        }

        foreach (var raw in rawEntries)
        {
            AddCandidate(raw, file, targetRelativePath, kind, typeName, entries, skipped, budget);
        }
    }

    private void ImportDefsFile(
        string file,
        string outputFilePrefix,
        ICollection<SourceEntry> entries,
        ICollection<SkippedSourceEntry> skipped,
        ICollection<string> warnings,
        ExtractionBudget budget)
    {
        RimWorldTranslatorDefReadResult result;
        try
        {
            result = RimWorldTranslatorRmkXlsxReader.ReadDefsDetailed(file);
        }
        catch (Exception ex)
        {
            warnings.Add($"Skipping an unreadable Def XML file ({SafeExceptionType(ex)}).");
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
            budget.ReserveRecord(raw.Key, text, raw.TypeName, raw.Field, file, target, reason);
            skipped.Add(NewSkipped(raw.Key, text, raw.TypeName, raw.Field, file, target, reason));
        }

        foreach (var raw in result.Entries)
        {
            var target = Path.Combine("DefInjected", raw.TypeName, outputFilePrefix + ".xml");
            AddCandidate(raw, file, target, "DefInjected", raw.TypeName, entries, skipped, budget);
        }
    }

    private void AddCandidate(
        RimWorldTranslatorRawSourceEntry raw,
        string sourceFile,
        string target,
        string kind,
        string typeName,
        ICollection<SourceEntry> entries,
        ICollection<SkippedSourceEntry> skipped,
        ExtractionBudget budget)
    {
        var text = NormalizeText(raw.Text);
        if (!HasHumanText(text) || LooksLikeCodeOrPath(text, raw.Field))
        {
            return;
        }

        var reason = GetInternalIdentifierReason(raw.Key, kind, typeName, raw.Field);
        if (reason is not null)
        {
            budget.ReserveRecord(raw.Key, text, typeName, raw.Field, sourceFile, target, reason);
            skipped.Add(NewSkipped(raw.Key, text, typeName, raw.Field, sourceFile, target, reason));
            return;
        }

        budget.ReserveRecord(raw.Key, text, kind, typeName, target, sourceFile, raw.Field);
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
        string text;
        try
        {
            var bytes = BoundedFileReader.ReadAllBytes(path, 1024 * 1024, "Def field rule file");
            text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Def field rule file is not valid UTF-8.", exception);
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
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

    private static IReadOnlyList<string> EnumerateXmlFiles(
        string root,
        string? trustedRoot,
        CancellationToken cancellationToken = default)
    {
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("An XML input root must not be a reparse or device path.");
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(root);
        if (PathSafety.IsNetworkPath(canonicalRoot))
            throw new InvalidDataException("XML inputs must use a local path.");
        var canonicalTrustedRoot = string.IsNullOrWhiteSpace(trustedRoot)
            ? null
            : PathSafety.Normalize(trustedRoot);
        if (canonicalTrustedRoot is not null
            && !IsInsideOrEqual(canonicalRoot, canonicalTrustedRoot))
        {
            throw new InvalidDataException("An XML input root resolves outside its trusted container.");
        }
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        pending.Push(canonicalRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var canonicalDirectory = PathSafety.GetCanonicalExistingDirectory(directory);
            if (!canonicalDirectory.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
                && !PathSafety.IsStrictlyInside(canonicalDirectory, canonicalRoot))
            {
                throw new InvalidDataException("An input directory resolves outside the selected language root.");
            }
            if (canonicalTrustedRoot is not null
                && !IsInsideOrEqual(canonicalDirectory, canonicalTrustedRoot))
            {
                throw new InvalidDataException("An input directory resolves outside its trusted container.");
            }
            if (!visited.Add(canonicalDirectory)) continue;
            if (visited.Count > MaximumEnumeratedDirectories)
                throw new InvalidDataException($"XML input contains more than {MaximumEnumeratedDirectories:N0} directories.");

            foreach (var entry in Directory.EnumerateFileSystemEntries(canonicalDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                if (!Path.GetExtension(entry).Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;

                var canonicalFile = PathSafety.GetCanonicalExistingFile(entry);
                if (!PathSafety.IsStrictlyInside(canonicalFile, canonicalRoot))
                    throw new InvalidDataException("An XML input resolves outside the selected language root.");
                if (canonicalTrustedRoot is not null
                    && !PathSafety.IsStrictlyInside(canonicalFile, canonicalTrustedRoot))
                {
                    throw new InvalidDataException("An XML input resolves outside its trusted container.");
                }
                files.Add(canonicalFile);
                if (files.Count > MaximumEnumeratedXmlFiles)
                    throw new InvalidDataException($"XML input contains more than {MaximumEnumeratedXmlFiles:N0} files.");
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool HasXml(
        string path,
        string trustedRoot,
        XmlDiscoveryBudget budget,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            var rootAttributes = File.GetAttributes(path);
            if ((rootAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) return false;
            var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(path);
            var canonicalTrustedRoot = PathSafety.Normalize(trustedRoot);
            if (!PathSafety.IsStrictlyInside(canonicalRoot, canonicalTrustedRoot)
                || PathSafety.IsNetworkPath(canonicalRoot))
            {
                return false;
            }
            var pending = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(canonicalRoot);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = PathSafety.GetCanonicalExistingDirectory(pending.Pop());
                if (!directory.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
                    && !PathSafety.IsStrictlyInside(directory, canonicalRoot))
                {
                    throw new InvalidDataException("A language discovery directory resolves outside its root.");
                }
                if (!PathSafety.IsStrictlyInside(directory, canonicalTrustedRoot))
                    throw new InvalidDataException("A language discovery directory resolves outside its trusted root.");
                if (!visited.Add(directory)) continue;
                budget.Reserve();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    budget.Reserve();
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }
                    if (!Path.GetExtension(entry).Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                    var canonicalFile = PathSafety.GetCanonicalExistingFile(entry);
                    return PathSafety.IsStrictlyInside(canonicalFile, canonicalRoot)
                           && PathSafety.IsStrictlyInside(canonicalFile, canonicalTrustedRoot);
                }
            }
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (XmlDiscoveryLimitException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryGetTrustedChildDirectory(
        string path,
        string trustedRoot,
        out string canonicalPath)
    {
        canonicalPath = string.Empty;
        try
        {
            if (!Directory.Exists(path)) return false;
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) return false;
            var candidate = PathSafety.GetCanonicalExistingDirectory(path);
            if (PathSafety.IsNetworkPath(candidate)
                || !PathSafety.IsStrictlyInside(candidate, PathSafety.Normalize(trustedRoot)))
            {
                return false;
            }
            canonicalPath = candidate;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static string? FindTrustedContainer(string path, IEnumerable<string> trustedRoots)
    {
        var canonicalPath = PathSafety.Normalize(path);
        return trustedRoots
            .Where(root => IsInsideOrEqual(canonicalPath, PathSafety.Normalize(root)))
            .OrderByDescending(root => PathSafety.Normalize(root).Length)
            .FirstOrDefault();
    }

    private static bool IsInsideOrEqual(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) || PathSafety.IsStrictlyInside(path, root);

    private sealed class XmlDiscoveryBudget(int maximumEntries)
    {
        private int entries;

        public void Reserve()
        {
            if (entries >= maximumEntries)
                throw new XmlDiscoveryLimitException(
                    $"Language discovery examined more than {maximumEntries:N0} filesystem entries.");
            entries++;
        }
    }

    private sealed class ExtractionBudget(int maximumRecords, long maximumCharacters, long maximumXmlBytes)
    {
        private int records;
        private long characters;
        private long xmlBytes;

        public void ReserveXmlFile(string path)
        {
            var length = new FileInfo(path).Length;
            if (length < 0 || length > maximumXmlBytes - xmlBytes)
                throw new InvalidDataException($"XML extraction input exceeds the aggregate {maximumXmlBytes:N0}-byte limit.");
            xmlBytes += length;
        }

        public void ReserveRecord(params string?[] values)
        {
            if (records >= maximumRecords)
                throw new InvalidDataException($"XML extraction contains more than {maximumRecords:N0} retained records.");
            long addedCharacters = 0;
            foreach (var value in values)
                addedCharacters = checked(addedCharacters + (value?.Length ?? 0));
            if (addedCharacters > maximumCharacters - characters)
                throw new InvalidDataException($"XML extraction exceeds the aggregate {maximumCharacters:N0}-character limit.");
            records++;
            characters += addedCharacters;
        }
    }

    private sealed class XmlDiscoveryLimitException(string message) : Exception(message);

    private static IEnumerable<string> EnumerateSafeChildDirectories(
        string root,
        ImmediateDirectoryBudget budget)
    {
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(root);
        foreach (var child in Directory.EnumerateDirectories(canonicalRoot, "*", SearchOption.TopDirectoryOnly))
        {
            budget.Reserve();
            var attributes = File.GetAttributes(child);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
            var canonicalChild = PathSafety.GetCanonicalExistingDirectory(child);
            if (PathSafety.IsStrictlyInside(canonicalChild, canonicalRoot)) yield return canonicalChild;
        }
    }

    private sealed class ImmediateDirectoryBudget(int maximumDirectories)
    {
        private int directories;

        public void Reserve()
        {
            if (directories >= maximumDirectories)
            {
                throw new InvalidDataException(
                    $"Language discovery contains more than {maximumDirectories:N0} immediate directories.");
            }
            directories++;
        }
    }

    private static void ValidateLanguageFolderName(string value)
    {
        if (Path.IsPathRooted(value) || !PathSafety.IsSafeFileNameSegment(value))
        {
            throw new InvalidDataException("Source language must be a single folder name below Languages.");
        }
    }

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

    private static string SafeExceptionType(Exception exception) => exception switch
    {
        UnauthorizedAccessException => nameof(UnauthorizedAccessException),
        IOException => nameof(IOException),
        System.Xml.XmlException => nameof(System.Xml.XmlException),
        InvalidDataException => nameof(InvalidDataException),
        _ => nameof(Exception)
    };

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
