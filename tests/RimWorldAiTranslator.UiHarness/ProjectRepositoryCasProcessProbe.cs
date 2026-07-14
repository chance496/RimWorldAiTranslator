using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.UiHarness;

internal static class ProjectRepositoryCasProcessProbe
{
    private const string ParentMode = "--project-repository-cas-probe";
    private const string ChildMode = "--project-repository-cas-child";
    private const string WriterA = "writer-a";
    private const string WriterB = "writer-b";
    private const string SeedProjectId = "synthetic-seed";
    private const string WriterAProjectId = "synthetic-writer-a";
    private const string WriterBProjectId = "synthetic-writer-b";
    private const string ExplicitConflictMessage =
        "The project store changed after it was loaded. Reload it before saving again.";
    private const int ParentTimeoutMilliseconds = 20_000;
    private const int ChildTimeoutMilliseconds = 45_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static bool TryRun(IReadOnlyList<string> args, out int exitCode)
    {
        var isParent = Has(args, ParentMode);
        var isChild = Has(args, ChildMode);
        if (!isParent && !isChild)
        {
            exitCode = 0;
            return false;
        }

        if (isParent == isChild)
        {
            exitCode = 64;
            return true;
        }

        try
        {
            exitCode = isParent
                ? RunParent(RequiredValue(args, "--report"))
                : RunChild(args);
        }
        catch (Exception exception)
        {
            if (isChild) TryWriteUnhandledChildFailure(args, exception);
            exitCode = 1;
        }

        return true;
    }

    private static int RunParent(string reportPath)
    {
        var runId = Guid.NewGuid().ToString("N");
        var ownedParent = Path.Combine(
            Path.GetTempPath(),
            "RimWorldAiTranslator-ui-harness-project-cas");
        var root = Path.Combine(ownedParent, runId);
        var writerAReady = Path.Combine(root, "writer-a.ready");
        var writerBReady = Path.Combine(root, "writer-b.ready");
        var writerARelease = Path.Combine(root, "writer-a.release");
        var writerBRelease = Path.Combine(root, "writer-b.release");
        var writerAReport = Path.Combine(root, "writer-a.json");
        var writerBReport = Path.Combine(root, "writer-b.json");
        var report = new ParentProbeReport
        {
            SchemaVersion = 1,
            Status = "FAIL",
            StartedAtUtc = DateTimeOffset.UtcNow,
            DataRootKind = "unique synthetic TEMP directory",
            UserDataUsed = false,
            ExternalOperations = "none",
            ExpectedProjectSchemaVersion = ProjectStoreDocument.CurrentVersion,
            WriterA = new ChildObservation { Role = WriterA },
            WriterB = new ChildObservation { Role = WriterB },
            Cleanup = new CleanupObservation()
        };
        Process? processA = null;
        Process? processB = null;
        Exception? failure = null;
        var stage = "initialize";

        try
        {
            stage = "create synthetic root";
            Directory.CreateDirectory(root);
            ValidateOwnedTempRoot(root, ownedParent, runId);

            stage = "seed project repository";
            var paths = new AppDataPaths(root);
            paths.EnsureExists();
            var repository = new ProjectRepository(new AtomicJsonStore(), paths);
            var seed = new ProjectStoreDocument();
            seed.Projects.Add(CreateProject(SeedProjectId));
            repository.Save(seed);
            report.Seed = ObserveDocument(seed, rawPath: paths.Projects);
            RequireSeed(report.Seed);

            stage = "start child writers";
            processA = StartSelfProcess(
                ChildMode,
                "--role", WriterA,
                "--data-root", root,
                "--ready-marker", writerAReady,
                "--release-marker", writerARelease,
                "--report", writerAReport,
                "--timeout-ms", ChildTimeoutMilliseconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            InitializeObservation(report.WriterA, processA);
            stage = "wait for writer A loaded revision";
            WaitForMarker(writerAReady, processA, ParentTimeoutMilliseconds, WriterA);
            report.WriterA.ReadyMarkerObserved = true;

            stage = "start stale writer B";
            processB = StartSelfProcess(
                ChildMode,
                "--role", WriterB,
                "--data-root", root,
                "--ready-marker", writerBReady,
                "--release-marker", writerBRelease,
                "--report", writerBReport,
                "--timeout-ms", ChildTimeoutMilliseconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            InitializeObservation(report.WriterB, processB);
            stage = "wait for writer B loaded revision";
            WaitForMarker(writerBReady, processB, ParentTimeoutMilliseconds, WriterB);
            report.WriterB.ReadyMarkerObserved = true;

            stage = "release writer A";
            WriteMarker(writerARelease);
            report.Cleanup.WriterAReleaseWritten = true;
            WaitForExit(processA, ParentTimeoutMilliseconds, WriterA);
            CaptureCompletedChild(report.WriterA, processA, writerAReport);
            RequireWriterA(report.WriterA, report.Seed);

            stage = "release stale writer B";
            WriteMarker(writerBRelease);
            report.Cleanup.WriterBReleaseWritten = true;
            WaitForExit(processB, ParentTimeoutMilliseconds, WriterB);
            CaptureCompletedChild(report.WriterB, processB, writerBReport);
            RequireWriterB(report.WriterB, report.Seed);
            if (report.WriterA.LoadedRevision != report.WriterB.LoadedRevision
                || !string.Equals(
                    report.WriterA.LoadedContentToken,
                    report.WriterB.LoadedContentToken,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The child writers did not report the same loaded revision and content token.");
            }

            stage = "verify final primary and backup";
            var final = repository.Load();
            var backup = ReadDocument(paths.Projects + ".bak");
            report.Final = ObserveDocument(final, rawPath: paths.Projects);
            report.Backup = ObserveDocument(backup, paths.Projects + ".bak");
            report.FinalMatchesExpected =
                IsExactProjectSet(final, SeedProjectId, WriterAProjectId)
                && final.Version == ProjectStoreDocument.CurrentVersion
                && final.Revision == report.Seed.Revision + 1
                && final.ObservedMissing == false
                && report.Final.RawVersion == ProjectStoreDocument.CurrentVersion
                && report.Final.RawRevision == report.Seed.Revision + 1
                && report.Final.RawFieldsMatchDocument
                && report.Final.ContentTokenMatchesDocument
                && IsSha256(final.ObservedContentSha256)
                && string.Equals(
                    final.ObservedContentSha256,
                    ComputeContentToken(final),
                    StringComparison.Ordinal)
                && string.Equals(
                    final.ObservedContentSha256,
                    report.WriterA.ResultContentToken,
                    StringComparison.Ordinal);
            report.BackupRetainsLastGood =
                IsExactProjectSet(backup, SeedProjectId)
                && report.Backup.Version == ProjectStoreDocument.CurrentVersion
                && report.Backup.Revision == report.Seed.Revision
                && report.Backup.RawVersion == ProjectStoreDocument.CurrentVersion
                && report.Backup.RawRevision == report.Seed.Revision
                && report.Backup.RawFieldsMatchDocument
                && report.Backup.ContentTokenMatchesDocument
                && string.Equals(
                    report.Backup.ContentToken,
                    report.Seed.ContentToken,
                    StringComparison.Ordinal);
            if (!report.FinalMatchesExpected)
                throw new InvalidOperationException("The final primary did not contain exactly seed plus writer A.");
            if (!report.BackupRetainsLastGood)
                throw new InvalidOperationException("The backup did not retain the last-good seed revision.");
        }
        catch (Exception exception)
        {
            failure = exception;
            report.FailureStage = stage;
        }
        finally
        {
            EnsureReleaseMarker(root, writerARelease, report.Cleanup, writerA: true);
            EnsureReleaseMarker(root, writerBRelease, report.Cleanup, writerA: false);
            CleanupChild(processA, report.WriterA, writerAReport);
            CleanupChild(processB, report.WriterB, writerBReport);
            report.Cleanup.AllChildrenExited = report.WriterA.Started
                                                && report.WriterB.Started
                                                && report.WriterA.Exited
                                                && report.WriterB.Exited;

            try
            {
                ValidateOwnedTempRoot(root, ownedParent, runId);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                report.Cleanup.TempRootRemoved = !Directory.Exists(root);
            }
            catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or InvalidDataException)
            {
                report.Cleanup.TempRootRemoved = false;
                report.Cleanup.TempRootRemovalFailureType = exception.GetType().Name;
            }

            if ((!report.Cleanup.AllChildrenExited || !report.Cleanup.TempRootRemoved)
                && failure is null)
            {
                failure = new InvalidOperationException(
                    "The project repository CAS probe did not complete bounded cleanup.");
                report.FailureStage = "cleanup";
            }

            report.Status = failure is null ? "PASS" : "FAIL";
            report.FailureType = failure?.GetType().Name;
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteJson(reportPath, report);
        }

        return failure is null ? 0 : 1;
    }

    private static int RunChild(IReadOnlyList<string> args)
    {
        var role = RequiredValue(args, "--role");
        if (role is not (WriterA or WriterB))
            throw new ArgumentException("The project repository child role is invalid.");
        var dataRoot = RequiredValue(args, "--data-root");
        var readyMarker = RequiredValue(args, "--ready-marker");
        var releaseMarker = RequiredValue(args, "--release-marker");
        var reportPath = RequiredValue(args, "--report");
        var timeoutMilliseconds = PositiveNumber(
            Value(args, "--timeout-ms"),
            ChildTimeoutMilliseconds);
        var report = NewChildReport(role);
        var exitCode = 0;
        var stage = "load";

        try
        {
            var repository = new ProjectRepository(
                new AtomicJsonStore(),
                new AppDataPaths(dataRoot));
            var document = repository.Load();
            report.LoadedVersion = document.Version;
            report.LoadedRevision = document.Revision;
            report.LoadedContentToken = document.ObservedContentSha256;
            report.LoadedContentTokenMatchesDocument =
                IsSha256(document.ObservedContentSha256)
                && string.Equals(
                    document.ObservedContentSha256,
                    ComputeContentToken(document),
                    StringComparison.Ordinal);
            report.LoadedObservedMissing = document.ObservedMissing;
            report.LoadedProjectIds = SortedProjectIds(document);

            stage = "mutate synthetic document";
            document.Projects.Add(CreateProject(
                role == WriterA ? WriterAProjectId : WriterBProjectId));
            WriteMarker(readyMarker);
            report.ReadyMarkerWritten = true;

            stage = "wait for parent release";
            report.ReleaseMarkerObserved = WaitForReleaseMarker(
                releaseMarker,
                timeoutMilliseconds);
            if (!report.ReleaseMarkerObserved)
                throw new TimeoutException("The parent did not release the project repository child in time.");

            stage = "save";
            try
            {
                repository.Save(document);
                report.SaveOutcome = "saved";
                report.SaveSucceeded = true;
            }
            catch (Exception exception)
            {
                report.SaveOutcome = exception is InvalidOperationException
                                     && exception.Message.Equals(
                                         ExplicitConflictMessage,
                                         StringComparison.Ordinal)
                    ? "concurrency-conflict"
                    : "unexpected-exception";
                report.SaveExceptionType = exception.GetType().Name;
                report.ExplicitConcurrencyConflict =
                    report.SaveOutcome.Equals("concurrency-conflict", StringComparison.Ordinal);
                report.ConflictCode = report.ExplicitConcurrencyConflict
                    ? "PROJECT_STORE_STALE_REVISION"
                    : null;
            }

            report.ResultVersion = document.Version;
            report.ResultRevision = document.Revision;
            report.ResultContentToken = document.ObservedContentSha256;
            report.ResultProjectIds = SortedProjectIds(document);
            report.ResultContentTokenMatchesDocument =
                IsSha256(document.ObservedContentSha256)
                && string.Equals(
                    document.ObservedContentSha256,
                    ComputeContentToken(document),
                    StringComparison.Ordinal);
            report.ObservedTokenPreservedAfterFailure =
                !report.SaveSucceeded
                && string.Equals(
                    report.ResultContentToken,
                    report.LoadedContentToken,
                    StringComparison.Ordinal);
            report.RevisionPreservedAfterFailure =
                !report.SaveSucceeded
                && report.ResultRevision == report.LoadedRevision;
            report.Status = "COMPLETED";
        }
        catch (Exception exception)
        {
            report.Status = "FAILED";
            report.FailureType = exception.GetType().Name;
            report.FailureStage = stage;
            exitCode = 1;
        }

        report.ExitCode = exitCode;
        report.CompletedAtUtc = DateTimeOffset.UtcNow;
        WriteJson(reportPath, report);
        return exitCode;
    }

    private static void RequireSeed(DocumentObservation seed)
    {
        if (seed.Version != ProjectStoreDocument.CurrentVersion
            || seed.RawVersion != ProjectStoreDocument.CurrentVersion
            || seed.Revision != 1
            || seed.RawRevision != 1
            || seed.ProjectIds.Count != 1
            || !seed.ProjectIds[0].Equals(SeedProjectId, StringComparison.Ordinal)
            || !seed.ContentTokenMatchesDocument
            || !seed.RawFieldsMatchDocument
            || !IsSha256(seed.ContentToken))
        {
            throw new InvalidOperationException("The parent seed did not persist as schema v3 revision 1.");
        }
    }

    private static void RequireWriterA(ChildObservation child, DocumentObservation seed)
    {
        RequireCommonChildEvidence(child, seed);
        if (!child.SaveSucceeded
            || !string.Equals(child.SaveOutcome, "saved", StringComparison.Ordinal)
            || child.ExplicitConcurrencyConflict
            || child.ResultVersion != ProjectStoreDocument.CurrentVersion
            || child.ResultRevision != seed.Revision + 1
            || child.ResultContentTokenMatchesDocument != true
            || !IsSha256(child.ResultContentToken)
            || !IsExactProjectSet(child.ResultProjectIds, SeedProjectId, WriterAProjectId))
        {
            throw new InvalidOperationException("Writer A did not publish the expected next revision.");
        }
    }

    private static void RequireWriterB(ChildObservation child, DocumentObservation seed)
    {
        RequireCommonChildEvidence(child, seed);
        if (child.SaveSucceeded
            || !string.Equals(child.SaveOutcome, "concurrency-conflict", StringComparison.Ordinal)
            || !child.ExplicitConcurrencyConflict
            || !string.Equals(child.ConflictCode, "PROJECT_STORE_STALE_REVISION", StringComparison.Ordinal)
            || !string.Equals(child.SaveExceptionType, nameof(InvalidOperationException), StringComparison.Ordinal)
            || child.ResultVersion != ProjectStoreDocument.CurrentVersion
            || child.ResultRevision != seed.Revision
            || !child.ObservedTokenPreservedAfterFailure
            || !child.RevisionPreservedAfterFailure
            || !IsExactProjectSet(child.ResultProjectIds, SeedProjectId, WriterBProjectId))
        {
            throw new InvalidOperationException("Writer B did not report the expected explicit stale-save conflict.");
        }
    }

    private static void RequireCommonChildEvidence(
        ChildObservation child,
        DocumentObservation seed)
    {
        if (!child.Started
            || !child.Exited
            || child.ExitCode != 0
            || !child.ReportObserved
            || child.ChildSchemaVersion != 1
            || !string.Equals(child.ReportedRole, child.Role, StringComparison.Ordinal)
            || child.ReportedProcessId != child.ProcessId
            || child.ChildReportedExitCode != child.ExitCode
            || !string.Equals(child.ChildStatus, "COMPLETED", StringComparison.Ordinal)
            || child.ReadyMarkerObserved == false
            || child.ReadyMarkerWritten != true
            || child.ReleaseMarkerObserved != true
            || child.LoadedVersion != ProjectStoreDocument.CurrentVersion
            || child.LoadedRevision != seed.Revision
            || child.LoadedObservedMissing != false
            || child.LoadedContentTokenMatchesDocument != true
            || !string.Equals(child.LoadedContentToken, seed.ContentToken, StringComparison.Ordinal)
            || !IsExactProjectSet(child.LoadedProjectIds, SeedProjectId)
            || child.UserDataUsed != false
            || !string.Equals(child.ExternalOperations, "none", StringComparison.Ordinal)
            || child.ReportPrivacySafe != true)
        {
            throw new InvalidOperationException(
                $"The {child.Role} child evidence was incomplete or unsafe.");
        }
    }

    private static DocumentObservation ObserveDocument(
        ProjectStoreDocument document,
        string rawPath)
    {
        var bytes = File.ReadAllBytes(rawPath);
        using var raw = JsonDocument.Parse(bytes);
        var rawVersion = raw.RootElement.GetProperty("version").GetInt32();
        var rawRevision = raw.RootElement.GetProperty("revision").GetInt64();
        var token = ComputeContentToken(document);
        return new DocumentObservation
        {
            Version = document.Version,
            Revision = document.Revision,
            ProjectIds = SortedProjectIds(document),
            ContentToken = token,
            ContentTokenMatchesDocument = IsSha256(token)
                                          && (document.ObservedContentSha256 is null
                                              || string.Equals(
                                                  document.ObservedContentSha256,
                                                  token,
                                                  StringComparison.Ordinal)),
            RawVersion = rawVersion,
            RawRevision = rawRevision,
            RawFieldsMatchDocument = rawVersion == document.Version
                                     && rawRevision == document.Revision
        };
    }

    private static ProjectStoreDocument ReadDocument(string path)
    {
        var document = JsonSerializer.Deserialize<ProjectStoreDocument>(
            File.ReadAllBytes(path),
            JsonOptions);
        return document ?? throw new InvalidDataException("The synthetic project document was empty.");
    }

    private static string ComputeContentToken(ProjectStoreDocument document) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document)));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9'
                                  or >= 'A' and <= 'F');

    private static TranslationProject CreateProject(string id) => new()
    {
        Id = id,
        Name = id,
        ModRoot = id + "-root",
        SourceKind = "synthetic",
        PackageId = "synthetic." + id,
        WorkshopId = string.Empty,
        SourceLanguageFolder = "English",
        LatestReviewRoot = string.Empty,
        LatestReviewAt = string.Empty,
        LastAppliedAt = string.Empty,
        CreatedAt = "2026-01-01T00:00:00.0000000+00:00",
        UpdatedAt = "2026-01-01T00:00:00.0000000+00:00"
    };

    private static List<string> SortedProjectIds(ProjectStoreDocument document) =>
        document.Projects.Select(project => project.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private static bool IsExactProjectSet(
        ProjectStoreDocument document,
        params string[] expected) =>
        IsExactProjectSet(SortedProjectIds(document), expected);

    private static bool IsExactProjectSet(
        IReadOnlyCollection<string>? actual,
        params string[] expected)
    {
        if (actual is null || actual.Count != expected.Length) return false;
        return actual.OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);
    }

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
        if (Path.GetFileNameWithoutExtension(processPath).Equals(
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)
               ?? throw new InvalidOperationException("The UI harness child process did not start.");
    }

    private static void InitializeObservation(ChildObservation observation, Process process)
    {
        observation.Started = true;
        observation.ProcessId = process.Id;
    }

    private static void WaitForMarker(
        string markerPath,
        Process process,
        int timeoutMilliseconds,
        string role)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (File.Exists(markerPath)) return;
            if (process.HasExited)
                throw new InvalidOperationException($"The {role} child exited before its ready marker.");
            Thread.Sleep(25);
        }
        throw new TimeoutException($"The {role} child did not publish its ready marker in time.");
    }

    private static bool WaitForReleaseMarker(string markerPath, int timeoutMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (File.Exists(markerPath)) return true;
            Thread.Sleep(25);
        }
        return File.Exists(markerPath);
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
            var child = JsonSerializer.Deserialize<ChildProbeReport>(json, JsonOptions);
            if (child is null) return;
            observation.ReportObserved = true;
            observation.ReportPrivacySafe = IsPrivacySafeChildReport(child, json);
            observation.ChildSchemaVersion = child.SchemaVersion;
            observation.ReportedRole = child.Role;
            observation.ReportedProcessId = child.ProcessId;
            observation.ChildStatus = child.Status;
            observation.ReadyMarkerWritten = child.ReadyMarkerWritten;
            observation.ReleaseMarkerObserved = child.ReleaseMarkerObserved;
            observation.LoadedVersion = child.LoadedVersion;
            observation.LoadedRevision = child.LoadedRevision;
            observation.LoadedContentToken = child.LoadedContentToken;
            observation.LoadedContentTokenMatchesDocument = child.LoadedContentTokenMatchesDocument;
            observation.LoadedObservedMissing = child.LoadedObservedMissing;
            observation.LoadedProjectIds = child.LoadedProjectIds;
            observation.SaveOutcome = child.SaveOutcome;
            observation.SaveSucceeded = child.SaveSucceeded;
            observation.ExplicitConcurrencyConflict = child.ExplicitConcurrencyConflict;
            observation.ConflictCode = child.ConflictCode;
            observation.SaveExceptionType = child.SaveExceptionType;
            observation.ResultVersion = child.ResultVersion;
            observation.ResultRevision = child.ResultRevision;
            observation.ResultContentToken = child.ResultContentToken;
            observation.ResultContentTokenMatchesDocument = child.ResultContentTokenMatchesDocument;
            observation.ResultProjectIds = child.ResultProjectIds;
            observation.ObservedTokenPreservedAfterFailure = child.ObservedTokenPreservedAfterFailure;
            observation.RevisionPreservedAfterFailure = child.RevisionPreservedAfterFailure;
            observation.UserDataUsed = child.UserDataUsed;
            observation.ExternalOperations = child.ExternalOperations;
            observation.ChildReportedExitCode = child.ExitCode;
            observation.ChildFailureType = child.FailureType;
            observation.ChildFailureStage = child.FailureStage;
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException)
        {
            observation.ReportReadFailureType = exception.GetType().Name;
        }
    }

    private static bool IsPrivacySafeChildReport(ChildProbeReport report, string json)
    {
        if (report.UserDataUsed
            || !string.Equals(report.ExternalOperations, "none", StringComparison.Ordinal)
            || ContainsAbsolutePathMaterial(json))
        {
            return false;
        }

        var allowedStrings = new HashSet<string>(StringComparer.Ordinal)
        {
            string.Empty,
            WriterA,
            WriterB,
            "COMPLETED",
            "FAILED",
            "saved",
            "concurrency-conflict",
            "unexpected-exception",
            "PROJECT_STORE_STALE_REVISION",
            nameof(InvalidOperationException),
            "none",
            SeedProjectId,
            WriterAProjectId,
            WriterBProjectId
        };
        return allowedStrings.Contains(report.Role)
               && allowedStrings.Contains(report.Status)
               && allowedStrings.Contains(report.SaveOutcome ?? string.Empty)
               && allowedStrings.Contains(report.ConflictCode ?? string.Empty)
               && allowedStrings.Contains(report.SaveExceptionType ?? string.Empty)
               && allowedStrings.Contains(report.ExternalOperations)
               && (report.LoadedContentToken is null || IsSha256(report.LoadedContentToken))
               && (report.ResultContentToken is null || IsSha256(report.ResultContentToken))
               && report.LoadedProjectIds.All(allowedStrings.Contains)
               && report.ResultProjectIds.All(allowedStrings.Contains)
               && report.FailureType is null
               && report.FailureStage is null;
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
        catch (Exception exception) when (exception is InvalidOperationException
                                               or System.ComponentModel.Win32Exception)
        {
            observation.CleanupFailureType = exception.GetType().Name;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void EnsureReleaseMarker(
        string root,
        string marker,
        CleanupObservation cleanup,
        bool writerA)
    {
        if (writerA ? cleanup.WriterAReleaseWritten : cleanup.WriterBReleaseWritten) return;
        try
        {
            if (!Directory.Exists(root)) return;
            WriteMarker(marker);
            if (writerA) cleanup.WriterAReleaseWritten = true;
            else cleanup.WriterBReleaseWritten = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (writerA) cleanup.WriterAReleaseFailureType = exception.GetType().Name;
            else cleanup.WriterBReleaseFailureType = exception.GetType().Name;
        }
    }

    private static void TryWriteUnhandledChildFailure(
        IReadOnlyList<string> args,
        Exception exception)
    {
        var reportPath = Value(args, "--report");
        var role = Value(args, "--role");
        if (string.IsNullOrWhiteSpace(reportPath)
            || role is not (WriterA or WriterB))
        {
            return;
        }

        try
        {
            var report = NewChildReport(role);
            report.Status = "FAILED";
            report.ExitCode = 1;
            report.FailureType = exception.GetType().Name;
            report.FailureStage = "dispatch";
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteJson(reportPath, report);
        }
        catch (Exception sinkFailure) when (sinkFailure is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(
                $"Project repository probe error sink unavailable ({sinkFailure.GetType().Name}).");
        }
    }

    private static ChildProbeReport NewChildReport(string role) => new()
    {
        SchemaVersion = 1,
        Status = "FAILED",
        Role = role,
        StartedAtUtc = DateTimeOffset.UtcNow,
        ProcessId = Environment.ProcessId,
        UserDataUsed = false,
        ExternalOperations = "none"
    };

    private static void WriteMarker(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            "synthetic project repository probe marker" + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static void WriteJson<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(value, JsonOptions),
            new UTF8Encoding(false));
    }

    private static void ValidateOwnedTempRoot(string root, string ownedParent, string runId)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedParent));
        if (!string.Equals(Path.GetDirectoryName(fullRoot), fullParent, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(fullRoot), runId, StringComparison.Ordinal)
            || runId.Length != 32
            || !runId.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The synthetic TEMP cleanup root failed ownership validation.");
        }
    }

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
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return string.Empty;
    }

    private static int PositiveNumber(string value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private sealed class ParentProbeReport
    {
        public int SchemaVersion { get; init; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public string DataRootKind { get; init; } = string.Empty;
        public bool UserDataUsed { get; init; }
        public string ExternalOperations { get; init; } = string.Empty;
        public int ExpectedProjectSchemaVersion { get; init; }
        public DocumentObservation Seed { get; set; } = new();
        public ChildObservation WriterA { get; init; } = new();
        public ChildObservation WriterB { get; init; } = new();
        public DocumentObservation Final { get; set; } = new();
        public DocumentObservation Backup { get; set; } = new();
        public bool FinalMatchesExpected { get; set; }
        public bool BackupRetainsLastGood { get; set; }
        public CleanupObservation Cleanup { get; init; } = new();
        public string? FailureStage { get; set; }
        public string? FailureType { get; set; }
    }

    private sealed class DocumentObservation
    {
        public int Version { get; init; }
        public long Revision { get; init; }
        public List<string> ProjectIds { get; init; } = [];
        public string ContentToken { get; init; } = string.Empty;
        public bool ContentTokenMatchesDocument { get; init; }
        public int RawVersion { get; init; }
        public long RawRevision { get; init; }
        public bool RawFieldsMatchDocument { get; init; }
    }

    private sealed class ChildObservation
    {
        public string Role { get; init; } = string.Empty;
        public bool Started { get; set; }
        public int? ProcessId { get; set; }
        public bool Exited { get; set; }
        public int? ExitCode { get; set; }
        public bool ReadyMarkerObserved { get; set; }
        public bool ReportObserved { get; set; }
        public bool? ReportPrivacySafe { get; set; }
        public int? ChildSchemaVersion { get; set; }
        public string? ReportedRole { get; set; }
        public int? ReportedProcessId { get; set; }
        public string? ChildStatus { get; set; }
        public bool? ReadyMarkerWritten { get; set; }
        public bool? ReleaseMarkerObserved { get; set; }
        public int? LoadedVersion { get; set; }
        public long? LoadedRevision { get; set; }
        public string? LoadedContentToken { get; set; }
        public bool? LoadedContentTokenMatchesDocument { get; set; }
        public bool? LoadedObservedMissing { get; set; }
        public List<string>? LoadedProjectIds { get; set; }
        public string? SaveOutcome { get; set; }
        public bool SaveSucceeded { get; set; }
        public bool ExplicitConcurrencyConflict { get; set; }
        public string? ConflictCode { get; set; }
        public string? SaveExceptionType { get; set; }
        public int? ResultVersion { get; set; }
        public long? ResultRevision { get; set; }
        public string? ResultContentToken { get; set; }
        public bool? ResultContentTokenMatchesDocument { get; set; }
        public List<string>? ResultProjectIds { get; set; }
        public bool ObservedTokenPreservedAfterFailure { get; set; }
        public bool RevisionPreservedAfterFailure { get; set; }
        public bool? UserDataUsed { get; set; }
        public string? ExternalOperations { get; set; }
        public int? ChildReportedExitCode { get; set; }
        public string? ChildFailureType { get; set; }
        public string? ChildFailureStage { get; set; }
        public bool KilledDuringCleanup { get; set; }
        public string? ReportReadFailureType { get; set; }
        public string? CleanupFailureType { get; set; }
    }

    private sealed class CleanupObservation
    {
        public bool WriterAReleaseWritten { get; set; }
        public bool WriterBReleaseWritten { get; set; }
        public string? WriterAReleaseFailureType { get; set; }
        public string? WriterBReleaseFailureType { get; set; }
        public bool AllChildrenExited { get; set; }
        public bool TempRootRemoved { get; set; }
        public string? TempRootRemovalFailureType { get; set; }
    }

    private sealed class ChildProbeReport
    {
        public int SchemaVersion { get; init; }
        public string Status { get; set; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public int ProcessId { get; init; }
        public bool UserDataUsed { get; init; }
        public string ExternalOperations { get; init; } = string.Empty;
        public bool ReadyMarkerWritten { get; set; }
        public bool ReleaseMarkerObserved { get; set; }
        public int LoadedVersion { get; set; }
        public long LoadedRevision { get; set; }
        public string? LoadedContentToken { get; set; }
        public bool LoadedContentTokenMatchesDocument { get; set; }
        public bool LoadedObservedMissing { get; set; }
        public List<string> LoadedProjectIds { get; set; } = [];
        public string? SaveOutcome { get; set; }
        public bool SaveSucceeded { get; set; }
        public bool ExplicitConcurrencyConflict { get; set; }
        public string? ConflictCode { get; set; }
        public string? SaveExceptionType { get; set; }
        public int ResultVersion { get; set; }
        public long ResultRevision { get; set; }
        public string? ResultContentToken { get; set; }
        public bool ResultContentTokenMatchesDocument { get; set; }
        public List<string> ResultProjectIds { get; set; } = [];
        public bool ObservedTokenPreservedAfterFailure { get; set; }
        public bool RevisionPreservedAfterFailure { get; set; }
        public int ExitCode { get; set; }
        public string? FailureType { get; set; }
        public string? FailureStage { get; set; }
    }
}
