using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.App;

namespace RimWorldAiTranslator.UiHarness;

internal static class CrossProcessSingleInstanceProbe
{
    private const string ParentMode = "--cross-process-single-instance-probe";
    private const string HolderMode = "--single-instance-holder-child";
    private const string ContenderMode = "--single-instance-contender-child";
    private const int ReportSchemaVersion = 5;
    private const int ParentTimeoutMilliseconds = 20_000;
    private const int HolderTimeoutMilliseconds = 45_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly HashSet<string> ExpectedChildReportProperties = new(
        [
            "schemaVersion",
            "status",
            "role",
            "expectedLeaseAcquired",
            "leaseAcquired",
            "readyMarkerRecorded",
            "releaseMarkerObserved",
            "leaseReleased",
            "lockFileName",
            "lockFileExists",
            "directLockOpenBlockedWhileHeld",
            "directLockOpenAfterReleaseSucceeded",
            "distinctSemaphoreScopeUsed",
            "rootNamespaceRenameBeforeAcquireSucceeded",
            "rootNamespaceRenameBlockedWhileHeld",
            "rootNamespaceRenameAfterReleaseSucceeded",
            "exitCode",
            "processId",
            "startedAtUtc",
            "completedAtUtc",
            "userDataUsed",
            "externalOperations",
            "failureType"
        ],
        StringComparer.Ordinal);

    internal static bool TryRun(IReadOnlyList<string> args, out int exitCode)
    {
        var isParent = Has(args, ParentMode);
        var isHolder = Has(args, HolderMode);
        var isContender = Has(args, ContenderMode);
        if (!isParent && !isHolder && !isContender)
        {
            exitCode = 0;
            return false;
        }

        var selectedModeCount = (isParent ? 1 : 0) + (isHolder ? 1 : 0) + (isContender ? 1 : 0);
        if (selectedModeCount != 1)
        {
            exitCode = 64;
            return true;
        }

        try
        {
            exitCode = isParent
                ? RunParent(RequiredValue(args, "--report"))
                : isHolder
                    ? RunHolderChild(args)
                    : RunContenderChild(args);
        }
        catch (Exception exception)
        {
            TryWriteUnhandledChildFailure(args, isHolder ? "holder" : isContender ? "contender" : "parent", exception);
            exitCode = 1;
        }

        return true;
    }

    private static int RunParent(string reportPath)
    {
        var runId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(
            Path.GetTempPath(),
            "RimWorldAiTranslator-ui-harness-single-instance-" + runId);
        var aliasSegment = Path.Combine(root, "synthetic-alias-segment");
        var aliasRoot = Path.Combine(aliasSegment, "..");
        var startupSwapRoot = Path.Combine(root, "startup-swap-root");
        var startupSwapDisplacedRoot = Path.Combine(root, "startup-swap-displaced");
        var readyMarker = Path.Combine(root, "holder.ready");
        var releaseMarker = Path.Combine(root, "holder.release");
        var holderReportPath = Path.Combine(root, "holder.json");
        var contenderReportPath = Path.Combine(root, "contender.json");
        var reacquirerReportPath = Path.Combine(root, "reacquirer.json");
        var report = new ParentProbeReport
        {
            SchemaVersion = ReportSchemaVersion,
            Status = "FAIL",
            StartedAtUtc = DateTimeOffset.UtcNow,
            DataRootKind = "unique synthetic TEMP directory",
            AliasKind = "existing child segment followed by parent (segment\\..)",
            UserDataUsed = false,
            ExternalOperations = "none",
            DistinctLocalSemaphoreScopes = true,
            Holder = new ChildObservation { Role = "holder", ExpectedLeaseAcquired = true },
            Contender = new ChildObservation { Role = "contender", ExpectedLeaseAcquired = false },
            Reacquirer = new ChildObservation { Role = "reacquirer", ExpectedLeaseAcquired = true },
            Cleanup = new CleanupObservation()
        };

        Process? holder = null;
        Process? contender = null;
        Process? reacquirer = null;
        Exception? failure = null;

        try
        {
            Directory.CreateDirectory(aliasSegment);
            report.AliasInputDiffers = !aliasRoot.Equals(root, StringComparison.Ordinal);
            report.AliasFullPathMatches = NormalizePath(aliasRoot).Equals(
                NormalizePath(root),
                StringComparison.OrdinalIgnoreCase);
            if (!report.AliasInputDiffers || !report.AliasFullPathMatches)
                throw new InvalidOperationException("The synthetic same-root alias was not established.");

            ExerciseStartupRootSwap(
                startupSwapRoot,
                startupSwapDisplacedRoot,
                report);

            holder = StartSelfProcess(
                HolderMode,
                "--lease-root", root,
                "--ready-marker", readyMarker,
                "--release-marker", releaseMarker,
                "--report", holderReportPath,
                "--semaphore-scope", "holder",
                "--timeout-ms", HolderTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            InitializeObservation(report.Holder, holder);
            WaitForMarker(readyMarker, holder, ParentTimeoutMilliseconds);
            report.Holder.ReadyMarkerObserved = true;

            contender = StartSelfProcess(
                ContenderMode,
                "--lease-root", aliasRoot,
                "--expect-acquired", "false",
                "--semaphore-scope", "contender",
                "--report", contenderReportPath);
            InitializeObservation(report.Contender, contender);
            WaitForExit(contender, ParentTimeoutMilliseconds, "contender");
            CaptureCompletedChild(report.Contender, contender, contenderReportPath);
            RequireExpectedResult(report.Contender);

            WriteMarker(releaseMarker);
            report.Cleanup.ReleaseMarkerWritten = true;
            WaitForExit(holder, ParentTimeoutMilliseconds, "holder");
            CaptureCompletedChild(report.Holder, holder, holderReportPath);
            RequireExpectedResult(report.Holder);
            if (report.Holder.ReleaseMarkerObserved != true || report.Holder.LeaseReleased != true)
                throw new InvalidOperationException("The holder did not report an orderly lease release.");

            reacquirer = StartSelfProcess(
                ContenderMode,
                "--lease-root", root,
                "--expect-acquired", "true",
                "--semaphore-scope", "reacquirer",
                "--report", reacquirerReportPath);
            InitializeObservation(report.Reacquirer, reacquirer);
            WaitForExit(reacquirer, ParentTimeoutMilliseconds, "reacquirer");
            CaptureCompletedChild(report.Reacquirer, reacquirer, reacquirerReportPath);
            RequireExpectedResult(report.Reacquirer);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (!report.Cleanup.ReleaseMarkerWritten)
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        WriteMarker(releaseMarker);
                        report.Cleanup.ReleaseMarkerWritten = true;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    report.Cleanup.ReleaseMarkerWriteFailureType = exception.GetType().Name;
                }
            }

            CleanupChild(holder, report.Holder, holderReportPath);
            CleanupChild(contender, report.Contender, contenderReportPath);
            CleanupChild(reacquirer, report.Reacquirer, reacquirerReportPath);
            report.Cleanup.AllChildrenExited = report.Holder.Exited
                                                && report.Contender.Exited
                                                && report.Reacquirer.Exited;

            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                report.Cleanup.TempRootRemoved = !Directory.Exists(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                report.Cleanup.TempRootRemoved = false;
                report.Cleanup.TempRootRemovalFailureType = exception.GetType().Name;
            }

            if ((!report.Cleanup.AllChildrenExited || !report.Cleanup.TempRootRemoved) && failure is null)
                failure = new InvalidOperationException("The cross-process probe did not complete bounded cleanup.");

            report.Status = failure is null ? "PASS" : "FAIL";
            report.FailureType = failure?.GetType().Name;
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteJson(reportPath, report);
        }

        return failure is null ? 0 : 1;
    }

    private static void ExerciseStartupRootSwap(
        string startupSwapRoot,
        string startupSwapDisplacedRoot,
        ParentProbeReport report)
    {
        const string OriginalMarker = "original-root";
        const string ReplacementMarker = "replacement-root";
        Directory.CreateDirectory(startupSwapRoot);
        File.WriteAllText(
            Path.Combine(startupSwapRoot, "identity.txt"),
            OriginalMarker,
            new UTF8Encoding(false));

        var hookCalled = false;
        var acquired = false;
        Exception? failure = null;
        SingleInstanceLease? unexpectedLease = null;
        try
        {
            acquired = SingleInstanceLease.TryAcquire(
                startupSwapRoot,
                out unexpectedLease,
                testSemaphoreScope: "startup-root-swap",
                afterRootIdentityCapturedTestHook: capturedRoot =>
                {
                    hookCalled = true;
                    if (!NormalizePath(capturedRoot).Equals(
                            NormalizePath(startupSwapRoot),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The startup-swap hook received an unexpected root.");
                    }
                    try
                    {
                        Directory.Move(startupSwapRoot, startupSwapDisplacedRoot);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidDataException(
                            "The startup data-root boundary blocked namespace substitution before the lease was acquired.",
                            exception);
                    }
                    Directory.CreateDirectory(startupSwapRoot);
                    File.WriteAllText(
                        Path.Combine(startupSwapRoot, "identity.txt"),
                        ReplacementMarker,
                        new UTF8Encoding(false));
                });
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            unexpectedLease?.Dispose();
        }

        report.StartupSwapHookCalled = hookCalled;
        report.StartupSwapRejected = !acquired && failure is InvalidDataException;
        report.StartupSwapFailureType = failure?.GetType().Name;
        var displacedOriginal = Path.Combine(startupSwapDisplacedRoot, "identity.txt");
        var canonicalIdentity = Path.Combine(startupSwapRoot, "identity.txt");
        var originalStayedPinned = File.Exists(canonicalIdentity)
                                   && File.ReadAllText(canonicalIdentity, Encoding.UTF8)
                                       .Equals(OriginalMarker, StringComparison.Ordinal)
                                   && !Directory.Exists(startupSwapDisplacedRoot);
        var originalWasDisplaced = File.Exists(displacedOriginal)
                                   && File.ReadAllText(displacedOriginal, Encoding.UTF8)
                                       .Equals(OriginalMarker, StringComparison.Ordinal);
        report.StartupSwapOriginalPreserved = originalStayedPinned || originalWasDisplaced;
        report.StartupSwapReplacementPreserved = originalStayedPinned
            || File.Exists(canonicalIdentity)
               && File.ReadAllText(canonicalIdentity, Encoding.UTF8)
                   .Equals(ReplacementMarker, StringComparison.Ordinal);

        var reacquired = SingleInstanceLease.TryAcquire(
            startupSwapRoot,
            out var replacementLease,
            testSemaphoreScope: "startup-root-swap");
        report.StartupSwapReplacementReacquired = reacquired && replacementLease is not null;
        replacementLease?.Dispose();

        if (!report.StartupSwapHookCalled
            || !report.StartupSwapRejected
            || !report.StartupSwapOriginalPreserved
            || !report.StartupSwapReplacementPreserved
            || !report.StartupSwapReplacementReacquired)
        {
            throw new InvalidOperationException(
                "A pre-lock application data-root substitution was not rejected without data loss.");
        }
    }

    private static int RunHolderChild(IReadOnlyList<string> args)
    {
        var leaseRoot = RequiredValue(args, "--lease-root");
        var readyMarker = RequiredValue(args, "--ready-marker");
        var releaseMarker = RequiredValue(args, "--release-marker");
        var reportPath = RequiredValue(args, "--report");
        var semaphoreScope = RequiredValue(args, "--semaphore-scope");
        var timeoutMilliseconds = PositiveNumber(Value(args, "--timeout-ms"), HolderTimeoutMilliseconds);
        var childReport = NewChildReport("holder", expectedLeaseAcquired: true);
        childReport.RootNamespaceRenameBeforeAcquireSucceeded = RootNamespaceRenameSucceeds(leaseRoot);

        if (!SingleInstanceLease.TryAcquire(leaseRoot, out var lease, semaphoreScope) || lease is null)
        {
            childReport.LeaseAcquired = false;
            childReport.ExitCode = 21;
            childReport.Status = "FAIL";
            childReport.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteJson(reportPath, childReport);
            return childReport.ExitCode;
        }

        childReport.LeaseAcquired = true;
        childReport.DistinctSemaphoreScopeUsed = true;
        var lockFilePath = lease.LockFilePath;
        using (lease)
        {
            childReport.LockFileName = Path.GetFileName(lockFilePath);
            childReport.LockFileExists = File.Exists(lockFilePath);
            childReport.DirectLockOpenBlockedWhileHeld = DirectLockOpenIsBlocked(lockFilePath);
            childReport.RootNamespaceRenameBlockedWhileHeld = RootNamespaceRenameIsBlocked(leaseRoot);
            WriteMarker(readyMarker);
            childReport.ReadyMarkerRecorded = true;
            var watch = Stopwatch.StartNew();
            while (!File.Exists(releaseMarker) && watch.ElapsedMilliseconds < timeoutMilliseconds)
                Thread.Sleep(25);
            childReport.ReleaseMarkerObserved = File.Exists(releaseMarker);
            if (childReport.ReleaseMarkerObserved != true)
            {
                childReport.ExitCode = 22;
                childReport.Status = "FAIL";
            }
        }

        childReport.LeaseReleased = true;
        childReport.DirectLockOpenAfterReleaseSucceeded = DirectLockOpenSucceeds(lockFilePath);
        childReport.RootNamespaceRenameAfterReleaseSucceeded = RootNamespaceRenameSucceeds(leaseRoot);
        if (childReport.ExitCode == 0) childReport.Status = "PASS";
        childReport.CompletedAtUtc = DateTimeOffset.UtcNow;
        WriteJson(reportPath, childReport);
        return childReport.ExitCode;
    }

    private static int RunContenderChild(IReadOnlyList<string> args)
    {
        var leaseRoot = RequiredValue(args, "--lease-root");
        var reportPath = RequiredValue(args, "--report");
        var expectedAcquired = BooleanValue(RequiredValue(args, "--expect-acquired"));
        var semaphoreScope = RequiredValue(args, "--semaphore-scope");
        var childReport = NewChildReport("contender", expectedAcquired);
        if (expectedAcquired)
            childReport.RootNamespaceRenameBeforeAcquireSucceeded = RootNamespaceRenameSucceeds(leaseRoot);

        var acquired = SingleInstanceLease.TryAcquire(leaseRoot, out var lease, semaphoreScope) && lease is not null;
        childReport.LeaseAcquired = acquired;
        childReport.DistinctSemaphoreScopeUsed = true;
        var lockFilePath = lease?.LockFilePath
                           ?? Path.Combine(Path.GetFullPath(leaseRoot), SingleInstanceLease.LockFileName);
        childReport.LockFileName = Path.GetFileName(lockFilePath);
        childReport.LockFileExists = File.Exists(lockFilePath);
        childReport.DirectLockOpenBlockedWhileHeld = DirectLockOpenIsBlocked(lockFilePath);
        if (acquired)
            childReport.RootNamespaceRenameBlockedWhileHeld = RootNamespaceRenameIsBlocked(leaseRoot);
        lease?.Dispose();
        childReport.LeaseReleased = acquired;
        if (acquired)
        {
            childReport.DirectLockOpenAfterReleaseSucceeded = DirectLockOpenSucceeds(lockFilePath);
            childReport.RootNamespaceRenameAfterReleaseSucceeded = RootNamespaceRenameSucceeds(leaseRoot);
        }
        childReport.ExitCode = acquired == expectedAcquired ? 0 : 23;
        childReport.Status = childReport.ExitCode == 0 ? "PASS" : "FAIL";
        childReport.CompletedAtUtc = DateTimeOffset.UtcNow;
        WriteJson(reportPath, childReport);
        return childReport.ExitCode;
    }

    private static ChildLeaseReport NewChildReport(string role, bool expectedLeaseAcquired) => new()
    {
        SchemaVersion = ReportSchemaVersion,
        Status = "FAIL",
        Role = role,
        ExpectedLeaseAcquired = expectedLeaseAcquired,
        StartedAtUtc = DateTimeOffset.UtcNow,
        ProcessId = Environment.ProcessId,
        UserDataUsed = false,
        ExternalOperations = "none"
    };

    private static Process StartSelfProcess(params string[] arguments)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The UI harness process path is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)
               ?? throw new InvalidOperationException("The UI harness child process did not start.");
    }

    private static void InitializeObservation(ChildObservation observation, Process process)
    {
        observation.Started = true;
        observation.ProcessId = process.Id;
    }

    private static void WaitForMarker(string markerPath, Process process, int timeoutMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (File.Exists(markerPath)) return;
            if (process.HasExited)
                throw new InvalidOperationException("The holder exited before publishing its ready marker.");
            Thread.Sleep(25);
        }
        throw new TimeoutException("The holder did not publish its ready marker within the bounded wait.");
    }

    private static void WaitForExit(Process process, int timeoutMilliseconds, string role)
    {
        if (!process.WaitForExit(timeoutMilliseconds))
            throw new TimeoutException($"The {role} child did not exit within the bounded wait.");
    }

    private static void CaptureCompletedChild(
        ChildObservation observation,
        Process process,
        string childReportPath)
    {
        observation.Exited = process.HasExited;
        if (observation.Exited) observation.ExitCode = process.ExitCode;
        CaptureChildReport(observation, childReportPath);
    }

    private static void CaptureChildReport(ChildObservation observation, string childReportPath)
    {
        if (!File.Exists(childReportPath)) return;
        try
        {
            var json = File.ReadAllText(childReportPath, Encoding.UTF8);
            var childReport = JsonSerializer.Deserialize<ChildLeaseReport>(
                json,
                JsonOptions);
            if (childReport is null) return;
            observation.ReportObserved = true;
            observation.ReportPrivacySafe = IsPrivacySafeChildReport(childReport, json);
            observation.ChildSchemaVersion = childReport.SchemaVersion;
            observation.ReportedRole = childReport.Role;
            observation.ReportedExpectedLeaseAcquired = childReport.ExpectedLeaseAcquired;
            observation.ReportedProcessId = childReport.ProcessId;
            observation.UserDataUsed = childReport.UserDataUsed;
            observation.ExternalOperations = childReport.ExternalOperations;
            observation.ChildFailureType = childReport.FailureType;
            observation.LeaseAcquired = childReport.LeaseAcquired;
            observation.ReadyMarkerRecorded = childReport.ReadyMarkerRecorded;
            observation.ReleaseMarkerObserved = childReport.ReleaseMarkerObserved;
            observation.LeaseReleased = childReport.LeaseReleased;
            observation.LockFileName = childReport.LockFileName;
            observation.LockFileExists = childReport.LockFileExists;
            observation.DirectLockOpenBlockedWhileHeld = childReport.DirectLockOpenBlockedWhileHeld;
            observation.DirectLockOpenAfterReleaseSucceeded = childReport.DirectLockOpenAfterReleaseSucceeded;
            observation.DistinctSemaphoreScopeUsed = childReport.DistinctSemaphoreScopeUsed;
            observation.RootNamespaceRenameBeforeAcquireSucceeded =
                childReport.RootNamespaceRenameBeforeAcquireSucceeded;
            observation.RootNamespaceRenameBlockedWhileHeld = childReport.RootNamespaceRenameBlockedWhileHeld;
            observation.RootNamespaceRenameAfterReleaseSucceeded =
                childReport.RootNamespaceRenameAfterReleaseSucceeded;
            observation.ChildReportedStatus = childReport.Status;
            observation.ChildReportedExitCode = childReport.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            observation.ReportReadFailureType = exception.GetType().Name;
        }
    }

    private static void RequireExpectedResult(ChildObservation observation)
    {
        var expectedReportedRole = observation.Role.Equals("holder", StringComparison.Ordinal)
            ? "holder"
            : "contender";
        if (!observation.Exited || observation.ExitCode != 0)
            throw new InvalidOperationException($"The {observation.Role} child returned a failing exit code.");
        if (!observation.ReportObserved)
            throw new InvalidOperationException($"The {observation.Role} child report was not observed.");
        if (observation.ReportPrivacySafe != true
            || observation.ChildSchemaVersion != ReportSchemaVersion
            || !string.Equals(observation.ReportedRole, expectedReportedRole, StringComparison.Ordinal)
            || observation.ReportedExpectedLeaseAcquired != observation.ExpectedLeaseAcquired
            || observation.ReportedProcessId != observation.ProcessId
            || observation.UserDataUsed != false
            || !string.Equals(observation.ExternalOperations, "none", StringComparison.Ordinal)
            || observation.ChildFailureType is not null)
        {
            throw new InvalidOperationException(
                $"The {observation.Role} child report schema, identity, or privacy evidence was invalid.");
        }
        if (observation.LeaseAcquired != observation.ExpectedLeaseAcquired)
            throw new InvalidOperationException($"The {observation.Role} lease result did not match its expectation.");
        if (!string.Equals(
                observation.LockFileName,
                SingleInstanceLease.LockFileName,
                StringComparison.Ordinal)
            || observation.LockFileExists != true)
            throw new InvalidOperationException($"The {observation.Role} did not observe the cross-session lock file.");
        if (observation.DirectLockOpenBlockedWhileHeld != true)
            throw new InvalidOperationException($"The {observation.Role} did not observe an exclusive file lease.");
        if (observation.DistinctSemaphoreScopeUsed != true)
            throw new InvalidOperationException($"The {observation.Role} did not bypass the shared Local semaphore namespace.");
        if (observation.ExpectedLeaseAcquired
            && observation.RootNamespaceRenameBeforeAcquireSucceeded != true)
        {
            throw new InvalidOperationException(
                $"The {observation.Role} data root could not be renamed before lease acquisition.");
        }
        if (observation.ExpectedLeaseAcquired
            && observation.RootNamespaceRenameBlockedWhileHeld != true)
        {
            throw new InvalidOperationException(
                $"The {observation.Role} did not hold the data-root namespace against rename.");
        }
        if (observation.ExpectedLeaseAcquired && observation.DirectLockOpenAfterReleaseSucceeded != true)
            throw new InvalidOperationException($"The {observation.Role} file lease remained locked after disposal.");
        if (observation.ExpectedLeaseAcquired
            && observation.RootNamespaceRenameAfterReleaseSucceeded != true)
        {
            throw new InvalidOperationException(
                $"The {observation.Role} data root could not be renamed after lease disposal.");
        }
        if (observation.ExpectedLeaseAcquired && observation.LeaseReleased != true)
            throw new InvalidOperationException($"The {observation.Role} did not report releasing its lease.");
        if (!string.Equals(observation.ChildReportedStatus, "PASS", StringComparison.Ordinal))
            throw new InvalidOperationException($"The {observation.Role} child did not report PASS.");
        if (observation.ChildReportedExitCode != observation.ExitCode)
            throw new InvalidOperationException($"The {observation.Role} child report and OS exit code differed.");
    }

    private static void CleanupChild(
        Process? process,
        ChildObservation observation,
        string childReportPath)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited && !process.WaitForExit(2_000))
            {
                process.Kill(entireProcessTree: true);
                observation.KilledDuringCleanup = true;
                _ = process.WaitForExit(10_000);
            }
            observation.Exited = process.HasExited;
            if (observation.Exited) observation.ExitCode = process.ExitCode;
            CaptureChildReport(observation, childReportPath);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            observation.CleanupFailureType = exception.GetType().Name;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryWriteUnhandledChildFailure(
        IReadOnlyList<string> args,
        string role,
        Exception exception)
    {
        var reportPath = Value(args, "--report");
        if (string.IsNullOrWhiteSpace(reportPath)) return;
        try
        {
            var report = NewChildReport(role, expectedLeaseAcquired: false);
            report.Status = "FAIL";
            report.ExitCode = 1;
            report.FailureType = exception.GetType().Name;
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteJson(reportPath, report);
        }
        catch (Exception sinkFailure) when (sinkFailure is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cross-process probe error sink unavailable ({sinkFailure.GetType().Name}).");
        }
    }

    private static void WriteMarker(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "synthetic probe marker" + Environment.NewLine, new UTF8Encoding(false));
    }

    private static bool DirectLockOpenIsBlocked(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            return true;
        }
    }

    private static bool DirectLockOpenSucceeds(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
        return true;
    }

    private static bool RootNamespaceRenameIsBlocked(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var movedRoot = fullRoot + ".rename-probe-" + Guid.NewGuid().ToString("N");
        var moved = false;
        try
        {
            try
            {
                Directory.Move(fullRoot, movedRoot);
                moved = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }
            Directory.Move(movedRoot, fullRoot);
            moved = false;
            return false;
        }
        finally
        {
            if (moved && !Directory.Exists(fullRoot) && Directory.Exists(movedRoot))
                Directory.Move(movedRoot, fullRoot);
        }
    }

    private static bool RootNamespaceRenameSucceeds(string root) =>
        !RootNamespaceRenameIsBlocked(root);

    private static bool IsPrivacySafeChildReport(ChildLeaseReport report, string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
        var properties = document.RootElement.EnumerateObject().ToArray();
        if (properties.Length != ExpectedChildReportProperties.Count
            || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
            != ExpectedChildReportProperties.Count
            || properties.Any(property => !ExpectedChildReportProperties.Contains(property.Name)))
        {
            return false;
        }

        return !report.UserDataUsed
               && string.Equals(report.ExternalOperations, "none", StringComparison.Ordinal)
               && string.Equals(report.LockFileName, SingleInstanceLease.LockFileName, StringComparison.Ordinal)
               && report.FailureType is null
               && report.StartedAtUtc != default
               && report.CompletedAtUtc >= report.StartedAtUtc
               && !ContainsAbsolutePathMaterial(json);
    }

    private static bool ContainsAbsolutePathMaterial(string value)
    {
        for (var index = 0; index < value.Length - 2; index++)
        {
            if (char.IsAsciiLetter(value[index])
                && value[index + 1] == ':'
                && value[index + 2] is '\\' or '/')
            {
                return true;
            }
        }
        return value.Contains("\\\\", StringComparison.Ordinal);
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static void WriteJson<T>(string path, T report)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false));
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Has(IReadOnlyList<string> args, string name) =>
        args.Any(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string RequiredValue(IReadOnlyList<string> args, string name)
    {
        var value = Value(args, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"A value is required for {name}.")
            : value;
    }

    private static string Value(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return string.Empty;
    }

    private static bool BooleanValue(string value) => value.ToUpperInvariant() switch
    {
        "TRUE" => true,
        "FALSE" => false,
        _ => throw new ArgumentException("The boolean probe value must be true or false.")
    };

    private static int PositiveNumber(string value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private sealed class ParentProbeReport
    {
        public int SchemaVersion { get; init; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public string DataRootKind { get; init; } = string.Empty;
        public string AliasKind { get; init; } = string.Empty;
        public bool AliasInputDiffers { get; set; }
        public bool AliasFullPathMatches { get; set; }
        public bool UserDataUsed { get; init; }
        public string ExternalOperations { get; init; } = string.Empty;
        public bool DistinctLocalSemaphoreScopes { get; init; }
        public bool StartupSwapHookCalled { get; set; }
        public bool StartupSwapRejected { get; set; }
        public string? StartupSwapFailureType { get; set; }
        public bool StartupSwapOriginalPreserved { get; set; }
        public bool StartupSwapReplacementPreserved { get; set; }
        public bool StartupSwapReplacementReacquired { get; set; }
        public ChildObservation Holder { get; init; } = new();
        public ChildObservation Contender { get; init; } = new();
        public ChildObservation Reacquirer { get; init; } = new();
        public CleanupObservation Cleanup { get; init; } = new();
        public string? FailureType { get; set; }
    }

    private sealed class ChildObservation
    {
        public string Role { get; init; } = string.Empty;
        public bool ExpectedLeaseAcquired { get; init; }
        public bool Started { get; set; }
        public int? ProcessId { get; set; }
        public bool Exited { get; set; }
        public int? ExitCode { get; set; }
        public bool ReportObserved { get; set; }
        public bool? ReportPrivacySafe { get; set; }
        public int? ChildSchemaVersion { get; set; }
        public string? ReportedRole { get; set; }
        public bool? ReportedExpectedLeaseAcquired { get; set; }
        public int? ReportedProcessId { get; set; }
        public bool? UserDataUsed { get; set; }
        public string? ExternalOperations { get; set; }
        public string? ChildFailureType { get; set; }
        public bool? LeaseAcquired { get; set; }
        public bool? ReadyMarkerRecorded { get; set; }
        public bool ReadyMarkerObserved { get; set; }
        public bool? ReleaseMarkerObserved { get; set; }
        public bool? LeaseReleased { get; set; }
        public string? LockFileName { get; set; }
        public bool? LockFileExists { get; set; }
        public bool? DirectLockOpenBlockedWhileHeld { get; set; }
        public bool? DirectLockOpenAfterReleaseSucceeded { get; set; }
        public bool? DistinctSemaphoreScopeUsed { get; set; }
        public bool? RootNamespaceRenameBeforeAcquireSucceeded { get; set; }
        public bool? RootNamespaceRenameBlockedWhileHeld { get; set; }
        public bool? RootNamespaceRenameAfterReleaseSucceeded { get; set; }
        public string? ChildReportedStatus { get; set; }
        public int? ChildReportedExitCode { get; set; }
        public bool KilledDuringCleanup { get; set; }
        public string? ReportReadFailureType { get; set; }
        public string? CleanupFailureType { get; set; }
    }

    private sealed class CleanupObservation
    {
        public bool ReleaseMarkerWritten { get; set; }
        public string? ReleaseMarkerWriteFailureType { get; set; }
        public bool AllChildrenExited { get; set; }
        public bool TempRootRemoved { get; set; }
        public string? TempRootRemovalFailureType { get; set; }
    }

    private sealed class ChildLeaseReport
    {
        public int SchemaVersion { get; init; }
        public string Status { get; set; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public bool ExpectedLeaseAcquired { get; init; }
        public bool? LeaseAcquired { get; set; }
        public bool? ReadyMarkerRecorded { get; set; }
        public bool? ReleaseMarkerObserved { get; set; }
        public bool? LeaseReleased { get; set; }
        public string? LockFileName { get; set; }
        public bool? LockFileExists { get; set; }
        public bool? DirectLockOpenBlockedWhileHeld { get; set; }
        public bool? DirectLockOpenAfterReleaseSucceeded { get; set; }
        public bool? DistinctSemaphoreScopeUsed { get; set; }
        public bool? RootNamespaceRenameBeforeAcquireSucceeded { get; set; }
        public bool? RootNamespaceRenameBlockedWhileHeld { get; set; }
        public bool? RootNamespaceRenameAfterReleaseSucceeded { get; set; }
        public int ExitCode { get; set; }
        public int ProcessId { get; init; }
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public bool UserDataUsed { get; init; }
        public string ExternalOperations { get; init; } = string.Empty;
        public string? FailureType { get; set; }
    }
}
