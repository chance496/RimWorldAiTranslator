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
    public string SourceKind { get; init; } = string.Empty;
    public string SourceLanguageFolder { get; init; } = "Auto";
    public string LanguageFolderName { get; init; } = "Korean";
    public bool Overwrite { get; init; }
    public bool DryRun { get; init; }
    public string? ExpectedPlanFingerprint { get; init; }
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
    int SkippedSourceChanged,
    int SkippedUnsafe,
    int SkippedBlank,
    int SkippedUnmapped,
    string PlanFingerprint);

public sealed partial class ReviewApplyService
{
    private readonly AtomicJsonStore jsonStore;
    private readonly LanguageFileService languageFiles;
    private readonly SourceExtractor sourceExtractor;
    private readonly string? trustedReviewsRoot;
    private readonly FileTransactionRecoveryAuthority? recoveryAuthority;
    internal Action? AfterModRootSnapshotCapturedTestHook { get; set; }
    internal Action? AfterWriteBoundaryLockedTestHook { get; set; }

    public ReviewApplyService(
        AtomicJsonStore jsonStore,
        LanguageFileService languageFiles,
        SourceExtractor sourceExtractor,
        string? trustedReviewsRoot = null,
        FileTransactionRecoveryAuthority? recoveryAuthority = null)
    {
        this.jsonStore = jsonStore;
        this.languageFiles = languageFiles;
        this.sourceExtractor = sourceExtractor;
        this.trustedReviewsRoot = trustedReviewsRoot;
        this.recoveryAuthority = recoveryAuthority;
    }

    public ReviewApplyResult Apply(ReviewApplyOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSegment(options.LanguageFolderName);
        if (PathSafety.IsNetworkPath(options.ModRoot) || PathSafety.IsNetworkPath(options.ReviewRoot))
            throw new InvalidDataException("Apply inputs and outputs must use local paths.");
        var requestedModRoot = PathSafety.Normalize(options.ModRoot);
        if (options.SourceKind.Equals("Workshop", StringComparison.OrdinalIgnoreCase)
            || PathSafety.IsWorkshopContentPath(requestedModRoot))
        {
            throw new InvalidOperationException("Steam Workshop sources are read-only. Apply to an explicit local mod folder or an RMK work clone.");
        }
        if (!Directory.Exists(requestedModRoot)) throw new DirectoryNotFoundException($"Mod root not found: {requestedModRoot}");
        PathSafety.EnsureNoReparsePointsToVolumeRoot(requestedModRoot);
        var modRoot = PathSafety.GetCanonicalExistingDirectory(requestedModRoot);
        if (PathSafety.IsWorkshopContentPath(modRoot))
        {
            throw new InvalidOperationException(
                "Steam Workshop sources are read-only and cannot be applied through a filesystem alias.");
        }
        var modRootSnapshot = PathSafety.GetExistingDirectorySnapshot(modRoot);
        AfterModRootSnapshotCapturedTestHook?.Invoke();
        var reviewRoot = ReviewRepository.ValidateWritableReviewRoot(
            options.ReviewRoot,
            trustedReviewsRoot);
        var reviewLanguageRoot = Path.Combine(reviewRoot, "Languages", options.LanguageFolderName);
        var outputLanguageRoot = Path.Combine(modRoot, "Languages", options.LanguageFolderName);
        PathSafety.EnsureNoReparsePoints(outputLanguageRoot, modRoot);
        var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
        if (!Directory.Exists(auditRoot)) throw new DirectoryNotFoundException($"Review audit folder not found: {auditRoot}");
        if (!Directory.Exists(reviewLanguageRoot)) throw new DirectoryNotFoundException($"Review language folder not found: {reviewLanguageRoot}");
        if (!new ReviewRepository(jsonStore).TryLoad(
                reviewRoot,
                out var decisions,
                trustedReviewsRoot,
                cancellationToken))
        {
            throw new FileNotFoundException(
                "Review decisions not found.",
                ReviewRepository.GetDecisionPath(reviewRoot));
        }
        var comparisonDocument = ReviewComparisonDocument.LoadExact(
            reviewRoot,
            decisions.Comparison,
            cancellationToken);
        ReviewComparisonDocument.RequireBoundDecisionStore(decisions, comparisonDocument.Evidence);
        var rows = comparisonDocument.Rows;
        var currentSources = ReviewSourceSnapshot.Capture(
            sourceExtractor,
            modRoot,
            options.SourceLanguageFolder,
            cancellationToken);

        var rowByTargetKey = new Dictionary<string, ReviewComparisonRow>(StringComparer.OrdinalIgnoreCase);
        var ambiguousTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keylessIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousKeylessIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = RelativeReviewTarget(row.Target, reviewLanguageRoot);
            if (relative is not null && !string.IsNullOrWhiteSpace(row.Key))
            {
                var targetKey = $"target:{relative}|key:{row.Key}";
                if (!ambiguousTargetKeys.Contains(targetKey) && !rowByTargetKey.TryAdd(targetKey, row))
                {
                    rowByTargetKey.Remove(targetKey);
                    ambiguousTargetKeys.Add(targetKey);
                }
            }
            if (string.IsNullOrWhiteSpace(row.Key))
            {
                if (relative is not null
                    && !string.IsNullOrWhiteSpace(row.DefClass)
                    && !string.IsNullOrWhiteSpace(row.Node))
                {
                    var identity = KeylessIdentity(relative, row.DefClass, row.Node);
                    if (!keylessIdentities.Add(identity)) ambiguousKeylessIdentities.Add(identity);
                }
                continue;
            }
        }
        if (ambiguousTargetKeys.Count > 0 || ambiguousKeylessIdentities.Count > 0)
            throw new InvalidDataException(
                $"Comparison contains ambiguous duplicate identities (target+key: {ambiguousTargetKeys.Count}, keyless target+defClass+node: {ambiguousKeylessIdentities.Count}).");

        ReviewComparisonRow? Resolve(ReviewDecision decision)
        {
            if (string.IsNullOrWhiteSpace(decision.Key)
                || string.IsNullOrWhiteSpace(decision.Target)) return null;
            return rowByTargetKey.TryGetValue(
                $"target:{ReviewTargetIdentity.Canonicalize(decision.Target)}|key:{decision.Key}",
                out var targetRow) ? targetRow : null;
        }

        var outputGroups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var safeRows = 0;
        var approvedRows = 0;
        var translatedRows = 0;
        var skippedNotApproved = 0;
        var skippedSourceChanged = 0;
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
            PathSafety.EnsureNoReparsePoints(target, modRoot);
            if (!outputGroups.TryGetValue(target, out var group))
            {
                group = new Dictionary<string, string>(StringComparer.Ordinal);
                outputGroups[target] = group;
            }
            group[row.Key] = text;
            safeRows++;
            if (status == ReviewStatuses.Approved) approvedRows++;
            else translatedRows++;
        }

        {
            ValidateDecisionIdentities(decisions.Items);
            var resolvedDecisions = decisions.Items
                .Select(decision => (Decision: decision, Row: Resolve(decision)))
                .ToArray();
            var resolvedRows = new HashSet<ReviewComparisonRow>(ReferenceEqualityComparer.Instance);
            if (resolvedDecisions.Any(item => item.Row is not null && !resolvedRows.Add(item.Row)))
                throw new InvalidDataException("Review decisions ambiguously resolve more than once to the same comparison row.");
            foreach (var resolved in resolvedDecisions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = resolved.Decision;
                var row = resolved.Row;
                if (row is null)
                {
                    skippedUnmapped++;
                    continue;
                }
                var status = decision.Status;
                var candidate = decision.Text;
                if (!ReviewSafety.HasDecisionSourceEvidence(decision)
                    || decision.SourceChanged
                    || ReviewSafety.DecisionSourceChanged(decision, row))
                {
                    skippedSourceChanged++;
                    continue;
                }
                if (!currentSources.Matches(row))
                {
                    skippedSourceChanged++;
                    continue;
                }
                if (!StatusIncluded(status, options.ApplyStatus))
                {
                    skippedNotApproved++;
                    continue;
                }
                if (!ReviewSafety.IsTranslationSafe(row, candidate, sourceExtractor))
                {
                    skippedUnsafe++;
                    continue;
                }
                AddCandidate(row, candidate, status);
            }
        }

        var appliedEntries = 0;
        var skippedExisting = 0;
        var outputEvidence = new Dictionary<string, ApplyOutputEvidence>(StringComparer.OrdinalIgnoreCase);
        ReviewComparisonDocument.VerifyUnchanged(reviewRoot, comparisonDocument.Evidence);
        VerifyModRootUnchanged(modRoot, modRootSnapshot);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var group in outputGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = File.Exists(group.Key);
            var existing = languageFiles.Read(group.Key);
            outputEvidence[group.Key] = new ApplyOutputEvidence(existed, existing);
            foreach (var key in group.Value.Keys)
            {
                if (options.Overwrite || !existing.ContainsKey(key)) appliedEntries++;
                else skippedExisting++;
            }
        }

        var planFingerprint = CreatePlanFingerprint(
            options,
            modRoot,
            reviewRoot,
            outputGroups,
            outputEvidence,
            safeRows,
            approvedRows,
            translatedRows,
            appliedEntries,
            skippedExisting,
            skippedNotApproved,
            skippedSourceChanged,
            skippedUnsafe,
            skippedBlank,
            skippedUnmapped);
        OperationPlanFingerprint.RequireExpected(options.ExpectedPlanFingerprint, planFingerprint);

        if (!options.DryRun)
        {
            VerifyModRootUnchanged(modRoot, modRootSnapshot);
            if (currentSources.SourceFiles.Any(source => outputGroups.ContainsKey(source)))
                throw new InvalidDataException("A source evidence file cannot also be a translation output target.");
            using var recoverySession = FileTransaction.AcquireRecoveryLease(
                recoveryAuthority,
                modRoot,
                cancellationToken);
            using var sourceBoundary = PathSafety.AcquireTrustedReadBoundary(
                modRoot,
                currentSources.SourceFiles,
                cancellationToken);
            var lockedSources = ReviewSourceSnapshot.Capture(
                sourceExtractor,
                modRoot,
                options.SourceLanguageFolder,
                cancellationToken);
            if (!currentSources.HasSameEvidence(lockedSources))
                throw new InvalidDataException("Source evidence changed before the write boundary could be locked.");
            using var writeBoundary = PathSafety.AcquireTrustedWriteBoundary(
                modRoot,
                outputGroups.Keys,
                cancellationToken);
            AfterWriteBoundaryLockedTestHook?.Invoke();
            writeBoundary.VerifyUnchanged();
            VerifyOutputEvidenceUnchanged(
                outputEvidence,
                languageFiles,
                writeBoundary,
                cancellationToken);
            VerifyModRootUnchanged(modRoot, modRootSnapshot);
            var finalSources = ReviewSourceSnapshot.Capture(
                sourceExtractor,
                modRoot,
                options.SourceLanguageFolder,
                cancellationToken);
            if (!lockedSources.HasSameEvidence(finalSources))
                throw new InvalidDataException("Source evidence changed after the write boundary was locked.");
            var results = languageFiles.WriteTransaction(
                outputGroups.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.OrdinalIgnoreCase),
                options.Overwrite,
                writeBoundary,
                recoverySession,
                cancellationToken);
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
            skippedSourceChanged,
            skippedUnsafe,
            skippedBlank,
            skippedUnmapped,
            planFingerprint);
    }

    private static string CreatePlanFingerprint(
        ReviewApplyOptions options,
        string modRoot,
        string reviewRoot,
        IReadOnlyDictionary<string, Dictionary<string, string>> outputGroups,
        IReadOnlyDictionary<string, ApplyOutputEvidence> outputEvidence,
        int safeRows,
        int approvedRows,
        int translatedRows,
        int appliedEntries,
        int skippedExisting,
        int skippedNotApproved,
        int skippedSourceChanged,
        int skippedUnsafe,
        int skippedBlank,
        int skippedUnmapped)
    {
        using var fingerprint = new OperationPlanFingerprint();
        fingerprint.Add("review-apply-plan-v1");
        fingerprint.AddPath(modRoot);
        fingerprint.AddPath(reviewRoot);
        fingerprint.Add(options.SourceKind);
        fingerprint.Add(options.SourceLanguageFolder);
        fingerprint.Add(options.LanguageFolderName);
        fingerprint.Add(options.ApplyStatus.ToString());
        fingerprint.Add(options.Overwrite);
        fingerprint.Add(safeRows);
        fingerprint.Add(approvedRows);
        fingerprint.Add(translatedRows);
        fingerprint.Add(outputGroups.Count);
        fingerprint.Add(appliedEntries);
        fingerprint.Add(skippedExisting);
        fingerprint.Add(skippedNotApproved);
        fingerprint.Add(skippedSourceChanged);
        fingerprint.Add(skippedUnsafe);
        fingerprint.Add(skippedBlank);
        fingerprint.Add(skippedUnmapped);

        foreach (var group in outputGroups.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            fingerprint.AddPath(group.Key);
            var evidence = outputEvidence[group.Key];
            fingerprint.Add(evidence.Existed);
            fingerprint.Add(evidence.Entries.Count);
            foreach (var pair in evidence.Entries)
            {
                fingerprint.Add(pair.Key);
                fingerprint.Add(pair.Value);
            }
            fingerprint.Add(group.Value.Count);
            foreach (var pair in group.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                fingerprint.Add(pair.Key);
                fingerprint.Add(pair.Value);
            }
        }

        return fingerprint.Complete();
    }

    private static void VerifyOutputEvidenceUnchanged(
        IReadOnlyDictionary<string, ApplyOutputEvidence> expected,
        LanguageFileService languageFiles,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken)
    {
        foreach (var pair in expected.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (writeBoundary.TargetExisted(pair.Key) != pair.Value.Existed)
                throw new InvalidDataException("A local apply output appeared or disappeared before its write boundary was locked.");
            var current = languageFiles.Read(pair.Key);
            if (!SameEntries(pair.Value.Entries, current))
                throw new InvalidDataException("A local apply output changed before its write boundary was locked.");
        }
    }

    private static bool SameEntries(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual) =>
        expected.Count == actual.Count
        && expected.All(pair => actual.TryGetValue(pair.Key, out var value)
            && value.Equals(pair.Value, StringComparison.Ordinal));

    private static bool StatusIncluded(string status, ReviewApplyStatus mode) =>
        status == ReviewStatuses.Approved
        || mode == ReviewApplyStatus.TranslatedAndApproved && status == ReviewStatuses.Translated;

    private static void VerifyModRootUnchanged(
        string modRoot,
        PathSafety.ExistingDirectorySnapshot expected)
    {
        PathSafety.EnsureNoReparsePointsToVolumeRoot(modRoot);
        var current = PathSafety.GetExistingDirectorySnapshot(modRoot);
        if (!current.CanonicalPath.Equals(expected.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || !current.Identity.Equals(expected.Identity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local mod root changed while the apply preview was being prepared.");
        }
        if (PathSafety.IsWorkshopContentPath(current.CanonicalPath))
            throw new InvalidDataException("The local mod root now resolves into Steam Workshop content.");
    }

    private static void ValidateDecisionIdentities(IEnumerable<ReviewDecision> decisions)
    {
        var targetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keylessIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in decisions)
        {
            if (string.IsNullOrWhiteSpace(decision.Key))
            {
                if (string.IsNullOrWhiteSpace(decision.Target)
                    || string.IsNullOrWhiteSpace(decision.DefClass)
                    || string.IsNullOrWhiteSpace(decision.Node))
                {
                    throw new InvalidDataException(
                        "Review decisions contain an active keyless decision without stable target+defClass+node identity.");
                }
                var keylessIdentity = KeylessIdentity(
                    ReviewTargetIdentity.Canonicalize(decision.Target),
                    decision.DefClass,
                    decision.Node);
                if (!keylessIdentities.Add(keylessIdentity))
                    throw new InvalidDataException($"Review decisions contain an ambiguous duplicate keyless identity: {keylessIdentity}");
                continue;
            }
            var identity = $"{ReviewTargetIdentity.Canonicalize(decision.Target)}|{decision.Key}";
            if (!targetKeys.Add(identity))
                throw new InvalidDataException($"Review decisions contain an ambiguous duplicate target+key identity: {identity}");
        }
    }

    private sealed record ApplyOutputEvidence(
        bool Existed,
        SortedDictionary<string, string> Entries);

    private static string KeylessIdentity(string target, string defClass, string node) =>
        $"{target.Length}:{target}{defClass.Length}:{defClass}{node.Length}:{node}";

    private static string? RelativeReviewTarget(string target, string reviewLanguageRoot)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        string fullTarget;
        try
        {
            fullTarget = Path.GetFullPath(target);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Invalid review target skipped ({exception.GetType().Name}).");
            return null;
        }
        if (!PathSafety.IsStrictlyInside(fullTarget, reviewLanguageRoot)) return null;
        var relative = Path.GetRelativePath(reviewLanguageRoot, fullTarget);
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? null
            : ReviewTargetIdentity.Canonicalize(relative);
    }

    private static void ValidateSegment(string value)
    {
        if (!PathSafety.IsSafeFileNameSegment(value))
            throw new InvalidDataException("LanguageFolderName must be a single safe folder-name segment.");
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
}
