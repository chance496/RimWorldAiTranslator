using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Utilities;
using RimWorldAiTranslator.Core.Validation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Core.Translation;

public sealed partial class TranslationEngine
{
    private const int MaximumPreservedDocumentBytes = 128 * 1024 * 1024;
    private readonly SourceExtractor extractor;
    private readonly LanguageFileService languageFiles;
    private readonly Func<IReadOnlyList<string>, TranslationApiClient> apiClientFactory;
    private readonly string applicationRoot;
    private readonly FileTransactionRecoveryAuthority? recoveryAuthority;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public TranslationEngine(
        string applicationRoot,
        SourceExtractor? extractor = null,
        LanguageFileService? languageFiles = null,
        Func<IReadOnlyList<string>, TranslationApiClient>? apiClientFactory = null,
        FileTransactionRecoveryAuthority? recoveryAuthority = null)
    {
        this.applicationRoot = Path.GetFullPath(applicationRoot);
        this.extractor = extractor ?? new SourceExtractor(Path.Combine(this.applicationRoot, "rimworld-def-field-rules.txt"));
        this.languageFiles = languageFiles ?? new LanguageFileService();
        this.apiClientFactory = apiClientFactory ?? (keys => new TranslationApiClient(keys));
        this.recoveryAuthority = recoveryAuthority;
    }

    public async Task<TranslationRunResult> RunAsync(
        TranslationEngineOptions options,
        IProgress<TranslationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var runState = new TranslationRunState();
        try
        {
            return await RunCoreAsync(options, progress, runState, cancellationToken).ConfigureAwait(false);
        }
        catch (TranslationRunCanceledException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var checkpointPersisted = runState.TryPersistCancelledCheckpoint();
            progress?.Report(new TranslationProgress("cancelled", "Translation cancelled.", IsWarning: true));
            throw new TranslationRunCanceledException(
                runState.CreateCancelledResult(checkpointPersisted),
                checkpointPersisted,
                cancellationToken);
        }
    }

    private async Task<TranslationRunResult> RunCoreAsync(
        TranslationEngineOptions options,
        IProgress<TranslationProgress>? progress,
        TranslationRunState runState,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        runState.CheckpointPersistenceAllowed = false;
        var modRoot = PathSafety.Normalize(options.ModRoot);
        if (!options.ReviewOnly)
            modRoot = RequireNonWorkshopOutputRoot(modRoot);
        var languageRoot = Path.Combine(modRoot, "Languages", options.LanguageFolderName);
        var existingLanguageRoot = string.IsNullOrWhiteSpace(options.ExistingLanguageRoot)
            ? languageRoot
            : PathSafety.Normalize(options.ExistingLanguageRoot);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string? reviewRunRoot;
        string outputLanguageRoot;
        string auditRoot;
        string transactionRoot;
        if (options.ReviewOnly)
        {
            var reviewBase = string.IsNullOrWhiteSpace(options.ReviewRoot)
                ? Path.Combine(applicationRoot, "reviews")
                : Path.GetFullPath(options.ReviewRoot);
            reviewBase = RequireNonWorkshopOutputRoot(reviewBase);
            transactionRoot = reviewBase;
            reviewRunRoot = CreateReviewRunRoot(reviewBase, modRoot, stamp);
            outputLanguageRoot = Path.Combine(reviewRunRoot, "Languages", options.LanguageFolderName);
            auditRoot = Path.Combine(reviewRunRoot, "_TranslationAudit");
        }
        else
        {
            reviewRunRoot = null;
            transactionRoot = modRoot;
            outputLanguageRoot = languageRoot;
            auditRoot = Path.Combine(modRoot, "_TranslationAudit");
        }

        var keys = TranslationApiClient.ParseKeys(options.ApiKeys);
        var providerKind = options.SourceOnly ? "SourceOnly" : keys.Count == 0 ? "Google" : options.Provider.ProviderKind;
        var auditProvider = SanitizeFileSegment(providerKind == "OpenAICompatible" ? options.Provider.Name : providerKind).ToLowerInvariant();
        var auditBase = Path.Combine(auditRoot, $"{auditProvider}-{stamp}");
        runState.ReviewRoot = reviewRunRoot;
        runState.AuditBase = auditBase;
        if (!options.DryRun)
        {
            transactionRoot = PrepareTransactionRoot(
                transactionRoot,
                allowCreate: options.ReviewOnly);
            if (recoveryAuthority is not null)
                FileTransaction.RecoverPending(recoveryAuthority, transactionRoot, cancellationToken);
            runState.CheckpointPersistenceAllowed = true;
            runState.PersistCheckpoint = complete => WriteCheckpoint(
                transactionRoot,
                auditBase,
                runState.TranslatedRows,
                runState.ComparisonRows,
                runState.TokenWarnings,
                runState.CompletedBatches,
                runState.TotalBatches,
                complete,
                CancellationToken.None);
        }
        progress?.Report(new TranslationProgress("initialize", "Translation workspace initialized."));

        var warnings = new List<string>();
        var history = ReadRmkHistory(options.ReferenceSourceWorkbook);
        var existingByIdentity = new Dictionary<string, ExistingInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var referenceRoot in options.ReferenceLanguageRoots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var pair in extractor.ReadExistingLanguageMap(
                         PathSafety.Normalize(referenceRoot),
                         warnings,
                         cancellationToken))
            {
                existingByIdentity.TryAdd(pair.Key, new ExistingInfo(true, pair.Value.Text, "rmk", string.Empty, string.Empty));
            }
        }
        foreach (var row in history.Data?.Rows ?? [])
        {
            var localizationNamespace = !string.IsNullOrWhiteSpace(row.ClassName)
                ? row.ClassName
                : ExtractNamespaceFromRmkIdentifier(row.Identifier);
            var identity = SourceExtractor.GetLocalizationIdentity(localizationNamespace, row.Key);
            if (!string.IsNullOrWhiteSpace(identity) && LanguageFileService.IsValidLocalizationKey(row.Key)
                && !string.IsNullOrWhiteSpace(row.Translation))
            {
                existingByIdentity.TryAdd(identity, new ExistingInfo(true, Normalize(row.Translation), "rmk", string.Empty, string.Empty));
            }
        }
        foreach (var pair in extractor.ReadExistingLanguageMap(existingLanguageRoot, warnings, cancellationToken))
        {
            existingByIdentity[pair.Key] = new ExistingInfo(true, pair.Value.Text, "mod", string.Empty, pair.Value.RelativePath);
        }

        var preservedLegacy = new Dictionary<string, ExistingInfo>(StringComparer.OrdinalIgnoreCase);
        ReadPreservedTranslations(options.PreserveTranslationFile, existingByIdentity, preservedLegacy);

        progress?.Report(new TranslationProgress("scan", "원문 분석 중"));
        var extraction = extractor.Extract(modRoot, options.SourceLanguageFolder, options.OutputFilePrefix, options.IncludePatches, cancellationToken);
        runState.SourceEntries = extraction.Entries.Count;
        warnings.AddRange(extraction.Warnings);
        foreach (var warning in warnings) progress?.Report(new TranslationProgress("warning", warning, IsWarning: true));

        var namespacesByKey = extraction.Entries
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.LocalizationNamespace).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        ExistingInfo GetExisting(SourceEntry entry)
        {
            if (existingByIdentity.TryGetValue(entry.Identity, out var existing)) return existing;
            return preservedLegacy.TryGetValue(entry.Key, out var legacy)
                   && namespacesByKey.TryGetValue(entry.Key, out var namespaces)
                   && namespaces.Count == 1
                ? legacy
                : ExistingInfo.Empty;
        }

        var currentRmkSources = BuildCurrentRmkSourceMap(history.SourceLanguage, extraction, modRoot, options, cancellationToken);
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reviewEntries = new List<SourceEntry>();
        var pending = new List<SourceEntry>();
        var skippedDuplicate = 0;
        var reusedExisting = 0;
        foreach (var entry in extraction.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Identity) || !dedupe.Add(entry.Identity))
            {
                skippedDuplicate++;
                runState.SkippedDuplicates = skippedDuplicate;
                continue;
            }
            if (options.Limit > 0 && reviewEntries.Count >= options.Limit) break;
            var existing = GetExisting(entry);
            var sourceChanged = IsPreservedSourceChanged(entry, existing)
                                || IsRmkSourceChanged(entry, existing, history.Data?.Map, currentRmkSources);
            if (existing.Present && !options.Overwrite && !options.ReviewOnly)
            {
                reusedExisting++;
                continue;
            }
            entry.Id = $"E{reviewEntries.Count + 1:D6}";
            reviewEntries.Add(entry);
            runState.ReviewEntries = reviewEntries.Count;
            if (options.TranslateMissingOnly && !string.IsNullOrWhiteSpace(existing.Text) && !sourceChanged)
            {
                reusedExisting++;
            }
            else
            {
                pending.Add(entry);
            }
        }

        progress?.Report(new TranslationProgress("source", $"Source language detection completed ({extraction.DetectedLanguages.Count} root(s))."));
        progress?.Report(new TranslationProgress("scan", $"Source entries: {extraction.Entries.Count}", extraction.Entries.Count, extraction.Entries.Count));
        progress?.Report(new TranslationProgress("prepare", $"Pending translation entries: {pending.Count}", 0, pending.Count));

        if (reviewEntries.Count == 0)
        {
            return new TranslationRunResult(reviewRunRoot, null, [], [], extraction.Entries.Count, 0, 0, skippedDuplicate, 0, 0);
        }
        if (options.DryRun)
        {
            return new TranslationRunResult(reviewRunRoot, null, [], [], extraction.Entries.Count, reviewEntries.Count, 0, skippedDuplicate, 0, 0);
        }

        if (!options.SourceOnly)
        {
            LanguageFileService.ValidateTransactionTargetCount(
                pending.Select(entry => ResolveOutputTarget(entry, GetExisting(entry), outputLanguageRoot)),
                cancellationToken);
        }

        if (options.ReviewOnly)
        {
            using var outputRootCreation =
                PathSafety.AcquireTrustedDirectoryCreationBoundary(
                    outputLanguageRoot,
                    cancellationToken);
        }
        using (PathSafety.AcquireTrustedDirectoryCreationBoundary(auditRoot, cancellationToken))
        {
        }
        WriteInitialAudit(
            transactionRoot,
            auditBase,
            reviewEntries,
            extraction.SkippedInternalIdentifiers,
            cancellationToken);

        var comparisonRows = runState.ComparisonRows;
        var translatedRows = runState.TranslatedRows;
        var tokenWarnings = runState.TokenWarnings;
        var outputGroups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var comparisonRowIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in reviewEntries)
        {
            comparisonRowIndexes[entry.Id] = comparisonRows.Count;
            comparisonRows.Add(CreateComparisonRow(entry, GetExisting(entry), string.Empty, string.Empty, outputLanguageRoot, history, currentRmkSources));
        }

        void ReplaceComparisonRow(SourceEntry entry, ReviewComparisonRow row) =>
            comparisonRows[comparisonRowIndexes[entry.Id]] = row;

        if (options.SourceOnly)
        {
            WriteCheckpoint(
                transactionRoot,
                auditBase,
                translatedRows,
                comparisonRows,
                tokenWarnings,
                0,
                0,
                true,
                cancellationToken);
            progress?.Report(new TranslationProgress("complete", "Done.", 0, 0));
            return new TranslationRunResult(reviewRunRoot, auditBase + "-comparison.json", comparisonRows, [], extraction.Entries.Count, reviewEntries.Count, 0, skippedDuplicate, 0, 0);
        }

        var glossary = new GlossaryService();
        var generatedGlossary = string.IsNullOrWhiteSpace(options.GeneratedGlossaryPath)
            ? Path.Combine(applicationRoot, "glossary.generated.ko.json")
            : options.GeneratedGlossaryPath;
        var curatedGlossary = string.IsNullOrWhiteSpace(options.CuratedGlossaryPath)
            ? Path.Combine(applicationRoot, "glossary.ko.json")
            : options.CuratedGlossaryPath;
        glossary.Load(generatedGlossary, curatedGlossary, options.UseCuratedGlossary);
        var basePrompt = TranslationPrompt.CreateSystem(
            GlossaryService.ToPrompt(glossary.Select([], options.MaxAlwaysGlossaryTerms, 0)),
            options.ExtraPrompt);
        var fixedPromptTokens = EstimateTokens(basePrompt) + 1800 + options.MaxGeneratedGlossaryTermsPerBatch * 14;
        var requestable = new List<SourceEntry>(pending.Count);
        var oversized = new List<SourceEntry>();
        foreach (var entry in pending)
        {
            if (IsEntryWithinInputLimits(entry, options, fixedPromptTokens)) requestable.Add(entry);
            else oversized.Add(entry);
        }
        var batches = SplitIntoBatches(requestable, options, fixedPromptTokens);
        runState.TotalBatches = batches.Count;
        var skippedUnsafe = oversized.Count;
        runState.SkippedUnsafe = skippedUnsafe;
        foreach (var entry in oversized)
        {
            var validation = TranslationValidator.Validate(entry.Text, entry.Text);
            tokenWarnings.Add(TokenWarningRow.Create(entry, entry.Text, "request_input_limit", validation));
            ReplaceComparisonRow(entry, CreateComparisonRow(
                entry,
                GetExisting(entry),
                string.Empty,
                "input-limit",
                outputLanguageRoot,
                history,
                currentRmkSources));
        }
        if (oversized.Count > 0)
        {
            progress?.Report(new TranslationProgress(
                "warning",
                $"Skipped {oversized.Count:N0} source entries that exceed the per-request input limit.",
                IsWarning: true));
        }
        WriteCheckpoint(
            transactionRoot,
            auditBase,
            translatedRows,
            comparisonRows,
            tokenWarnings,
            0,
            batches.Count,
            false,
            cancellationToken);

        using (var api = apiClientFactory(keys))
        {
            api.SetRequestAttemptLimit(options.MaxProviderRequestsPerRun);
            var completedEntries = 0;
            var overallTotal = requestable.Count;
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = batches[batchIndex];
                progress?.Report(new TranslationProgress(
                    "translate",
                    $"Translating {completedEntries:N0} of {overallTotal:N0} entries.",
                    completedEntries,
                    overallTotal));
                var overallProgress = new OverallTranslationProgress(progress, () => completedEntries, overallTotal);
                var map = await TranslateWithSplitAsync(
                        api,
                        options,
                        glossary,
                        batch,
                        $"{batchIndex + 1}/{batches.Count}",
                        overallProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var entry in batch)
                {
                    var translated = LanguageFileService.RemoveInvalidXmlCharacters(map.TryGetValue(entry.Id, out var value) ? value : entry.Text);
                    var validation = TranslationValidator.Validate(entry.Text, translated);
                    var sameAsSource = translated.Equals(entry.Text, StringComparison.Ordinal);
                    var safe = validation.IsSafe && !sameAsSource;
                    if (validation.MissingTokens.Count > 0)
                    {
                        tokenWarnings.Add(TokenWarningRow.Create(entry, translated, "missing_tokens", validation));
                    }
                    if (validation.UnexpectedTokens.Count > 0 || validation.TokenCountMismatches.Count > 0 || validation.GrammarPrefixMoved)
                    {
                        tokenWarnings.Add(TokenWarningRow.Create(entry, translated, "token_structure_changed", validation));
                    }
                    if (validation.Pathological)
                    {
                        tokenWarnings.Add(TokenWarningRow.Create(entry, translated, "pathological_newlines", validation));
                    }
                    if (validation.InvalidParticles.Count > 0)
                    {
                        tokenWarnings.Add(TokenWarningRow.Create(entry, translated, "invalid_korean_particle_notation", validation));
                    }

                    var existing = GetExisting(entry);
                    var target = ResolveOutputTarget(entry, existing, outputLanguageRoot);
                    if (options.ReviewOnly || safe)
                    {
                        if (!outputGroups.TryGetValue(target, out var group))
                        {
                            group = new Dictionary<string, string>(StringComparer.Ordinal);
                            outputGroups[target] = group;
                        }
                        group[entry.Key] = translated;
                    }
                    else
                    {
                        skippedUnsafe++;
                        runState.SkippedUnsafe = skippedUnsafe;
                    }

                    translatedRows.Add(new TranslatedAuditRow(entry, target, translated));
                    ReplaceComparisonRow(
                        entry,
                        CreateComparisonRow(entry, existing, translated, "ai", outputLanguageRoot, history, currentRmkSources, validation, safe));
                }
                completedEntries += batch.Count;
                progress?.Report(new TranslationProgress(
                    "translate",
                    $"Translated {completedEntries:N0} of {overallTotal:N0} entries.",
                    completedEntries,
                    overallTotal));
                runState.CompletedBatches = batchIndex + 1;
                WriteCheckpoint(
                    transactionRoot,
                    auditBase,
                    translatedRows,
                    comparisonRows,
                    tokenWarnings,
                    runState.CompletedBatches,
                    batches.Count,
                    false,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteLanguageTransaction(
                transactionRoot,
                outputGroups.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.OrdinalIgnoreCase),
                options.Overwrite,
                cancellationToken);
            WriteCheckpoint(
                transactionRoot,
                auditBase,
                translatedRows,
                comparisonRows,
                tokenWarnings,
                batches.Count,
                batches.Count,
                true,
                cancellationToken);
        }

        progress?.Report(new TranslationProgress("complete", "Done.", requestable.Count, requestable.Count));
        return new TranslationRunResult(
            reviewRunRoot,
            auditBase + "-comparison.json",
            comparisonRows,
            outputGroups.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            extraction.Entries.Count,
            reviewEntries.Count,
            translatedRows.Count,
            skippedDuplicate,
            skippedUnsafe,
            tokenWarnings.Count);
    }

    private async Task<Dictionary<string, string>> TranslateWithSplitAsync(
        TranslationApiClient api,
        TranslationEngineOptions options,
        GlossaryService glossary,
        IReadOnlyList<SourceEntry> batch,
        string label,
        IProgress<TranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (TranslationApiClient.ParseKeys(options.ApiKeys).Count == 0)
            {
                var googleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var googleEndpoint = ResolveGoogleEndpoint(options);
                foreach (var entry in batch)
                {
                    try
                    {
                        googleMap[entry.Id] = await api.TranslateGoogleAsync(
                            entry.Text,
                            googleEndpoint,
                            options.Timeout,
                            options.MaxRetries,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsExpectedGoogleItemFailure(ex))
                    {
                        progress?.Report(new TranslationProgress(
                            "warning",
                            $"Google translation failed for batch {label}; the source value was retained.",
                            IsWarning: true));
                        googleMap[entry.Id] = entry.Text;
                    }
                }
                return googleMap;
            }

            var systemPrompt = TranslationPrompt.CreateSystem(
                GlossaryService.ToPrompt(glossary.Select(batch, options.MaxAlwaysGlossaryTerms, options.MaxGeneratedGlossaryTermsPerBatch)),
                options.ExtraPrompt);
            var map = await api.TranslateOpenAiAsync(options, batch, systemPrompt, progress, cancellationToken).ConfigureAwait(false);
            var missing = batch.Where(entry => !map.ContainsKey(entry.Id)).Select(entry => entry.Id).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException($"Model response missed {missing.Length} ids in {label}. Missing sample: {string.Join(", ", missing.Take(5))}");
            }
            return map;
        }
        catch (Exception ex) when (CanSplitBatchFailure(ex) && batch.Count > 1)
        {
            var leftCount = (int)Math.Ceiling(batch.Count / 2.0);
            var left = batch.Take(leftCount).ToArray();
            var right = batch.Skip(leftCount).ToArray();
            progress?.Report(new TranslationProgress(
                "retry",
                $"Batch {label} failed; retrying as {left.Length} and {right.Length} entry groups.",
                left.Length,
                batch.Count,
                true));
            var merged = await TranslateWithSplitAsync(api, options, glossary, left, label + ".1", progress, cancellationToken).ConfigureAwait(false);
            foreach (var pair in await TranslateWithSplitAsync(api, options, glossary, right, label + ".2", progress, cancellationToken).ConfigureAwait(false))
            {
                merged[pair.Key] = pair.Value;
            }
            return merged;
        }
        catch (Exception ex) when (CanSplitBatchFailure(ex))
        {
            throw new InvalidOperationException($"Batch {label} failed at single-entry fallback.", ex);
        }
    }

    private static bool CanSplitBatchFailure(Exception exception) => exception switch
    {
        InvalidDataException => true,
        ProviderRequestException provider => provider.CanSplitBatch,
        _ => false
    };

    private static bool IsExpectedGoogleItemFailure(Exception exception) => exception is
        TimeoutException or HttpRequestException or JsonException or InvalidDataException;

    private sealed class OverallTranslationProgress(
        IProgress<TranslationProgress>? destination,
        Func<int> current,
        int total) : IProgress<TranslationProgress>
    {
        public void Report(TranslationProgress value)
        {
            if (destination is null) return;
            if (value.Stage is "retry" or "rate-limit" or "warning")
            {
                destination.Report(value with
                {
                    Current = current(),
                    Total = total
                });
                return;
            }
            destination.Report(value);
        }
    }

    private static IReadOnlyList<IReadOnlyList<SourceEntry>> SplitIntoBatches(IReadOnlyList<SourceEntry> entries, TranslationEngineOptions options, int fixedPromptTokens)
    {
        var batches = new List<IReadOnlyList<SourceEntry>>();
        var current = new List<SourceEntry>();
        var characters = 0;
        var tokens = fixedPromptTokens;
        foreach (var entry in entries)
        {
            if (!IsEntryWithinInputLimits(entry, options, fixedPromptTokens))
                throw new InvalidDataException("A source entry exceeds the provider request input limit.");
            var entryCharacters = entry.Text.Length + entry.Key.Length + 80;
            var entryTokens = EstimateTokens(entry.Text) + EstimateTokens(entry.Key) + 40;
            var exceedsCharacters = options.MaxInputCharactersPerBatch > 0 && characters + entryCharacters > options.MaxInputCharactersPerBatch;
            var exceedsTokens = options.MaxInputTokensPerBatch > 0 && tokens + entryTokens > options.MaxInputTokensPerBatch;
            if (current.Count > 0 && (current.Count >= options.BatchSize || exceedsCharacters || exceedsTokens))
            {
                batches.Add(current.ToArray());
                current = [];
                characters = 0;
                tokens = fixedPromptTokens;
            }
            current.Add(entry);
            characters += entryCharacters;
            tokens += entryTokens;
        }
        if (current.Count > 0) batches.Add(current.ToArray());
        return batches;
    }

    private static bool IsEntryWithinInputLimits(
        SourceEntry entry,
        TranslationEngineOptions options,
        int fixedPromptTokens)
    {
        var entryCharacters = entry.Text.Length + entry.Key.Length + 80;
        var entryTokens = EstimateTokens(entry.Text) + EstimateTokens(entry.Key) + 40;
        return (options.MaxInputCharactersPerBatch <= 0 || entryCharacters <= options.MaxInputCharactersPerBatch)
               && (options.MaxInputTokensPerBatch <= 0 || fixedPromptTokens + entryTokens <= options.MaxInputTokensPerBatch);
    }

    private ReviewComparisonRow CreateComparisonRow(
        SourceEntry entry,
        ExistingInfo existing,
        string candidate,
        string candidateOrigin,
        string outputLanguageRoot,
        RmkHistory history,
        IReadOnlyDictionary<string, string> currentRmkSources,
        TranslationValidationResult? validation = null,
        bool safe = false)
    {
        var identifier = GetRmkIdentifier(entry);
        var historicalSource = history.Data is not null && history.Data.Map.TryGetValue(identifier, out var historical) ? Normalize(historical.Source) : string.Empty;
        var currentSource = currentRmkSources.TryGetValue(identifier, out var current) ? current : string.Empty;
        var sourceChanged = !string.IsNullOrWhiteSpace(historicalSource)
            && !string.IsNullOrWhiteSpace(currentSource)
            && !SourceTextEquals(historicalSource, currentSource);
        var target = ResolveOutputTarget(entry, existing, outputLanguageRoot);
        var origin = string.IsNullOrWhiteSpace(candidateOrigin) ? existing.Origin : candidateOrigin;
        var existingSourceChanged = IsPreservedSourceChanged(entry, existing);
        var existingPreviousSourceText = existing.PreviousSourceText;
        if (existingSourceChanged && !existing.SourceChanged)
            existingPreviousSourceText = existing.SourceText;
        return new ReviewComparisonRow
        {
            Id = entry.Id,
            Key = entry.Key,
            Kind = entry.Kind,
            DefClass = entry.TypeName,
            Node = entry.Key,
            Field = entry.Field,
            Target = target,
            Source = entry.Text,
            Existing = existing.Text,
            ExistingSourceChanged = existingSourceChanged,
            ExistingSourceHash = existing.SourceHash,
            ExistingSourceText = existing.SourceText,
            ExistingPreviousSourceText = existingPreviousSourceText,
            Candidate = candidate,
            ExistingOrigin = existing.Origin,
            TranslationOrigin = origin,
            TranslationUpdatedAt = existing.TranslationUpdatedAt,
            RmkIdentifier = identifier,
            RmkHistoricalSource = historicalSource,
            RmkCurrentSource = currentSource,
            RmkSourceChanged = sourceChanged,
            RmkWorkbook = history.Path,
            ExistingPresent = !string.IsNullOrWhiteSpace(existing.Text),
            ExistingHasKorean = ContainsKorean(existing.Text),
            CandidateHasKorean = ContainsKorean(candidate),
            ExistingSameAsSource = existing.Text.Equals(entry.Text, StringComparison.Ordinal),
            CandidateSameAsSource = candidate.Equals(entry.Text, StringComparison.Ordinal),
            CandidateBlank = string.IsNullOrWhiteSpace(candidate),
            MissingTokens = validation is null ? string.Empty : string.Join('|', validation.MissingTokens),
            UnexpectedTokens = validation is null ? string.Empty : string.Join('|', validation.UnexpectedTokens),
            TokenCountMismatches = validation is null ? string.Empty : string.Join('|', validation.TokenCountMismatches),
            GrammarPrefixMoved = validation?.GrammarPrefixMoved ?? false,
            PathologicalCandidate = validation?.Pathological ?? false,
            InvalidKoreanParticles = validation is null ? string.Empty : string.Join('|', validation.InvalidParticles),
            SafeToApply = safe
        };
    }

    private static string ResolveOutputTarget(SourceEntry entry, ExistingInfo existing, string outputLanguageRoot)
    {
        var relative = existing.Origin is "mod" or "local" && !string.IsNullOrWhiteSpace(existing.TargetRelativePath)
            ? existing.TargetRelativePath
            : entry.TargetRelativePath;
        return PathSafety.ResolveInside(outputLanguageRoot, relative);
    }

    private Dictionary<string, string> BuildCurrentRmkSourceMap(
        string sourceLanguage,
        SourceExtractionResult extraction,
        string modRoot,
        TranslationEngineOptions options,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceLanguage)) return map;
        var selected = extraction.DetectedLanguages.Any(language =>
            language.Name.Equals(sourceLanguage, StringComparison.OrdinalIgnoreCase)
            || language.Name.StartsWith(sourceLanguage + " ", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<SourceEntry> entries;
        if (selected)
        {
            entries = extraction.Entries;
        }
        else
        {
            try
            {
                entries = extractor.Extract(modRoot, sourceLanguage, options.OutputFilePrefix, options.IncludePatches, cancellationToken).Entries;
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("Source language folder has no XML files:", StringComparison.Ordinal))
            {
                // An RMK workbook can describe an older source language that the current mod no longer ships.
                // Comparing unlike languages would mark every row as changed, so source-history comparison is skipped.
                return map;
            }
        }
        foreach (var entry in entries)
        {
            var id = GetRmkIdentifier(entry);
            if (!string.IsNullOrWhiteSpace(id)) map.TryAdd(id, entry.Text);
        }
        return map;
    }

    private static bool IsRmkSourceChanged(
        SourceEntry entry,
        ExistingInfo existing,
        IReadOnlyDictionary<string, RimWorldTranslatorRmkHistoryRow>? history,
        IReadOnlyDictionary<string, string> currentSources)
    {
        var identifier = GetRmkIdentifier(entry);
        return existing.Origin == "rmk"
            && history is not null
            && history.TryGetValue(identifier, out var historical)
            && currentSources.TryGetValue(identifier, out var current)
            && !string.IsNullOrWhiteSpace(historical.Source)
            && !string.IsNullOrWhiteSpace(current)
            && !SourceTextEquals(historical.Source, current);
    }

    private static RmkHistory ReadRmkHistory(string workbookPath)
    {
        if (string.IsNullOrWhiteSpace(workbookPath)) return new RmkHistory(string.Empty, string.Empty, null);
        var fullPath = Path.GetFullPath(workbookPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("RMK source workbook not found.", fullPath);
        if (!info.Extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("RMK source workbook must be an .xlsx file.");
        if (info.Length > 256L * 1024 * 1024) throw new InvalidDataException("RMK source workbook is too large.");
        var data = RimWorldTranslatorRmkXlsxReader.Read(fullPath);
        return new RmkHistory(fullPath, data.SourceLanguage, data);
    }

    private static void ReadPreservedTranslations(
        string path,
        IDictionary<string, ExistingInfo> existing,
        IDictionary<string, ExistingInfo> legacy)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Preserved translation file was not found.", fullPath);
        if (info.Length > 64L * 1024 * 1024) throw new InvalidDataException("Preserved translation file is too large.");
        using var document = ParsePreservedDocument(fullPath);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version)
            || version is not (1 or 2)
            || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Preserved translation file has an unsupported structure or version.");
        }
        if (version == 2
            && (!root.TryGetProperty("languageRoot", out var languageRoot)
                || languageRoot.ValueKind != JsonValueKind.String))
        {
            throw new InvalidDataException("Preserved translation v2 file is missing its language root.");
        }

        var preservedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preservedLegacyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Preserved translation file contains a non-object item.");
            var key = GetRequiredString(item, "key").Trim();
            var text = Normalize(GetRequiredString(item, "text"));
            if (!LanguageFileService.IsValidLocalizationKey(key) || string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("Preserved translation file contains an invalid key or blank translation.");
            var kind = GetRequiredString(item, "kind");
            var target = GetRequiredString(item, "target");
            var localizationNamespace = GetRequiredString(item, "namespace");
            if (string.IsNullOrWhiteSpace(localizationNamespace))
            {
                localizationNamespace = kind == "Keyed" ? "Keyed" : GetRequiredString(item, "defClass");
            }
            if (string.IsNullOrWhiteSpace(localizationNamespace) && !string.IsNullOrWhiteSpace(target))
            {
                localizationNamespace = SourceExtractor.GetNamespaceFromRelativePath(target);
            }
            var sourceChanged = version == 1 || GetRequiredBoolean(item, "sourceChanged");
            var sourceHash = version == 2 ? GetRequiredString(item, "sourceHash") : string.Empty;
            var sourceText = version == 2 ? GetRequiredString(item, "sourceText") : string.Empty;
            var previousSourceText = version == 2 ? GetRequiredString(item, "previousSourceText") : string.Empty;
            if (version == 2
                && (!IsSha256(sourceHash)
                    || !sourceHash.Equals(
                        StableIdentity.Sha256(Normalize(sourceText)),
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Preserved translation v2 file contains invalid source evidence.");
            }
            var infoEntry = new ExistingInfo(
                true,
                text,
                string.IsNullOrWhiteSpace(GetString(item, "origin")) ? "local" : GetString(item, "origin"),
                GetString(item, "translationUpdatedAt"),
                target,
                true,
                sourceChanged,
                sourceHash,
                sourceText,
                previousSourceText);
            var identity = SourceExtractor.GetLocalizationIdentity(localizationNamespace, key);
            if (string.IsNullOrWhiteSpace(identity))
            {
                if (!preservedLegacyKeys.Add(key))
                    throw new InvalidDataException("Preserved translation file contains a duplicate legacy key.");
                legacy[key] = infoEntry;
            }
            else
            {
                if (!preservedIdentities.Add(identity))
                    throw new InvalidDataException("Preserved translation file contains a duplicate identity.");
                existing[identity] = infoEntry;
            }
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static JsonDocument ParsePreservedDocument(string path)
    {
        try
        {
            return JsonDocument.Parse(BoundedFileReader.ReadAllBytes(
                path,
                MaximumPreservedDocumentBytes,
                "preserved translation file"));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Preserved translation file is not valid UTF-8 JSON.", exception);
        }
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Preserved translation item requires a string '{name}' property.");
        return value.GetString() ?? string.Empty;
    }

    private static bool GetRequiredBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Preserved translation item requires a boolean '{name}' property.");
        }
        return value.GetBoolean();
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsPreservedSourceChanged(SourceEntry entry, ExistingInfo existing)
    {
        if (!existing.IsPreserved) return false;
        if (existing.SourceChanged) return true;
        if (!IsSha256(existing.SourceHash)) return true;
        return !existing.SourceHash.Equals(
                   StableIdentity.Sha256(Normalize(entry.Text)),
                   StringComparison.OrdinalIgnoreCase)
               || !SourceTextEquals(existing.SourceText, entry.Text);
    }

    private void WriteInitialAudit(
        string transactionRoot,
        string auditBase,
        IReadOnlyList<SourceEntry> sourceEntries,
        IReadOnlyList<SkippedSourceEntry> skippedEntries,
        CancellationToken cancellationToken)
    {
        var sourcePath = auditBase + "-source.json";
        var skippedPath = auditBase + "-skipped-internal-identifiers.json";
        ExecuteRecoveryBackedTransaction(
            transactionRoot,
            [sourcePath, skippedPath],
            () =>
            {
                WriteJson(sourcePath, sourceEntries);
                WriteJson(skippedPath, skippedEntries);
            },
            "Translation review initialization",
            cancellationToken);
    }

    private void WriteCheckpoint(
        string transactionRoot,
        string auditBase,
        IReadOnlyList<TranslatedAuditRow> translated,
        IReadOnlyList<ReviewComparisonRow> comparisons,
        IReadOnlyList<TokenWarningRow> warnings,
        int completedBatches,
        int totalBatches,
        bool complete,
        CancellationToken cancellationToken)
    {
        var translatedPath = auditBase + "-translated.json";
        var comparisonJsonPath = auditBase + "-comparison.json";
        var comparisonCsvPath = auditBase + "-comparison.csv";
        var warningsPath = auditBase + "-token-warnings.json";
        var progressPath = auditBase + "-progress.json";
        ExecuteRecoveryBackedTransaction(
            transactionRoot,
            [translatedPath, comparisonJsonPath, comparisonCsvPath, warningsPath, progressPath],
            () =>
            {
                WriteJson(translatedPath, translated);
                WriteJson(comparisonJsonPath, comparisons);
                WriteComparisonCsv(comparisonCsvPath, comparisons);
                WriteJson(warningsPath, warnings);
                WriteJson(progressPath, new[]
                {
                    new { version = 1, completedBatches, totalBatches, complete, updatedAt = DateTimeOffset.UtcNow.ToString("O") }
                });
            },
            "Translation review checkpoint",
            cancellationToken);
    }

    private void ExecuteRecoveryBackedTransaction(
        string transactionRoot,
        IReadOnlyList<string> paths,
        Action action,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (recoveryAuthority is null)
        {
            action();
            return;
        }

        using var recoverySession = FileTransaction.AcquireRecoveryLease(
            recoveryAuthority,
            transactionRoot,
            cancellationToken);
        FileTransaction.Execute(
            paths,
            action,
            operationName,
            () => { },
            recoverySession,
            cancellationToken);
    }

    private IReadOnlyDictionary<string, LanguageWriteResult> WriteLanguageTransaction(
        string transactionRoot,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> outputGroups,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (recoveryAuthority is null || outputGroups.Count == 0)
            return languageFiles.WriteTransaction(outputGroups, overwrite, cancellationToken);

        using var recoverySession = FileTransaction.AcquireRecoveryLease(
            recoveryAuthority,
            transactionRoot,
            cancellationToken);
        using var writeBoundary = PathSafety.AcquireTrustedWriteBoundary(
            transactionRoot,
            outputGroups.Keys,
            cancellationToken);
        return languageFiles.WriteTransaction(
            outputGroups,
            overwrite,
            writeBoundary,
            recoverySession,
            cancellationToken);
    }

    private static void WriteJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        AtomicFile.WriteUtf8Validated(
            path,
            json,
            temporaryPath => ValidatedArtifactFile.ValidateJson(temporaryPath, json));
    }

    private static void WriteComparisonCsv(string path, IEnumerable<ReviewComparisonRow> rows)
    {
        static string Escape(string? value)
        {
            var text = value ?? string.Empty;
            return '"' + text.Replace("\"", "\"\"") + '"';
        }

        static string NeutralizeSpreadsheetFormula(string value)
        {
            var index = 0;
            while (index < value.Length)
            {
                if (!Rune.TryGetRuneAt(value, index, out var rune))
                {
                    // Invalid UTF-16 must not become a way to hide an active prefix.
                    index++;
                    continue;
                }

                var category = Rune.GetUnicodeCategory(rune);
                if (!Rune.IsWhiteSpace(rune)
                    && category is not (UnicodeCategory.Control or UnicodeCategory.Format))
                {
                    break;
                }
                index += rune.Utf16SequenceLength;
            }

            return index < value.Length && value[index] is '=' or '+' or '-' or '@'
                ? "'" + value
                : value;
        }

        var materializedRows = rows.ToArray();
        var builder = new StringBuilder();
        var columns = new[]
        {
            "id", "key", "kind", "defClass", "node", "field", "target", "source", "existing", "candidate",
            "existingOrigin", "translationOrigin", "translationUpdatedAt", "rmkIdentifier", "rmkHistoricalSource",
            "rmkCurrentSource", "rmkSourceChanged", "rmkWorkbook", "existingPresent", "existingHasKorean",
            "candidateHasKorean", "existingSameAsSource", "candidateSameAsSource", "candidateBlank", "missingTokens",
            "unexpectedTokens", "tokenCountMismatches", "grammarPrefixMoved", "pathologicalCandidate", "invalidKoreanParticles", "safeToApply"
        };
        var expectedRecords = new List<string[]>(materializedRows.Length + 1) { columns };
        builder.AppendLine(string.Join(',', columns.Select(Escape)));
        foreach (var row in materializedRows)
        {
            var values = new object?[]
            {
                row.Id, row.Key, row.Kind, row.DefClass, row.Node, row.Field, row.Target, row.Source, row.Existing, row.Candidate,
                row.ExistingOrigin, row.TranslationOrigin, row.TranslationUpdatedAt, row.RmkIdentifier, row.RmkHistoricalSource,
                row.RmkCurrentSource, row.RmkSourceChanged, row.RmkWorkbook, row.ExistingPresent, row.ExistingHasKorean,
                row.CandidateHasKorean, row.ExistingSameAsSource, row.CandidateSameAsSource, row.CandidateBlank, row.MissingTokens,
                row.UnexpectedTokens, row.TokenCountMismatches, row.GrammarPrefixMoved,
                row.PathologicalCandidate, row.InvalidKoreanParticles, row.SafeToApply
            };
            var textValues = values
                .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
                .ToArray();
            var csvValues = textValues.Select(NeutralizeSpreadsheetFormula).ToArray();
            expectedRecords.Add(csvValues);
            builder.AppendLine(string.Join(',', csvValues.Select(Escape)));
        }
        AtomicFile.WriteUtf8Validated(
            path,
            builder.ToString(),
            temporaryPath => ValidatedArtifactFile.ValidateCsv(temporaryPath, expectedRecords));
    }

    private static string CreateReviewRunRoot(string baseRoot, string modRoot, string stamp)
    {
        var leaf = Path.GetFileName(modRoot);
        if (string.IsNullOrWhiteSpace(leaf)) leaf = "mod";
        leaf = InvalidFileSegmentRegex().Replace(leaf, "_");
        var candidate = Path.Combine(baseRoot, $"{leaf}-{stamp}");
        if (!Directory.Exists(candidate)) return candidate;
        return Path.Combine(baseRoot, $"{leaf}-{stamp}-{Guid.NewGuid():N}"[..Math.Min(leaf.Length + stamp.Length + 10, leaf.Length + stamp.Length + 10)]);
    }

    private static string PrepareTransactionRoot(string path, bool allowCreate)
    {
        var requested = PathSafety.Normalize(path);
        if (!Directory.Exists(requested))
        {
            if (!allowCreate)
                throw new DirectoryNotFoundException("The translation transaction root does not exist.");
            if (File.Exists(requested))
                throw new IOException("The translation transaction root is occupied by a file.");
            using (PathSafety.AcquireTrustedDirectoryCreationBoundary(requested))
            {
            }
        }

        PathSafety.EnsureNoReparsePointsToVolumeRoot(requested);
        var canonical = PathSafety.GetCanonicalExistingDirectory(requested);
        if (!canonical.Equals(requested, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Translation writes require a canonical physical transaction root.");
        return canonical;
    }

    private static string RequireNonWorkshopOutputRoot(string path)
    {
        var requested = PathSafety.Normalize(path);
        if (PathSafety.IsWorkshopContentPath(requested))
            throw new InvalidOperationException("Steam Workshop content is read-only and cannot be used as a translation output root.");
        var canonical = PathSafety.GetCanonicalProspectiveDirectory(requested);
        if (PathSafety.IsNetworkPath(canonical))
            throw new InvalidDataException("Translation outputs must use local paths.");
        if (PathSafety.IsWorkshopContentPath(canonical))
            throw new InvalidOperationException("Steam Workshop content is read-only and cannot be used through a filesystem alias.");
        return canonical;
    }

    private static void ValidateOptions(TranslationEngineOptions options)
    {
        if (PathSafety.IsNetworkPath(options.ModRoot)
            || PathSafety.IsNetworkPath(options.ReviewRoot)
            || PathSafety.IsNetworkPath(options.ReferenceSourceWorkbook)
            || PathSafety.IsNetworkPath(options.GeneratedGlossaryPath)
            || PathSafety.IsNetworkPath(options.CuratedGlossaryPath)
            || PathSafety.IsNetworkPath(options.PreserveTranslationFile)
            || PathSafety.IsNetworkPath(options.ExistingLanguageRoot)
            || options.ReferenceLanguageRoots.Any(PathSafety.IsNetworkPath))
        {
            throw new InvalidDataException("Translation inputs and outputs must use local paths.");
        }
        if (!Directory.Exists(options.ModRoot)) throw new DirectoryNotFoundException($"Mod root was not found: {options.ModRoot}");
        if (!SafeSegmentRegex().IsMatch(options.LanguageFolderName) || !PathSafety.IsSafeFileNameSegment(options.LanguageFolderName))
            throw new InvalidDataException("LanguageFolderName is invalid.");
        if (!SafeSegmentRegex().IsMatch(options.OutputFilePrefix) || !PathSafety.IsSafeFileNameSegment(options.OutputFilePrefix))
            throw new InvalidDataException("OutputFilePrefix is invalid.");
        if (options.BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), options.BatchSize, "BatchSize must be positive.");
        if (options.MaxRetries <= 0) throw new ArgumentOutOfRangeException(nameof(options), options.MaxRetries, "MaxRetries must be positive.");
        if (options.MaxProviderRequestsPerRun <= 0 || options.MaxProviderRequestsPerRun > 10_000)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxProviderRequestsPerRun, "MaxProviderRequestsPerRun must be between 1 and 10,000.");
        if (!options.SourceOnly && TranslationApiClient.ParseKeys(options.ApiKeys).Count > 0)
        {
            TranslationApiClient.ValidateEndpoint(options.ProviderSettings.Url, options.AllowInsecureLoopback);
            if (string.IsNullOrWhiteSpace(options.ProviderSettings.Model) || options.ProviderSettings.Model.Length > 200 || ControlCharacterRegex().IsMatch(options.ProviderSettings.Model))
                throw new InvalidDataException("Model is invalid.");
        }
    }

    private static string ResolveGoogleEndpoint(TranslationEngineOptions options)
    {
        if (options.Provider.Id.Equals("Google", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(options.ProviderSettings.Url))
        {
            return options.ProviderSettings.Url.Trim();
        }
        return ApiProviderCatalog.Get("Google").Url;
    }

    private static bool SourceTextEquals(string left, string right)
    {
        return Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);
    }

    private static string GetRmkIdentifier(SourceEntry entry) => $"{entry.LocalizationNamespace}+{entry.Key}";
    private static string ExtractNamespaceFromRmkIdentifier(string identifier) => identifier.Contains('+') ? identifier[..identifier.IndexOf('+')] : string.Empty;
    private static bool ContainsKorean(string text) => text.Any(character => character is >= '\uac00' and <= '\ud7af');
    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    private static int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 3.0);
    private static string SanitizeFileSegment(string value) => InvalidFileSegmentRegex().Replace(value, "-");

    private sealed record ExistingInfo(
        bool Present,
        string Text,
        string Origin,
        string TranslationUpdatedAt,
        string TargetRelativePath,
        bool IsPreserved = false,
        bool SourceChanged = false,
        string SourceHash = "",
        string SourceText = "",
        string PreviousSourceText = "")
    {
        public static ExistingInfo Empty { get; } = new(false, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private sealed record RmkHistory(string Path, string SourceLanguage, RimWorldTranslatorRmkHistoryData? Data);

    private sealed class TranslationRunState
    {
        public string? ReviewRoot { get; set; }
        public string? AuditBase { get; set; }
        public int SourceEntries { get; set; }
        public int ReviewEntries { get; set; }
        public int SkippedDuplicates { get; set; }
        public int SkippedUnsafe { get; set; }
        public int CompletedBatches { get; set; }
        public int TotalBatches { get; set; }
        public bool CheckpointPersistenceAllowed { get; set; }
        public Action<bool>? PersistCheckpoint { get; set; }
        public List<ReviewComparisonRow> ComparisonRows { get; } = [];
        public List<TranslatedAuditRow> TranslatedRows { get; } = [];
        public List<TokenWarningRow> TokenWarnings { get; } = [];

        public bool TryPersistCancelledCheckpoint()
        {
            if (!CheckpointPersistenceAllowed
                || string.IsNullOrWhiteSpace(AuditBase)
                || PersistCheckpoint is null)
            {
                return false;
            }
            try
            {
                PersistCheckpoint(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException
                                      or JsonException
                                      or NotSupportedException)
            {
                return false;
            }
        }

        public TranslationRunResult CreateCancelledResult(bool checkpointPersisted) => new(
            ReviewRoot,
            checkpointPersisted ? AuditBase + "-comparison.json" : null,
            ComparisonRows.ToArray(),
            [],
            SourceEntries,
            ReviewEntries,
            TranslatedRows.Count,
            SkippedDuplicates,
            SkippedUnsafe,
            TokenWarnings.Count,
            Cancelled: true);
    }

    private sealed class TranslatedAuditRow
    {
        public TranslatedAuditRow(SourceEntry entry, string target, string translation)
        {
            Id = entry.Id; Key = entry.Key; Kind = entry.Kind; DefClass = entry.TypeName; Node = entry.Key;
            Field = entry.Field; Target = target; Source = entry.Text; Translation = translation;
        }
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; }
        [System.Text.Json.Serialization.JsonPropertyName("key")] public string Key { get; }
        [System.Text.Json.Serialization.JsonPropertyName("kind")] public string Kind { get; }
        [System.Text.Json.Serialization.JsonPropertyName("defClass")] public string DefClass { get; }
        [System.Text.Json.Serialization.JsonPropertyName("node")] public string Node { get; }
        [System.Text.Json.Serialization.JsonPropertyName("field")] public string Field { get; }
        [System.Text.Json.Serialization.JsonPropertyName("target")] public string Target { get; }
        [System.Text.Json.Serialization.JsonPropertyName("source")] public string Source { get; }
        [System.Text.Json.Serialization.JsonPropertyName("translation")] public string Translation { get; }
        [System.Text.Json.Serialization.JsonPropertyName("translationOrigin")] public string TranslationOrigin => "ai";
    }

    private sealed class TokenWarningRow
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("translation")] public string Translation { get; init; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("missingTokens")] public IReadOnlyList<string> MissingTokens { get; init; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("unexpectedTokens")] public IReadOnlyList<string> UnexpectedTokens { get; init; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("tokenCountMismatches")] public IReadOnlyList<string> TokenCountMismatches { get; init; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("grammarPrefixMoved")] public bool GrammarPrefixMoved { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("invalidKoreanParticles")] public IReadOnlyList<string> InvalidKoreanParticles { get; init; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
        public static TokenWarningRow Create(SourceEntry entry, string translation, string reason, TranslationValidationResult validation) => new()
        {
            Id = entry.Id,
            Key = entry.Key,
            Source = entry.Text,
            Translation = translation,
            MissingTokens = validation.MissingTokens,
            UnexpectedTokens = validation.UnexpectedTokens,
            TokenCountMismatches = validation.TokenCountMismatches,
            GrammarPrefixMoved = validation.GrammarPrefixMoved,
            InvalidKoreanParticles = validation.InvalidParticles,
            Reason = reason
        };
    }

    [GeneratedRegex("[^A-Za-z0-9_.-]", RegexOptions.CultureInvariant)] private static partial Regex InvalidFileSegmentRegex();
    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)] private static partial Regex SafeSegmentRegex();
    [GeneratedRegex("[\\x00-\\x1F\\x7F]", RegexOptions.CultureInvariant)] private static partial Regex ControlCharacterRegex();
}
