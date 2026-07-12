using System.Text.Json;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
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

    private static string NormalizeStatus(string status) => status == "reviewed" ? ReviewStatuses.Approved : status;
}

public sealed class ReviewWorkspace
{
    internal ReviewWorkspace(string reviewRoot, string comparisonFile, List<ReviewItem> items)
    {
        ReviewRoot = reviewRoot;
        ComparisonFile = comparisonFile;
        Items = items;
    }

    public string ReviewRoot { get; }
    public string ComparisonFile { get; }
    public List<ReviewItem> Items { get; }
    public bool Dirty { get; internal set; }
    public int ImportedDecisions { get; internal set; }
    public int ChangedSources { get; internal set; }
}

public sealed class ReviewWorkspaceService
{
    private readonly AtomicJsonStore store;
    private readonly SourceExtractor extractor;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ReviewWorkspaceService(AtomicJsonStore store, SourceExtractor extractor)
    {
        this.store = store;
        this.extractor = extractor;
    }

    public Task<ReviewWorkspace> LoadAsync(
        string reviewRoot,
        TranslationProject? project = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(reviewRoot, project, cancellationToken), cancellationToken);

    public ReviewWorkspace Load(string reviewRoot, TranslationProject? project = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(reviewRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Review folder was not found: {root}");
        var comparison = FindComparisonFile(root);
        var rows = JsonSerializer.Deserialize<List<ReviewComparisonRow>>(File.ReadAllText(comparison), JsonOptions)
            ?? throw new InvalidDataException("Comparison file is empty.");
        rows = rows.Where(row => extractor.GetInternalIdentifierReason(row.Key, row.Kind, row.DefClass, row.Field) is null).ToList();

        var decisionPath = ReviewRepository.GetDecisionPath(root);
        var hasCurrentDecisions = store.Exists(decisionPath);
        var current = hasCurrentDecisions
            ? store.Read<ReviewDecisionDocument>(decisionPath) ?? throw new InvalidDataException("Review decision store is empty.")
            : new ReviewDecisionDocument { ReviewRoot = root, Comparison = comparison };
        var lookup = BuildDecisionLookup(current.Items);
        PreviousReview? previous = null;
        if (!hasCurrentDecisions && project is not null) previous = LoadPrevious(project, root);

        var languageRoot = Path.Combine(root, "Languages", "Korean");
        var items = new List<ReviewItem>(rows.Count);
        var workspace = new ReviewWorkspace(root, comparison, items);
        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            var relative = RelativeTarget(row.Target, languageRoot);
            var decision = ResolveDecision(lookup, row, relative);
            var imported = false;
            if (decision is null && previous is not null)
            {
                decision = ResolveDecision(previous.Decisions, row, relative);
                if (decision is not null)
                {
                    decision = CloneForRow(decision, row, relative);
                    imported = true;
                    workspace.ImportedDecisions++;
                }
            }
            decision ??= CreateDefaultDecision(row, relative);
            var changed = NormalizeDecision(row, decision, relative);

            if (previous is not null && previous.Sources.TryGetValue(SourceIdentity(row), out var oldSource)
                && !string.IsNullOrWhiteSpace(oldSource) && !SourceEquals(oldSource, row.Source))
            {
                decision.Status = ReviewStatuses.Pending;
                if (!decision.SourceChanged || string.IsNullOrWhiteSpace(decision.PreviousSourceText)) decision.PreviousSourceText = oldSource;
                decision.SourceChanged = true;
                decision.SourceHash = StableIdentity.Sha256(row.Source);
                decision.SourceText = row.Source;
                if (string.IsNullOrWhiteSpace(decision.UpdatedAt)) decision.UpdatedAt = DateTimeOffset.Now.ToString("O");
                changed = true;
            }

            var item = new ReviewItem(row, decision, relative, index) { Touched = hasCurrentDecisions || imported };
            items.Add(item);
            if (decision.SourceChanged) workspace.ChangedSources++;
            if (changed || imported) workspace.Dirty = true;
        }
        return workspace;
    }

    public Task SaveAsync(ReviewWorkspace workspace, CancellationToken cancellationToken = default) =>
        Task.Run(() => Save(workspace, cancellationToken), cancellationToken);

    public void Save(ReviewWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var document = new ReviewDecisionDocument
        {
            ReviewRoot = workspace.ReviewRoot,
            Comparison = workspace.ComparisonFile
        };
        foreach (var item in workspace.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldPersist(item)) document.Items.Add(item.Decision);
        }
        new ReviewRepository(store).Save(workspace.ReviewRoot, document);
        foreach (var item in workspace.Items) item.Touched = false;
        workspace.Dirty = false;
    }

    public IReadOnlyList<ReviewItem> Query(ReviewWorkspace workspace, ReviewQuery query)
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

    public ReviewWorkspaceStats GetStats(ReviewWorkspace workspace)
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

    public void UpdateTranslation(ReviewWorkspace workspace, ReviewItem item, string translation, string note, bool editedByUser)
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
            item.Decision.TranslationOrigin = "local";
            item.Decision.TranslationUpdatedAt = now;
            if (string.IsNullOrWhiteSpace(text)) item.Decision.Status = ReviewStatuses.Pending;
            else if (item.Decision.Status is ReviewStatuses.Pending or ReviewStatuses.Approved)
            {
                item.Decision.Status = ReviewStatuses.Translated;
                item.Decision.SourceChanged = false;
                item.Decision.PreviousSourceText = string.Empty;
            }
        }
        item.Decision.UpdatedAt = now;
        item.Touched = true;
        workspace.Dirty = true;
    }

    public void SetStatus(ReviewWorkspace workspace, ReviewItem item, string status)
    {
        if (status == "reviewed") status = ReviewStatuses.Approved;
        item.Decision.Status = status;
        if (status != ReviewStatuses.Pending)
        {
            item.Decision.SourceChanged = false;
            item.Decision.PreviousSourceText = string.Empty;
        }
        item.Decision.UpdatedAt = DateTimeOffset.Now.ToString("O");
        item.Touched = true;
        workspace.Dirty = true;
    }

    public int ApplyToDuplicateSources(ReviewWorkspace workspace, ReviewItem sourceItem)
    {
        if (string.IsNullOrWhiteSpace(sourceItem.Row.Source) || string.IsNullOrWhiteSpace(sourceItem.Decision.Text)) return 0;
        var now = DateTimeOffset.Now.ToString("O");
        var changed = 0;
        foreach (var item in workspace.Items)
        {
            if (ReferenceEquals(item, sourceItem) || !item.Row.Source.Equals(sourceItem.Row.Source, StringComparison.Ordinal)) continue;
            if (item.Decision.Text.Equals(sourceItem.Decision.Text, StringComparison.Ordinal)
                && item.EffectiveStatus is ReviewStatuses.Translated or ReviewStatuses.Approved
                && !item.Decision.SourceChanged) continue;
            item.Decision.Text = sourceItem.Decision.Text;
            item.Decision.Status = ReviewStatuses.Translated;
            item.Decision.TranslationOrigin = "local";
            item.Decision.TranslationUpdatedAt = now;
            item.Decision.SourceChanged = false;
            item.Decision.PreviousSourceText = string.Empty;
            item.Decision.UpdatedAt = now;
            item.Touched = true;
            changed++;
        }
        if (changed > 0) workspace.Dirty = true;
        return changed;
    }

    public int ApproveAllEligible(ReviewWorkspace workspace)
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
        if (count > 0) workspace.Dirty = true;
        return count;
    }

    public static string FindComparisonFile(string reviewRoot)
    {
        var audit = Path.Combine(Path.GetFullPath(reviewRoot), "_TranslationAudit");
        if (!Directory.Exists(audit)) throw new DirectoryNotFoundException($"Translation audit folder was not found: {audit}");
        return Directory.EnumerateFiles(audit, "*-comparison.json", SearchOption.TopDirectoryOnly)
                   .OrderByDescending(File.GetLastWriteTimeUtc)
                   .FirstOrDefault()
               ?? throw new FileNotFoundException($"Comparison JSON was not found in: {audit}");
    }

    private PreviousReview? LoadPrevious(TranslationProject project, string currentRoot)
    {
        var roots = project.Runs.OrderByDescending(run => run.CreatedAt).Select(run => run.ReviewRoot)
            .Prepend(project.LatestReviewRoot)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => !Path.GetFullPath(root).Equals(currentRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
            .ToArray();
        var decisionFile = roots.Select(ReviewRepository.GetDecisionPath).FirstOrDefault(store.Exists);
        var comparisonFile = roots.Select(root =>
        {
            try { return FindComparisonFile(root); } catch { return string.Empty; }
        }).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (decisionFile is null && comparisonFile is null) return null;
        var decisions = decisionFile is null
            ? new DecisionLookup()
            : BuildDecisionLookup(store.Read<ReviewDecisionDocument>(decisionFile)?.Items ?? []);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (comparisonFile is not null)
        {
            var rows = JsonSerializer.Deserialize<List<ReviewComparisonRow>>(File.ReadAllText(comparisonFile), JsonOptions) ?? [];
            foreach (var row in rows)
            {
                var identity = SourceIdentity(row);
                if (string.IsNullOrWhiteSpace(identity) || ambiguous.Contains(identity)) continue;
                if (sources.TryGetValue(identity, out var existing) && !SourceEquals(existing, row.Source))
                {
                    sources.Remove(identity);
                    ambiguous.Add(identity);
                }
                else sources[identity] = row.Source;
            }
        }
        return new PreviousReview(decisions, sources);
    }

    private static DecisionLookup BuildDecisionLookup(IEnumerable<ReviewDecision> decisions)
    {
        var result = new DecisionLookup();
        foreach (var decision in decisions)
        {
            if (!string.IsNullOrWhiteSpace(decision.Id)) result.ById[decision.Id] = decision;
            if (string.IsNullOrWhiteSpace(decision.Key)) continue;
            if (!string.IsNullOrWhiteSpace(decision.Target)) result.ByTargetKey[$"{decision.Target}|{decision.Key}"] = decision;
            if (result.AmbiguousKeys.Contains(decision.Key)) continue;
            if (!result.ByUniqueKey.TryAdd(decision.Key, decision))
            {
                result.ByUniqueKey.Remove(decision.Key);
                result.AmbiguousKeys.Add(decision.Key);
            }
        }
        return result;
    }

    private static ReviewDecision? ResolveDecision(DecisionLookup lookup, ReviewComparisonRow row, string relativeTarget)
    {
        if (lookup.ByTargetKey.TryGetValue($"{relativeTarget}|{row.Key}", out var target)) return target;
        if (!string.IsNullOrWhiteSpace(row.Id) && lookup.ById.TryGetValue(row.Id, out var id)) return id;
        return lookup.ByUniqueKey.TryGetValue(row.Key, out var key) ? key : null;
    }

    private static ReviewDecision CreateDefaultDecision(ReviewComparisonRow row, string relativeTarget)
    {
        var text = DefaultTranslation(row);
        var origin = DefaultOrigin(row);
        var changed = origin != "ai" && row.RmkSourceChanged;
        return new ReviewDecision
        {
            Id = row.Id,
            Key = row.Key,
            Target = relativeTarget,
            Status = string.IsNullOrWhiteSpace(text) || changed ? ReviewStatuses.Pending : ReviewStatuses.Translated,
            Text = text,
            TranslationOrigin = origin,
            TranslationUpdatedAt = row.TranslationUpdatedAt,
            SourceText = row.Source,
            SourceChanged = changed,
            PreviousSourceText = changed ? row.RmkHistoricalSource : string.Empty
        };
    }

    private static ReviewDecision CloneForRow(ReviewDecision source, ReviewComparisonRow row, string relativeTarget) => new()
    {
        Id = row.Id,
        Key = row.Key,
        Target = relativeTarget,
        Status = source.Status,
        Text = source.Text,
        Note = source.Note,
        TranslationOrigin = source.TranslationOrigin,
        TranslationUpdatedAt = source.TranslationUpdatedAt,
        SourceHash = source.SourceHash,
        SourceText = source.SourceText,
        SourceChanged = source.SourceChanged,
        PreviousSourceText = source.PreviousSourceText,
        UpdatedAt = source.UpdatedAt
    };

    private static bool NormalizeDecision(ReviewComparisonRow row, ReviewDecision decision, string relativeTarget)
    {
        var changed = false;
        var defaultText = DefaultTranslation(row);
        if (string.IsNullOrWhiteSpace(decision.Status)) decision.Status = string.IsNullOrWhiteSpace(defaultText) ? ReviewStatuses.Pending : ReviewStatuses.Translated;
        if (decision.Status == "reviewed") { decision.Status = ReviewStatuses.Approved; changed = true; }
        if (string.IsNullOrWhiteSpace(decision.TranslationOrigin))
        {
            decision.TranslationOrigin = InferOrigin(row, decision);
            changed = true;
        }
        var currentHash = StableIdentity.Sha256(row.Source);
        var sourceChangedNow = !string.IsNullOrWhiteSpace(decision.SourceHash)
            ? !decision.SourceHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(decision.SourceText) && !SourceEquals(decision.SourceText, row.Source);
        var rmkChanged = decision.TranslationOrigin != "ai" && row.RmkSourceChanged;
        if (sourceChangedNow || rmkChanged || decision.SourceChanged)
        {
            decision.Status = ReviewStatuses.Pending;
            if (string.IsNullOrWhiteSpace(decision.Text)) decision.Text = defaultText;
            if (string.IsNullOrWhiteSpace(decision.PreviousSourceText))
                decision.PreviousSourceText = rmkChanged && !string.IsNullOrWhiteSpace(row.RmkHistoricalSource) ? row.RmkHistoricalSource : decision.SourceText;
            decision.SourceChanged = true;
            changed = true;
        }
        else if (decision.Status == ReviewStatuses.Pending && string.IsNullOrWhiteSpace(decision.Text) && !string.IsNullOrWhiteSpace(defaultText) && string.IsNullOrWhiteSpace(decision.UpdatedAt))
        {
            decision.Status = ReviewStatuses.Translated;
            decision.Text = defaultText;
            changed = true;
        }
        if (!decision.SourceHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase)) { decision.SourceHash = currentHash; changed = true; }
        if (!SourceEquals(decision.SourceText, row.Source)) { decision.SourceText = row.Source; changed = true; }
        if (!decision.Target.Equals(relativeTarget, StringComparison.OrdinalIgnoreCase)) { decision.Target = relativeTarget; changed = true; }
        decision.Id = row.Id;
        decision.Key = row.Key;
        return changed;
    }

    private static bool ShouldPersist(ReviewItem item)
    {
        var defaultText = DefaultTranslation(item.Row);
        var defaultOrigin = DefaultOrigin(item.Row);
        var defaultChanged = defaultOrigin != "ai" && item.Row.RmkSourceChanged;
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
        return relative;
    }

    private static string SourceIdentity(ReviewComparisonRow row)
    {
        var className = row.Kind == "Keyed" ? "Keyed" : !string.IsNullOrWhiteSpace(row.DefClass) ? row.DefClass : row.Kind;
        var node = !string.IsNullOrWhiteSpace(row.Node) ? row.Node : row.Key;
        return string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(node) ? string.Empty : $"{className}+{node}";
    }

    private static bool SourceEquals(string left, string right) => Normalize(left).Trim().Equals(Normalize(right).Trim(), StringComparison.Ordinal);
    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private sealed class DecisionLookup
    {
        public Dictionary<string, ReviewDecision> ByTargetKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReviewDecision> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReviewDecision> ByUniqueKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record PreviousReview(DecisionLookup Decisions, Dictionary<string, string> Sources);
}
