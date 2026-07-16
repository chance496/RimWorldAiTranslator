using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.App;
using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.Core.Discovery;

namespace RimWorldAiTranslator.UiHarness;

internal static class Phase08MainFormMemoryProbe
{
    private const string Mode = "--phase08-mainform-memory";
    private const string OwnedRootPrefix = "RimWorldAiTranslator-ui-memory-";
    private const int FixtureRows = 5_000;
    private const int WarmupCycles = 2;
    private const int MeasuredCycles = 20;
    private const double GrowthLimitPercent = 15;
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CycleWatchdogTimeout = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static bool TryRun(IReadOnlyList<string> args, out int exitCode)
    {
        if (!args.Any(value => value.Equals(Mode, StringComparison.OrdinalIgnoreCase)))
        {
            exitCode = 0;
            return false;
        }

        var reportPath = Value(args, "--report");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            exitCode = 64;
            return true;
        }

        try
        {
            exitCode = Run(Path.GetFullPath(reportPath));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"MainForm memory probe failed before reporting ({exception.GetType().Name}).");
            exitCode = 1;
        }
        return true;
    }

    private static int Run(string reportPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.Combine(tempRoot, OwnedRootPrefix + Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var discoveryRoot = Path.Combine(root, "discovery");
        var report = new MemoryProbeReport
        {
            SchemaVersion = "phase08-mainform-memory-v1",
            Status = "FAIL",
            StartedAtUtc = DateTimeOffset.UtcNow,
            FixtureRows = FixtureRows,
            WarmupCycles = WarmupCycles,
            MeasuredCycles = MeasuredCycles,
            GrowthLimitPercent = GrowthLimitPercent,
            SyntheticFixture = true,
            UserDataUsed = false,
            ExternalNetworkUsed = false,
            MeasurementDefinition =
                "Each cycle creates a real MainForm, completes startup, opens the 5,000-row project, " +
                "performs a normal close and service disposal, then releases references and performs two full GCs."
        };
        Exception? failure = null;

        try
        {
            VerifyOwnedRoot(root, tempRoot, requireExists: false);
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(discoveryRoot);
            File.WriteAllText(
                Path.Combine(discoveryRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
                RimWorldModDiscoveryService.IsolationMarkerContent + Environment.NewLine,
                new UTF8Encoding(false));
            SyntheticUiFixture.Create(dataRoot, discoveryRoot, FixtureRows, "golden");

            for (var cycle = 1; cycle <= WarmupCycles; cycle++)
            {
                var execution = ExecuteCycle(dataRoot, discoveryRoot);
                var sample = CollectSample(execution, cycle, warmup: true);
                report.WarmupSamples.Add(sample);
                RequireCycleReleased(sample);
            }

            for (var cycle = 1; cycle <= MeasuredCycles; cycle++)
            {
                var execution = ExecuteCycle(dataRoot, discoveryRoot);
                var sample = CollectSample(execution, cycle, warmup: false);
                report.Samples.Add(sample);
                RequireCycleReleased(sample);
            }

            report.Summary = CreateSummary(report.Samples);
            if (!report.Summary.WithinGrowthLimit)
            {
                throw new InvalidOperationException(
                    "The stabilized MainForm memory growth exceeded the Phase 08 limit.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
        }

        try
        {
            if (Directory.Exists(root))
            {
                VerifyOwnedRoot(root, tempRoot, requireExists: true);
                AssertNoReparseTree(root);
                Directory.Delete(root, recursive: true);
            }
            report.Cleanup.TempRootRemoved = !Directory.Exists(root);
        }
        catch (Exception cleanupException) when (cleanupException is IOException
                                                   or UnauthorizedAccessException
                                                   or InvalidOperationException)
        {
            report.Cleanup.TempRootRemoved = false;
            report.Cleanup.FailureType = cleanupException.GetType().Name;
            failure = failure is null
                ? cleanupException
                : new AggregateException(failure, cleanupException);
        }

        report.Status = failure is null ? "PASS" : "FAIL";
        report.FailureType = failure?.GetType().Name;
        report.CompletedAtUtc = DateTimeOffset.UtcNow;
        WriteReport(reportPath, report);
        return failure is null ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CycleExecution ExecuteCycle(
        string dataRoot,
        string discoveryRoot)
    {
        var startupWatch = Stopwatch.StartNew();
        var openMilliseconds = 0d;
        var closeMilliseconds = 0d;
        var rowCount = 0;
        var formClosed = false;
        var normalCloseRequested = false;
        var unsavedPromptCount = 0;
        var activePromptCount = 0;
        var forcePromptCount = 0;
        Exception? failure = null;
        Task? scenarioTask = null;
        Task? teardownTask = null;
        var teardownStarted = 0;
        var discovery = RimWorldModDiscoveryService.CreateIsolated(discoveryRoot);
        var hooks = new UiIoHooks(
            ErrorPresenter: _ => failure ??= new InvalidOperationException(
                "The MainForm memory cycle presented an unexpected UI error."));
        using var form = new MainForm(
            dataRoot,
            discovery,
            closeBarrierTimeout: TimeSpan.FromSeconds(10),
            forceCloseConfirmation: () =>
            {
                forcePromptCount++;
                return false;
            },
            ioHooks: hooks,
            activeOperationCloseConfirmation: () =>
            {
                activePromptCount++;
                return DialogResult.OK;
            },
            unsavedCloseConfirmation: () =>
            {
                unsavedPromptCount++;
                return DialogResult.No;
            });
        var closeWatch = new Stopwatch();
        using var watchdog = new System.Windows.Forms.Timer
        {
            Interval = checked((int)CycleWatchdogTimeout.TotalMilliseconds)
        };

        async Task FailAndDisposeAsync(Exception exception)
        {
            failure ??= exception;
            if (Interlocked.Exchange(ref teardownStarted, 1) != 0) return;
            try
            {
                await form.DisposeAfterFailedBootstrapAsync();
            }
            catch (Exception cleanupException)
            {
                failure = new AggregateException(failure, cleanupException);
                try { form.Dispose(); }
                catch (Exception disposeException)
                {
                    failure = new AggregateException(failure, disposeException);
                }
            }
        }

        async Task RunScenarioAsync()
        {
            try
            {
                await WaitForStartupAsync(form);
                startupWatch.Stop();

                var openWatch = Stopwatch.StartNew();
                await SnapshotCapture.OpenFirstProjectAsync(form, UiTimeout);
                await WaitForUiQuiescenceAsync(form);
                openWatch.Stop();
                openMilliseconds = openWatch.Elapsed.TotalMilliseconds;
                rowCount = form.WorkspaceControlForTesting.Workspace?.Items.Count ?? 0;
                if (rowCount != FixtureRows)
                    throw new InvalidOperationException("The MainForm memory cycle did not open 5,000 review rows.");

                normalCloseRequested = true;
                closeWatch.Start();
                form.Close();
            }
            catch (Exception exception)
            {
                await FailAndDisposeAsync(exception);
            }
        }

        form.FormClosed += (_, _) =>
        {
            formClosed = true;
            if (closeWatch.IsRunning)
            {
                closeWatch.Stop();
                closeMilliseconds = closeWatch.Elapsed.TotalMilliseconds;
            }
        };
        form.Shown += (_, _) => scenarioTask = RunScenarioAsync();
        watchdog.Tick += async (_, _) =>
        {
            watchdog.Stop();
            teardownTask = FailAndDisposeAsync(new TimeoutException(
                "The MainForm memory cycle exceeded its watchdog timeout."));
            await teardownTask;
        };
        watchdog.Start();
        Application.Run(form);
        watchdog.Stop();

        if (scenarioTask is null)
        {
            failure ??= new InvalidOperationException(
                "The MainForm memory cycle did not enter its shown scenario.");
        }
        else
        {
            failure = ObserveTaskAfterMessageLoop(
                scenarioTask,
                "MainForm memory scenario",
                failure);
        }
        if (teardownTask is not null)
        {
            failure = ObserveTaskAfterMessageLoop(
                teardownTask,
                "MainForm memory watchdog teardown",
                failure);
        }
        if (failure is null && (!normalCloseRequested || !formClosed || closeMilliseconds <= 0))
        {
            failure = new InvalidOperationException(
                "The MainForm memory cycle did not complete a normal close.");
        }
        if (failure is null && form.HasIncompleteUiWorkflowsForTesting)
        {
            failure = new InvalidOperationException(
                "The MainForm memory cycle retained an incomplete UI workflow.");
        }
        if (failure is null
            && (unsavedPromptCount != 0 || activePromptCount != 0 || forcePromptCount != 0))
        {
            failure = new InvalidOperationException(
                "The read-only MainForm memory cycle required an unexpected close prompt.");
        }

        var formReference = new WeakReference(form);
        try { form.Dispose(); }
        catch (Exception disposeException)
        {
            failure = failure is null
                ? disposeException
                : new AggregateException(failure, disposeException);
        }
        var openFormsAfterClose = Application.OpenForms.Count;
        if (failure is not null) throw failure;
        return new CycleExecution(
            startupWatch.Elapsed.TotalMilliseconds,
            openMilliseconds,
            closeMilliseconds,
            rowCount,
            openFormsAfterClose,
            formReference);
    }

    private static Exception? ObserveTaskAfterMessageLoop(
        Task task,
        string operation,
        Exception? existingFailure)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        if (!task.IsCompleted)
        {
            var timeout = new TimeoutException(
                $"The {operation} did not settle after the WinForms message loop ended.");
            return existingFailure is null
                ? timeout
                : new AggregateException(existingFailure, timeout);
        }

        try
        {
            task.GetAwaiter().GetResult();
            return existingFailure;
        }
        catch (Exception exception)
        {
            return existingFailure is null
                ? exception
                : new AggregateException(existingFailure, exception);
        }
    }

    private static async Task WaitForStartupAsync(MainForm form)
    {
        await SnapshotCapture.WaitForReadyAsync(form, UiTimeout);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < UiTimeout)
        {
            Application.DoEvents();
            if (form.StartupFailedForTesting)
                throw new InvalidOperationException("The MainForm reported a startup failure.");
            if (!form.HasIncompleteUiWorkflowsForTesting) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The MainForm startup workflow did not drain before the memory-cycle timeout.");
    }

    private static async Task WaitForUiQuiescenceAsync(MainForm form)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < UiTimeout)
        {
            Application.DoEvents();
            if (!form.HasIncompleteUiWorkflowsForTesting
                && !form.WorkspaceControlForTesting.UiDataWorkPendingForTesting)
            {
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("The MainForm review workspace did not become quiescent.");
    }

    private static MemoryCycleSample CollectSample(CycleExecution execution, int cycle, bool warmup)
    {
        StabilizeManagedMemory();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBytes = process.WorkingSet64;
        var privateMemoryBytes = process.PrivateMemorySize64;
        var managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        return new MemoryCycleSample
        {
            Cycle = cycle,
            Warmup = warmup,
            StartupMilliseconds = Round(execution.StartupMilliseconds),
            ProjectOpenMilliseconds = Round(execution.ProjectOpenMilliseconds),
            NormalCloseMilliseconds = Round(execution.NormalCloseMilliseconds),
            ReviewRows = execution.ReviewRows,
            WorkingSet64Bytes = workingSetBytes,
            PrivateMemorySize64Bytes = privateMemoryBytes,
            ManagedHeapBytes = managedHeapBytes,
            WorkingSetMegabytes = Megabytes(workingSetBytes),
            PrivateMemoryMegabytes = Megabytes(privateMemoryBytes),
            ManagedHeapMegabytes = Megabytes(managedHeapBytes),
            HandleCount = process.HandleCount,
            GdiObjectCount = GetGuiResources(process.Handle, 0),
            UserObjectCount = GetGuiResources(process.Handle, 1),
            FormCollected = !execution.FormReference.IsAlive,
            OpenFormsAfterClose = execution.OpenFormsAfterClose
        };
    }

    private static void RequireCycleReleased(MemoryCycleSample sample)
    {
        if (!sample.FormCollected || sample.OpenFormsAfterClose != 0)
            throw new InvalidOperationException("A MainForm memory cycle retained a Form or Application.OpenForms entry.");
        if (sample.ReviewRows != FixtureRows)
            throw new InvalidOperationException("A MainForm memory cycle measured an incomplete fixture.");
    }

    private static MemorySummary CreateSummary(IReadOnlyList<MemoryCycleSample> samples)
    {
        if (samples.Count != MeasuredCycles)
            throw new InvalidOperationException("The MainForm memory report does not contain 20 measured cycles.");
        var first = samples.Take(3).ToArray();
        var last = samples.TakeLast(3).ToArray();
        var initialWorkingSetBytes = Median(first.Select(sample => (double)sample.WorkingSet64Bytes));
        var initialPrivateMemoryBytes = Median(first.Select(sample => (double)sample.PrivateMemorySize64Bytes));
        var initialManagedHeapBytes = Median(first.Select(sample => (double)sample.ManagedHeapBytes));
        var finalWorkingSetBytes = Median(last.Select(sample => (double)sample.WorkingSet64Bytes));
        var finalPrivateMemoryBytes = Median(last.Select(sample => (double)sample.PrivateMemorySize64Bytes));
        var finalManagedHeapBytes = Median(last.Select(sample => (double)sample.ManagedHeapBytes));
        var initial = new MemoryTriple(
            Megabytes(initialWorkingSetBytes),
            Megabytes(initialPrivateMemoryBytes),
            Megabytes(initialManagedHeapBytes));
        var final = new MemoryTriple(
            Megabytes(finalWorkingSetBytes),
            Megabytes(finalPrivateMemoryBytes),
            Megabytes(finalManagedHeapBytes));
        var growth = new MemoryGrowthPercent(
            GrowthPercent(initialWorkingSetBytes, finalWorkingSetBytes),
            GrowthPercent(initialPrivateMemoryBytes, finalPrivateMemoryBytes),
            GrowthPercent(initialManagedHeapBytes, finalManagedHeapBytes));
        return new MemorySummary
        {
            InitialFirstThreeMedian = initial,
            FinalLastThreeMedian = final,
            GrowthPercent = growth,
            HandleCountDelta = samples[^1].HandleCount - samples[0].HandleCount,
            GdiObjectCountDelta = checked((long)samples[^1].GdiObjectCount - samples[0].GdiObjectCount),
            UserObjectCountDelta = checked((long)samples[^1].UserObjectCount - samples[0].UserObjectCount),
            WithinGrowthLimit = growth.WorkingSetPercent <= GrowthLimitPercent
                                && growth.PrivateMemoryPercent <= GrowthLimitPercent
                                && growth.ManagedHeapPercent <= GrowthLimitPercent
        };
    }

    private static void StabilizeManagedMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Thread.Sleep(50);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return 0;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static double GrowthPercent(double initial, double final) =>
        initial <= 0 ? final <= 0 ? 0 : 100 : (final - initial) * 100 / initial;

    private static double Megabytes(double bytes) => Round(bytes / 1024d / 1024d);

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static void VerifyOwnedRoot(string root, string tempRoot, bool requireExists)
    {
        if (!Directory.Exists(tempRoot)
            || (File.GetAttributes(tempRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The system TEMP root is missing or is a reparse point.");
        }

        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(tempRoot, fullRoot);
        if (Path.IsPathRooted(relative)
            || relative.Contains(Path.DirectorySeparatorChar)
            || relative.Contains(Path.AltDirectorySeparatorChar)
            || !relative.StartsWith(OwnedRootPrefix, StringComparison.Ordinal)
            || relative.Length != OwnedRootPrefix.Length + 32
            || !Guid.TryParseExact(relative[OwnedRootPrefix.Length..], "N", out _)
            || (requireExists && !Directory.Exists(fullRoot)))
        {
            throw new InvalidOperationException("The MainForm memory probe root is not a verified TEMP child.");
        }
    }

    private static void AssertNoReparseTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The MainForm memory probe root contains a reparse point.");
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The MainForm memory probe root contains a reparse point.");
                if (Directory.Exists(child)) pending.Push(child);
            }
        }
    }

    private static void WriteReport(string path, MemoryProbeReport report)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false));
    }

    private static string Value(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return string.Empty;
    }

    private sealed record CycleExecution(
        double StartupMilliseconds,
        double ProjectOpenMilliseconds,
        double NormalCloseMilliseconds,
        int ReviewRows,
        int OpenFormsAfterClose,
        WeakReference FormReference);

    private sealed class MemoryProbeReport
    {
        public string SchemaVersion { get; init; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public int FixtureRows { get; init; }
        public int WarmupCycles { get; init; }
        public int MeasuredCycles { get; init; }
        public double GrowthLimitPercent { get; init; }
        public bool SyntheticFixture { get; init; }
        public bool UserDataUsed { get; init; }
        public bool ExternalNetworkUsed { get; init; }
        public string MeasurementDefinition { get; init; } = string.Empty;
        public List<MemoryCycleSample> WarmupSamples { get; } = [];
        public List<MemoryCycleSample> Samples { get; } = [];
        public MemorySummary? Summary { get; set; }
        public CleanupResult Cleanup { get; } = new();
        public string? FailureType { get; set; }
    }

    private sealed class MemoryCycleSample
    {
        public int Cycle { get; init; }
        public bool Warmup { get; init; }
        public double StartupMilliseconds { get; init; }
        public double ProjectOpenMilliseconds { get; init; }
        public double NormalCloseMilliseconds { get; init; }
        public int ReviewRows { get; init; }
        public long WorkingSet64Bytes { get; init; }
        public long PrivateMemorySize64Bytes { get; init; }
        public long ManagedHeapBytes { get; init; }
        public double WorkingSetMegabytes { get; init; }
        public double PrivateMemoryMegabytes { get; init; }
        public double ManagedHeapMegabytes { get; init; }
        public int HandleCount { get; init; }
        public uint GdiObjectCount { get; init; }
        public uint UserObjectCount { get; init; }
        public bool FormCollected { get; init; }
        public int OpenFormsAfterClose { get; init; }
    }

    private sealed class MemorySummary
    {
        public MemoryTriple InitialFirstThreeMedian { get; init; } = new(0, 0, 0);
        public MemoryTriple FinalLastThreeMedian { get; init; } = new(0, 0, 0);
        public MemoryGrowthPercent GrowthPercent { get; init; } = new(0, 0, 0);
        public int HandleCountDelta { get; init; }
        public long GdiObjectCountDelta { get; init; }
        public long UserObjectCountDelta { get; init; }
        public bool WithinGrowthLimit { get; init; }
    }

    private sealed record MemoryTriple(
        double WorkingSetMegabytes,
        double PrivateMemoryMegabytes,
        double ManagedHeapMegabytes);

    private sealed record MemoryGrowthPercent(
        double WorkingSetPercent,
        double PrivateMemoryPercent,
        double ManagedHeapPercent);

    private sealed class CleanupResult
    {
        public bool TempRootRemoved { get; set; }
        public string? FailureType { get; set; }
    }

#pragma warning disable SYSLIB1054 // GetGuiResources is a simple Windows-only process counter boundary.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
#pragma warning restore SYSLIB1054
}
