using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Validation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Core.Translation;

public sealed partial class TranslationEngine
{
    private readonly SourceExtractor extractor;
    private readonly LanguageFileService languageFiles;
    private readonly Func<IReadOnlyList<string>, TranslationApiClient> apiClientFactory;
    private readonly string applicationRoot;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public TranslationEngine(
        string applicationRoot,
        SourceExtractor? extractor = null,
        LanguageFileService? languageFiles = null,
        Func<IReadOnlyList<string>, TranslationApiClient>? apiClientFactory = null)
    {
        this.applicationRoot = Path.GetFullPath(applicationRoot);
        this.extractor = extractor ?? new SourceExtractor(Path.Combine(this.applicationRoot, "rimworld-def-field-rules.txt"));
        this.languageFiles = languageFiles ?? new LanguageFileService();
        this.apiClientFactory = apiClientFactory ?? (keys => new TranslationApiClient(keys));
    }

    public async Task<TranslationRunResult> RunAsync(
        TranslationEngineOptions options,
        IProgress<TranslationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var modRoot = PathSafety.Normalize(options.ModRoot);
        var languageRoot = Path.Combine(modRoot, "Languages", options.LanguageFolderName);
        var existingLanguageRoot = string.IsNullOrWhiteSpace(options.ExistingLanguageRoot)
            ? languageRoot
            : PathSafety.Normalize(options.ExistingLanguageRoot);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string? reviewRunRoot;
        string outputLanguageRoot;
        string auditRoot;
        if (options.ReviewOnly)
        {
            var reviewBase = string.IsNullOrWhiteSpace(options.ReviewRoot)
                ? Path.Combine(applicationRoot, "reviews")
                : Path.GetFullPath(options.ReviewRoot);
            reviewRunRoot = CreateReviewRunRoot(reviewBase, modRoot, stamp);
            outputLanguageRoot = Path.Combine(reviewRunRoot, "Languages", options.LanguageFolderName);
            auditRoot = Path.Combine(reviewRunRoot, "_TranslationAudit");
        }
        else
        {
            reviewRunRoot = null;
            outputLanguageRoot = languageRoot;
            auditRoot = Path.Combine(modRoot, "_TranslationAudit");
        }

        var keys = TranslationApiClient.ParseKeys(options.ApiKeys);
        var providerKind = options.SourceOnly ? "SourceOnly" : keys.Count == 0 ? "Google" : options.Provider.ProviderKind;
        var auditProvider = SanitizeFileSegment(providerKind == "OpenAICompatible" ? options.Provider.Name : providerKind).ToLowerInvariant();
        var auditBase = Path.Combine(auditRoot, $"{auditProvider}-{stamp}");
        progress?.Report(new TranslationProgress("initialize", $"Mod root: {modRoot}"));

        var warnings = new List<string>();
        var history = ReadRmkHistory(options.ReferenceSourceWorkbook);
        var existingByIdentity = new Dictionary<string, ExistingInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var referenceRoot in options.ReferenceLanguageRoots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var pair in extractor.ReadExistingLanguageMap(PathSafety.Normalize(referenceRoot), warnings))
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
        foreach (var pair in extractor.ReadExistingLanguageMap(existingLanguageRoot, warnings))
        {
            existingByIdentity[pair.Key] = new ExistingInfo(true, pair.Value.Text, "mod", string.Empty, pair.Value.RelativePath);
        }

        var preservedLegacy = new Dictionary<string, ExistingInfo>(StringComparer.OrdinalIgnoreCase);
        ReadPreservedTranslations(options.PreserveTranslationFile, existingByIdentity, preservedLegacy);

        progress?.Report(new TranslationProgress("scan", "원문 분석 중"));
        var extraction = extractor.Extract(modRoot, options.SourceLanguageFolder, options.OutputFilePrefix, options.IncludePatches, cancellationToken);
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
                continue;
            }
            if (options.Limit > 0 && reviewEntries.Count >= options.Limit) break;
            var existing = GetExisting(entry);
            var sourceChanged = IsRmkSourceChanged(entry, existing, history.Data?.Map, currentRmkSources);
            if (existing.Present && !options.Overwrite && !options.ReviewOnly)
            {
                reusedExisting++;
                continue;
            }
            entry.Id = $"E{reviewEntries.Count + 1:D6}";
            reviewEntries.Add(entry);
            if (options.TranslateMissingOnly && !string.IsNullOrWhiteSpace(existing.Text) && !sourceChanged)
            {
                reusedExisting++;
            }
            else
            {
                pending.Add(entry);
            }
        }

        progress?.Report(new TranslationProgress("source", $"Detected source language: {string.Join(", ", extraction.DetectedLanguages.Select(language => language.Name))}"));
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

        if (options.ReviewOnly) Directory.CreateDirectory(outputLanguageRoot);
        Directory.CreateDirectory(auditRoot);
        WriteJson(auditBase + "-source.json", reviewEntries);
        WriteJson(auditBase + "-skipped-internal-identifiers.json", extraction.SkippedInternalIdentifiers);

        var comparisonRows = new List<ReviewComparisonRow>();
        var translatedRows = new List<TranslatedAuditRow>();
        var tokenWarnings = new List<TokenWarningRow>();
        var outputGroups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var pendingIds = pending.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in reviewEntries.Where(entry => !pendingIds.Contains(entry.Id)))
        {
            comparisonRows.Add(CreateComparisonRow(entry, GetExisting(entry), string.Empty, string.Empty, outputLanguageRoot, history, currentRmkSources));
        }

        if (options.SourceOnly)
        {
            foreach (var entry in pending)
            {
                comparisonRows.Add(CreateComparisonRow(entry, GetExisting(entry), string.Empty, string.Empty, outputLanguageRoot, history, currentRmkSources));
            }
            WriteCheckpoint(auditBase, translatedRows, comparisonRows, tokenWarnings, 0, 0, true);
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
        var batches = SplitIntoBatches(pending, options, fixedPromptTokens);
        WriteCheckpoint(auditBase, translatedRows, comparisonRows, tokenWarnings, 0, batches.Count, false);

        var skippedUnsafe = 0;
        using var api = apiClientFactory(keys);
        try
        {
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = batches[batchIndex];
                progress?.Report(new TranslationProgress("translate", $"Translating batch {batchIndex + 1}/{batches.Count} ({batch.Count} entries)", batchIndex + 1, batches.Count));
                var map = await TranslateWithSplitAsync(api, options, glossary, batch, $"{batchIndex + 1}/{batches.Count}", progress, cancellationToken).ConfigureAwait(false);
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
                    }

                    translatedRows.Add(new TranslatedAuditRow(entry, target, translated));
                    comparisonRows.Add(CreateComparisonRow(entry, existing, translated, "ai", outputLanguageRoot, history, currentRmkSources, validation, safe));
                }
                WriteCheckpoint(auditBase, translatedRows, comparisonRows, tokenWarnings, batchIndex + 1, batches.Count, false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            languageFiles.WriteTransaction(
                outputGroups.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.OrdinalIgnoreCase),
                options.Overwrite);
            WriteCheckpoint(auditBase, translatedRows, comparisonRows, tokenWarnings, batches.Count, batches.Count, true);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new TranslationProgress("cancelled", "Translation cancelled.", IsWarning: true));
            throw;
        }

        progress?.Report(new TranslationProgress("complete", "Done.", batches.Count, batches.Count));
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
                foreach (var entry in batch)
                {
                    try
                    {
                        googleMap[entry.Id] = await api.TranslateGoogleAsync(
                            entry.Text,
                            options.Provider.Id == "Google" ? options.ProviderSettings.Url : ApiProviderCatalog.Get("Google").Url,
                            options.Timeout,
                            options.MaxRetries,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        progress?.Report(new TranslationProgress("warning", $"Google Translate failed for {label}; keeping source text. {Compact(ex.Message)}", IsWarning: true));
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
        catch (Exception ex) when (ex is not OperationCanceledException && batch.Count > 1)
        {
            var leftCount = (int)Math.Ceiling(batch.Count / 2.0);
            var left = batch.Take(leftCount).ToArray();
            var right = batch.Skip(leftCount).ToArray();
            progress?.Report(new TranslationProgress("retry", $"Batch {label} failed; splitting {batch.Count} entries into {left.Length}+{right.Length}. {Compact(ex.Message)}", IsWarning: true));
            var merged = await TranslateWithSplitAsync(api, options, glossary, left, label + ".1", progress, cancellationToken).ConfigureAwait(false);
            foreach (var pair in await TranslateWithSplitAsync(api, options, glossary, right, label + ".2", progress, cancellationToken).ConfigureAwait(false))
            {
                merged[pair.Key] = pair.Value;
            }
            return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Batch {label} failed at single-entry fallback. {Compact(ex.Message)}", ex);
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
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        if (!document.RootElement.TryGetProperty("items", out var items)) return;
        foreach (var item in items.EnumerateArray())
        {
            var key = GetString(item, "key").Trim();
            var text = Normalize(GetString(item, "text"));
            if (!LanguageFileService.IsValidLocalizationKey(key) || string.IsNullOrWhiteSpace(text)) continue;
            var kind = GetString(item, "kind");
            var target = GetString(item, "target");
            var localizationNamespace = GetString(item, "namespace");
            if (string.IsNullOrWhiteSpace(localizationNamespace))
            {
                localizationNamespace = kind == "Keyed" ? "Keyed" : GetString(item, "defClass");
            }
            if (string.IsNullOrWhiteSpace(localizationNamespace) && !string.IsNullOrWhiteSpace(target))
            {
                localizationNamespace = SourceExtractor.GetNamespaceFromRelativePath(target);
            }
            var infoEntry = new ExistingInfo(
                true,
                text,
                string.IsNullOrWhiteSpace(GetString(item, "origin")) ? "local" : GetString(item, "origin"),
                GetString(item, "translationUpdatedAt"),
                target);
            var identity = SourceExtractor.GetLocalizationIdentity(localizationNamespace, key);
            if (string.IsNullOrWhiteSpace(identity)) legacy[key] = infoEntry;
            else existing[identity] = infoEntry;
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static void WriteCheckpoint(
        string auditBase,
        IReadOnlyList<TranslatedAuditRow> translated,
        IReadOnlyList<ReviewComparisonRow> comparisons,
        IReadOnlyList<TokenWarningRow> warnings,
        int completedBatches,
        int totalBatches,
        bool complete)
    {
        WriteJson(auditBase + "-translated.json", translated);
        WriteJson(auditBase + "-comparison.json", comparisons);
        WriteComparisonCsv(auditBase + "-comparison.csv", comparisons);
        WriteJson(auditBase + "-token-warnings.json", warnings);
        WriteJson(auditBase + "-progress.json", new[]
        {
            new { version = 1, completedBatches, totalBatches, complete, updatedAt = DateTimeOffset.UtcNow.ToString("O") }
        });
    }

    private static void WriteJson<T>(string path, T value) => AtomicFile.WriteUtf8(path, JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteComparisonCsv(string path, IEnumerable<ReviewComparisonRow> rows)
    {
        static string Escape(string? value)
        {
            var text = value ?? string.Empty;
            return '"' + text.Replace("\"", "\"\"") + '"';
        }
        var builder = new StringBuilder();
        var columns = new[]
        {
            "id", "key", "kind", "defClass", "node", "field", "target", "source", "existing", "candidate",
            "existingOrigin", "translationOrigin", "translationUpdatedAt", "rmkIdentifier", "rmkHistoricalSource",
            "rmkCurrentSource", "rmkSourceChanged", "rmkWorkbook", "existingPresent", "existingHasKorean",
            "candidateHasKorean", "existingSameAsSource", "candidateSameAsSource", "candidateBlank", "missingTokens",
            "unexpectedTokens", "tokenCountMismatches", "grammarPrefixMoved", "pathologicalCandidate", "invalidKoreanParticles", "safeToApply"
        };
        builder.AppendLine(string.Join(',', columns.Select(Escape)));
        foreach (var row in rows)
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
            builder.AppendLine(string.Join(',', values.Select(value => Escape(Convert.ToString(value, CultureInfo.InvariantCulture)))));
        }
        AtomicFile.WriteUtf8(path, builder.ToString());
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

    private static void ValidateOptions(TranslationEngineOptions options)
    {
        if (!Directory.Exists(options.ModRoot)) throw new DirectoryNotFoundException($"Mod root was not found: {options.ModRoot}");
        if (!SafeSegmentRegex().IsMatch(options.LanguageFolderName)) throw new InvalidDataException("LanguageFolderName is invalid.");
        if (!SafeSegmentRegex().IsMatch(options.OutputFilePrefix)) throw new InvalidDataException("OutputFilePrefix is invalid.");
        if (options.BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(options.BatchSize));
        if (options.MaxRetries <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxRetries));
        if (!options.SourceOnly && TranslationApiClient.ParseKeys(options.ApiKeys).Count > 0)
        {
            TranslationApiClient.ValidateEndpoint(options.ProviderSettings.Url, options.AllowInsecureLoopback);
            if (string.IsNullOrWhiteSpace(options.ProviderSettings.Model) || options.ProviderSettings.Model.Length > 200 || ControlCharacterRegex().IsMatch(options.ProviderSettings.Model))
                throw new InvalidDataException("Model is invalid.");
        }
    }

    private static bool SourceTextEquals(string left, string right)
    {
        static string Clean(string value) => TrailingWhitespaceRegex().Replace(Normalize(value).Trim(), string.Empty);
        return Clean(left).Equals(Clean(right), StringComparison.Ordinal);
    }

    private static string GetRmkIdentifier(SourceEntry entry) => $"{entry.LocalizationNamespace}+{entry.Key}";
    private static string ExtractNamespaceFromRmkIdentifier(string identifier) => identifier.Contains('+') ? identifier[..identifier.IndexOf('+')] : string.Empty;
    private static bool ContainsKorean(string text) => text.Any(character => character is >= '\uac00' and <= '\ud7af');
    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    private static int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 3.0);
    private static string Compact(string? text)
    {
        var value = WhitespaceRegex().Replace(text ?? "unknown error", " ").Trim();
        return value.Length > 320 ? value[..320] + "..." : value;
    }
    private static string SanitizeFileSegment(string value) => InvalidFileSegmentRegex().Replace(value, "-");

    private sealed record ExistingInfo(bool Present, string Text, string Origin, string TranslationUpdatedAt, string TargetRelativePath)
    {
        public static ExistingInfo Empty { get; } = new(false, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private sealed record RmkHistory(string Path, string SourceLanguage, RimWorldTranslatorRmkHistoryData? Data);

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
    [GeneratedRegex("[ \\t\\u00A0]+(?=\\n|$)", RegexOptions.CultureInvariant)] private static partial Regex TrailingWhitespaceRegex();
    [GeneratedRegex("[\\r\\n\\t\\s]+", RegexOptions.CultureInvariant)] private static partial Regex WhitespaceRegex();
}
