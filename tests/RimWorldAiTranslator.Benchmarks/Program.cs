using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Benchmarks;

internal static class Program
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private const int DefaultRows = 5_000;
    private const int DefaultIterations = 5;
    private const int ReviewOpenCloseCycles = 20;

    private static int Main(string[] args)
    {
        var rows = ReadPositiveArgument(args, "--rows", DefaultRows);
        var iterations = ReadPositiveArgument(args, "--iterations", DefaultIterations);
        if (rows < DefaultRows)
            throw new ArgumentException($"Phase 08 evidence requires at least {DefaultRows} rows.", nameof(args));
        if (iterations < DefaultIterations)
            throw new ArgumentException($"Phase 08 evidence requires at least {DefaultIterations} iterations.", nameof(args));
        var tempRoot = Path.Combine(Path.GetTempPath(), "RimWorldAiTranslator-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var modRoot = CreateFixture(tempRoot, rows);
            var extractor = new SourceExtractor();

            var coldExtraction = MeasureOnce(() => extractor.Extract(modRoot, "English"));
            RequireRowCount(coldExtraction.Result.Entries.Count, rows, "cold extraction");
            var extraction = coldExtraction.Result;
            var fixtureIdentitySha256 = ComputeFixtureIdentity(extraction.Entries);

            var extractionSamples = Measure(iterations, () =>
            {
                var result = extractor.Extract(modRoot, "English");
                RequireRowCount(result.Entries.Count, rows, "warm extraction");
            });

            var reviewRoot = CreateReviewFixture(tempRoot, extraction.Entries);
            var reviewService = new ReviewWorkspaceService(new AtomicJsonStore(), extractor);
            PrepareApprovedReviewFixture(reviewService, reviewRoot);
            var coldReviewLoad = MeasureOnce(() => reviewService.Load(reviewRoot));
            RequireRowCount(coldReviewLoad.Result.Items.Count, rows, "cold review load");

            var loadSamples = Measure(iterations, () =>
            {
                var workspace = reviewService.Load(reviewRoot);
                RequireRowCount(workspace.Items.Count, rows, "warm review load");
            });

            var loaded = coldReviewLoad.Result;
            var querySamples = Measure(iterations * 20, () =>
            {
                var result = reviewService.Query(loaded, new ReviewQuery("Term04999", ReviewSearchField.Key));
                if (result.Count != 1) throw new InvalidOperationException($"Expected one search result, got {result.Count}.");
            });

            var statusSamples = Measure(iterations * 20, () =>
            {
                var result = reviewService.Query(loaded, new ReviewQuery(Status: ReviewStatusFilter.Approved));
                RequireRowCount(result.Count, rows, "approved status query");
            });

            var sortSamples = Measure(iterations, () =>
            {
                var result = reviewService.Query(loaded, new ReviewQuery(Sort: ReviewSortMode.Key));
                RequireRowCount(result.Count, rows, "key sort");
                if (!result[0].Row.Key.Equals("Benchmark.Term00000", StringComparison.Ordinal))
                    throw new InvalidOperationException("The sorted review result changed its first key.");
            });
            var statusChangeSamples = MeasureStatusChanges(reviewService, loaded, iterations);
            var saveMeasurements = MeasureReviewSaves(reviewService, loaded, iterations);
            var projectLifecycle = MeasureProjectLifecycle(tempRoot, modRoot, iterations);
            var applyLifecycle = MeasureApplyLifecycle(modRoot, reviewRoot, extractor, rows, iterations);
            var rmkFullExport = MeasureFullRmkExport(tempRoot, modRoot, reviewRoot, extractor, rows, iterations);

            var preCancelledExtractionMs = MeasurePreCancelledExtraction(extractor, modRoot);
            var memoryCycles = MeasureReviewOpenCloseCycles(reviewService, reviewRoot, rows);
            var rmkCore = MeasureRmkCore(tempRoot, extraction.Entries, iterations, fixtureIdentitySha256);
            var activeCancellationSamples = MeasureActiveCancellation(
                tempRoot,
                modRoot,
                extractor,
                iterations);
            var measurementOverheadSamples = Measure(iterations * 20, static () => { });

            var gate = Phase08PerformanceEvidence.Evaluate(
                args,
                new Phase08CoreGateInput(
                    rows,
                    iterations,
                    fixtureIdentitySha256,
                    RoundSamples(querySamples),
                    RoundSamples(statusSamples),
                    RoundSamples(activeCancellationSamples),
                    memoryCycles.StabilizedGrowthPercent.WorkingSetPercent,
                    memoryCycles.StabilizedGrowthPercent.PrivateMemoryPercent,
                    memoryCycles.WithinThreshold));

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var report = new
            {
                schemaVersion = "phase08-performance-evidence-v2",
                result = gate.Result,
                evidenceCompleteness = gate.EvidenceCompleteness,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                machine = "redacted",
                framework = Environment.Version.ToString(),
                measurementTool = new
                {
                    assembly = typeof(Program).Assembly.GetName().Name,
                    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                    schema = "phase08-performance-evidence-v2"
                },
                rows,
                iterations,
                extractionMs = Summary(extractionSamples),
                reviewLoadMs = Summary(loadSamples),
                keySearchMs = Summary(querySamples),
                statusFilterMs = Summary(statusSamples),
                keySortMs = Summary(sortSamples),
                statusChangeMs = Summary(statusChangeSamples),
                preCancelledExtractionMs,
                workingSetMb = RoundMegabytes(process.WorkingSet64),
                privateMemoryMb = RoundMegabytes(process.PrivateMemorySize64),
                fixture = new
                {
                    schema = "rimworld-ai-translator-benchmark-fixture-v1",
                    identitySha256 = fixtureIdentitySha256,
                    rows,
                    synthetic = true
                },
                measurement = new
                {
                    stopwatch = "System.Diagnostics.Stopwatch",
                    emptyActionOverheadMs = Summary(measurementOverheadSamples),
                    emptyActionOverheadRawSamplesMs = RoundSamples(measurementOverheadSamples),
                    coldFirstDefinition = "First measured call before warm samples; OS caches are not flushed.",
                    memoryStabilization = "Forced full GC before each sample; growth compares medians of cycles 1-3 and 18-20.",
                    networkTransport = "In-memory HttpMessageHandler; external network requests are impossible.",
                    coreInteractionDefinition = "Key search, status filter, key sort, and status mutation call ReviewWorkspaceService without WinForms dispatch; actual UI latency is reported separately.",
                    uiDefinition = "UI search/status/feedback evidence is accepted only from five or more separate UiHarness reports. Core query timings are not substituted for UI latency.",
                    comparisonPolicy = "Golden script-host and candidate C# measurements with different process, query, fixture, or operation boundaries are reference-only; no 10/20 percent relative gate is calculated.",
                    containsAbsolutePaths = false,
                    containsMachineName = false
                },
                coldFirstVsWarmMs = new
                {
                    extraction = new
                    {
                        coldFirst = RoundMilliseconds(coldExtraction.ElapsedMs),
                        warm = Summary(extractionSamples)
                    },
                    reviewLoad = new
                    {
                        coldFirst = RoundMilliseconds(coldReviewLoad.ElapsedMs),
                        warm = Summary(loadSamples)
                    }
                },
                rawSamplesMs = new
                {
                    extractionWarm = RoundSamples(extractionSamples),
                    reviewLoadWarm = RoundSamples(loadSamples),
                    keySearch = RoundSamples(querySamples),
                    statusFilter = RoundSamples(statusSamples),
                    keySort = RoundSamples(sortSamples),
                    statusChange = RoundSamples(statusChangeSamples),
                    reviewFullSaveWarm = saveMeasurements.FullSaveWarmSamplesMs,
                    reviewAutosave = saveMeasurements.AutoSaveSamplesMs,
                    preCancelledExtraction = new[] { preCancelledExtractionMs },
                    activeCancellation = RoundSamples(activeCancellationSamples)
                },
                activeCancellationMs = Summary(activeCancellationSamples),
                reviewOpenCloseMemory = memoryCycles,
                projectLifecycle,
                reviewSave = saveMeasurements,
                applyLifecycle,
                rmkCore,
                rmkFullExport,
                phase08Gate = gate
            };

            var reportJson = JsonSerializer.Serialize(report, ReportJsonOptions);
            WriteReport(args, reportJson);
            if (gate.AutomatedGateResult.Equals("FAIL", StringComparison.Ordinal)) return 1;
            if (HasArgument(args, "--require-complete-phase08")
                && gate.EvidenceCompleteness.Equals("BLOCKED", StringComparison.Ordinal)) return 2;
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var failure = new
            {
                result = "FAIL",
                errorType = exception.GetType().Name,
                target = exception.TargetSite is null
                    ? "unknown"
                    : $"{exception.TargetSite.DeclaringType?.Name}.{exception.TargetSite.Name}",
                containsExceptionMessage = false,
                containsAbsolutePath = false
            };
            Console.Error.WriteLine(JsonSerializer.Serialize(failure, ReportJsonOptions));
            return 1;
        }
        finally
        {
            var fullRoot = Path.GetFullPath(tempRoot);
            var tempBase = Path.GetFullPath(Path.GetTempPath());
            if (fullRoot.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullRoot).StartsWith("RimWorldAiTranslator-benchmark-", StringComparison.Ordinal))
            {
                try
                {
                    Directory.Delete(fullRoot, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Benchmark fixture cleanup skipped ({exception.GetType().Name}).");
                }
            }
        }
    }

    private static string CreateFixture(string root, int rows)
    {
        var modRoot = Path.Combine(root, "mod");
        var keyedRoot = Path.Combine(modRoot, "Languages", "English", "Keyed");
        Directory.CreateDirectory(keyedRoot);
        var file = Path.Combine(keyedRoot, "Benchmark.xml");
        var settings = new XmlWriterSettings { Indent = false, Encoding = new UTF8Encoding(false) };
        using var writer = XmlWriter.Create(file, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("LanguageData");
        for (var index = 0; index < rows; index++)
        {
            writer.WriteElementString($"Benchmark.Term{index:D5}", $"Benchmark source text {index:D5}");
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
        return modRoot;
    }

    private static string CreateReviewFixture(string root, IReadOnlyList<SourceEntry> entries)
    {
        var reviewRoot = Path.Combine(root, "review");
        var target = Path.Combine(reviewRoot, "Languages", "Korean", "Keyed", "Benchmark.xml");
        var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
        Directory.CreateDirectory(auditRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var rows = entries.Select(entry => new ReviewComparisonRow
        {
            Id = entry.Id,
            Key = entry.Key,
            Kind = entry.Kind,
            DefClass = entry.TypeName,
            Node = entry.Key,
            Field = entry.Field,
            Target = target,
            Source = entry.Text,
            Candidate = $"합성 번역 {entry.Key}",
            CandidateBlank = false,
            SafeToApply = true
        }).ToArray();
        File.WriteAllText(
            Path.Combine(auditRoot, "benchmark-comparison.json"),
            JsonSerializer.Serialize(rows),
            new UTF8Encoding(false));
        return reviewRoot;
    }

    private static void PrepareApprovedReviewFixture(ReviewWorkspaceService service, string reviewRoot)
    {
        var workspace = service.Load(reviewRoot);
        foreach (var item in workspace.Items)
        {
            service.UpdateTranslation(
                workspace,
                item,
                $"합성 번역 {item.OriginalIndex:D5}",
                string.Empty,
                editedByUser: true,
                translationOrigin: "local");
            service.SetStatus(workspace, item, ReviewStatuses.Approved);
        }
        service.Save(workspace);
        if (workspace.Dirty)
            throw new InvalidOperationException("The synthetic review fixture remained dirty after preparation.");
    }

    private static double[] MeasureStatusChanges(
        ReviewWorkspaceService service,
        ReviewWorkspace workspace,
        int iterations)
    {
        var item = workspace.Items[0];
        var samples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            samples[iteration] = MeasureAction(() => service.SetStatus(workspace, item, ReviewStatuses.Pending));
            service.SetStatus(workspace, item, ReviewStatuses.Approved);
        }
        return samples;
    }

    private static ReviewSaveResult MeasureReviewSaves(
        ReviewWorkspaceService service,
        ReviewWorkspace workspace,
        int iterations)
    {
        var coldFirst = MeasureAction(() => service.Save(workspace));
        var warmSamples = Measure(iterations, () => service.Save(workspace));
        var decisionPath = ReviewRepository.GetDecisionPath(workspace.ReviewRoot);
        if (!File.Exists(decisionPath))
            throw new InvalidOperationException("The measured full review save did not preserve its decision file.");

        var autosaveSamples = new double[iterations];
        using var coordinator = new ReviewSaveCoordinator(service);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var item = workspace.Items[iteration % workspace.Items.Count];
            service.UpdateTranslation(
                workspace,
                item,
                item.Decision.Text,
                $"synthetic autosave revision {iteration:D2}",
                editedByUser: false);
            autosaveSamples[iteration] = MeasureAction(
                () => coordinator.SaveAsync(workspace).GetAwaiter().GetResult());
            if (workspace.Dirty)
                throw new InvalidOperationException("The measured autosave returned while the workspace was dirty.");
        }

        return new ReviewSaveResult(
            RoundMilliseconds(coldFirst),
            Summary(warmSamples),
            RoundSamples(warmSamples),
            Summary(autosaveSamples),
            RoundSamples(autosaveSamples));
    }

    private static ProjectLifecycleResult MeasureProjectLifecycle(
        string tempRoot,
        string modRoot,
        int iterations)
    {
        var discoveryRoot = tempRoot;
        File.WriteAllText(
            Path.Combine(discoveryRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
            RimWorldModDiscoveryService.IsolationMarkerContent + Environment.NewLine,
            new UTF8Encoding(false));
        var discovery = RimWorldModDiscoveryService.CreateIsolated(discoveryRoot);
        var mod = new RimWorldModInfo(
            "Synthetic benchmark",
            "Synthetic benchmark",
            modRoot,
            "Folder",
            Path.GetFileName(modRoot),
            "synthetic.phase08.benchmark",
            string.Empty,
            "synthetic benchmark");
        var createSamples = new double[iterations];
        ProjectService? openService = null;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var paths = new AppDataPaths(Path.Combine(tempRoot, "project-lifecycle", $"create-{iteration:D2}"));
            paths.EnsureExists();
            var repository = new ProjectRepository(new AtomicJsonStore(), paths);
            var service = new ProjectService(repository, discovery, new ProjectCleanupService());
            var document = new ProjectStoreDocument();
            createSamples[iteration] = MeasureAction(() => service.Upsert(document, mod, "English"));
            if (document.Projects.Count != 1 || !File.Exists(paths.Projects))
                throw new InvalidOperationException("The measured project create did not persist exactly one project.");
            openService = service;
        }

        if (openService is null)
            throw new InvalidOperationException("No project lifecycle samples were prepared.");
        var coldOpen = MeasureOnce(() => openService.Load());
        RequireRowCount(coldOpen.Result.Projects.Count, 1, "cold project open");
        var openSamples = Measure(iterations, () =>
        {
            var result = openService.Load();
            RequireRowCount(result.Projects.Count, 1, "warm project open");
        });
        return new ProjectLifecycleResult(
            "Create uses a fresh isolated app-data root for every raw sample.",
            Summary(createSamples),
            RoundSamples(createSamples),
            RoundMilliseconds(coldOpen.ElapsedMs),
            Summary(openSamples),
            RoundSamples(openSamples));
    }

    private static ApplyLifecycleResult MeasureApplyLifecycle(
        string modRoot,
        string reviewRoot,
        SourceExtractor extractor,
        int expectedRows,
        int iterations)
    {
        var service = new ReviewApplyService(
            new AtomicJsonStore(),
            new LanguageFileService(),
            extractor);
        var dryRunOptions = new ReviewApplyOptions
        {
            ModRoot = modRoot,
            ReviewRoot = reviewRoot,
            SourceKind = "Folder",
            SourceLanguageFolder = "English",
            LanguageFolderName = "Korean",
            Overwrite = true,
            DryRun = true,
            ApplyStatus = ReviewApplyStatus.ApprovedOnly
        };
        var applyOptions = new ReviewApplyOptions
        {
            ModRoot = modRoot,
            ReviewRoot = reviewRoot,
            SourceKind = "Folder",
            SourceLanguageFolder = "English",
            LanguageFolderName = "Korean",
            Overwrite = true,
            DryRun = false,
            ApplyStatus = ReviewApplyStatus.ApprovedOnly
        };

        var dryRunSamples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var measured = MeasureOnce(() => service.Apply(dryRunOptions));
            ValidateApplyResult(measured.Result, expectedRows, "DryRun");
            dryRunSamples[iteration] = measured.ElapsedMs;
        }

        var coldApply = MeasureOnce(() => service.Apply(applyOptions));
        ValidateApplyResult(coldApply.Result, expectedRows, "cold apply");
        var applySamples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var measured = MeasureOnce(() => service.Apply(applyOptions));
            ValidateApplyResult(measured.Result, expectedRows, "warm apply");
            applySamples[iteration] = measured.ElapsedMs;
        }

        var outputPath = Path.Combine(modRoot, "Languages", "Korean", "Keyed", "Benchmark.xml");
        if (!File.Exists(outputPath) || !File.Exists(outputPath + ".bak"))
            throw new InvalidOperationException("The measured Apply path did not establish its output and backup.");
        var rollbackSamples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var outputBefore = File.ReadAllBytes(outputPath);
            var backupBefore = File.ReadAllBytes(outputPath + ".bak");
            var watch = Stopwatch.StartNew();
            try
            {
                FileTransaction.Execute(
                    [outputPath, outputPath + ".bak"],
                    () =>
                    {
                        File.WriteAllText(outputPath, "synthetic interrupted output", new UTF8Encoding(false));
                        File.WriteAllText(outputPath + ".bak", "synthetic interrupted backup", new UTF8Encoding(false));
                        throw new InvalidOperationException("synthetic rollback trigger");
                    },
                    "synthetic Apply rollback benchmark");
                throw new InvalidOperationException("The synthetic rollback transaction completed unexpectedly.");
            }
            catch (IOException)
            {
                watch.Stop();
            }
            if (!File.ReadAllBytes(outputPath).SequenceEqual(outputBefore)
                || !File.ReadAllBytes(outputPath + ".bak").SequenceEqual(backupBefore))
            {
                throw new InvalidOperationException("The measured rollback did not restore output and backup bytes.");
            }
            rollbackSamples[iteration] = watch.Elapsed.TotalMilliseconds;
        }

        return new ApplyLifecycleResult(
            Summary(dryRunSamples),
            RoundSamples(dryRunSamples),
            RoundMilliseconds(coldApply.ElapsedMs),
            Summary(applySamples),
            RoundSamples(applySamples),
            "Public FileTransaction rollback over the Apply output and backup; this is not an end-user rollback command.",
            Summary(rollbackSamples),
            RoundSamples(rollbackSamples));
    }

    private static void ValidateApplyResult(ReviewApplyResult result, int expectedRows, string operation)
    {
        if (result.AppliedEntries != expectedRows
            || result.ApprovedRows != expectedRows
            || result.SkippedUnsafe != 0
            || result.SkippedSourceChanged != 0)
        {
            throw new InvalidOperationException($"The synthetic {operation} result changed its eligible row set.");
        }
    }

    private static FullRmkExportResult MeasureFullRmkExport(
        string tempRoot,
        string modRoot,
        string reviewRoot,
        SourceExtractor extractor,
        int expectedRows,
        int iterations)
    {
        var workspaceRoot = CreateRmkWorkspace(tempRoot, out var entryRoot);
        var workbookPath = Path.Combine(entryRoot, "history.xlsx");
        var service = new RmkExportService(
            new AtomicJsonStore(),
            new LanguageFileService(),
            extractor);
        var options = new RmkExportOptions
        {
            RmkWorkspaceRoot = workspaceRoot,
            RmkEntryRoot = entryRoot,
            ReviewRoot = reviewRoot,
            ModRoot = modRoot,
            ReviewLanguageFolderName = "Korean",
            RmkLanguageFolderName = "Korean",
            SourceLanguage = "English",
            WorkbookPath = workbookPath,
            ApplyStatus = ReviewApplyStatus.ApprovedOnly,
            Overwrite = true
        };

        var coldExport = MeasureOnce(() => service.Export(options));
        ValidateRmkExport(coldExport.Result, expectedRows);
        var exportSamples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var measured = MeasureOnce(() => service.Export(options));
            ValidateRmkExport(measured.Result, expectedRows);
            exportSamples[iteration] = measured.ElapsedMs;
        }
        return new FullRmkExportResult(
            "In-process C# RmkExportService including review/source validation, XML merge, XLSX update, backup, and atomic transaction.",
            RoundMilliseconds(coldExport.ElapsedMs),
            Summary(exportSamples),
            RoundSamples(exportSamples),
            new FileInfo(workbookPath).Length);
    }

    private static string CreateRmkWorkspace(string root, out string entryRoot)
    {
        var workspaceRoot = Path.Combine(root, "rmk-full-export");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "Data"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, ".git"));
        File.WriteAllText(
            Path.Combine(workspaceRoot, ".git", "HEAD"),
            "ref: refs/heads/bus\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(workspaceRoot, "ModList.tsv"), string.Empty, new UTF8Encoding(false));
        entryRoot = Path.Combine(workspaceRoot, "Data", "Synthetic benchmark - 8080");
        Directory.CreateDirectory(entryRoot);
        return workspaceRoot;
    }

    private static void ValidateRmkExport(RmkExportResult result, int expectedRows)
    {
        if (result.EligibleEntries != expectedRows
            || result.WorkbookRows != expectedRows
            || result.SkippedUnsafe != 0
            || result.SkippedSourceChanged != 0)
        {
            throw new InvalidOperationException("The synthetic full RMK export changed its eligible row set.");
        }
        if (!File.Exists(result.WorkbookPath))
            throw new InvalidOperationException("The synthetic full RMK export did not preserve its workbook.");
    }

    private static double MeasurePreCancelledExtraction(SourceExtractor extractor, string modRoot)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var watch = Stopwatch.StartNew();
        try
        {
            extractor.Extract(modRoot, "English", cancellationToken: cancellation.Token);
            throw new InvalidOperationException("A pre-cancelled extraction completed unexpectedly.");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return RoundMilliseconds(watch.Elapsed.TotalMilliseconds);
        }
    }

    private static double[] MeasureActiveCancellation(
        string tempRoot,
        string modRoot,
        SourceExtractor extractor,
        int iterations)
    {
        var samples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            using var handler = new BlockingSyntheticHandler();
            var engine = new TranslationEngine(
                tempRoot,
                extractor,
                apiClientFactory: keys => new TranslationApiClient(keys, handler));
            using var cancellation = new CancellationTokenSource();
            var options = new TranslationEngineOptions
            {
                ModRoot = modRoot,
                ApiKeys = ["synthetic-benchmark-key"],
                SourceLanguageFolder = "English",
                ReviewOnly = true,
                ReviewRoot = Path.Combine(tempRoot, "active-cancellation", $"run-{iteration:D2}"),
                BatchSize = 40,
                MaxInputCharactersPerBatch = 100_000,
                MaxInputTokensPerBatch = 100_000,
                RequestsPerMinutePerKey = 0,
                InputTokensPerMinutePerKey = 0,
                DailyTokenBudgetPerKey = 0,
                MaxRetries = 1,
                MaxProviderRequestsPerRun = 2,
                Timeout = TimeSpan.FromSeconds(30),
                AllowInsecureLoopback = true,
                Provider = ApiProviderCatalog.Get("Custom"),
                ProviderSettings = new ApiProviderSettings
                {
                    Name = "Synthetic benchmark",
                    Url = "http://127.0.0.1:1/v1/chat/completions",
                    Model = "synthetic-benchmark-model",
                    Temperature = 0
                }
            };

            var run = engine.RunAsync(options, cancellationToken: cancellation.Token);
            if (!handler.RequestStarted.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The synthetic provider request did not start before the benchmark timeout.");

            var watch = Stopwatch.StartNew();
            cancellation.Cancel();
            try
            {
                run.GetAwaiter().GetResult();
                throw new InvalidOperationException("An actively cancelled translation completed unexpectedly.");
            }
            catch (TranslationRunCanceledException exception)
            {
                watch.Stop();
                if (!exception.PartialResult.Cancelled || handler.RequestCount != 1)
                    throw new InvalidOperationException("Active cancellation did not preserve the expected synthetic cancellation contract.");
                samples[iteration] = watch.Elapsed.TotalMilliseconds;
            }
        }
        return samples;
    }

    private static MemoryCycleResult MeasureReviewOpenCloseCycles(
        ReviewWorkspaceService reviewService,
        string reviewRoot,
        int expectedRows)
    {
        var samples = new MemoryCycleSample[ReviewOpenCloseCycles];
        var process = Process.GetCurrentProcess();
        for (var cycle = 0; cycle < ReviewOpenCloseCycles; cycle++)
        {
            var watch = Stopwatch.StartNew();
            OpenAndReleaseReview(reviewService, reviewRoot, expectedRows);
            watch.Stop();
            StabilizeManagedMemory();
            process.Refresh();
            samples[cycle] = new MemoryCycleSample(
                cycle + 1,
                RoundMilliseconds(watch.Elapsed.TotalMilliseconds),
                RoundMegabytes(process.WorkingSet64),
                RoundMegabytes(process.PrivateMemorySize64));
        }

        var initialWorkingSet = Median(samples.Take(3).Select(sample => sample.WorkingSetMb));
        var finalWorkingSet = Median(samples.TakeLast(3).Select(sample => sample.WorkingSetMb));
        var initialPrivate = Median(samples.Take(3).Select(sample => sample.PrivateMemoryMb));
        var finalPrivate = Median(samples.TakeLast(3).Select(sample => sample.PrivateMemoryMb));
        var workingGrowth = GrowthPercent(initialWorkingSet, finalWorkingSet);
        var privateGrowth = GrowthPercent(initialPrivate, finalPrivate);
        return new MemoryCycleResult(
            ReviewOpenCloseCycles,
            "ReviewWorkspaceService.Load followed by reference release and forced full GC",
            samples,
            new MemoryPair(initialWorkingSet, initialPrivate),
            new MemoryPair(finalWorkingSet, finalPrivate),
            new MemoryGrowth(workingGrowth, privateGrowth),
            15,
            workingGrowth <= 15 && privateGrowth <= 15);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpenAndReleaseReview(
        ReviewWorkspaceService reviewService,
        string reviewRoot,
        int expectedRows)
    {
        var workspace = reviewService.Load(reviewRoot);
        RequireRowCount(workspace.Items.Count, expectedRows, "review open/close cycle");
    }

    private static void StabilizeManagedMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Thread.Sleep(25);
    }

    private static RmkCoreResult MeasureRmkCore(
        string tempRoot,
        IReadOnlyList<SourceEntry> entries,
        int iterations,
        string fixtureIdentitySha256)
    {
        var workbookRoot = Path.Combine(tempRoot, "rmk-core");
        Directory.CreateDirectory(workbookRoot);
        var workbook = Path.Combine(workbookRoot, "history.xlsx");
        var rows = entries.Select((entry, index) => new RimWorldTranslatorRmkHistoryRow
        {
            Identifier = $"{entry.LocalizationNamespace}+{entry.Key}",
            ClassName = entry.LocalizationNamespace,
            Key = entry.Key,
            RequiredMods = index % 3 == 0 ? "synthetic.dependency" : string.Empty,
            Source = entry.Text,
            Translation = $"Synthetic translation {index:D5}"
        }).ToArray();

        var initialWriteMs = MeasureAction(() => RimWorldTranslatorRmkXlsxWriter.Write(workbook, rows, "English"));
        ValidateRmkWorkbook(workbook, rows.Length, rows[0].Identifier, rows[0].Translation);

        var firstReadMs = MeasureValidatedRmkRead(workbook, rows.Length);
        var readWarmSamples = Measure(iterations, () => ValidateRmkRead(RimWorldTranslatorRmkXlsxReader.Read(workbook), rows.Length));

        PrepareRmkUpdate(rows, 0);
        var firstUpdateMs = MeasureAction(() => RimWorldTranslatorRmkXlsxWriter.Write(workbook, rows, "English"));
        ValidateRmkWorkbook(workbook, rows.Length, rows[0].Identifier, rows[0].Translation);

        var updateWarmSamples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            PrepareRmkUpdate(rows, iteration + 1);
            updateWarmSamples[iteration] = MeasureAction(() => RimWorldTranslatorRmkXlsxWriter.Write(workbook, rows, "English"));
            ValidateRmkWorkbook(workbook, rows.Length, rows[0].Identifier, rows[0].Translation);
        }

        return new RmkCoreResult(
            rows.Length,
            fixtureIdentitySha256,
            new FileInfo(workbook).Length,
            RoundMilliseconds(initialWriteMs),
            RoundMilliseconds(firstReadMs),
            Summary(readWarmSamples),
            RoundSamples(readWarmSamples),
            RoundMilliseconds(firstUpdateMs),
            Summary(updateWarmSamples),
            RoundSamples(updateWarmSamples));
    }

    private static void PrepareRmkUpdate(IReadOnlyList<RimWorldTranslatorRmkHistoryRow> rows, int version)
    {
        for (var index = 0; index < rows.Count; index++)
            rows[index].Translation = $"Synthetic update {version:D2}-{index:D5}";
    }

    private static double MeasureValidatedRmkRead(string workbook, int expectedRows)
    {
        var watch = Stopwatch.StartNew();
        var data = RimWorldTranslatorRmkXlsxReader.Read(workbook);
        watch.Stop();
        ValidateRmkRead(data, expectedRows);
        return watch.Elapsed.TotalMilliseconds;
    }

    private static void ValidateRmkWorkbook(
        string workbook,
        int expectedRows,
        string sentinelIdentifier,
        string sentinelTranslation)
    {
        var data = RimWorldTranslatorRmkXlsxReader.Read(workbook);
        ValidateRmkRead(data, expectedRows);
        if (!data.Map.TryGetValue(sentinelIdentifier, out var sentinel)
            || !sentinel.Translation.Equals(sentinelTranslation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The synthetic RMK update did not round-trip its sentinel translation.");
        }
    }

    private static void ValidateRmkRead(RimWorldTranslatorRmkHistoryData data, int expectedRows)
    {
        RequireRowCount(data.Rows.Count, expectedRows, "RMK workbook read");
        if (!data.SourceLanguage.Equals("English", StringComparison.Ordinal))
            throw new InvalidOperationException("The synthetic RMK workbook changed its source language.");
    }

    private static string ComputeFixtureIdentity(IReadOnlyList<SourceEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashValue(hash, "rimworld-ai-translator-benchmark-fixture-v1");
        Span<byte> countBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(countBytes, entries.Count);
        hash.AppendData(countBytes);
        foreach (var entry in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            AppendHashValue(hash, entry.Key);
            AppendHashValue(hash, entry.Text);
            AppendHashValue(hash, entry.Kind);
            AppendHashValue(hash, entry.TypeName);
            AppendHashValue(hash, entry.Field);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHashValue(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static double[] Measure(int count, Action action)
    {
        var samples = new double[count];
        for (var index = 0; index < count; index++) samples[index] = MeasureAction(action);
        return samples;
    }

    private static double MeasureAction(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static Measured<T> MeasureOnce<T>(Func<T> action)
    {
        var watch = Stopwatch.StartNew();
        var result = action();
        watch.Stop();
        return new Measured<T>(result, watch.Elapsed.TotalMilliseconds);
    }

    private static MeasurementSummary Summary(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) throw new ArgumentException("At least one measurement is required.", nameof(values));
        return new MeasurementSummary(
            RoundMilliseconds(Percentile(ordered, 0.5)),
            RoundMilliseconds(Percentile(ordered, 0.95)),
            RoundMilliseconds(ordered[^1]));
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 1) return ordered[0];
        var position = (ordered.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return ordered[lower];
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * (position - lower));
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return RoundMilliseconds(Percentile(ordered, 0.5));
    }

    private static double GrowthPercent(double initial, double final) =>
        initial <= 0 ? 0 : RoundMilliseconds(((final - initial) / initial) * 100);

    private static double[] RoundSamples(IEnumerable<double> values) =>
        values.Select(RoundMilliseconds).ToArray();

    private static double RoundMilliseconds(double value) => Math.Round(value, 3);

    private static double RoundMegabytes(long bytes) => Math.Round(bytes / 1024d / 1024d, 3);

    private static void RequireRowCount(int actual, int expected, string operation)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Expected {expected} rows for {operation}, got {actual}.");
    }

    private static int ReadPositiveArgument(IReadOnlyList<string> args, string name, int fallback)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(args[index + 1], out var value) && value > 0) return value;
            throw new ArgumentException($"{name} must be a positive integer.");
        }
        return fallback;
    }

    private static bool HasArgument(IEnumerable<string> args, string name) =>
        args.Any(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void WriteReport(IReadOnlyList<string> args, string json)
    {
        var output = ReadOptionalArgument(args, "--output");
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(json);
            return;
        }
        var fullPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        AtomicFile.WriteUtf8(fullPath, json + Environment.NewLine, keepBackup: false);
    }

    private static string ReadOptionalArgument(IReadOnlyList<string> args, string name)
    {
        var result = string.Empty;
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(result))
                throw new ArgumentException($"{name} may be supplied only once.");
            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} requires a value.");
            result = args[++index];
        }
        return result;
    }

    private sealed class BlockingSyntheticHandler : HttpMessageHandler
    {
        private int requestCount;

        public ManualResetEventSlim RequestStarted { get; } = new(false);
        public int RequestCount => Volatile.Read(ref requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            Interlocked.Increment(ref requestCount);
            RequestStarted.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) RequestStarted.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed record Measured<T>(T Result, double ElapsedMs);
    private sealed record MeasurementSummary(double Median, double P95, double Max);
    private sealed record ReviewSaveResult(
        double ColdFirstMs,
        MeasurementSummary FullSaveWarmMs,
        IReadOnlyList<double> FullSaveWarmSamplesMs,
        MeasurementSummary AutoSaveMs,
        IReadOnlyList<double> AutoSaveSamplesMs);
    private sealed record ProjectLifecycleResult(
        string CreateDefinition,
        MeasurementSummary CreateMs,
        IReadOnlyList<double> CreateSamplesMs,
        double ColdFirstOpenMs,
        MeasurementSummary WarmOpenMs,
        IReadOnlyList<double> WarmOpenSamplesMs);
    private sealed record ApplyLifecycleResult(
        MeasurementSummary DryRunMs,
        IReadOnlyList<double> DryRunSamplesMs,
        double ColdFirstApplyMs,
        MeasurementSummary WarmApplyMs,
        IReadOnlyList<double> WarmApplySamplesMs,
        string RollbackDefinition,
        MeasurementSummary RollbackMs,
        IReadOnlyList<double> RollbackSamplesMs);
    private sealed record FullRmkExportResult(
        string Definition,
        double ColdFirstMs,
        MeasurementSummary WarmMs,
        IReadOnlyList<double> WarmSamplesMs,
        long WorkbookBytes);
    private sealed record MemoryCycleSample(int Cycle, double ReviewLoadMs, double WorkingSetMb, double PrivateMemoryMb);
    private sealed record MemoryPair(double WorkingSetMb, double PrivateMemoryMb);
    private sealed record MemoryGrowth(double WorkingSetPercent, double PrivateMemoryPercent);
    private sealed record MemoryCycleResult(
        int Cycles,
        string Operation,
        IReadOnlyList<MemoryCycleSample> Samples,
        MemoryPair InitialStabilizedMedian,
        MemoryPair FinalStabilizedMedian,
        MemoryGrowth StabilizedGrowthPercent,
        double InvestigationThresholdPercent,
        bool WithinThreshold);
    private sealed record RmkCoreResult(
        int Rows,
        string FixtureIdentitySha256,
        long WorkbookBytes,
        double InitialWriteMs,
        double FirstReadMs,
        MeasurementSummary ReadWarmMs,
        IReadOnlyList<double> ReadWarmSamplesMs,
        double FirstUpdateMs,
        MeasurementSummary UpdateWarmMs,
        IReadOnlyList<double> UpdateWarmSamplesMs);
}
