using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Quality;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Utilities;
using RimWorldAiTranslator.Core.Validation;

namespace RimWorldAiTranslator.Core.Review;

public enum ReviewSearchField
{
    All,
    Text,
    Key,
    DefClass,
    Node
}

public enum ReviewStatusFilter
{
    All,
    Pending,
    Translated,
    Approved,
    Updated,
    Warning,
    HasCandidate,
    HasExisting,
    Rmk,
    Local
}

public enum ReviewSortMode
{
    Default,
    TranslationNewest,
    TranslationOldest,
    Key
}

public sealed record ReviewQuery(
    string Text = "",
    ReviewSearchField SearchField = ReviewSearchField.All,
    ReviewStatusFilter Status = ReviewStatusFilter.All,
    string RelativeFile = "",
    ReviewSortMode Sort = ReviewSortMode.Default);

public sealed record ReviewWorkspaceStats(
    int Total,
    int Pending,
    int Translated,
    int Approved,
    int Updated,
    int Warnings);

public sealed class ReviewItem
{
    internal ReviewItem(ReviewComparisonRow row, ReviewDecision decision, string relativeTarget, int originalIndex)
    {
        Row = row;
        Decision = decision;
        RelativeTarget = relativeTarget;
        OriginalIndex = originalIndex;
    }

    public ReviewComparisonRow Row { get; }
    public ReviewDecision Decision { get; }
    public string RelativeTarget { get; }
    public int OriginalIndex { get; }
    public bool Touched { get; internal set; }
    public string EffectiveStatus => Decision.SourceChanged ? ReviewStatuses.Pending : NormalizeStatus(Decision.Status);
    public string EffectiveTranslation => Decision.Text;
    public bool IsWarning
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Decision.Text)) return false;
            var validation = TranslationValidator.Validate(Row.Source, Decision.Text);
            return !validation.IsSafe || Decision.Text.Equals(Row.Source, StringComparison.Ordinal);
        }
    }

    public override string ToString()
    {
        var source = AccessiblePreview(Row.Source);
        var translation = AccessiblePreview(Decision.Text);
        var status = Decision.SourceChanged ? "변경됨" : EffectiveStatus switch
        {
            ReviewStatuses.Approved => "검토 완료",
            ReviewStatuses.Translated => "번역됨",
            _ => "미번역"
        };
        return string.IsNullOrWhiteSpace(translation)
            ? $"{source}, {status}, 번역 대기"
            : $"{source}, {status}, {translation}";
    }

    private static string AccessiblePreview(string? value)
    {
        var text = string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 160 ? text : text[..157] + "...";
    }

    private static string NormalizeStatus(string status) => status == "reviewed" ? ReviewStatuses.Approved : status;
}

public sealed class ReviewWorkspace
{
    internal ReviewWorkspace(
        string reviewRoot,
        string comparisonFile,
        List<ReviewItem> items,
        Dictionary<string, JsonElement>? extensionData,
        List<ReviewDecision> quarantinedItems,
        ReviewComparisonEvidence comparisonEvidence)
    {
        ReviewRoot = reviewRoot;
        ComparisonFile = comparisonFile;
        Items = items;
        ExtensionData = extensionData;
        QuarantinedItems = quarantinedItems;
        ComparisonEvidence = comparisonEvidence;
    }

    public string ReviewRoot { get; }
    public string ComparisonFile { get; }
    public List<ReviewItem> Items { get; }
    internal Dictionary<string, JsonElement>? ExtensionData { get; }
    internal List<ReviewDecision> QuarantinedItems { get; }
    internal ReviewComparisonEvidence ComparisonEvidence { get; }
    public bool Dirty { get; internal set; }
    public bool MigrationPending { get; internal set; }
    public int ImportedDecisions { get; internal set; }
    public int ChangedSources { get; internal set; }
    internal object SyncRoot { get; } = new();
    internal long Revision { get; set; }
}

public sealed class ReviewWorkspaceService
{
    internal const long DefaultLegacyComparisonMaximumAggregateBytes = 512L * 1024 * 1024;
    internal const int DefaultLegacyComparisonMaximumRows = 1_000_000;
    private readonly AtomicJsonStore store;
    private readonly SourceExtractor extractor;
    private readonly string? trustedReviewsRoot;
    internal Action<int, int>? LegacyComparisonRetentionTestHook { get; set; }
    internal long LegacyComparisonMaximumAggregateBytes { get; init; } =
        DefaultLegacyComparisonMaximumAggregateBytes;
    internal int LegacyComparisonMaximumRows { get; init; } =
        DefaultLegacyComparisonMaximumRows;
    public ReviewWorkspaceService(
        AtomicJsonStore store,
        SourceExtractor extractor,
        string? trustedReviewsRoot = null)
    {
        this.store = store;
        this.extractor = extractor;
        this.trustedReviewsRoot = string.IsNullOrWhiteSpace(trustedReviewsRoot)
            ? null
            : ReviewRepository.ValidateWritableReviewRoot(trustedReviewsRoot);
    }

    public Task<ReviewWorkspace> LoadAsync(
        string reviewRoot,
        TranslationProject? project = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(reviewRoot, project, cancellationToken), cancellationToken);

    public ReviewWorkspace Load(string reviewRoot, TranslationProject? project = null, CancellationToken cancellationToken = default)
    {
        var root = ReviewRepository.ValidateWritableReviewRoot(reviewRoot, trustedReviewsRoot);
        var hasCurrentDecisions = new ReviewRepository(store).TryLoad(
            root,
            out var current,
            trustedReviewsRoot,
            cancellationToken);
        var legacyUnboundDecisions = hasCurrentDecisions
            && current.Version < ReviewDecisionDocument.CurrentVersion;
        var activeDecisions = current.Items.ToList();
        var quarantinedDecisions = current.QuarantinedItems.Select(CloneDecision).ToList();
        if (legacyUnboundDecisions)
        {
            foreach (var keyless in activeDecisions.Where(decision => string.IsNullOrWhiteSpace(decision.Key)).ToArray())
            {
                activeDecisions.Remove(keyless);
                quarantinedDecisions.Add(CloneDecision(keyless));
            }
        }
        var languageRoot = Path.Combine(root, "Languages", "Korean");
        DecisionLookup lookup;
        PreparedComparison preparedComparison;
        if (hasCurrentDecisions)
        {
            if (legacyUnboundDecisions)
            {
                preparedComparison = FindLegacyComparison(
                    root,
                    current.Comparison,
                    languageRoot,
                    activeDecisions,
                    cancellationToken);
                var partition = PartitionLegacyDecisions(
                    activeDecisions,
                    preparedComparison.Rows,
                    languageRoot,
                    cancellationToken);
                activeDecisions = partition.Active;
                quarantinedDecisions.AddRange(partition.Quarantined.Select(CloneDecision));
                lookup = BuildDecisionLookup(
                    activeDecisions,
                    rejectAmbiguousIdentities: true,
                    cancellationToken: cancellationToken);
                ValidateDecisionCoverage(
                    activeDecisions,
                    lookup,
                    preparedComparison.Rows,
                    languageRoot,
                    preparedComparison.AmbiguousKeys,
                    allowUnscopedKeyFallback: false,
                    cancellationToken: cancellationToken);
            }
            else
            {
                lookup = BuildDecisionLookup(
                    activeDecisions,
                    rejectAmbiguousIdentities: true,
                    cancellationToken: cancellationToken);
                preparedComparison = LoadPreparedComparison(
                    root,
                    current.Comparison,
                    languageRoot,
                    cancellationToken: cancellationToken);
                ReviewComparisonDocument.RequireBoundDecisionStore(
                    current,
                    preparedComparison.Document.Evidence);
                ValidateDecisionCoverage(
                    activeDecisions,
                    lookup,
                    preparedComparison.Rows,
                    languageRoot,
                    preparedComparison.AmbiguousKeys,
                    allowUnscopedKeyFallback: false,
                    cancellationToken: cancellationToken);
            }
        }
        else
        {
            lookup = BuildDecisionLookup(
                activeDecisions,
                rejectAmbiguousIdentities: false,
                cancellationToken: cancellationToken);
            preparedComparison = LoadPreparedComparison(
                root,
                FindComparisonFile(root),
                languageRoot,
                cancellationToken: cancellationToken);
            current.Comparison = preparedComparison.Document.Evidence.Path;
        }
        var comparisonDocument = preparedComparison.Document;
        var comparison = comparisonDocument.Evidence.Path;
        var rows = preparedComparison.Rows;
        PreviousReview? previous = null;
        if (!hasCurrentDecisions && project is not null)
            previous = LoadPrevious(project, root, cancellationToken);

        var ambiguousComparisonKeys = preparedComparison.AmbiguousKeys;
        if (hasCurrentDecisions && activeDecisions.Any(decision =>
                !string.IsNullOrWhiteSpace(decision.Key)
                && string.IsNullOrWhiteSpace(decision.Target)
                && ambiguousComparisonKeys.Contains(decision.Key)))
        {
            throw new InvalidDataException(
                "Review decisions contain an unscoped key that ambiguously matches multiple comparison targets.");
        }
        var storedSources = hasCurrentDecisions && !legacyUnboundDecisions
            ? BuildSourceLookup(comparisonDocument.Rows, languageRoot, cancellationToken)
            : new SourceLookup();
        var items = new List<ReviewItem>(rows.Count);
        var workspace = new ReviewWorkspace(
            root,
            comparison,
            items,
            CloneExtensionData(current.ExtensionData),
            quarantinedDecisions,
            comparisonDocument.Evidence);
        var matchedCurrentDecisions = new HashSet<ReviewDecision>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            var relative = RelativeTarget(row.Target, languageRoot);
            var decision = ResolveDecision(
                lookup,
                row,
                relative,
                ambiguousComparisonKeys,
                allowUnscopedKeyFallback: false);
            if (decision is not null && hasCurrentDecisions) matchedCurrentDecisions.Add(decision);
            var sourceEvidence = decision is null
                ? null
                : ResolveSource(
                    storedSources,
                    row,
                    relative,
                    ambiguousComparisonKeys,
                    allowUnscopedKeyFallback: false);
            var imported = false;
            var importedFromUnboundPrevious = false;
            if (decision is null && previous is not null)
            {
                var previousDecision = ResolveDecision(
                    previous.Decisions,
                    row,
                    relative,
                    ambiguousComparisonKeys,
                    allowUnscopedKeyFallback: false);
                var previousSource = previousDecision is null
                    ? null
                    : ResolveSource(
                        previous.Sources,
                        row,
                        relative,
                        ambiguousComparisonKeys,
                        allowUnscopedKeyFallback: false);
                var safeUnscopedMatch = previousDecision is not null
                    && (string.IsNullOrWhiteSpace(previousDecision.Target)
                        ? previousSource is not null
                          || ReviewSafety.HasDecisionSourceEvidence(previousDecision)
                          && !ReviewSafety.DecisionSourceChanged(previousDecision, row)
                        : true);
                if (safeUnscopedMatch)
                {
                    decision = CloneForRow(previousDecision!, row, relative);
                    sourceEvidence = previousSource;
                    imported = true;
                    importedFromUnboundPrevious = previous.Unbound;
                    workspace.ImportedDecisions++;
                }
            }
            var retainedDecision = decision is not null;
            decision ??= CreateDefaultDecision(row, relative);
            var unscopedDecision = retainedDecision
                && !string.IsNullOrWhiteSpace(decision.Key)
                && string.IsNullOrWhiteSpace(decision.Target);
            var verifiedPowerShellV5Source = retainedDecision
                && current.Version == 5
                && !string.IsNullOrWhiteSpace(decision.SourceHash)
                && decision.SourceHash.Equals(
                    StableIdentity.Sha256(Normalize(row.Source)),
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(decision.SourceText)
                && SourceEquals(decision.SourceText, row.Source);
            var sourceVerificationUnavailable = retainedDecision
                && (legacyUnboundDecisions && !verifiedPowerShellV5Source
                    || importedFromUnboundPrevious
                    || unscopedDecision
                    || string.IsNullOrWhiteSpace(decision.SourceHash)
                    && string.IsNullOrEmpty(decision.SourceText)
                    && (IsSourceVerificationUnavailable(decision) || sourceEvidence is null));
            if (retainedDecision
                && !sourceVerificationUnavailable
                && string.IsNullOrWhiteSpace(decision.SourceHash)
                && string.IsNullOrEmpty(decision.SourceText)
                && !IsSourceVerificationUnavailable(decision)
                && sourceEvidence is not null)
            {
                decision.SourceText = sourceEvidence;
                decision.SourceHash = StableIdentity.Sha256(Normalize(sourceEvidence));
            }
            var sourceWasAlreadyChanged = decision.SourceChanged;
            var changed = NormalizeDecision(row, decision, relative, sourceVerificationUnavailable);

            var oldSource = previous is null
                ? null
                : ResolveSource(
                    previous.Sources,
                    row,
                    relative,
                    ambiguousComparisonKeys,
                    allowUnscopedKeyFallback: false);
            if (oldSource is not null && !SourceEquals(oldSource, row.Source))
            {
                decision.Status = ReviewStatuses.Pending;
                if (!sourceWasAlreadyChanged || string.IsNullOrEmpty(decision.PreviousSourceText))
                    decision.PreviousSourceText = oldSource;
                decision.SourceChanged = true;
                decision.SourceHash = StableIdentity.Sha256(Normalize(row.Source));
                decision.SourceText = row.Source;
                if (string.IsNullOrWhiteSpace(decision.UpdatedAt)) decision.UpdatedAt = DateTimeOffset.Now.ToString("O");
                changed = true;
            }

            var item = new ReviewItem(row, decision, relative, index) { Touched = retainedDecision || imported };
            items.Add(item);
            if (decision.SourceChanged) workspace.ChangedSources++;
            if (changed || imported) workspace.MigrationPending = true;
        }
        if (hasCurrentDecisions && matchedCurrentDecisions.Count != activeDecisions.Count)
            throw new InvalidDataException(
                "Review decisions do not exactly match their declared comparison file; the decision store was left unchanged.");
        if (legacyUnboundDecisions || quarantinedDecisions.Count != current.QuarantinedItems.Count)
            workspace.MigrationPending = true;
        return workspace;
    }

    public Task SaveAsync(ReviewWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureSaveSnapshot(workspace, cancellationToken);
        return Task.Run(() => PersistSaveSnapshot(workspace, snapshot, cancellationToken), cancellationToken);
    }

    public void Save(ReviewWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureSaveSnapshot(workspace, cancellationToken);
        PersistSaveSnapshot(workspace, snapshot, cancellationToken);
    }

    private SaveSnapshot CaptureSaveSnapshot(ReviewWorkspace workspace, CancellationToken cancellationToken)
    {
        lock (workspace.SyncRoot)
        {
            var document = new ReviewDecisionDocument
            {
                ReviewRoot = workspace.ReviewRoot,
                Comparison = workspace.ComparisonFile,
                ComparisonSha256 = workspace.ComparisonEvidence.Sha256,
                QuarantinedItems = workspace.QuarantinedItems.Select(CloneDecision).ToList(),
                ExtensionData = CloneExtensionData(workspace.ExtensionData)
            };
            foreach (var item in workspace.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldPersist(item)) continue;
                var persisted = CloneForRow(item.Decision, item.Row, item.RelativeTarget);
                if (string.IsNullOrWhiteSpace(persisted.Key) && !HasStableKeylessIdentity(persisted))
                    document.QuarantinedItems.Add(persisted);
                else
                    document.Items.Add(persisted);
            }
            return new SaveSnapshot(document, workspace.Revision, workspace.ComparisonEvidence);
        }
    }

    private void PersistSaveSnapshot(ReviewWorkspace workspace, SaveSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeReviewRoot = ReviewRepository.ValidateWritableReviewRoot(
            workspace.ReviewRoot,
            trustedReviewsRoot);
        if (!safeReviewRoot.Equals(workspace.ReviewRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The review workspace path changed before save.");
        ReviewComparisonDocument.VerifyUnchanged(safeReviewRoot, snapshot.ComparisonEvidence);
        cancellationToken.ThrowIfCancellationRequested();
        new ReviewRepository(store).Save(
            safeReviewRoot,
            snapshot.Document,
            snapshot.ComparisonEvidence,
            cancellationToken);
        lock (workspace.SyncRoot)
        {
            if (workspace.Revision != snapshot.Revision) return;
            foreach (var item in workspace.Items) item.Touched = false;
            workspace.Dirty = false;
            workspace.MigrationPending = false;
        }
    }

    public IReadOnlyList<ReviewItem> Query(ReviewWorkspace workspace, ReviewQuery query)
    {
        lock (workspace.SyncRoot)
        {
            IEnumerable<ReviewItem> result = workspace.Items;
            if (!string.IsNullOrWhiteSpace(query.RelativeFile))
                result = result.Where(item => item.RelativeTarget.Equals(query.RelativeFile, StringComparison.OrdinalIgnoreCase));
            result = result.Where(item => MatchesStatus(item, query.Status));
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                var needle = query.Text.Trim();
                result = result.Where(item => SearchBlob(item, query.SearchField).Contains(needle, StringComparison.CurrentCultureIgnoreCase));
            }
            return query.Sort switch
            {
                ReviewSortMode.TranslationNewest => result.OrderByDescending(item => ParseTime(item.Decision.TranslationUpdatedAt)).ThenBy(item => item.OriginalIndex).ToArray(),
                ReviewSortMode.TranslationOldest => result.OrderBy(item => ParseTime(item.Decision.TranslationUpdatedAt)).ThenBy(item => item.OriginalIndex).ToArray(),
                ReviewSortMode.Key => result.OrderBy(item => item.Row.Key, StringComparer.OrdinalIgnoreCase).ToArray(),
                _ => result.OrderBy(item => item.OriginalIndex).ToArray()
            };
        }
    }

    public ReviewWorkspaceStats GetStats(ReviewWorkspace workspace)
    {
        lock (workspace.SyncRoot)
        {
            var pending = 0;
            var translated = 0;
            var approved = 0;
            var updated = 0;
            var warnings = 0;
            foreach (var item in workspace.Items)
            {
                switch (item.EffectiveStatus)
                {
                    case ReviewStatuses.Translated: translated++; break;
                    case ReviewStatuses.Approved: approved++; break;
                    default: pending++; break;
                }
                if (item.Decision.SourceChanged) updated++;
                if (item.IsWarning) warnings++;
            }
            return new ReviewWorkspaceStats(workspace.Items.Count, pending, translated, approved, updated, warnings);
        }
    }

    public IReadOnlyList<QualityEntry> CreateQualityEntries(ReviewWorkspace workspace)
    {
        lock (workspace.SyncRoot)
        {
            return workspace.Items.Select((item, index) => new QualityEntry(
                index,
                item.Row.Key,
                item.RelativeTarget,
                item.Row.DefClass,
                item.Row.Source,
                item.Decision.Text,
                item.Row.Existing,
                item.EffectiveStatus,
                item.Decision.SourceChanged,
                ReviewSafety.IsTranslationSafe(item.Row, item.Decision.Text, extractor))).ToArray();
        }
    }

    public void UpdateTranslation(
        ReviewWorkspace workspace,
        ReviewItem item,
        string translation,
        string note,
        bool editedByUser,
        string? translationOrigin = null)
    {
        lock (workspace.SyncRoot)
        {
            var text = Normalize(translation);
            var textChanged = !item.Decision.Text.Equals(text, StringComparison.Ordinal);
            var noteChanged = !item.Decision.Note.Equals(note ?? string.Empty, StringComparison.Ordinal);
            if (!textChanged && !noteChanged) return;
            item.Decision.Text = text;
            item.Decision.Note = note ?? string.Empty;
            var now = DateTimeOffset.Now.ToString("O");
            if (textChanged && editedByUser)
            {
                item.Decision.TranslationOrigin = string.IsNullOrWhiteSpace(translationOrigin)
                    ? "local"
                    : translationOrigin.Trim().ToLowerInvariant();
                item.Decision.TranslationUpdatedAt = now;
                if (string.IsNullOrWhiteSpace(text)) item.Decision.Status = ReviewStatuses.Pending;
                else if (item.Decision.Status is ReviewStatuses.Pending or ReviewStatuses.Approved)
                {
                    item.Decision.Status = ReviewStatuses.Translated;
                    EstablishSourceSnapshot(item);
                }
            }
            item.Decision.UpdatedAt = now;
            item.Touched = true;
            MarkDirty(workspace);
        }
    }

    public void SetStatus(ReviewWorkspace workspace, ReviewItem item, string status)
    {
        lock (workspace.SyncRoot)
        {
            if (status == "reviewed") status = ReviewStatuses.Approved;
            item.Decision.Status = status;
            if (status != ReviewStatuses.Pending)
            {
                EstablishSourceSnapshot(item);
            }
            item.Decision.UpdatedAt = DateTimeOffset.Now.ToString("O");
            item.Touched = true;
            MarkDirty(workspace);
        }
    }

    public int ApplyToDuplicateSources(ReviewWorkspace workspace, ReviewItem sourceItem)
    {
        lock (workspace.SyncRoot)
        {
            if (string.IsNullOrWhiteSpace(sourceItem.Row.Source)
                || !ReviewSafety.IsTranslationSafe(sourceItem.Row, sourceItem.Decision.Text, extractor)) return 0;
            var now = DateTimeOffset.Now.ToString("O");
            var changed = 0;
            foreach (var item in workspace.Items)
            {
                if (ReferenceEquals(item, sourceItem) || !item.Row.Source.Equals(sourceItem.Row.Source, StringComparison.Ordinal)) continue;
                if (!ReviewSafety.IsTranslationSafe(item.Row, sourceItem.Decision.Text, extractor)) continue;
                if (item.Decision.Text.Equals(sourceItem.Decision.Text, StringComparison.Ordinal)
                    && item.EffectiveStatus is ReviewStatuses.Translated or ReviewStatuses.Approved
                    && !item.Decision.SourceChanged) continue;
                item.Decision.Text = sourceItem.Decision.Text;
                item.Decision.Status = ReviewStatuses.Translated;
                item.Decision.TranslationOrigin = "local";
                item.Decision.TranslationUpdatedAt = now;
                EstablishSourceSnapshot(item);
                item.Decision.UpdatedAt = now;
                item.Touched = true;
                changed++;
            }
            if (changed > 0) MarkDirty(workspace);
            return changed;
        }
    }

    public int ApproveAllEligible(ReviewWorkspace workspace)
    {
        lock (workspace.SyncRoot)
        {
            var now = DateTimeOffset.Now.ToString("O");
            var count = 0;
            foreach (var item in workspace.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Decision.Text) || item.Decision.SourceChanged || item.IsWarning) continue;
                if (item.EffectiveStatus == ReviewStatuses.Approved) continue;
                item.Decision.Status = ReviewStatuses.Approved;
                item.Decision.UpdatedAt = now;
                item.Touched = true;
                count++;
            }
            if (count > 0) MarkDirty(workspace);
            return count;
        }
    }

    private static void MarkDirty(ReviewWorkspace workspace)
    {
        workspace.Dirty = true;
        workspace.Revision++;
    }

    public static string FindComparisonFile(string reviewRoot)
    {
        if (PathSafety.IsNetworkPath(reviewRoot))
            throw new InvalidDataException("Review workspaces must use a local path.");
        var audit = Path.Combine(Path.GetFullPath(reviewRoot), "_TranslationAudit");
        if (!Directory.Exists(audit)) throw new DirectoryNotFoundException($"Translation audit folder was not found: {audit}");
        return ReviewComparisonDocument.EnumerateCandidateFiles(audit)
                   .OrderByDescending(File.GetLastWriteTimeUtc)
                   .FirstOrDefault()
               ?? throw new FileNotFoundException($"Comparison JSON was not found in: {audit}");
    }

    private PreparedComparison LoadPreparedComparison(
        string reviewRoot,
        string comparisonPath,
        string languageRoot,
        Action<long>? lengthObserver = null,
        Action<int>? rowObserver = null,
        CancellationToken cancellationToken = default)
    {
        var document = ReviewComparisonDocument.LoadExact(
            reviewRoot,
            comparisonPath,
            new ReviewComparisonLimits(),
            lengthObserver,
            rowObserver,
            cancellationToken);
        var rows = new List<ReviewComparisonRow>(document.Rows.Count);
        foreach (var row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (extractor.GetInternalIdentifierReason(row.Key, row.Kind, row.DefClass, row.Field) is null)
                rows.Add(row);
        }
        return new PreparedComparison(
            document,
            rows,
            ValidateComparisonIdentities(rows, languageRoot, cancellationToken));
    }

    private PreparedComparison FindLegacyComparison(
        string reviewRoot,
        string declaredComparisonPath,
        string languageRoot,
        IReadOnlyCollection<ReviewDecision> decisions,
        CancellationToken cancellationToken)
    {
        var auditRoot = Path.Combine(Path.GetFullPath(reviewRoot), "_TranslationAudit");
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PreparedComparison? declared = null;
        PreparedComparison? winner = null;
        string? winnerFingerprint = null;
        var maximumMatches = -1;
        var conflictingFinalists = false;
        var processedCandidates = 0;
        long processedBytes = 0;
        var processedRows = 0;
        Exception? lastError = null;

        void TryProcess(string candidatePath, bool isDeclared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string admittedPath;
            try
            {
                admittedPath = ReviewComparisonDocument.ResolveExactPathForAdmission(reviewRoot, candidatePath);
            }
            catch (Exception exception) when (IsComparisonMigrationFailure(exception))
            {
                lastError = exception;
                return;
            }
            if (!seenPaths.Add(admittedPath)) return;

            void ObserveLength(long candidateLength)
            {
                if (candidateLength < 0
                    || candidateLength > LegacyComparisonMaximumAggregateBytes - processedBytes)
                {
                    throw new LegacyComparisonLimitException(
                        $"Legacy comparison migration exceeds the aggregate {LegacyComparisonMaximumAggregateBytes:N0}-byte input limit.");
                }
                processedBytes += candidateLength;
            }

            void ObserveRows(int rawRowCount)
            {
                if (rawRowCount > LegacyComparisonMaximumRows - processedRows)
                {
                    throw new LegacyComparisonLimitException(
                        $"Legacy comparison migration exceeds the aggregate {LegacyComparisonMaximumRows:N0}-row processing limit.");
                }
                processedRows += rawRowCount;
            }

            PreparedComparison candidate;
            try
            {
                candidate = LoadPreparedComparison(
                    reviewRoot,
                    admittedPath,
                    languageRoot,
                    ObserveLength,
                    ObserveRows,
                    cancellationToken);
            }
            catch (LegacyComparisonLimitException exception)
            {
                throw new InvalidDataException(exception.Message, exception);
            }
            catch (Exception exception) when (IsComparisonMigrationFailure(exception))
            {
                lastError = exception;
                System.Diagnostics.Debug.WriteLine(
                    $"Legacy comparison candidate was not compatible ({exception.GetType().Name}).");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var matches = CountStableLegacyMatches(
                decisions,
                candidate.Rows,
                languageRoot,
                cancellationToken);
            var fingerprint = ComparisonSemanticFingerprint(
                candidate,
                languageRoot,
                cancellationToken);
            if (isDeclared) declared = candidate;
            if (matches > maximumMatches)
            {
                maximumMatches = matches;
                winner = candidate;
                winnerFingerprint = fingerprint;
                conflictingFinalists = false;
            }
            else if (matches == maximumMatches
                     && !fingerprint.Equals(winnerFingerprint, StringComparison.Ordinal))
            {
                conflictingFinalists = true;
            }

            if (maximumMatches > 0
                && declared is not null
                && !ReferenceEquals(declared, winner))
            {
                declared = null;
            }
            processedCandidates++;
            LegacyComparisonRetentionTestHook?.Invoke(
                processedCandidates,
                winner is null ? 0 : declared is null || ReferenceEquals(declared, winner) ? 1 : 2);
        }

        TryProcess(declaredComparisonPath, isDeclared: true);
        if (Directory.Exists(auditRoot))
        {
            foreach (var candidatePath in ReviewComparisonDocument.EnumerateCandidateFiles(auditRoot)
                      .OrderByDescending(File.GetLastWriteTimeUtc)
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                TryProcess(candidatePath, isDeclared: false);
            }
        }

        if (processedCandidates == 0 || winner is null)
            throw new InvalidDataException(
                "Legacy review decisions have no readable comparison file in this review's audit folder.",
                lastError);

        if (maximumMatches == 0 && declared is not null) return declared;
        if (conflictingFinalists)
        {
            throw new InvalidDataException(
                "Legacy review decisions match multiple semantically different comparison files; the decision store was left unchanged.",
                lastError);
        }
        return winner;
    }

    private static int CountStableLegacyMatches(
        IEnumerable<ReviewDecision> decisions,
        IEnumerable<ReviewComparisonRow> rows,
        string languageRoot,
        CancellationToken cancellationToken)
    {
        var decisionIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in decisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = TryGetStableKeyedIdentity(decision);
            if (identity is not null) decisionIdentities.Add(identity);
        }
        var comparisonIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(row.Key))
                comparisonIdentities.Add(StableKeyedIdentity(RelativeTarget(row.Target, languageRoot), row.Key));
        }
        decisionIdentities.IntersectWith(comparisonIdentities);
        return decisionIdentities.Count;
    }

    private static LegacyDecisionPartition PartitionLegacyDecisions(
        IEnumerable<ReviewDecision> decisions,
        IEnumerable<ReviewComparisonRow> rows,
        string languageRoot,
        CancellationToken cancellationToken)
    {
        var comparisonIdentities = rows
            .Select(row =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return row;
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .Select(row => StableKeyedIdentity(RelativeTarget(row.Target, languageRoot), row.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = new List<ReviewDecision>();
        var quarantined = new List<ReviewDecision>();
        foreach (var group in decisions.GroupBy(
                     decision =>
                     {
                         cancellationToken.ThrowIfCancellationRequested();
                         return TryGetStableKeyedIdentity(decision);
                     },
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Key is not null
                && comparisonIdentities.Contains(group.Key)
                && group.Take(2).Count() == 1)
            {
                active.Add(group.Single());
            }
            else
            {
                quarantined.AddRange(group);
            }
        }
        return new LegacyDecisionPartition(active, quarantined);
    }

    private static string? TryGetStableKeyedIdentity(ReviewDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Key)
            || string.IsNullOrWhiteSpace(decision.Target)) return null;
        try
        {
            return StableKeyedIdentity(
                ReviewTargetIdentity.Canonicalize(decision.Target),
                decision.Key);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string StableKeyedIdentity(string target, string key) =>
        $"{target.Length}:{target}{key.Length}:{key}";

    private static string ComparisonSemanticFingerprint(
        PreparedComparison comparison,
        string languageRoot,
        CancellationToken cancellationToken)
    {
        var rowHashes = new List<byte[]>(comparison.Rows.Count);
        foreach (var row in comparison.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serializedRow = JsonSerializer.SerializeToUtf8Bytes(row);
            cancellationToken.ThrowIfCancellationRequested();
            var clone = JsonSerializer.Deserialize<ReviewComparisonRow>(serializedRow)
                ?? throw new InvalidDataException("Comparison row could not be canonicalized.");
            clone.Target = RelativeTarget(row.Target, languageRoot);
            if (clone.ExtensionData is not null)
            {
                clone.ExtensionData = clone.ExtensionData
                    .OrderBy(property => property.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        property => property.Key,
                        property => property.Value.Clone(),
                        StringComparer.Ordinal);
            }
            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(clone);
            rowHashes.Add(ComputeCancellableHash(canonicalBytes, cancellationToken));
        }

        var sortedHashes = SortHashes(rowHashes, cancellationToken);
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> count = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(count, sortedHashes.Length);
        aggregate.AppendData(count);
        foreach (var rowHash in sortedHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            aggregate.AppendData(rowHash);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(aggregate.GetHashAndReset());
    }

    private static byte[] ComputeCancellableHash(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunkSize = 64 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(bytes.Slice(offset, Math.Min(chunkSize, bytes.Length - offset)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return hash.GetHashAndReset();
    }

    private static byte[][] SortHashes(
        IReadOnlyCollection<byte[]> hashes,
        CancellationToken cancellationToken)
    {
        var source = hashes.ToArray();
        if (source.Length < 2) return source;
        var target = new byte[source.Length][];
        for (var width = 1; width < source.Length; width *= 2)
        {
            for (var left = 0; left < source.Length; left += width * 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var middle = Math.Min(left + width, source.Length);
                var right = Math.Min(left + width * 2, source.Length);
                var first = left;
                var second = middle;
                var output = left;
                while (first < middle || second < right)
                {
                    if (second >= right
                        || first < middle
                        && source[first].AsSpan().SequenceCompareTo(source[second]) <= 0)
                    {
                        target[output++] = source[first++];
                    }
                    else
                    {
                        target[output++] = source[second++];
                    }
                }
            }
            (source, target) = (target, source);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return source;
    }

    private static bool IsComparisonMigrationFailure(Exception exception) =>
        exception is InvalidDataException
            or FileNotFoundException
            or UnauthorizedAccessException
            or IOException;

    private sealed class LegacyComparisonLimitException(string message) : Exception(message);

    private static void ValidateDecisionCoverage(
        IReadOnlyCollection<ReviewDecision> decisions,
        DecisionLookup lookup,
        IReadOnlyList<ReviewComparisonRow> rows,
        string languageRoot,
        IReadOnlySet<string> ambiguousComparisonKeys,
        bool allowUnscopedKeyFallback,
        CancellationToken cancellationToken)
    {
        var matched = new HashSet<ReviewDecision>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeTarget = RelativeTarget(row.Target, languageRoot);
            var decision = ResolveDecision(
                lookup,
                row,
                relativeTarget,
                ambiguousComparisonKeys,
                allowUnscopedKeyFallback);
            if (decision is not null) matched.Add(decision);
        }

        if (matched.Count != decisions.Count)
        {
            throw new InvalidDataException(
                "Review decisions do not exactly match their declared comparison file; the decision store was left unchanged.");
        }
    }

    private PreviousReview? LoadPrevious(
        TranslationProject project,
        string currentRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = project.Runs.OrderByDescending(run => run.CreatedAt).Select(run => run.ReviewRoot)
            .Prepend(project.LatestReviewRoot)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => GetEligiblePreviousReviewRoot(root, currentRoot))
            .Where(root => root is not null)
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string? decisionRoot = null;
        ReviewDecisionDocument? document = null;
        var repository = new ReviewRepository(store);
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!repository.TryLoad(
                    root,
                    out var candidate,
                    trustedReviewsRoot,
                    cancellationToken)) continue;
            decisionRoot = root;
            document = candidate;
            break;
        }
        if (decisionRoot is null || document is null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var decisions = BuildDecisionLookup(
            document.Items,
            rejectAmbiguousIdentities: false,
            cancellationToken: cancellationToken);
        var sources = new SourceLookup();
        var unbound = document.Version < ReviewDecisionDocument.CurrentVersion;
        if (unbound)
        {
            System.Diagnostics.Debug.WriteLine(
                "Previous legacy review decisions have no bound comparison evidence; imported translations will require revalidation.");
            return new PreviousReview(decisions, sources, Unbound: true);
        }
        try
        {
            var comparison = ReviewComparisonDocument.LoadExact(
                decisionRoot,
                document.Comparison,
                cancellationToken);
            ReviewComparisonDocument.RequireBoundDecisionStore(document, comparison.Evidence);
            sources = BuildSourceLookup(
                comparison.Rows,
                Path.Combine(decisionRoot, "Languages", "Korean"),
                cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or FileNotFoundException
                                           or UnauthorizedAccessException
                                           or IOException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Previous comparison source evidence was unavailable ({exception.GetType().Name}).");
            return null;
        }
        return new PreviousReview(decisions, sources, Unbound: false);
    }

    private static SourceLookup BuildSourceLookup(
        IEnumerable<ReviewComparisonRow> rows,
        string languageRoot,
        CancellationToken cancellationToken = default)
    {
        var result = new SourceLookup();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(row.Key))
            {
                if (string.IsNullOrWhiteSpace(row.Id) || result.AmbiguousLegacyIds.Contains(row.Id)) continue;
                if (!result.ByLegacyId.TryAdd(row.Id, row.Source))
                {
                    result.ByLegacyId.Remove(row.Id);
                    result.AmbiguousLegacyIds.Add(row.Id);
                }
                continue;
            }

            var relative = RelativeTarget(row.Target, languageRoot);
            var targetKey = $"{relative}|{row.Key}";
            if (!result.AmbiguousTargetKeys.Contains(targetKey)
                && !result.ByTargetKey.TryAdd(targetKey, row.Source))
            {
                result.ByTargetKey.Remove(targetKey);
                result.AmbiguousTargetKeys.Add(targetKey);
            }
            if (result.AmbiguousKeys.Contains(row.Key)) continue;
            if (!result.ByUniqueKey.TryAdd(row.Key, row.Source))
            {
                result.ByUniqueKey.Remove(row.Key);
                result.AmbiguousKeys.Add(row.Key);
            }
        }
        return result;
    }

    private static string? ResolveSource(
        SourceLookup lookup,
        ReviewComparisonRow row,
        string relativeTarget,
        IReadOnlySet<string> ambiguousComparisonKeys,
        bool allowUnscopedKeyFallback)
    {
        if (!string.IsNullOrWhiteSpace(row.Key))
        {
            if (lookup.ByTargetKey.TryGetValue($"{relativeTarget}|{row.Key}", out var target)) return target;
            if (!allowUnscopedKeyFallback || ambiguousComparisonKeys.Contains(row.Key)) return null;
            return lookup.ByUniqueKey.TryGetValue(row.Key, out var source) ? source : null;
        }
        return null;
    }

    private static HashSet<string> ValidateComparisonIdentities(
        IEnumerable<ReviewComparisonRow> rows,
        string languageRoot,
        CancellationToken cancellationToken)
    {
        var targetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keylessIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(row.Key))
            {
                var relativeTarget = RelativeTarget(row.Target, languageRoot);
                if (TryGetKeylessIdentity(relativeTarget, row.DefClass, row.Node, out var keylessIdentity)
                    && !keylessIdentities.Add(keylessIdentity))
                {
                    throw new InvalidDataException(
                        $"Comparison contains an ambiguous duplicate keyless target+defClass+node identity: {keylessIdentity}");
                }
                continue;
            }
            var identity = $"{RelativeTarget(row.Target, languageRoot)}|{row.Key}";
            if (!targetKeys.Add(identity))
                throw new InvalidDataException($"Comparison contains an ambiguous duplicate target+key identity: {identity}");
            if (!uniqueKeys.Add(row.Key)) ambiguousKeys.Add(row.Key);
        }
        return ambiguousKeys;
    }

    private static DecisionLookup BuildDecisionLookup(
        IEnumerable<ReviewDecision> decisions,
        bool rejectAmbiguousIdentities,
        CancellationToken cancellationToken = default)
    {
        var result = new DecisionLookup();
        foreach (var decision in decisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(decision.Key))
            {
                if (!TryGetKeylessIdentity(
                        ReviewTargetIdentity.Canonicalize(decision.Target),
                        decision.DefClass,
                        decision.Node,
                        out var identity))
                {
                    result.UnstableKeylessDecisions++;
                    continue;
                }
                if (!result.ByKeylessIdentity.TryAdd(identity, decision))
                {
                    result.ByKeylessIdentity.Remove(identity);
                    result.AmbiguousKeylessIdentities.Add(identity);
                }
                continue;
            }
            var normalizedTarget = ReviewTargetIdentity.Canonicalize(decision.Target);
            var decisionIdentity = $"{normalizedTarget}|{decision.Key}";
            if (!result.SeenTargetKeys.Add(decisionIdentity))
                result.AmbiguousDecisionIdentities.Add(decisionIdentity);
            if (!string.IsNullOrWhiteSpace(normalizedTarget))
            {
                var targetKey = $"{normalizedTarget}|{decision.Key}";
                if (!result.AmbiguousTargetKeys.Contains(targetKey)
                    && !result.ByTargetKey.TryAdd(targetKey, decision))
                {
                    result.ByTargetKey.Remove(targetKey);
                    result.AmbiguousTargetKeys.Add(targetKey);
                }
            }
            else if (!result.AmbiguousKeys.Contains(decision.Key)
                     && !result.ByUniqueKey.TryAdd(decision.Key, decision))
            {
                result.ByUniqueKey.Remove(decision.Key);
                result.AmbiguousKeys.Add(decision.Key);
            }
        }
        if (rejectAmbiguousIdentities
            && (result.AmbiguousDecisionIdentities.Count > 0
                || result.AmbiguousKeylessIdentities.Count > 0
                || result.UnstableKeylessDecisions > 0))
        {
            throw new InvalidDataException(
                $"Review decisions contain invalid identities (target+key duplicates: {result.AmbiguousDecisionIdentities.Count}, keyless target+defClass+node duplicates: {result.AmbiguousKeylessIdentities.Count}, unstable keyless: {result.UnstableKeylessDecisions}).");
        }
        return result;
    }

    private static ReviewDecision? ResolveDecision(
        DecisionLookup lookup,
        ReviewComparisonRow row,
        string relativeTarget,
        IReadOnlySet<string> ambiguousComparisonKeys,
        bool allowUnscopedKeyFallback)
    {
        if (!string.IsNullOrWhiteSpace(row.Key))
        {
            var targetIdentity = ReviewTargetIdentity.Canonicalize(relativeTarget);
            if (lookup.ByTargetKey.TryGetValue($"{targetIdentity}|{row.Key}", out var target)) return target;
            if (!allowUnscopedKeyFallback || ambiguousComparisonKeys.Contains(row.Key)) return null;
            return lookup.ByUniqueKey.TryGetValue(row.Key, out var key) ? key : null;
        }
        return TryGetKeylessIdentity(relativeTarget, row.DefClass, row.Node, out var identity)
            && lookup.ByKeylessIdentity.TryGetValue(identity, out var keyless)
                ? keyless
                : null;
    }

    private static ReviewDecision CreateDefaultDecision(ReviewComparisonRow row, string relativeTarget)
    {
        var text = DefaultTranslation(row);
        var origin = DefaultOrigin(row);
        var changed = row.ExistingSourceChanged || origin != "ai" && row.RmkSourceChanged;
        var previousSource = !string.IsNullOrEmpty(row.ExistingPreviousSourceText)
            ? row.ExistingPreviousSourceText
            : changed ? row.RmkHistoricalSource : string.Empty;
        return new ReviewDecision
        {
            Id = row.Id,
            Key = row.Key,
            Target = relativeTarget,
            DefClass = row.DefClass,
            Node = row.Node,
            Status = string.IsNullOrWhiteSpace(text) || changed ? ReviewStatuses.Pending : ReviewStatuses.Translated,
            Text = text,
            TranslationOrigin = origin,
            TranslationUpdatedAt = row.TranslationUpdatedAt,
            SourceText = row.Source,
            SourceChanged = changed,
            PreviousSourceText = previousSource
        };
    }

    private static ReviewDecision CloneForRow(ReviewDecision source, ReviewComparisonRow row, string relativeTarget) => new()
    {
        Id = row.Id,
        Key = row.Key,
        Target = relativeTarget,
        DefClass = row.DefClass,
        Node = row.Node,
        Status = source.Status,
        Text = source.Text,
        Note = source.Note,
        TranslationOrigin = source.TranslationOrigin,
        TranslationUpdatedAt = source.TranslationUpdatedAt,
        SourceHash = source.SourceHash,
        SourceText = source.SourceText,
        SourceChanged = source.SourceChanged,
        PreviousSourceText = source.PreviousSourceText,
        UpdatedAt = source.UpdatedAt,
        ExtensionData = CloneExtensionData(source.ExtensionData)
    };

    private static ReviewDecision CloneDecision(ReviewDecision source) => new()
    {
        Id = source.Id,
        Key = source.Key,
        Target = source.Target,
        DefClass = source.DefClass,
        Node = source.Node,
        Status = source.Status,
        Text = source.Text,
        Note = source.Note,
        TranslationOrigin = source.TranslationOrigin,
        TranslationUpdatedAt = source.TranslationUpdatedAt,
        SourceHash = source.SourceHash,
        SourceText = source.SourceText,
        SourceChanged = source.SourceChanged,
        PreviousSourceText = source.PreviousSourceText,
        UpdatedAt = source.UpdatedAt,
        ExtensionData = CloneExtensionData(source.ExtensionData)
    };

    private static Dictionary<string, JsonElement>? CloneExtensionData(Dictionary<string, JsonElement>? source)
    {
        if (source is null) return null;
        var clone = new Dictionary<string, JsonElement>(source.Count, StringComparer.Ordinal);
        foreach (var property in source) clone[property.Key] = property.Value.Clone();
        return clone;
    }

    private static bool HasStableKeylessIdentity(ReviewDecision decision) =>
        TryGetKeylessIdentity(decision.Target, decision.DefClass, decision.Node, out _);

    private static bool TryGetKeylessIdentity(
        string target,
        string defClass,
        string node,
        out string identity)
    {
        identity = string.Empty;
        if (string.IsNullOrWhiteSpace(target)
            || string.IsNullOrWhiteSpace(defClass)
            || string.IsNullOrWhiteSpace(node))
        {
            return false;
        }
        var canonicalTarget = ReviewTargetIdentity.Canonicalize(target);
        identity = $"{canonicalTarget.Length}:{canonicalTarget}{defClass.Length}:{defClass}{node.Length}:{node}";
        return true;
    }

    private static void EstablishSourceSnapshot(ReviewItem item)
    {
        item.Decision.SourceHash = StableIdentity.Sha256(Normalize(item.Row.Source));
        item.Decision.SourceText = item.Row.Source;
        item.Decision.SourceChanged = false;
        if (item.Decision.ExtensionData is null) return;
        foreach (var key in item.Decision.ExtensionData.Keys
                     .Where(key => key.Equals("sourceVerification", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            item.Decision.ExtensionData.Remove(key);
        }
    }

    private string? GetEligiblePreviousReviewRoot(string root, string currentRoot)
    {
        try
        {
            var candidate = ReviewRepository.ValidateWritableReviewRoot(root, trustedReviewsRoot);
            return candidate.Equals(currentRoot, StringComparison.OrdinalIgnoreCase)
                ? null
                : candidate;
        }
        catch (Exception exception) when (exception is ArgumentException
                                               or NotSupportedException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or InvalidOperationException
                                               or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsSourceVerificationUnavailable(ReviewDecision decision)
    {
        if (decision.ExtensionData is null) return false;
        foreach (var property in decision.ExtensionData)
        {
            if (!property.Key.Equals("sourceVerification", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.String) continue;
            return property.Value.GetString()?.Equals("unavailable", StringComparison.OrdinalIgnoreCase) == true;
        }
        return false;
    }

    private static void MarkSourceVerificationUnavailable(ReviewDecision decision)
    {
        decision.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in decision.ExtensionData.Keys
                     .Where(key => key.Equals("sourceVerification", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            decision.ExtensionData.Remove(key);
        }
        decision.ExtensionData["sourceVerification"] = JsonSerializer.SerializeToElement("unavailable");
    }

    private sealed record SaveSnapshot(
        ReviewDecisionDocument Document,
        long Revision,
        ReviewComparisonEvidence ComparisonEvidence);

    private sealed record PreparedComparison(
        ReviewComparisonDocument Document,
        List<ReviewComparisonRow> Rows,
        HashSet<string> AmbiguousKeys);

    private sealed record LegacyDecisionPartition(
        List<ReviewDecision> Active,
        List<ReviewDecision> Quarantined);

    private static bool NormalizeDecision(
        ReviewComparisonRow row,
        ReviewDecision decision,
        string relativeTarget,
        bool sourceVerificationUnavailable)
    {
        var changed = false;
        var wasSourceChanged = decision.SourceChanged;
        var defaultText = DefaultTranslation(row);
        if (string.IsNullOrWhiteSpace(decision.Status)) decision.Status = string.IsNullOrWhiteSpace(defaultText) ? ReviewStatuses.Pending : ReviewStatuses.Translated;
        if (decision.Status == "reviewed") { decision.Status = ReviewStatuses.Approved; changed = true; }
        if (string.IsNullOrWhiteSpace(decision.TranslationOrigin))
        {
            decision.TranslationOrigin = InferOrigin(row, decision);
            changed = true;
        }
        var currentHash = StableIdentity.Sha256(Normalize(row.Source));
        if (string.IsNullOrEmpty(decision.PreviousSourceText)
            && !string.IsNullOrEmpty(row.ExistingPreviousSourceText))
        {
            decision.PreviousSourceText = row.ExistingPreviousSourceText;
            changed = true;
        }
        var sourceChangedNow = !string.IsNullOrWhiteSpace(decision.SourceHash)
            ? !decision.SourceHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrEmpty(decision.SourceText) && !SourceEquals(decision.SourceText, row.Source);
        var rmkChanged = decision.TranslationOrigin != "ai" && row.RmkSourceChanged;
        if (sourceChangedNow && !wasSourceChanged && !string.IsNullOrEmpty(decision.SourceText))
        {
            decision.PreviousSourceText = decision.SourceText;
            changed = true;
        }
        else if (rmkChanged
                 && !wasSourceChanged
                 && !string.IsNullOrEmpty(row.RmkHistoricalSource))
        {
            decision.PreviousSourceText = row.RmkHistoricalSource;
            changed = true;
        }
        if (sourceChangedNow
            || rmkChanged
            || decision.SourceChanged
            || sourceVerificationUnavailable)
        {
            decision.Status = ReviewStatuses.Pending;
            if (string.IsNullOrWhiteSpace(decision.Text)) decision.Text = defaultText;
            if (string.IsNullOrEmpty(decision.PreviousSourceText))
            {
                if (!string.IsNullOrEmpty(row.ExistingPreviousSourceText))
                    decision.PreviousSourceText = row.ExistingPreviousSourceText;
                else if (!row.ExistingSourceChanged)
                    decision.PreviousSourceText = rmkChanged && !string.IsNullOrEmpty(row.RmkHistoricalSource)
                        ? row.RmkHistoricalSource
                        : decision.SourceText;
            }
            decision.SourceChanged = true;
            if (sourceVerificationUnavailable) MarkSourceVerificationUnavailable(decision);
            changed = true;
        }
        else if (decision.Status == ReviewStatuses.Pending && string.IsNullOrWhiteSpace(decision.Text) && !string.IsNullOrWhiteSpace(defaultText) && string.IsNullOrWhiteSpace(decision.UpdatedAt))
        {
            decision.Status = ReviewStatuses.Translated;
            decision.Text = defaultText;
            changed = true;
        }
        if (!sourceVerificationUnavailable)
        {
            if (!decision.SourceHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase)) { decision.SourceHash = currentHash; changed = true; }
            if (!SourceEquals(decision.SourceText, row.Source)) { decision.SourceText = row.Source; changed = true; }
        }
        if (!decision.Target.Equals(relativeTarget, StringComparison.OrdinalIgnoreCase)) { decision.Target = relativeTarget; changed = true; }
        if (!decision.DefClass.Equals(row.DefClass, StringComparison.Ordinal)) { decision.DefClass = row.DefClass; changed = true; }
        if (!decision.Node.Equals(row.Node, StringComparison.Ordinal)) { decision.Node = row.Node; changed = true; }
        decision.Id = row.Id;
        decision.Key = row.Key;
        return changed;
    }

    private static bool ShouldPersist(ReviewItem item)
    {
        var defaultText = DefaultTranslation(item.Row);
        var defaultOrigin = DefaultOrigin(item.Row);
        var defaultChanged = item.Row.ExistingSourceChanged || defaultOrigin != "ai" && item.Row.RmkSourceChanged;
        var defaultStatus = string.IsNullOrWhiteSpace(defaultText) || defaultChanged ? ReviewStatuses.Pending : ReviewStatuses.Translated;
        var durableDefault = !string.IsNullOrWhiteSpace(item.Row.Candidate) || defaultOrigin is "ai" or "local";
        return durableDefault || item.Touched || item.Decision.SourceChanged
            || item.Decision.Status != defaultStatus
            || !item.Decision.Text.Equals(defaultText, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(item.Decision.Note)
            || item.Decision.TranslationOrigin != defaultOrigin
            || !string.IsNullOrWhiteSpace(item.Decision.UpdatedAt);
    }

    private static bool MatchesStatus(ReviewItem item, ReviewStatusFilter filter) => filter switch
    {
        ReviewStatusFilter.Pending => item.EffectiveStatus == ReviewStatuses.Pending,
        ReviewStatusFilter.Translated => item.EffectiveStatus == ReviewStatuses.Translated,
        ReviewStatusFilter.Approved => item.EffectiveStatus == ReviewStatuses.Approved,
        ReviewStatusFilter.Updated => item.Decision.SourceChanged,
        ReviewStatusFilter.Warning => item.IsWarning,
        ReviewStatusFilter.HasCandidate => !string.IsNullOrWhiteSpace(item.Row.Candidate),
        ReviewStatusFilter.HasExisting => !string.IsNullOrWhiteSpace(item.Row.Existing),
        ReviewStatusFilter.Rmk => item.Decision.TranslationOrigin == "rmk",
        ReviewStatusFilter.Local => item.Decision.TranslationOrigin == "local",
        _ => true
    };

    private static string SearchBlob(ReviewItem item, ReviewSearchField field) => field switch
    {
        ReviewSearchField.Key => string.Join('\n', item.Row.Id, item.Row.Key, item.RelativeTarget),
        ReviewSearchField.Text => string.Join('\n', item.Row.Source, item.Row.Existing, item.Row.Candidate, item.Decision.Text, item.Decision.Note),
        ReviewSearchField.DefClass => string.Join('\n', item.Row.DefClass, item.Row.Kind),
        ReviewSearchField.Node => string.Join('\n', item.Row.Node, item.Row.Field, item.Row.Key),
        _ => string.Join('\n', item.Row.Id, item.Row.Key, item.RelativeTarget, item.Row.Source, item.Row.Existing, item.Row.Candidate, item.Decision.Text, item.Decision.Note, item.Row.DefClass, item.Row.Node, item.Row.Field)
    };

    private static string DefaultTranslation(ReviewComparisonRow row) =>
        !string.IsNullOrWhiteSpace(row.Candidate) ? Normalize(row.Candidate) : Normalize(row.Existing);

    private static string DefaultOrigin(ReviewComparisonRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.Candidate)) return string.IsNullOrWhiteSpace(row.TranslationOrigin) ? "ai" : row.TranslationOrigin.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(row.Existing)) return string.IsNullOrWhiteSpace(row.ExistingOrigin) ? "existing" : row.ExistingOrigin.ToLowerInvariant();
        return string.Empty;
    }

    private static string InferOrigin(ReviewComparisonRow row, ReviewDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Text)) return string.Empty;
        if (decision.Text.Equals(row.Candidate, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(row.Candidate)) return "ai";
        if (decision.Text.Equals(row.Existing, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(row.Existing)) return DefaultOrigin(row);
        return !string.IsNullOrWhiteSpace(decision.UpdatedAt) ? "local" : DefaultOrigin(row);
    }

    private static string RelativeTarget(string target, string languageRoot)
    {
        if (string.IsNullOrWhiteSpace(target)) return string.Empty;
        var full = Path.GetFullPath(target);
        var relative = Path.GetRelativePath(languageRoot, full);
        if (relative.StartsWith("..", StringComparison.Ordinal)) throw new InvalidDataException($"Review target is outside the review language folder: {target}");
        return ReviewTargetIdentity.Canonicalize(relative);
    }

    private static bool SourceEquals(string left, string right) => Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);
    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private sealed class DecisionLookup
    {
        public Dictionary<string, ReviewDecision> ByTargetKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReviewDecision> ByKeylessIdentity { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReviewDecision> ByUniqueKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SeenTargetKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousDecisionIdentities { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousTargetKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousKeylessIdentities { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int UnstableKeylessDecisions { get; set; }
    }

    private sealed class SourceLookup
    {
        public Dictionary<string, string> ByTargetKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ByLegacyId { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ByUniqueKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousTargetKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousLegacyIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record PreviousReview(DecisionLookup Decisions, SourceLookup Sources, bool Unbound);
}

/// <summary>
/// Serializes review saves and does not complete successfully while a revision
/// observed during the save is still dirty.
/// </summary>
public sealed class ReviewSaveCoordinator : IDisposable
{
    private readonly ReviewWorkspaceService service;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public ReviewSaveCoordinator(ReviewWorkspaceService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        this.service = service;
    }

    public async Task SaveAsync(ReviewWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDirty(workspace)) return;
                await service.SaveAsync(workspace, cancellationToken).ConfigureAwait(false);
            }
            while (IsDirty(workspace));
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gate.Dispose();
    }

    private static bool IsDirty(ReviewWorkspace workspace)
    {
        lock (workspace.SyncRoot) return workspace.Dirty;
    }
}
