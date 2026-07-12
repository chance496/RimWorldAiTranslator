using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Utilities;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Core.Rmk;

public sealed class RmkExportOptions
{
    public required string RmkEntryRoot { get; init; }
    public required string ReviewRoot { get; init; }
    public string ReviewLanguageFolderName { get; init; } = "Korean";
    public string RmkLanguageFolderName { get; init; } = "Korean (한국어)";
    public string SourceLanguage { get; init; } = "English";
    public string WorkbookPath { get; init; } = string.Empty;
    public ReviewApplyStatus ApplyStatus { get; init; } = ReviewApplyStatus.ApprovedOnly;
    public bool Overwrite { get; init; }
    public bool DryRun { get; init; }
}

public sealed record RmkExportResult(
    string WorkbookPath,
    int WorkbookRows,
    int EligibleEntries,
    int UpdatedExisting,
    int AddedNew,
    int WrittenFiles,
    int SkippedStatus,
    int SkippedSourceChanged,
    int SkippedUnsafe,
    int SkippedUnmapped,
    int SkippedAmbiguous);

public sealed class RmkExportService
{
    private readonly AtomicJsonStore jsonStore;
    private readonly LanguageFileService languageFiles;
    private readonly SourceExtractor extractor;

    public RmkExportService(AtomicJsonStore jsonStore, LanguageFileService languageFiles, SourceExtractor extractor)
    {
        this.jsonStore = jsonStore;
        this.languageFiles = languageFiles;
        this.extractor = extractor;
    }

    public RmkExportResult Export(RmkExportOptions options)
    {
        ValidateSegment(options.ReviewLanguageFolderName, nameof(options.ReviewLanguageFolderName));
        ValidateSegment(options.RmkLanguageFolderName, nameof(options.RmkLanguageFolderName));
        var entryRoot = PathSafety.Normalize(options.RmkEntryRoot);
        var reviewRoot = PathSafety.Normalize(options.ReviewRoot);
        var reviewLanguageRoot = Path.Combine(reviewRoot, "Languages", options.ReviewLanguageFolderName);
        var rmkLanguageRoot = Path.Combine(entryRoot, "Languages", options.RmkLanguageFolderName);
        var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
        if (!Directory.Exists(auditRoot)) throw new DirectoryNotFoundException($"Review audit folder not found: {auditRoot}");
        var decisionPath = ReviewRepository.GetDecisionPath(reviewRoot);
        if (!jsonStore.Exists(decisionPath)) throw new FileNotFoundException("Review decisions not found.", decisionPath);
        var comparisonPath = Directory.EnumerateFiles(auditRoot, "*-comparison.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? throw new FileNotFoundException($"Comparison JSON not found in: {auditRoot}");
        var rows = (jsonStore.Read<List<ReviewComparisonRow>>(comparisonPath) ?? [])
            .Where(row => extractor.GetInternalIdentifierReason(row.Key, row.Kind, row.DefClass, row.Field) is null)
            .ToArray();
        var decisions = jsonStore.Read<ReviewDecisionDocument>(decisionPath)
            ?? throw new InvalidDataException("Review decisions are empty.");

        var rowByTargetKey = new Dictionary<string, ReviewComparisonRow>(StringComparer.OrdinalIgnoreCase);
        var rowById = new Dictionary<string, ReviewComparisonRow>(StringComparer.OrdinalIgnoreCase);
        var uniqueKeyRows = new Dictionary<string, ReviewComparisonRow>(StringComparer.OrdinalIgnoreCase);
        var ambiguousKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var relative = RelativeTarget(row.Target, reviewLanguageRoot);
            if (relative is not null && !string.IsNullOrWhiteSpace(row.Key)) rowByTargetKey[$"target:{relative}|key:{row.Key}"] = row;
            if (!string.IsNullOrWhiteSpace(row.Id)) rowById[$"id:{row.Id}"] = row;
            if (string.IsNullOrWhiteSpace(row.Key)) continue;
            var key = $"key:{row.Key}";
            if (ambiguousKeys.Contains(key)) continue;
            if (uniqueKeyRows.Remove(key)) ambiguousKeys.Add(key);
            else uniqueKeyRows[key] = row;
        }

        ReviewComparisonRow? Resolve(ReviewDecision decision)
        {
            if (!string.IsNullOrWhiteSpace(decision.Target) && !string.IsNullOrWhiteSpace(decision.Key)
                && rowByTargetKey.TryGetValue($"target:{decision.Target}|key:{decision.Key}", out var targetRow)) return targetRow;
            if (!string.IsNullOrWhiteSpace(decision.Id) && rowById.TryGetValue($"id:{decision.Id}", out var idRow)) return idRow;
            return !string.IsNullOrWhiteSpace(decision.Key) && uniqueKeyRows.TryGetValue($"key:{decision.Key}", out var keyRow) ? keyRow : null;
        }

        var decisionByHistory = new Dictionary<string, ReviewDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in decisions.Items)
        {
            var row = Resolve(decision);
            var identifier = row is null ? string.Empty : HistoryIdentifier(row);
            if (!string.IsNullOrWhiteSpace(identifier)) decisionByHistory.TryAdd(identifier, decision);
        }

        var fileStates = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var keyFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(rmkLanguageRoot))
        {
            foreach (var file in Directory.EnumerateFiles(rmkLanguageRoot, "*.xml", SearchOption.AllDirectories))
            {
                var state = new FileState(Path.GetFullPath(file), languageFiles.Read(file));
                fileStates[state.Path] = state;
                foreach (var key in state.Entries.Keys)
                {
                    if (!keyFiles.TryGetValue(key, out var paths))
                    {
                        paths = [];
                        keyFiles[key] = paths;
                    }
                    paths.Add(state.Path);
                }
            }
        }

        var eligible = 0;
        var updatedExisting = 0;
        var addedNew = 0;
        var skippedStatus = 0;
        var skippedChanged = 0;
        var skippedUnsafe = 0;
        var skippedUnmapped = 0;
        var skippedAmbiguous = 0;
        var exportedTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var decision in decisions.Items)
        {
            var status = decision.Status == "reviewed" ? ReviewStatuses.Approved : decision.Status;
            if (!StatusIncluded(status, options.ApplyStatus))
            {
                skippedStatus++;
                continue;
            }
            var row = Resolve(decision);
            if (row is null || !LanguageFileService.IsValidLocalizationKey(row.Key))
            {
                skippedUnmapped++;
                continue;
            }
            if (decision.SourceChanged || ReviewSafety.DecisionSourceChanged(decision, row))
            {
                skippedChanged++;
                continue;
            }
            var translation = ReviewSafety.Normalize(decision.Text);
            if (!ReviewSafety.IsTranslationSafe(row, translation, extractor))
            {
                skippedUnsafe++;
                continue;
            }
            eligible++;

            string target;
            if (keyFiles.TryGetValue(row.Key, out var paths))
            {
                if (paths.Count > 1)
                {
                    skippedAmbiguous++;
                    continue;
                }
                if (!options.Overwrite)
                {
                    skippedUnmapped++;
                    continue;
                }
                target = paths[0];
                updatedExisting++;
            }
            else
            {
                var relative = RelativeTarget(row.Target, reviewLanguageRoot);
                if (relative is null)
                {
                    skippedUnmapped++;
                    continue;
                }
                target = PathSafety.ResolveInside(rmkLanguageRoot, relative);
                addedNew++;
            }
            if (!fileStates.TryGetValue(target, out var state))
            {
                state = new FileState(target, languageFiles.Read(target));
                fileStates[target] = state;
            }
            state.Entries[row.Key] = translation;
            state.Dirty = true;
            var identifier = HistoryIdentifier(row);
            if (!string.IsNullOrWhiteSpace(identifier)) exportedTranslations[identifier] = translation;
        }

        var workbookPath = ResolveWorkbookPath(options.WorkbookPath, entryRoot);
        var workbookData = File.Exists(workbookPath) ? RimWorldTranslatorRmkXlsxReader.Read(workbookPath) : null;
        var effectiveSourceLanguage = !string.IsNullOrWhiteSpace(workbookData?.SourceLanguage)
            ? workbookData.SourceLanguage
            : !string.IsNullOrWhiteSpace(options.SourceLanguage) && options.SourceLanguage != "Auto" ? options.SourceLanguage : "English";
        var workbookUsesDifferentLanguage = workbookData is not null
            && !string.IsNullOrWhiteSpace(workbookData.SourceLanguage)
            && !string.IsNullOrWhiteSpace(options.SourceLanguage)
            && !workbookData.SourceLanguage.Equals(options.SourceLanguage, StringComparison.OrdinalIgnoreCase);
        var historyById = new Dictionary<string, RimWorldTranslatorRmkHistoryRow>(StringComparer.OrdinalIgnoreCase);
        var historyOrder = new List<string>();
        if (workbookData is not null)
        {
            foreach (var row in workbookData.Rows)
            {
                if (string.IsNullOrWhiteSpace(row.Identifier) || historyById.ContainsKey(row.Identifier)) continue;
                historyById[row.Identifier] = Copy(row);
                historyOrder.Add(row.Identifier);
            }
        }

        foreach (var row in rows)
        {
            var identifier = HistoryIdentifier(row);
            if (string.IsNullOrWhiteSpace(identifier)) continue;
            var className = row.Kind == "Keyed" ? "Keyed" : !string.IsNullOrWhiteSpace(row.DefClass) ? row.DefClass : row.Kind;
            var node = !string.IsNullOrWhiteSpace(row.Node) ? row.Node : row.Key;
            var currentSource = ReviewSafety.Normalize(row.Source);
            var workbookCurrentSource = !string.IsNullOrWhiteSpace(row.RmkCurrentSource)
                ? ReviewSafety.Normalize(row.RmkCurrentSource)
                : !workbookUsesDifferentLanguage ? currentSource : string.Empty;
            decisionByHistory.TryGetValue(identifier, out var decision);
            var sourceChanged = row.RmkSourceChanged || decision?.SourceChanged == true
                || decision is not null && ReviewSafety.DecisionSourceChanged(decision, row);

            if (historyById.TryGetValue(identifier, out var history))
            {
                if (!sourceChanged && !string.IsNullOrWhiteSpace(workbookCurrentSource)) history.Source = workbookCurrentSource;
                if (string.IsNullOrWhiteSpace(history.ClassName)) history.ClassName = className;
                if (string.IsNullOrWhiteSpace(history.Key)) history.Key = node;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(workbookCurrentSource) && workbookUsesDifferentLanguage) continue;
                var historicalSource = sourceChanged
                    ? !string.IsNullOrWhiteSpace(decision?.PreviousSourceText) ? ReviewSafety.Normalize(decision.PreviousSourceText) : ReviewSafety.Normalize(row.RmkHistoricalSource)
                    : string.Empty;
                history = new RimWorldTranslatorRmkHistoryRow
                {
                    Identifier = identifier,
                    ClassName = className,
                    Key = node,
                    RequiredMods = string.Empty,
                    Source = !string.IsNullOrWhiteSpace(historicalSource) ? historicalSource : workbookCurrentSource,
                    Translation = row.ExistingOrigin == "rmk" ? ReviewSafety.Normalize(row.Existing) : string.Empty
                };
                historyById[identifier] = history;
                historyOrder.Add(identifier);
            }
            if (exportedTranslations.TryGetValue(identifier, out var exported))
            {
                history.Translation = exported;
                if (!string.IsNullOrWhiteSpace(workbookCurrentSource)) history.Source = workbookCurrentSource;
            }
        }

        var historyRows = historyOrder.Where(historyById.ContainsKey).Select(id => historyById[id]).ToArray();
        var dirtyStates = fileStates.Values.Where(state => state.Dirty).OrderBy(state => state.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!options.DryRun)
        {
            var paths = dirtyStates.Select(state => state.Path).Prepend(workbookPath);
            FileTransaction.Execute(paths, () =>
            {
                RimWorldTranslatorRmkXlsxWriter.Write(workbookPath, historyRows, effectiveSourceLanguage);
                foreach (var state in dirtyStates)
                {
                    languageFiles.Write(state.Path, state.Entries, true);
                }
            }, "RMK export");
        }

        return new RmkExportResult(
            workbookPath,
            historyRows.Length,
            eligible,
            updatedExisting,
            addedNew,
            dirtyStates.Length,
            skippedStatus,
            skippedChanged,
            skippedUnsafe,
            skippedUnmapped,
            skippedAmbiguous);
    }

    private static string ResolveWorkbookPath(string requestedPath, string entryRoot)
    {
        string candidate;
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            candidate = Path.IsPathRooted(requestedPath) ? requestedPath : Path.Combine(entryRoot, requestedPath);
        }
        else
        {
            candidate = Directory.EnumerateFiles(entryRoot, "*.xlsx", SearchOption.TopDirectoryOnly).OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                var workshopId = string.Empty;
                var cursor = new DirectoryInfo(entryRoot);
                for (var depth = 0; depth < 3 && cursor is not null; depth++, cursor = cursor.Parent)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(cursor.Name, " - (\\d+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                    if (match.Success)
                    {
                        workshopId = match.Groups[1].Value;
                        break;
                    }
                }
                candidate = Path.Combine(entryRoot, string.IsNullOrWhiteSpace(workshopId) ? "RimWorldTranslation.xlsx" : workshopId + ".xlsx");
            }
        }
        var full = Path.GetFullPath(candidate);
        if (!PathSafety.IsStrictlyInside(full, entryRoot) || !Path.GetExtension(full).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"RMK workbook path must be an XLSX file inside the RMK entry root: {full}");
        return full;
    }

    private static string HistoryIdentifier(ReviewComparisonRow row)
    {
        var className = row.Kind == "Keyed" ? "Keyed" : !string.IsNullOrWhiteSpace(row.DefClass) ? row.DefClass : row.Kind;
        var node = !string.IsNullOrWhiteSpace(row.Node) ? row.Node : row.Key;
        return string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(node) ? string.Empty : $"{className}+{node}";
    }

    private static string? RelativeTarget(string target, string reviewLanguageRoot)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        string full;
        try { full = Path.GetFullPath(target); } catch { return null; }
        if (!PathSafety.IsStrictlyInside(full, reviewLanguageRoot)) return null;
        var relative = Path.GetRelativePath(reviewLanguageRoot, full);
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
    }

    private static bool StatusIncluded(string status, ReviewApplyStatus mode) =>
        status == ReviewStatuses.Approved || mode == ReviewApplyStatus.TranslatedAndApproved && status == ReviewStatuses.Translated;

    private static RimWorldTranslatorRmkHistoryRow Copy(RimWorldTranslatorRmkHistoryRow row) => new()
    {
        Identifier = row.Identifier,
        ClassName = row.ClassName,
        Key = row.Key,
        RequiredMods = row.RequiredMods,
        Source = ReviewSafety.Normalize(row.Source),
        Translation = ReviewSafety.Normalize(row.Translation)
    };

    private static void ValidateSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new InvalidDataException($"{name} must be a single safe folder-name segment.");
    }

    private sealed class FileState(string path, SortedDictionary<string, string> entries)
    {
        public string Path { get; } = path;
        public SortedDictionary<string, string> Entries { get; } = entries;
        public bool Dirty { get; set; }
    }
}
