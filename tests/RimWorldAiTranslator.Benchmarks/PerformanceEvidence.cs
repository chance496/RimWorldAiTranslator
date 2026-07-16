using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimWorldAiTranslator.Benchmarks;

internal sealed record Phase08CoreGateInput(
    int Rows,
    int Iterations,
    string FixtureIdentitySha256,
    IReadOnlyList<double> KeySearchSamplesMs,
    IReadOnlyList<double> StatusFilterSamplesMs,
    IReadOnlyList<double> ActiveCancellationSamplesMs,
    double WorkingSetGrowthPercent,
    double PrivateMemoryGrowthPercent,
    bool CoreMemoryWithinThreshold);

internal sealed record Phase08Thresholds(
    int MinimumRows,
    int MinimumSamples,
    double SearchAndStatusMedianMilliseconds,
    double RepeatedUiStallMilliseconds,
    int RepeatedUiStallCount,
    double StopFeedbackWorstMilliseconds,
    double SafeCancellationWorstMilliseconds,
    double TwentyCycleMemoryGrowthPercent);

internal sealed record Phase08SampleSummary(double Median, double P95, double Worst);

internal sealed record Phase08GateCheck(
    string Name,
    string Status,
    string Classification,
    string Criterion,
    string Actual);

internal sealed record Phase08UiEvidence(
    string Status,
    string FixtureProfile,
    int Rows,
    string FixtureDefinitionSha256,
    int Reports,
    string ReportSetSha256,
    IReadOnlyList<string> ReportSha256,
    IReadOnlyList<double> SearchSamplesMs,
    Phase08SampleSummary? SearchMs,
    IReadOnlyList<double> StatusFilterSamplesMs,
    Phase08SampleSummary? StatusFilterMs,
    IReadOnlyList<double> StopFeedbackSamplesMs,
    Phase08SampleSummary? StopFeedbackMs,
    int SearchStallsAtOrAboveThreshold,
    int StatusStallsAtOrAboveThreshold,
    string Definition);

internal sealed record Phase08PackageSizeEvidence(
    string Status,
    long PublishDirectoryBytes,
    int PublishFileCount,
    string PublishManifestSha256,
    long ZipBytes,
    string ZipSha256,
    string Definition);

internal sealed record GoldenUiReference(
    string Classification,
    string ContentSha256,
    int Rows,
    int Iterations,
    IReadOnlyList<double> SearchSamplesMs,
    Phase08SampleSummary SearchMs,
    IReadOnlyList<double> SaveSamplesMs,
    Phase08SampleSummary SaveMs);

internal sealed record GoldenRmkReference(
    string Classification,
    string ContentSha256,
    int Rows,
    int Iterations,
    double CreateMilliseconds,
    IReadOnlyList<double> UpdateSamplesMs,
    Phase08SampleSummary UpdateMs,
    long WorkbookBytes);

internal sealed record Phase08GoldenReference(
    string ComparisonPolicy,
    GoldenUiReference? Ui,
    GoldenRmkReference? Rmk);

internal sealed record Phase08GateReport(
    string Result,
    string AutomatedGateResult,
    string EvidenceCompleteness,
    Phase08Thresholds Thresholds,
    IReadOnlyList<Phase08GateCheck> Checks,
    Phase08UiEvidence Ui,
    Phase08PackageSizeEvidence PackageSize,
    Phase08GoldenReference GoldenReference,
    IReadOnlyList<string> UnmeasuredOrNonEquivalent);

internal static class Phase08PerformanceEvidence
{
    private const int MinimumRows = 5_000;
    private const int MinimumSamples = 5;
    private const double SearchAndStatusThresholdMs = 200;
    private const double StopFeedbackThresholdMs = 250;
    private const double SafeCancellationThresholdMs = 2_000;
    private const double MemoryGrowthThresholdPercent = 15;
    private const int MaximumJsonBytes = 8 * 1024 * 1024;

    public static Phase08GateReport Evaluate(
        IReadOnlyList<string> args,
        Phase08CoreGateInput core)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(core);
        var thresholds = new Phase08Thresholds(
            MinimumRows,
            MinimumSamples,
            SearchAndStatusThresholdMs,
            SearchAndStatusThresholdMs,
            RepeatedUiStallCount: 2,
            StopFeedbackThresholdMs,
            SafeCancellationThresholdMs,
            MemoryGrowthThresholdPercent);
        var checks = new List<Phase08GateCheck>();

        AddCheck(
            checks,
            "fixture.minimum-rows",
            core.Rows >= MinimumRows,
            "candidate-absolute",
            $"rows >= {MinimumRows}",
            $"rows={core.Rows}");
        AddCheck(
            checks,
            "fixture.minimum-samples",
            core.Iterations >= MinimumSamples,
            "candidate-absolute",
            $"iterations >= {MinimumSamples}",
            $"iterations={core.Iterations}");
        AddCheck(
            checks,
            "fixture.identity-sha256",
            core.FixtureIdentitySha256.Length == 64
            && core.FixtureIdentitySha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "candidate-absolute",
            "lowercase SHA-256 over the deterministic fixture identity",
            $"characters={core.FixtureIdentitySha256.Length}");
        AddSampleGate(
            checks,
            "supplemental.core-key-search",
            core.KeySearchSamplesMs,
            SearchAndStatusThresholdMs,
            useWorst: false,
            "supplemental-core-only");
        AddSampleGate(
            checks,
            "supplemental.core-status-filter",
            core.StatusFilterSamplesMs,
            SearchAndStatusThresholdMs,
            useWorst: false,
            "supplemental-core-only");
        AddSampleGate(
            checks,
            "cancellation.safe-stop",
            core.ActiveCancellationSamplesMs,
            SafeCancellationThresholdMs,
            useWorst: true,
            "candidate-absolute");
        AddCheck(
            checks,
            "supplemental.core-workspace-memory-20-cycles",
            core.CoreMemoryWithinThreshold
            && core.WorkingSetGrowthPercent <= MemoryGrowthThresholdPercent
            && core.PrivateMemoryGrowthPercent <= MemoryGrowthThresholdPercent,
            "supplemental-core-only",
            $"working-set and private-memory growth <= {MemoryGrowthThresholdPercent:0.###}%",
            $"workingSet={core.WorkingSetGrowthPercent:0.###}%;private={core.PrivateMemoryGrowthPercent:0.###}%");

        var gaps = new List<string>();
        var ui = ReadUiEvidence(args, core.Rows, thresholds, checks, gaps);
        var package = ReadPackageSizeEvidence(args, gaps);
        var golden = ReadGoldenReference(args, gaps);

        gaps.Add("Actual application cold/warm process startup and first usable screen are not instrumented by this Core benchmark.");
        gaps.Add("Idle working set of the packaged application is not measured; the top-level working-set fields belong to the benchmark process.");
        gaps.Add("The 20-cycle memory series opens/releases ReviewWorkspaceService data, not twenty MainForm open/close cycles.");
        gaps.Add("UiHarness report files do not independently prove packaged-app exit code, child-process count, or post-exit zombie absence.");
        gaps.Add("The 2-second safe-stop samples cover an active fake-HTTP translation; project load, Apply, and RMK cancellation durations are not sampled here.");
        gaps.Add("Candidate full RMK export runs in-process, while Golden RMK samples include a new script-host process; they are non-equivalent and receive no 10/20 percent ratio.");
        gaps.Add("Recovery-manifest scaling with many independently written target files is not covered by the one-XML/one-workbook 5,000-row fixture.");

        var automatedResult = checks.Any(check => check.Status.Equals("FAIL", StringComparison.Ordinal))
            ? "FAIL"
            : "PASS";
        var evidenceCompleteness = gaps.Count == 0 ? "PASS" : "BLOCKED";
        return new Phase08GateReport(
            automatedResult.Equals("FAIL", StringComparison.Ordinal)
                ? "FAIL"
                : evidenceCompleteness,
            automatedResult,
            evidenceCompleteness,
            thresholds,
            checks,
            ui,
            package,
            golden,
            gaps);
    }

    private static Phase08UiEvidence ReadUiEvidence(
        IReadOnlyList<string> args,
        int expectedRows,
        Phase08Thresholds thresholds,
        ICollection<Phase08GateCheck> checks,
        List<string> gaps)
    {
        var paths = ReadValues(args, "--ui-report");
        if (paths.Count == 0)
        {
            gaps.Add("Five independent C# UiHarness reports were not supplied; actual UI search, status-filter, and stop-feedback gates remain unmeasured.");
            AddBlockedCheck(
                checks,
                "ui.minimum-independent-reports",
                "candidate-absolute",
                $"at least {MinimumSamples} --ui-report inputs",
                "reports=0");
            return EmptyUiEvidence("BLOCKED", expectedRows);
        }

        var reports = paths.Select(ReadUiReport).ToArray();
        var hashes = reports.Select(report => report.Sha256).ToArray();
        var uniqueHashes = hashes.Distinct(StringComparer.Ordinal).Count();
        var search = reports.Select(report => report.SearchMs).ToArray();
        var status = reports.Select(report => report.StatusFilterMs).ToArray();
        var stop = reports.Select(report => report.StopFeedbackMs).ToArray();
        var allReportsPass = reports.All(report => report.Passed);
        var fixtureMatches = reports.All(report =>
            report.Rows == expectedRows
            && report.Rows >= MinimumRows
            && report.FixtureProfile.Equals("golden", StringComparison.OrdinalIgnoreCase));
        var enoughIndependentReports = reports.Length >= MinimumSamples && uniqueHashes >= MinimumSamples;
        var searchStats = Summarize(search);
        var statusStats = Summarize(status);
        var stopStats = Summarize(stop);
        var searchStalls = search.Count(sample => sample >= thresholds.RepeatedUiStallMilliseconds);
        var statusStalls = status.Count(sample => sample >= thresholds.RepeatedUiStallMilliseconds);

        AddCheck(
            checks,
            "ui.minimum-independent-reports",
            enoughIndependentReports,
            "candidate-absolute",
            $"at least {MinimumSamples} reports with distinct content hashes",
            $"reports={reports.Length};uniqueHashes={uniqueHashes}");
        AddCheck(
            checks,
            "ui.report-contract",
            allReportsPass && fixtureMatches,
            "candidate-absolute",
            "every report PASS; fixtureProfile=golden; rows match benchmark and are at least 5000",
            $"allPass={allReportsPass};fixtureMatches={fixtureMatches}");
        AddCheck(
            checks,
            "ui.search-5000",
            searchStats.Median <= thresholds.SearchAndStatusMedianMilliseconds
            && searchStalls < thresholds.RepeatedUiStallCount,
            "candidate-absolute",
            $"median <= {thresholds.SearchAndStatusMedianMilliseconds:0.###}ms and fewer than {thresholds.RepeatedUiStallCount} samples >= {thresholds.RepeatedUiStallMilliseconds:0.###}ms",
            $"median={searchStats.Median:0.###}ms;p95={searchStats.P95:0.###}ms;worst={searchStats.Worst:0.###}ms;stalls={searchStalls}");
        AddCheck(
            checks,
            "ui.status-filter-5000",
            statusStats.Median <= thresholds.SearchAndStatusMedianMilliseconds
            && statusStalls < thresholds.RepeatedUiStallCount,
            "candidate-absolute",
            $"median <= {thresholds.SearchAndStatusMedianMilliseconds:0.###}ms and fewer than {thresholds.RepeatedUiStallCount} samples >= {thresholds.RepeatedUiStallMilliseconds:0.###}ms",
            $"median={statusStats.Median:0.###}ms;p95={statusStats.P95:0.###}ms;worst={statusStats.Worst:0.###}ms;stalls={statusStalls}");
        AddCheck(
            checks,
            "ui.stop-feedback",
            stopStats.Worst <= thresholds.StopFeedbackWorstMilliseconds,
            "candidate-absolute",
            $"worst <= {thresholds.StopFeedbackWorstMilliseconds:0.###}ms",
            $"median={stopStats.Median:0.###}ms;p95={stopStats.P95:0.###}ms;worst={stopStats.Worst:0.###}ms");

        var statusText = checks
            .Where(check => check.Name.StartsWith("ui.", StringComparison.Ordinal))
            .Any(check => check.Status.Equals("FAIL", StringComparison.Ordinal))
            ? "FAIL"
            : "PASS";
        return new Phase08UiEvidence(
            statusText,
            "golden",
            expectedRows,
            Sha256Text($"SyntheticUiFixture|profile=golden|rows={expectedRows}|schema=phase06-ui-interaction-v1"),
            reports.Length,
            CombineHashes(hashes),
            hashes,
            RoundSamples(search),
            searchStats,
            RoundSamples(status),
            statusStats,
            RoundSamples(stop),
            stopStats,
            searchStalls,
            statusStalls,
            "Each raw sample comes from a separate C# UiHarness process and measures its actual WinForms interaction; it is not a Golden script-host-relative ratio.");
    }

    private static Phase08UiEvidence EmptyUiEvidence(string status, int rows) => new(
        status,
        "not-supplied",
        rows,
        string.Empty,
        0,
        string.Empty,
        [],
        [],
        null,
        [],
        null,
        [],
        null,
        0,
        0,
        "No UI reports were supplied.");

    private static UiReport ReadUiReport(string path)
    {
        var bytes = ReadBoundedBytes(path);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        var root = RequireObject(document.RootElement, "UI report");
        var schemaVersion = RequireString(root, "SchemaVersion");
        if (!schemaVersion.Equals("phase06-ui-interaction-v1", StringComparison.Ordinal))
            throw new InvalidDataException("A UI report uses an unsupported schema version.");
        var result = RequireString(root, "Result");
        var fixtureProfile = RequireString(root, "FixtureProfile");
        var rows = RequireInt32(root, "RowCount");
        var timings = RequireObject(RequireProperty(root, "Timings"), "UI report timings");
        var checks = RequireProperty(root, "Checks");
        if (checks.ValueKind != JsonValueKind.Array || checks.GetArrayLength() == 0)
            throw new InvalidDataException("A UI report is missing its non-empty Checks array.");
        var allChecksPass = checks.EnumerateArray().All(check =>
        {
            var value = RequireProperty(RequireObject(check, "UI check"), "Passed");
            return value.ValueKind == JsonValueKind.True;
        });
        var search = RequireFiniteNonNegativeDouble(timings, "SearchFilterMilliseconds");
        var status = RequireFiniteNonNegativeDouble(timings, "StatusFilterMilliseconds");
        var stop = RequireFiniteNonNegativeDouble(timings, "StopFeedbackMilliseconds");
        return new UiReport(
            result.Equals("PASS", StringComparison.Ordinal) && allChecksPass,
            fixtureProfile,
            rows,
            search,
            status,
            stop,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static Phase08PackageSizeEvidence ReadPackageSizeEvidence(
        IReadOnlyList<string> args,
        List<string> gaps)
    {
        var directory = ReadSingleValue(args, "--package-directory");
        var zip = ReadSingleValue(args, "--zip-path");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(zip))
        {
            gaps.Add("Both --package-directory and --zip-path are required to record publish and ZIP sizes; one or both were not supplied.");
            return new Phase08PackageSizeEvidence(
                "BLOCKED",
                0,
                0,
                string.Empty,
                0,
                string.Empty,
                "Package size evidence was not supplied.");
        }

        var root = Path.GetFullPath(directory);
        var zipPath = Path.GetFullPath(zip);
        if (!Directory.Exists(root) || !File.Exists(zipPath))
            throw new InvalidDataException("Package size evidence requires an existing publish directory and ZIP file.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Package size evidence cannot use a redirected publish root.");
        var manifestLines = new List<string>();
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var file in EnumerateRegularFiles(root)
                     .OrderBy(file => Path.GetRelativePath(root, file), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var fileIdentity = HashRegularFile(file, 2L * 1024 * 1024 * 1024);
            manifestLines.Add($"{relative}\t{fileIdentity.Length}\t{fileIdentity.Sha256}");
            totalBytes = checked(totalBytes + fileIdentity.Length);
            fileCount++;
        }
        if (fileCount == 0)
            throw new InvalidDataException("Package size evidence cannot use an empty publish directory.");
        var zipIdentity = HashRegularFile(zipPath, 2L * 1024 * 1024 * 1024);
        return new Phase08PackageSizeEvidence(
            "PASS",
            totalBytes,
            fileCount,
            Sha256Text(string.Join('\n', manifestLines)),
            zipIdentity.Length,
            zipIdentity.Sha256,
            "Sizes and hashes cover only caller-supplied local package artifacts; no upload or network operation occurs.");
    }

    private static IEnumerable<string> EnumerateRegularFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new InvalidDataException("Package size evidence cannot include redirected or device entries.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    yield return entry;
            }
        }
    }

    private static Phase08GoldenReference ReadGoldenReference(
        IReadOnlyList<string> args,
        List<string> gaps)
    {
        var uiPath = ReadSingleValue(args, "--golden-ui");
        var rmkPath = ReadSingleValue(args, "--golden-rmk");
        GoldenUiReference? ui = null;
        GoldenRmkReference? rmk = null;
        if (string.IsNullOrWhiteSpace(uiPath))
        {
            gaps.Add("Golden UI performance JSON was not supplied with --golden-ui.");
        }
        else
        {
            ui = ReadGoldenUi(uiPath);
        }
        if (string.IsNullOrWhiteSpace(rmkPath))
        {
            gaps.Add("Golden RMK performance JSON was not supplied with --golden-rmk.");
        }
        else
        {
            rmk = ReadGoldenRmk(rmkPath);
        }
        return new Phase08GoldenReference(
            "Reference-only: Golden and C# candidate boundaries are not wire-equivalent, so this report intentionally emits no relative slowdown percentage.",
            ui,
            rmk);
    }

    private static GoldenUiReference ReadGoldenUi(string path)
    {
        var bytes = ReadBoundedBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var root = RequireObject(document.RootElement, "Golden UI report");
        var rows = RequireInt32(root, "rows");
        var iterations = RequireInt32(root, "iterations");
        var search = ReadDoubleArray(RequireObject(RequireProperty(root, "search"), "Golden UI search"), "samples");
        var save = ReadDoubleArray(RequireObject(RequireProperty(root, "save"), "Golden UI save"), "samples");
        RequireAtLeastSamples(search, iterations, "Golden UI search");
        RequireAtLeastSamples(save, iterations, "Golden UI save");
        return new GoldenUiReference(
            "reference-only-non-equivalent",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            rows,
            iterations,
            RoundSamples(search),
            Summarize(search),
            RoundSamples(save),
            Summarize(save));
    }

    private static GoldenRmkReference ReadGoldenRmk(string path)
    {
        var bytes = ReadBoundedBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var root = RequireObject(document.RootElement, "Golden RMK report");
        var rows = RequireInt32(root, "rows");
        var iterations = RequireInt32(root, "updateIterations");
        var create = RequireFiniteNonNegativeDouble(
            RequireObject(RequireProperty(root, "create"), "Golden RMK create"),
            "elapsedMs");
        var update = ReadDoubleArray(RequireObject(RequireProperty(root, "update"), "Golden RMK update"), "samples");
        RequireAtLeastSamples(update, iterations, "Golden RMK update");
        var workbookBytes = RequireInt64(root, "workbookBytes");
        return new GoldenRmkReference(
            "reference-only-non-equivalent",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            rows,
            iterations,
            create,
            RoundSamples(update),
            Summarize(update),
            workbookBytes);
    }

    private static void AddSampleGate(
        ICollection<Phase08GateCheck> checks,
        string name,
        IReadOnlyList<double> samples,
        double threshold,
        bool useWorst,
        string classification)
    {
        var enough = samples.Count >= MinimumSamples;
        var stats = enough ? Summarize(samples) : null;
        var actual = stats is null
            ? $"samples={samples.Count}"
            : $"samples={samples.Count};median={stats.Median:0.###}ms;p95={stats.P95:0.###}ms;worst={stats.Worst:0.###}ms";
        var passed = enough && (useWorst ? stats!.Worst : stats!.Median) <= threshold;
        AddCheck(
            checks,
            name,
            passed,
            classification,
            $"at least {MinimumSamples} samples; {(useWorst ? "worst" : "median")} <= {threshold:0.###}ms",
            actual);
    }

    private static void AddCheck(
        ICollection<Phase08GateCheck> checks,
        string name,
        bool passed,
        string classification,
        string criterion,
        string actual) =>
        checks.Add(new Phase08GateCheck(name, passed ? "PASS" : "FAIL", classification, criterion, actual));

    private static void AddBlockedCheck(
        ICollection<Phase08GateCheck> checks,
        string name,
        string classification,
        string criterion,
        string actual) =>
        checks.Add(new Phase08GateCheck(name, "BLOCKED", classification, criterion, actual));

    private static Phase08SampleSummary Summarize(IEnumerable<double> samples)
    {
        var ordered = samples.Order().ToArray();
        if (ordered.Length == 0)
            throw new InvalidDataException("A performance summary requires at least one sample.");
        return new Phase08SampleSummary(
            Round(Percentile(ordered, 0.5)),
            Round(Percentile(ordered, 0.95)),
            Round(ordered[^1]));
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

    private static List<string> ReadValues(IReadOnlyList<string> args, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} requires a value.");
            values.Add(args[++index]);
        }
        return values;
    }

    private static string ReadSingleValue(IReadOnlyList<string> args, string name)
    {
        var values = ReadValues(args, name);
        if (values.Count > 1) throw new ArgumentException($"{name} may be supplied only once.");
        return values.Count == 0 ? string.Empty : values[0];
    }

    private static byte[] ReadBoundedBytes(string path) => ReadBoundedArtifactBytes(path, MaximumJsonBytes);

    private static FileIdentity HashRegularFile(string path, long maximumBytes)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException("A performance evidence file is missing, empty, or exceeds its size limit.");
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Performance evidence must be a regular non-redirected file.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (stream.Length != info.Length)
            throw new InvalidDataException("A performance evidence file changed while it was hashed.");
        return new FileIdentity(info.Length, hash);
    }

    private static byte[] ReadBoundedArtifactBytes(string path, long maximumBytes)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException("A performance evidence file is missing, empty, or exceeds its size limit.");
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Performance evidence must be a regular non-redirected file.");
        if (info.Length > int.MaxValue)
            throw new InvalidDataException("A performance evidence file is too large to inspect.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var result = new byte[(int)info.Length];
        stream.ReadExactly(result);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("A performance evidence file changed while it was read.");
        return result;
    }

    private static JsonElement RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context} must be a JSON object.");
        return element;
    }

    private static JsonElement RequireProperty(JsonElement parent, string name)
    {
        foreach (var property in parent.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        throw new InvalidDataException($"A performance report is missing the {name} property.");
    }

    private static string RequireString(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"A performance report has an invalid {name} string.");
        return value.GetString()!;
    }

    private static int RequireInt32(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (!value.TryGetInt32(out var parsed) || parsed <= 0)
            throw new InvalidDataException($"A performance report has an invalid {name} integer.");
        return parsed;
    }

    private static long RequireInt64(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (!value.TryGetInt64(out var parsed) || parsed <= 0)
            throw new InvalidDataException($"A performance report has an invalid {name} integer.");
        return parsed;
    }

    private static double RequireFiniteNonNegativeDouble(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (!value.TryGetDouble(out var parsed) || !double.IsFinite(parsed) || parsed < 0)
            throw new InvalidDataException($"A performance report has an invalid {name} measurement.");
        return parsed;
    }

    private static double[] ReadDoubleArray(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"A performance report has an invalid {name} sample array.");
        return value.EnumerateArray().Select(element =>
        {
            if (!element.TryGetDouble(out var sample) || !double.IsFinite(sample) || sample < 0)
                throw new InvalidDataException($"A performance report has an invalid {name} sample.");
            return sample;
        }).ToArray();
    }

    private static void RequireAtLeastSamples(IReadOnlyCollection<double> samples, int expected, string context)
    {
        if (expected < MinimumSamples || samples.Count < expected)
            throw new InvalidDataException($"{context} does not contain its declared minimum sample count.");
    }

    private static string CombineHashes(IEnumerable<string> hashes) =>
        Sha256Text(string.Join('\n', hashes.Order(StringComparer.Ordinal)));

    private static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static double[] RoundSamples(IEnumerable<double> samples) => samples.Select(Round).ToArray();

    private static double Round(double value) => Math.Round(value, 3);

    private sealed record UiReport(
        bool Passed,
        string FixtureProfile,
        int Rows,
        double SearchMs,
        double StatusFilterMs,
        double StopFeedbackMs,
        string Sha256);
    private sealed record FileIdentity(long Length, string Sha256);
}
