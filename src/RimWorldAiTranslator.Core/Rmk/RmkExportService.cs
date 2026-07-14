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
    public required string RmkWorkspaceRoot { get; init; }
    public required string RmkEntryRoot { get; init; }
    public required string ReviewRoot { get; init; }
    public required string ModRoot { get; init; }
    public string ReviewLanguageFolderName { get; init; } = "Korean";
    public string RmkLanguageFolderName { get; init; } = "Korean (한국어)";
    public string SourceLanguage { get; init; } = "English";
    public string WorkbookPath { get; init; } = string.Empty;
    public ReviewApplyStatus ApplyStatus { get; init; } = ReviewApplyStatus.ApprovedOnly;
    public bool Overwrite { get; init; }
    public bool DryRun { get; init; }
    public string? ExpectedPlanFingerprint { get; init; }
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
    int SkippedAmbiguous,
    string PlanFingerprint);

internal readonly record struct RmkExportResourceLimits(
    int MaximumWorkbookDiscoveryEntries,
    int MaximumLanguageFiles,
    long MaximumLanguageXmlBytes,
    int MaximumLanguageRecords,
    long MaximumLanguageCharacters)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumWorkbookDiscoveryEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLanguageFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLanguageXmlBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLanguageRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLanguageCharacters);
    }
}

public sealed class RmkExportService
{
    private const int MaximumRmkLanguageDirectories = 10_000;
    private const int MaximumRmkLanguageEntries = 100_000;
    private const int MaximumRmkLanguageDepth = 32;
    private static readonly RmkExportResourceLimits DefaultResourceLimits = new(
        MaximumWorkbookDiscoveryEntries: 100_000,
        MaximumLanguageFiles: 10_000,
        MaximumLanguageXmlBytes: 512L * 1024 * 1024,
        MaximumLanguageRecords: 250_000,
        MaximumLanguageCharacters: 64L * 1024 * 1024);
    private readonly AtomicJsonStore jsonStore;
    private readonly LanguageFileService languageFiles;
    private readonly SourceExtractor extractor;
    private readonly RmkWorkspaceService workspaceService;
    private readonly string? trustedReviewsRoot;
    private readonly FileTransactionRecoveryAuthority? recoveryAuthority;
    internal Action? BeforeWriteBoundaryLockedTestHook { get; set; }
    internal Action? AfterWriteBoundaryLockedTestHook { get; set; }
    internal Action<string>? AfterWorkbookPreparedValidationTestHook { get; set; }
    internal RmkExportResourceLimits? ResourceLimitsTestOverride { get; set; }

    public RmkExportService(
        AtomicJsonStore jsonStore,
        LanguageFileService languageFiles,
        SourceExtractor extractor,
        RmkWorkspaceService? workspaceService = null,
        string? trustedReviewsRoot = null,
        FileTransactionRecoveryAuthority? recoveryAuthority = null)
    {
        this.jsonStore = jsonStore;
        this.languageFiles = languageFiles;
        this.extractor = extractor;
        this.workspaceService = workspaceService ?? new RmkWorkspaceService();
        this.trustedReviewsRoot = trustedReviewsRoot;
        this.recoveryAuthority = recoveryAuthority;
    }

    public RmkExportResult Export(RmkExportOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resourceLimits = ResourceLimitsTestOverride ?? DefaultResourceLimits;
        resourceLimits.Validate();
        ValidateSegment(options.ReviewLanguageFolderName, nameof(options.ReviewLanguageFolderName));
        ValidateSegment(options.RmkLanguageFolderName, nameof(options.RmkLanguageFolderName));
        if (PathSafety.IsNetworkPath(options.ModRoot)
            || PathSafety.IsNetworkPath(options.ReviewRoot)
            || PathSafety.IsNetworkPath(options.RmkWorkspaceRoot)
            || PathSafety.IsNetworkPath(options.RmkEntryRoot)
            || PathSafety.IsNetworkPath(options.WorkbookPath))
        {
            throw new InvalidDataException("RMK export inputs and outputs must use local paths.");
        }
        var workspaceRoot = workspaceService.RequireWritableWorkspace(options.RmkWorkspaceRoot);
        var entryRoot = PathSafety.Normalize(options.RmkEntryRoot);
        var dataRoot = Path.Combine(workspaceRoot, "Data");
        if (!PathSafety.IsStrictlyInside(entryRoot, dataRoot))
            throw new InvalidDataException("RMK entry root must be inside the work clone Data folder.");
        PathSafety.EnsureNoReparsePoints(entryRoot, workspaceRoot);
        var modRoot = PathSafety.Normalize(options.ModRoot);
        if (!Directory.Exists(modRoot)) throw new DirectoryNotFoundException($"Mod root not found: {modRoot}");
        var reviewRoot = ReviewRepository.ValidateWritableReviewRoot(
            options.ReviewRoot,
            trustedReviewsRoot);
        var reviewLanguageRoot = Path.Combine(reviewRoot, "Languages", options.ReviewLanguageFolderName);
        var rmkLanguageRoot = Path.Combine(entryRoot, "Languages", options.RmkLanguageFolderName);
        var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
        if (!Directory.Exists(auditRoot)) throw new DirectoryNotFoundException($"Review audit folder not found: {auditRoot}");
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
        var rows = comparisonDocument.Rows
            .Where(row => extractor.GetInternalIdentifierReason(row.Key, row.Kind, row.DefClass, row.Field) is null)
            .ToArray();
        var duplicateHistoryIdentifier = rows
            .Select(HistoryIdentifier)
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .GroupBy(identifier => identifier, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateHistoryIdentifier is not null)
            throw new InvalidDataException(
                $"Comparison contains an ambiguous duplicate RMK history identity: {duplicateHistoryIdentifier.Key}");
        var currentSources = ReviewSourceSnapshot.Capture(
            extractor,
            modRoot,
            string.IsNullOrWhiteSpace(options.SourceLanguage) ? "Auto" : options.SourceLanguage,
            cancellationToken);

        var rowByTargetKey = new Dictionary<string, ReviewComparisonRow>(StringComparer.OrdinalIgnoreCase);
        var ambiguousTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keylessIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousKeylessIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var relative = RelativeTarget(row.Target, reviewLanguageRoot);
            if (relative is not null && !string.IsNullOrWhiteSpace(row.Key))
            {
                var targetKey = $"target:{relative}|key:{row.Key}";
                if (!ambiguousTargetKeys.Contains(targetKey) && !rowByTargetKey.TryAdd(targetKey, row))
                {
                    rowByTargetKey.Remove(targetKey);
                    ambiguousTargetKeys.Add(targetKey);
                }
            }
            if (string.IsNullOrWhiteSpace(row.Key)
                && relative is not null
                && !string.IsNullOrWhiteSpace(row.DefClass)
                && !string.IsNullOrWhiteSpace(row.Node))
            {
                var identity = KeylessIdentity(relative, row.DefClass, row.Node);
                if (!keylessIdentities.Add(identity)) ambiguousKeylessIdentities.Add(identity);
            }
            if (string.IsNullOrWhiteSpace(row.Key)) continue;
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

        ValidateDecisionIdentities(decisions.Items);
        var resolvedDecisions = decisions.Items
            .Select(decision => (Decision: decision, Row: Resolve(decision)))
            .ToArray();
        var resolvedRows = new HashSet<ReviewComparisonRow>(ReferenceEqualityComparer.Instance);
        if (resolvedDecisions.Any(item => item.Row is not null && !resolvedRows.Add(item.Row)))
            throw new InvalidDataException("Review decisions ambiguously resolve more than once to the same comparison row.");
        var decisionByHistory = new Dictionary<string, ReviewDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolved in resolvedDecisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = resolved.Decision;
            var row = resolved.Row;
            var identifier = row is null ? string.Empty : HistoryIdentifier(row);
            if (!string.IsNullOrWhiteSpace(identifier) && !decisionByHistory.TryAdd(identifier, decision))
                throw new InvalidDataException($"Review decisions contain an ambiguous duplicate RMK history identity: {identifier}");
        }

        var fileStates = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var keyFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var languageFileCount = 0;
        var languageXmlBytes = 0L;
        var languageRecordCount = 0;
        var languageCharacters = 0L;
        if (Directory.Exists(rmkLanguageRoot))
        {
            foreach (var file in EnumerateRmkLanguageXmlFiles(
                         rmkLanguageRoot,
                         workspaceRoot,
                         resourceLimits.MaximumLanguageFiles,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                languageFileCount++;
                if (languageFileCount > resourceLimits.MaximumLanguageFiles)
                    throw new InvalidDataException(
                        $"RMK language input contains more than {resourceLimits.MaximumLanguageFiles:N0} XML files.");
                var fileBytesBeforeRead = new FileInfo(file).Length;
                languageXmlBytes = AddWithinLimit(
                    languageXmlBytes,
                    fileBytesBeforeRead,
                    resourceLimits.MaximumLanguageXmlBytes,
                    "RMK language XML input bytes");
                var entries = languageFiles.Read(file);
                var fileBytesAfterRead = new FileInfo(file).Length;
                if (fileBytesAfterRead > fileBytesBeforeRead)
                {
                    languageXmlBytes = AddWithinLimit(
                        languageXmlBytes,
                        fileBytesAfterRead - fileBytesBeforeRead,
                        resourceLimits.MaximumLanguageXmlBytes,
                        "RMK language XML input bytes");
                }
                foreach (var pair in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    languageRecordCount++;
                    if (languageRecordCount > resourceLimits.MaximumLanguageRecords)
                        throw new InvalidDataException(
                            $"RMK language input contains more than {resourceLimits.MaximumLanguageRecords:N0} localization records.");
                    languageCharacters = AddWithinLimit(
                        languageCharacters,
                        (long)pair.Key.Length + pair.Value.Length,
                        resourceLimits.MaximumLanguageCharacters,
                        "RMK language key/value characters");
                }
                var state = new FileState(Path.GetFullPath(file), entries);
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

        foreach (var resolved in resolvedDecisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = resolved.Decision;
            var status = decision.Status == "reviewed" ? ReviewStatuses.Approved : decision.Status;
            if (!StatusIncluded(status, options.ApplyStatus))
            {
                skippedStatus++;
                continue;
            }
            var row = resolved.Row;
            if (row is null || !LanguageFileService.IsValidLocalizationKey(row.Key))
            {
                skippedUnmapped++;
                continue;
            }
            if (!ReviewSafety.HasDecisionSourceEvidence(decision)
                || decision.SourceChanged
                || ReviewSafety.DecisionSourceChanged(decision, row))
            {
                skippedChanged++;
                continue;
            }
            if (!currentSources.Matches(row))
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
            string target;
            var updatesExisting = false;
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
                updatesExisting = true;
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
            }
            PathSafety.EnsureNoReparsePoints(target, workspaceRoot);
            if (!fileStates.TryGetValue(target, out var state))
            {
                state = new FileState(target, languageFiles.Read(target));
                fileStates[target] = state;
            }
            state.Entries[row.Key] = translation;
            state.Dirty = true;
            eligible++;
            if (updatesExisting) updatedExisting++;
            else addedNew++;
            var identifier = HistoryIdentifier(row);
            if (!string.IsNullOrWhiteSpace(identifier)) exportedTranslations[identifier] = translation;
        }

        var workbookPath = ResolveWorkbookPath(
            options.WorkbookPath,
            entryRoot,
            resourceLimits.MaximumWorkbookDiscoveryEntries,
            cancellationToken);
        PathSafety.EnsureNoReparsePoints(workbookPath, workspaceRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var workbookExisted = File.Exists(workbookPath);
        var workbookData = workbookExisted ? RimWorldTranslatorRmkXlsxReader.Read(workbookPath) : null;
        var duplicateWorkbookIdentifier = workbookData?.Rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Identifier))
            .GroupBy(row => row.Identifier, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateWorkbookIdentifier is not null)
            throw new InvalidDataException(
                $"RMK workbook contains an ambiguous duplicate history identifier: {duplicateWorkbookIdentifier.Key}");
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
                if (string.IsNullOrWhiteSpace(row.Identifier)) continue;
                historyById[row.Identifier] = Copy(row);
                historyOrder.Add(row.Identifier);
            }
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identifier = HistoryIdentifier(row);
            if (string.IsNullOrWhiteSpace(identifier)) continue;
            var className = row.Kind == "Keyed" ? "Keyed" : !string.IsNullOrWhiteSpace(row.DefClass) ? row.DefClass : row.Kind;
            var node = !string.IsNullOrWhiteSpace(row.Node) ? row.Node : row.Key;
            var currentSource = ReviewSafety.Normalize(row.Source);
            var workbookCurrentSource = !string.IsNullOrWhiteSpace(row.RmkCurrentSource)
                ? ReviewSafety.Normalize(row.RmkCurrentSource)
                : !workbookUsesDifferentLanguage ? currentSource : string.Empty;
            decisionByHistory.TryGetValue(identifier, out var decision);
            var sourceChanged = row.RmkSourceChanged
                || decision is not null && !ReviewSafety.HasDecisionSourceEvidence(decision)
                || decision?.SourceChanged == true
                || decision is not null && ReviewSafety.DecisionSourceChanged(decision, row)
                || !currentSources.Matches(row);

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
        ReviewComparisonDocument.VerifyUnchanged(reviewRoot, comparisonDocument.Evidence);
        cancellationToken.ThrowIfCancellationRequested();
        var planFingerprint = CreatePlanFingerprint(
            options,
            workspaceRoot,
            entryRoot,
            reviewRoot,
            modRoot,
            workbookPath,
            workbookExisted,
            workbookData,
            effectiveSourceLanguage,
            historyRows,
            dirtyStates,
            eligible,
            updatedExisting,
            addedNew,
            skippedStatus,
            skippedChanged,
            skippedUnsafe,
            skippedUnmapped,
            skippedAmbiguous);
        OperationPlanFingerprint.RequireExpected(options.ExpectedPlanFingerprint, planFingerprint);
        if (!options.DryRun && eligible > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = dirtyStates.Select(state => state.Path).Prepend(workbookPath).ToArray();
            _ = workspaceService.RequireWritableWorkspace(workspaceRoot);
            if (currentSources.SourceFiles.Any(source => paths.Contains(source, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("A source evidence file cannot also be an RMK output target.");
            using var recoverySession = FileTransaction.AcquireRecoveryLease(
                recoveryAuthority,
                workspaceRoot,
                cancellationToken);
            using var sourceBoundary = PathSafety.AcquireTrustedReadBoundary(
                modRoot,
                currentSources.SourceFiles,
                cancellationToken);
            var lockedSources = ReviewSourceSnapshot.Capture(
                extractor,
                modRoot,
                string.IsNullOrWhiteSpace(options.SourceLanguage) ? "Auto" : options.SourceLanguage,
                cancellationToken);
            if (!currentSources.HasSameEvidence(lockedSources))
                throw new InvalidDataException("Source evidence changed before the RMK write boundary could be locked.");
            BeforeWriteBoundaryLockedTestHook?.Invoke();
            using var writeBoundary = PathSafety.AcquireTrustedWriteBoundary(
                workspaceRoot,
                paths,
                cancellationToken);
            AfterWriteBoundaryLockedTestHook?.Invoke();
            writeBoundary.VerifyUnchanged();
            _ = workspaceService.RequireWritableWorkspace(workspaceRoot);
            var finalSources = ReviewSourceSnapshot.Capture(
                extractor,
                modRoot,
                string.IsNullOrWhiteSpace(options.SourceLanguage) ? "Auto" : options.SourceLanguage,
                cancellationToken);
            if (!lockedSources.HasSameEvidence(finalSources))
                throw new InvalidDataException("Source evidence changed after the RMK write boundary was locked.");
            VerifyOutputEvidenceUnchanged(
                dirtyStates,
                workbookPath,
                workbookExisted,
                workbookData,
                languageFiles,
                writeBoundary,
                cancellationToken);
            FileTransaction.Execute(paths, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteWorkbookThroughBoundary(
                    workbookPath,
                    historyRows,
                    effectiveSourceLanguage,
                    writeBoundary,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var state in dirtyStates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    languageFiles.Write(state.Path, state.Entries, true, writeBoundary, cancellationToken);
                }
            },
                "RMK export",
                writeBoundary.ReleaseWriteLeafLocksForRollback,
                writeBoundary,
                recoverySession,
                cancellationToken);
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
            skippedAmbiguous,
            planFingerprint);
    }

    private static string CreatePlanFingerprint(
        RmkExportOptions options,
        string workspaceRoot,
        string entryRoot,
        string reviewRoot,
        string modRoot,
        string workbookPath,
        bool workbookExisted,
        RimWorldTranslatorRmkHistoryData? workbookData,
        string effectiveSourceLanguage,
        IReadOnlyList<RimWorldTranslatorRmkHistoryRow> historyRows,
        IReadOnlyList<FileState> dirtyStates,
        int eligible,
        int updatedExisting,
        int addedNew,
        int skippedStatus,
        int skippedChanged,
        int skippedUnsafe,
        int skippedUnmapped,
        int skippedAmbiguous)
    {
        using var fingerprint = new OperationPlanFingerprint();
        fingerprint.Add("rmk-export-plan-v1");
        fingerprint.AddPath(workspaceRoot);
        fingerprint.AddPath(entryRoot);
        fingerprint.AddPath(reviewRoot);
        fingerprint.AddPath(modRoot);
        fingerprint.Add(options.ReviewLanguageFolderName);
        fingerprint.Add(options.RmkLanguageFolderName);
        fingerprint.Add(options.SourceLanguage);
        fingerprint.Add(options.ApplyStatus.ToString());
        fingerprint.Add(options.Overwrite);
        fingerprint.AddPath(workbookPath);
        fingerprint.Add(workbookExisted);
        AddWorkbook(fingerprint, workbookData);
        fingerprint.Add(effectiveSourceLanguage);
        fingerprint.Add(historyRows.Count);
        foreach (var row in historyRows)
            AddWorkbookRow(fingerprint, row);

        fingerprint.Add(eligible);
        fingerprint.Add(updatedExisting);
        fingerprint.Add(addedNew);
        fingerprint.Add(dirtyStates.Count);
        fingerprint.Add(skippedStatus);
        fingerprint.Add(skippedChanged);
        fingerprint.Add(skippedUnsafe);
        fingerprint.Add(skippedUnmapped);
        fingerprint.Add(skippedAmbiguous);
        foreach (var state in dirtyStates)
        {
            fingerprint.AddPath(state.Path);
            fingerprint.Add(state.OriginalFileExisted);
            fingerprint.Add(state.OriginalEntries.Count);
            foreach (var pair in state.OriginalEntries)
            {
                fingerprint.Add(pair.Key);
                fingerprint.Add(pair.Value);
            }
            fingerprint.Add(state.Entries.Count);
            foreach (var pair in state.Entries)
            {
                fingerprint.Add(pair.Key);
                fingerprint.Add(pair.Value);
            }
        }

        return fingerprint.Complete();
    }

    private static void AddWorkbook(
        OperationPlanFingerprint fingerprint,
        RimWorldTranslatorRmkHistoryData? workbook)
    {
        fingerprint.Add(workbook is not null);
        if (workbook is null) return;
        fingerprint.Add(workbook.SourceLanguage);
        fingerprint.Add(workbook.Rows.Count);
        foreach (var row in workbook.Rows)
            AddWorkbookRow(fingerprint, row);
    }

    private static void AddWorkbookRow(
        OperationPlanFingerprint fingerprint,
        RimWorldTranslatorRmkHistoryRow row)
    {
        fingerprint.Add(row.Identifier);
        fingerprint.Add(row.ClassName);
        fingerprint.Add(row.Key);
        fingerprint.Add(row.RequiredMods);
        fingerprint.Add(row.Source);
        fingerprint.Add(row.Translation);
    }

    private static string ResolveWorkbookPath(
        string requestedPath,
        string entryRoot,
        int maximumDiscoveryEntries,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDiscoveryEntries);
        string candidate;
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            candidate = Path.IsPathRooted(requestedPath) ? requestedPath : Path.Combine(entryRoot, requestedPath);
        }
        else
        {
            candidate = FindDefaultWorkbook(
                entryRoot,
                maximumDiscoveryEntries,
                cancellationToken) ?? string.Empty;
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

    private static string? FindDefaultWorkbook(
        string entryRoot,
        int maximumDiscoveryEntries,
        CancellationToken cancellationToken)
    {
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(entryRoot);
        string? best = null;
        var entryCount = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     canonicalRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > maximumDiscoveryEntries)
                throw new InvalidDataException(
                    $"The RMK entry root contains more than {maximumDiscoveryEntries:N0} top-level filesystem entries while discovering its workbook.");
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                continue;
            if (!Path.GetExtension(entry).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) continue;
            var canonicalFile = PathSafety.GetCanonicalExistingFile(entry);
            if (!PathSafety.IsStrictlyInside(canonicalFile, canonicalRoot))
                throw new InvalidDataException("An RMK workbook candidate resolves outside its selected entry root.");
            if (best is null || CompareWorkbookCandidates(canonicalFile, best) < 0) best = canonicalFile;
        }
        return best;
    }

    private static int CompareWorkbookCandidates(string left, string right)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(
            Path.GetFileName(left),
            Path.GetFileName(right));
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
    }

    private static long AddWithinLimit(long current, long addition, long maximum, string description)
    {
        if (addition < 0 || current > maximum - addition)
            throw new InvalidDataException($"{description} exceed the aggregate {maximum:N0} limit.");
        return current + addition;
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
        try { full = Path.GetFullPath(target); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Invalid RMK review target skipped ({exception.GetType().Name}).");
            return null;
        }
        if (!PathSafety.IsStrictlyInside(full, reviewLanguageRoot)) return null;
        var relative = Path.GetRelativePath(reviewLanguageRoot, full);
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? null
            : ReviewTargetIdentity.Canonicalize(relative);
    }

    private static bool StatusIncluded(string status, ReviewApplyStatus mode) =>
        status == ReviewStatuses.Approved || mode == ReviewApplyStatus.TranslatedAndApproved && status == ReviewStatuses.Translated;

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

    private static IReadOnlyList<string> EnumerateRmkLanguageXmlFiles(
        string languageRoot,
        string workspaceRoot,
        int maximumXmlFiles,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumXmlFiles);
        var canonicalWorkspace = PathSafety.GetCanonicalExistingDirectory(workspaceRoot);
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(languageRoot);
        if (!PathSafety.IsStrictlyInside(canonicalRoot, canonicalWorkspace))
            throw new InvalidDataException("The RMK language root resolves outside its writable workspace.");

        var pending = new Stack<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        var entryCount = 0;
        pending.Push((canonicalRoot, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth > MaximumRmkLanguageDepth)
                throw new InvalidDataException(
                    $"The RMK language tree exceeds the supported depth of {MaximumRmkLanguageDepth}.");
            var canonicalDirectory = PathSafety.GetCanonicalExistingDirectory(directory);
            if (!canonicalDirectory.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
                && !PathSafety.IsStrictlyInside(canonicalDirectory, canonicalRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidDataException("An RMK language directory resolves outside its selected root.");
            }
            if (!visited.Add(canonicalDirectory)) continue;
            if (visited.Count > MaximumRmkLanguageDirectories)
                throw new InvalidDataException(
                    $"The RMK language tree contains more than {MaximumRmkLanguageDirectories:N0} directories.");

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         canonicalDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                entryCount++;
                if (entryCount > MaximumRmkLanguageEntries)
                    throw new InvalidDataException(
                        $"The RMK language tree contains more than {MaximumRmkLanguageEntries:N0} filesystem entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entry, depth + 1));
                    continue;
                }
                if (!Path.GetExtension(entry).Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                var canonicalFile = PathSafety.GetCanonicalExistingFile(entry);
                if (!PathSafety.IsStrictlyInside(canonicalFile, canonicalRoot))
                    throw new InvalidDataException("An RMK language XML resolves outside its selected root.");
                if (files.Count >= maximumXmlFiles)
                    throw new InvalidDataException(
                        $"RMK language input contains more than {maximumXmlFiles:N0} XML files.");
                files.Add(canonicalFile);
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static void VerifyOutputEvidenceUnchanged(
        IEnumerable<FileState> dirtyStates,
        string workbookPath,
        bool workbookExisted,
        RimWorldTranslatorRmkHistoryData? workbookData,
        LanguageFileService languageFiles,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken)
    {
        foreach (var state in dirtyStates)
        {
            if (writeBoundary.TargetExisted(state.Path) != state.OriginalFileExisted)
                throw new InvalidDataException("An RMK output XML appeared or disappeared before its write boundary was locked.");
            var lockedEntries = languageFiles.Read(state.Path);
            if (!SameEntries(state.OriginalEntries, lockedEntries))
                throw new InvalidDataException("An RMK output XML changed before its write boundary was locked.");
        }

        if (writeBoundary.TargetExisted(workbookPath) != workbookExisted)
            throw new InvalidDataException("The RMK workbook appeared or disappeared before its write boundary was locked.");
        if (!workbookExisted) return;

        var directory = Path.GetDirectoryName(workbookPath)
            ?? throw new InvalidDataException("RMK workbook path has no parent directory.");
        var evidencePath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(workbookPath)}.{Guid.NewGuid():N}.evidence.xlsx");
        SnapshotLeafFingerprint? evidenceFingerprint = null;
        try
        {
            evidenceFingerprint = writeBoundary.CopyTargetToNewPinned(
                workbookPath,
                evidencePath,
                cancellationToken);
            var lockedWorkbook = RimWorldTranslatorRmkXlsxReader.Read(evidencePath);
            if (!SameWorkbook(workbookData, lockedWorkbook))
                throw new InvalidDataException("The RMK workbook changed before its write boundary was locked.");
        }
        finally
        {
            if (evidenceFingerprint is not null && File.Exists(evidencePath))
            {
                _ = AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                    evidencePath,
                    evidenceFingerprint.VolumeSerialNumber,
                    evidenceFingerprint.FileIndex,
                    evidenceFingerprint.Length,
                    evidenceFingerprint.Sha256);
            }
        }
    }

    private static bool SameEntries(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual) =>
        expected.Count == actual.Count
        && expected.All(pair => actual.TryGetValue(pair.Key, out var value)
            && value.Equals(pair.Value, StringComparison.Ordinal));

    private static bool SameWorkbook(
        RimWorldTranslatorRmkHistoryData? expected,
        RimWorldTranslatorRmkHistoryData actual)
    {
        if (expected is null
            || !expected.SourceLanguage.Equals(actual.SourceLanguage, StringComparison.Ordinal)
            || expected.Rows.Count != actual.Rows.Count)
        {
            return false;
        }
        return expected.Rows.Zip(actual.Rows).All(pair =>
            pair.First.Identifier.Equals(pair.Second.Identifier, StringComparison.Ordinal)
            && pair.First.ClassName.Equals(pair.Second.ClassName, StringComparison.Ordinal)
            && pair.First.Key.Equals(pair.Second.Key, StringComparison.Ordinal)
            && pair.First.RequiredMods.Equals(pair.Second.RequiredMods, StringComparison.Ordinal)
            && pair.First.Source.Equals(pair.Second.Source, StringComparison.Ordinal)
            && pair.First.Translation.Equals(pair.Second.Translation, StringComparison.Ordinal));
    }

    private void WriteWorkbookThroughBoundary(
        string workbookPath,
        IEnumerable<RimWorldTranslatorRmkHistoryRow> rows,
        string sourceLanguage,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken)
    {
        _ = Path.GetDirectoryName(workbookPath)
            ?? throw new InvalidDataException("RMK workbook path has no parent directory.");
        var recoveryPlan = FileTransactionRecoverySession.ReservePreparedWrite(
            workbookPath,
            keepBackup: true);
        var preparedPath = recoveryPlan?.PreparedPath
            ?? AtomicTemporaryFiles.CreatePath(workbookPath);
        SnapshotLeafFingerprint? preparedFingerprint = null;
        try
        {
            var sourceWorkbookPath = writeBoundary.TargetExisted(workbookPath)
                ? workbookPath
                : null;
            var evidence = RimWorldTranslatorRmkXlsxWriter.WritePreparedWithEvidence(
                sourceWorkbookPath,
                preparedPath,
                rows,
                sourceLanguage);
            preparedFingerprint = new SnapshotLeafFingerprint(
                SnapshotLeafKind.File,
                evidence.Length,
                Convert.FromHexString(evidence.Sha256Hex),
                evidence.LastWriteTimeUtcTicks,
                evidence.VolumeSerialNumber,
                evidence.FileIndex);
            AfterWorkbookPreparedValidationTestHook?.Invoke(preparedPath);
            FileTransactionRecoverySession.MarkPreparedWriteReady(
                recoveryPlan,
                preparedFingerprint);
            writeBoundary.CommitPreparedFile(
                preparedPath,
                workbookPath,
                keepBackup: true,
                recoveryPlan,
                preparedFingerprint);
        }
        finally
        {
            if (preparedFingerprint is not null)
                _ = AtomicTemporaryFiles.TryDeleteRegularFileByHandle(
                    preparedPath,
                    preparedFingerprint.VolumeSerialNumber,
                    preparedFingerprint.FileIndex,
                    preparedFingerprint.Length,
                    preparedFingerprint.Sha256);
        }
    }

    private static string KeylessIdentity(string target, string defClass, string node) =>
        $"{target.Length}:{target}{defClass.Length}:{defClass}{node.Length}:{node}";

    private static RimWorldTranslatorRmkHistoryRow Copy(RimWorldTranslatorRmkHistoryRow row) => new()
    {
        Identifier = row.Identifier,
        ClassName = row.ClassName,
        Key = row.Key,
        RequiredMods = row.RequiredMods,
        Source = row.Source,
        Translation = row.Translation
    };

    private static void ValidateSegment(string value, string name)
    {
        if (!PathSafety.IsSafeFileNameSegment(value))
            throw new InvalidDataException($"{name} must be a single safe folder-name segment.");
    }

    private sealed class FileState(string path, SortedDictionary<string, string> entries)
    {
        public string Path { get; } = path;
        public bool OriginalFileExisted { get; } = File.Exists(path);
        public SortedDictionary<string, string> OriginalEntries { get; } = new(entries, StringComparer.Ordinal);
        public SortedDictionary<string, string> Entries { get; } = entries;
        public bool Dirty { get; set; }
    }
}
