using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Core.Apply;

public enum ReviewApplyStatus
{
    ApprovedOnly,
    TranslatedAndApproved
}

public sealed class ReviewApplyOptions
{
    public required string ModRoot { get; init; }
    public required string ReviewRoot { get; init; }
    public string LanguageFolderName { get; init; } = "Korean";
    public bool Overwrite { get; init; }
    public bool DryRun { get; init; }
    public ReviewApplyStatus ApplyStatus { get; init; } = ReviewApplyStatus.ApprovedOnly;
}

public sealed record ReviewApplyResult(
    int SafeCandidateRows,
    int ApprovedRows,
    int TranslatedRows,
    int WrittenFiles,
    int AppliedEntries,
    int SkippedExistingEntries,
    int SkippedNotApproved,
    int SkippedUnsafe,
    int SkippedBlank,
    int SkippedUnmapped);

public sealed partial class ReviewApplyService
{
    private readonly AtomicJsonStore jsonStore;
    private readonly LanguageFileService languageFiles;
    private readonly SourceExtractor sourceExtractor;

    public ReviewApplyService(AtomicJsonStore jsonStore, LanguageFileService languageFiles, SourceExtractor sourceExtractor)
    {
        this.jsonStore = jsonStore;
        this.languageFiles = languageFiles;
        this.sourceExtractor = sourceExtractor;
    }

    public ReviewApplyResult Apply(ReviewApplyOptions options)
    {
        ValidateSegment(options.LanguageFolderName);
        var modRoot = PathSafety.Normalize(options.ModRoot);
        var reviewRoot = PathSafety.Normalize(options.ReviewRoot);
        var reviewLanguageRoot = Path.Combine(reviewRoot, "Languages", options.LanguageFolderName);
        var outputLanguageRoot = Path.Combine(modRoot, "Languages", options.LanguageFolderName);
        var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
        if (!Directory.Exists(auditRoot)) throw new DirectoryNotFoundException($"Review audit folder not found: {auditRoot}");
        if (!Directory.Exists(reviewLanguageRoot)) throw new DirectoryNotFoundException($"Review language folder not found: {reviewLanguageRoot}");
        var comparisonPath = Directory.EnumerateFiles(auditRoot, "*-comparison.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? throw new FileNotFoundException($"Comparison JSON not found in: {auditRoot}");
        var rows = jsonStore.Read<List<ReviewComparisonRow>>(comparisonPath)
            ?? throw new InvalidDataException("Comparison JSON is empty.");

        var rowByIdentity = new Dictionary<string, ReviewComparisonRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var relative = RelativeReviewTarget(row.Target, reviewLanguageRoot);
            if (relative is not null && !string.IsNullOrWhiteSpace(row.Key)) rowByIdentity[$"target:{relative}|key:{row.Key}"] = row;
            if (!string.IsNullOrWhiteSpace(row.Id)) rowByIdentity[$"id:{row.Id}"] = row;
            if (!string.IsNullOrWhiteSpace(row.Key)) rowByIdentity[$"key:{row.Key}"] = row;
        }

        var outputGroups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var safeRows = 0;
        var approvedRows = 0;
        var translatedRows = 0;
        var skippedNotApproved = 0;
        var skippedUnsafe = 0;
        var skippedUnmapped = 0;
        var skippedBlank = 0;

        void AddCandidate(ReviewComparisonRow row, string candidate, string status)
        {
            var text = Normalize(candidate);
            if (string.IsNullOrWhiteSpace(text))
            {
                skippedBlank++;
                return;
            }
            if (!LanguageFileService.IsValidLocalizationKey(row.Key))
            {
                skippedUnmapped++;
                return;
            }
            var relative = RelativeReviewTarget(row.Target, reviewLanguageRoot);
            if (relative is null)
            {
                skippedUnmapped++;
                return;
            }
            var target = PathSafety.ResolveInside(outputLanguageRoot, relative);
            if (!outputGroups.TryGetValue(target, out var group))
            {
                group = new Dictionary<string, string>(StringComparer.Ordinal);
                outputGroups[target] = group;
            }
            group[row.Key] = text;
            if (status == ReviewStatuses.Approved) approvedRows++;
            else translatedRows++;
        }

        var decisionPath = ReviewRepository.GetDecisionPath(reviewRoot);
        if (jsonStore.Exists(decisionPath))
        {
            var decisions = jsonStore.Read<ReviewDecisionDocument>(decisionPath)
                ?? throw new InvalidDataException("Review decisions are empty.");
            foreach (var decision in decisions.Items)
            {
                var identity = !string.IsNullOrWhiteSpace(decision.Target) && !string.IsNullOrWhiteSpace(decision.Key)
                    ? $"target:{decision.Target}|key:{decision.Key}"
                    : !string.IsNullOrWhiteSpace(decision.Id) ? $"id:{decision.Id}" : $"key:{decision.Key}";
                if (!rowByIdentity.TryGetValue(identity, out var row))
                {
                    skippedUnmapped++;
                    continue;
                }
                var status = decision.Status;
                var candidate = decision.Text;
                if (decision.SourceChanged || ReviewSafety.DecisionSourceChanged(decision, row))
                {
                    status = ReviewStatuses.Pending;
                    candidate = string.Empty;
                }
                if (!StatusIncluded(status, options.ApplyStatus))
                {
                    skippedNotApproved++;
                    continue;
                }
                if (!ReviewSafety.IsStructureSafe(row, candidate, sourceExtractor)
                    || status == ReviewStatuses.Translated && !ReviewSafety.IsTranslationSafe(row, candidate, sourceExtractor))
                {
                    skippedUnsafe++;
                    continue;
                }
                AddCandidate(row, candidate, status);
            }
        }
        else if (options.ApplyStatus == ReviewApplyStatus.TranslatedAndApproved)
        {
            foreach (var row in rows)
            {
                if (!ReviewSafety.IsTranslationSafe(row, row.Candidate, sourceExtractor))
                {
                    skippedUnsafe++;
                    continue;
                }
                safeRows++;
                AddCandidate(row, row.Candidate, ReviewStatuses.Translated);
            }
        }
        else
        {
            skippedNotApproved += rows.Count;
        }

        var appliedEntries = 0;
        var skippedExisting = 0;
        if (options.DryRun)
        {
            foreach (var group in outputGroups)
            {
                var existing = languageFiles.Read(group.Key);
                foreach (var key in group.Value.Keys)
                {
                    if (options.Overwrite || !existing.ContainsKey(key)) appliedEntries++;
                    else skippedExisting++;
                }
            }
        }
        else
        {
            var results = languageFiles.WriteTransaction(
                outputGroups.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.OrdinalIgnoreCase),
                options.Overwrite);
            appliedEntries = results.Values.Sum(result => result.Applied);
            skippedExisting = results.Values.Sum(result => result.SkippedExisting);
        }

        return new ReviewApplyResult(
            safeRows,
            approvedRows,
            translatedRows,
            outputGroups.Count,
            appliedEntries,
            skippedExisting,
            skippedNotApproved,
            skippedUnsafe,
            skippedBlank,
            skippedUnmapped);
    }

    private static bool StatusIncluded(string status, ReviewApplyStatus mode) =>
        status == ReviewStatuses.Approved
        || mode == ReviewApplyStatus.TranslatedAndApproved && status == ReviewStatuses.Translated;

    private static string? RelativeReviewTarget(string target, string reviewLanguageRoot)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        string fullTarget;
        try
        {
            fullTarget = Path.GetFullPath(target);
        }
        catch
        {
            return null;
        }
        if (!PathSafety.IsStrictlyInside(fullTarget, reviewLanguageRoot)) return null;
        var relative = Path.GetRelativePath(reviewLanguageRoot, fullTarget);
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
    }

    private static void ValidateSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new InvalidDataException("LanguageFolderName must be a single safe folder-name segment.");
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
}
