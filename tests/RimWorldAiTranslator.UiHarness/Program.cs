using RimWorldAiTranslator.App;
using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.App.Dialogs;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Logging;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Storage;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RimWorldAiTranslator.UiHarness;

internal static class Program
{
    private static readonly JsonSerializerOptions FailureJsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static void Main(string[] args)
    {
        if (ProjectRepositoryCasProcessProbe.TryRun(args, out var probeExitCode))
        {
            Environment.ExitCode = probeExitCode;
            return;
        }
        if (CrossProcessSingleInstanceProbe.TryRun(args, out probeExitCode))
        {
            Environment.ExitCode = probeExitCode;
            return;
        }
        if (Phase08MainFormMemoryProbe.TryRun(args, out probeExitCode))
        {
            Environment.ExitCode = probeExitCode;
            return;
        }
        if (RecoveryNoticePresentationProbe.TryRun(args, out probeExitCode))
        {
            Environment.ExitCode = probeExitCode;
            return;
        }
        if (LocalGapClosureProbe.TryRun(args, out probeExitCode))
        {
            Environment.ExitCode = probeExitCode;
            return;
        }

        var options = SnapshotOptions.Parse(args);
        Application.SetHighDpiMode(options.DpiUnaware ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!string.IsNullOrWhiteSpace(options.CompareBaseline)
            || !string.IsNullOrWhiteSpace(options.CompareCandidate))
        {
            if (string.IsNullOrWhiteSpace(options.CompareBaseline)
                || string.IsNullOrWhiteSpace(options.CompareCandidate)
                || string.IsNullOrWhiteSpace(options.CompareOutput))
                throw new ArgumentException("Snapshot comparison requires --compare-baseline, --compare-candidate and --compare-output.");
            SnapshotComparison.Create(
                options.CompareBaseline,
                options.CompareCandidate,
                options.CompareOutput,
                options.CompareName);
            return;
        }

        var runId = Guid.NewGuid().ToString("N");
        var ownsRoot = string.IsNullOrWhiteSpace(options.DataRoot);
        var root = ownsRoot
            ? Path.Combine(Path.GetTempPath(), "RimWorldAiTranslator-ui-harness", runId)
            : Path.GetFullPath(options.DataRoot);
        var ownsDiscoveryRoot = string.IsNullOrWhiteSpace(options.DiscoveryRoot);
        var discoveryRoot = ownsDiscoveryRoot
            ? Path.Combine(Path.GetTempPath(), "RimWorldAiTranslator-ui-harness-discovery", runId)
            : options.DiscoveryRoot;
        var errorOutput = string.IsNullOrWhiteSpace(options.ErrorOutput)
            ? Path.Combine(Path.GetTempPath(), "RimWorldAiTranslator-ui-harness-errors", runId + ".txt")
            : Path.GetFullPath(options.ErrorOutput);
        System.Windows.Forms.Timer? closeRaceTimer = null;
        System.Windows.Forms.Timer? ioProbeTimer = null;
        System.Windows.Forms.Timer? loggerDrainTimer = null;
        var glossaryProbeRelease = new ManualResetEventSlim(false);
        var rmkProbeRelease = new ManualResetEventSlim(false);
        var glossaryProbeStarted = new ManualResetEventSlim(false);
        var rmkProbeStarted = new ManualResetEventSlim(false);
        BlockingLogTextWriter? slowLogWriter = null;
        AppServices? slowLogServices = null;

        try
        {
            Directory.CreateDirectory(root);
            if (ownsDiscoveryRoot)
            {
                Directory.CreateDirectory(discoveryRoot);
                File.WriteAllText(
                    Path.Combine(discoveryRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
                    RimWorldModDiscoveryService.IsolationMarkerContent + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            var fixtureRows = options.UiInteractionProbe
                ? 5_000
                : options.MetadataAccessibilityProbe ? 3 : options.SeedRows;
            var fixtureProfile = options.UiInteractionProbe
                ? options.FixtureProfile
                : options.MetadataAccessibilityProbe ? "dashboard" : options.FixtureProfile;
            if (fixtureRows > 0)
                SyntheticUiFixture.Create(root, discoveryRoot, fixtureRows, fixtureProfile);
            if (File.Exists(errorOutput)) File.Delete(errorOutput);

            if (options.CloseBehaviorProbe)
            {
                CloseBehaviorProbe.Run(root);
                return;
            }
            if (options.UiInteractionProbe)
            {
                UiInteractionProbe.Run(root, discoveryRoot, options.ReportPath, fixtureProfile);
                return;
            }
            if (options.MetadataAccessibilityProbe)
            {
                MetadataAccessibilityProbe.Run(root, discoveryRoot, options.ReportPath);
                return;
            }
            if (options.CommandPaletteProbe)
            {
                CommandPaletteProbe.Run(options.ReportPath);
                return;
            }
            if (options.SingleInstanceProbe)
            {
                RunSingleInstanceProbe(root);
                return;
            }
            var discovery = RimWorldModDiscoveryService.CreateIsolated(discoveryRoot);
            Exception? harnessAssertion = null;
            Task? snapshotTask = null;
            Task? snapshotObserver = null;
            CancellationTokenRegistration closeRaceCancellationRegistration = default;
            var closeRacePendingChecked = false;
            var closeRaceCompletionObserved = false;
            var closeRaceDurableAccepted = false;
            var closeRaceDurableObserved = false;
            var closeRaceRejectedDurableAcceptRan = false;
            var closeRaceOperationStarted = false;
            var closeRaceOperationCompleted = false;
            var closeRaceCancellationCallbackCompleted = 0;
            var closeRaceEmergencyForce = false;
            var forcePromptCount = 0;
            var uiThreadId = Environment.CurrentManagedThreadId;
            var ioProbeThreads = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            Exception? ioProbeAssertion = null;
            var ioProbeCompleted = false;
            var ioProbeBuildCompleted = false;
            var ioProbeState = 0;
            var ioProbeStartedAt = DateTime.UtcNow;
            var ioProbeReleasedAt = DateTime.MinValue;
            Exception? loggerDrainAssertion = null;
            var loggerCloseRequested = false;
            var loggerDrainReleased = false;
            var loggerResponsiveTicks = 0;
            var loggerProbeStartedAt = DateTime.UtcNow;

            if (options.SlowBootstrap)
            {
                RunSlowBootstrapHarness(options, root, discovery, uiThreadId);
                return;
            }
            if (options.BootstrapTransitionFailure)
            {
                RunBootstrapTransitionFailureHarness(root, discovery);
                return;
            }
            if (options.Phase08UiTruthfulness)
            {
                RunPhase08UiTruthfulnessHarness(root, discoveryRoot, discovery);
                return;
            }
            if (options.BootstrapCleanupTimeout)
            {
                RunBootstrapCleanupTimeoutHarness();
                return;
            }
            if (options.ProviderUrlSecurity)
            {
                RunProviderUrlSecurityHarness(root, discovery);
                return;
            }

            bool GlossaryFileExistsProbe(string path)
            {
                var slow = path.EndsWith("slow-glossary.txt", StringComparison.OrdinalIgnoreCase);
                ioProbeThreads[slow ? "glossary-slow" : "glossary-fast"] = Environment.CurrentManagedThreadId;
                if (!slow) return false;
                glossaryProbeStarted.Set();
                glossaryProbeRelease.Wait(TimeSpan.FromSeconds(10));
                return true;
            }

            bool WritableRmkProbe(string path)
            {
                var slow = path.EndsWith("slow-rmk", StringComparison.OrdinalIgnoreCase);
                var build = path.EndsWith("build-rmk", StringComparison.OrdinalIgnoreCase);
                ioProbeThreads[slow ? "rmk-status-slow" : build ? "rmk-build-preflight" : "rmk-status-fast"] =
                    Environment.CurrentManagedThreadId;
                if (slow)
                {
                    rmkProbeStarted.Set();
                    rmkProbeRelease.Wait(TimeSpan.FromSeconds(10));
                    return true;
                }
                return build;
            }

            async Task<RmkBuildResult> BuildRmkProbeAsync(string path, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ioProbeThreads["rmk-build"] = Environment.CurrentManagedThreadId;
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                ioProbeThreads["rmk-build-inner-completed"] = Environment.CurrentManagedThreadId;
                return new RmkBuildResult(
                    "synthetic RMK build",
                    Path.Combine(path, "LoadFolders.xml"),
                    Path.Combine(path, "ModList.tsv"));
            }

            UiIoHooks? ioHooks = options.SlowIoProbe
                ? new UiIoHooks(
                    GlossaryFileExists: GlossaryFileExistsProbe,
                    DirectoryExists: _ =>
                    {
                        ioProbeThreads["rmk-directory"] = Environment.CurrentManagedThreadId;
                        return true;
                    },
                    IsRmkWorkspaceRoot: _ =>
                    {
                        ioProbeThreads["rmk-workspace-root"] = Environment.CurrentManagedThreadId;
                        return false;
                    },
                    IsWritableRmkWorkspace: WritableRmkProbe,
                    OpenDirectory: _ => ioProbeThreads["rmk-open"] = Environment.CurrentManagedThreadId,
                    BuildRmkWorkspace: BuildRmkProbeAsync)
                : null;
            Func<bool>? forceClose = options.CloseTimeoutRecover
                ? () =>
                {
                    forcePromptCount++;
                    return closeRacePendingChecked || closeRaceEmergencyForce;
                }
                : null;

            MainForm CreateHarnessForm()
            {
                if (!options.SlowLoggerDrain)
                {
                    return new MainForm(
                        root,
                        discovery,
                        closeBarrierTimeout: options.CloseTimeoutRecover ? TimeSpan.FromMilliseconds(120) : null,
                        forceCloseConfirmation: forceClose,
                        ioHooks: ioHooks,
                        activeOperationCloseConfirmation: options.CloseTimeoutRecover
                            ? () => DialogResult.OK
                            : null);
                }

                var writer = new BlockingLogTextWriter();
                slowLogWriter = writer;
                var appServices = new AppServices(
                    root,
                    discovery,
                    _ => new AppLogger(
                        writer,
                        queueCapacity: 8,
                        disposeDrainTimeout: TimeSpan.FromSeconds(2)));
                slowLogServices = appServices;
                return new MainForm(
                    appServices,
                    appServices.ProjectStats.Load(),
                    closeBarrierTimeout: null,
                    forceCloseConfirmation: null,
                    ioHooks: ioHooks);
            }

            using var form = CreateHarnessForm();
            if (options.CloseDuringStartup)
                form.Shown += (_, _) => form.Close();
            if (options.CloseTimeoutRecover)
            {
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var blockerStarted = false;
                var initialCloseRequested = false;
                var finalCloseRequested = false;
                var raceStarted = DateTime.UtcNow;
                form.Shown += (_, _) =>
                {
                    if (!form.StartUiWorkflowForTesting(
                            "synthetic close barrier",
                            cancellationToken =>
                            {
                                closeRaceCancellationRegistration = cancellationToken.Register(
                                    () =>
                                    {
                                        try
                                        {
                                            Thread.Sleep(650);
                                            throw new InvalidOperationException("synthetic cancellation callback failure");
                                        }
                                        finally
                                        {
                                            Interlocked.Exchange(ref closeRaceCancellationCallbackCompleted, 1);
                                        }
                                    });
                                blockerStarted = true;
                                return release.Task;
                            },
                            () =>
                            {
                                closeRaceCompletionObserved = true;
                                if (Environment.CurrentManagedThreadId != uiThreadId)
                                {
                                    harnessAssertion = new InvalidOperationException(
                                        "The tracked workflow completion callback did not run on the UI thread.");
                                }
                            }))
                    {
                        harnessAssertion = new InvalidOperationException("The synthetic close-barrier workflow was rejected before closing began.");
                    }
                    if (!form.StartUiWorkflowForTesting(
                            "synthetic post-cancel operation",
                            lifetimeToken => form.RunOperationForTesting(
                                async operationToken =>
                                {
                                    closeRaceOperationStarted = true;
                                    try
                                    {
                                        await Task.Delay(Timeout.InfiniteTimeSpan, operationToken);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw new InvalidOperationException("synthetic post-cancel operation failure");
                                    }
                                },
                                lifetimeToken),
                            () => closeRaceOperationCompleted = true))
                    {
                        harnessAssertion = new InvalidOperationException(
                            "The synthetic post-cancel operation was rejected before closing began.");
                    }
                };
                closeRaceTimer = new System.Windows.Forms.Timer { Interval = 15 };
                closeRaceTimer.Tick += (_, _) =>
                {
                    if (blockerStarted && closeRaceOperationStarted && !initialCloseRequested)
                    {
                        if (!form.StartDurableUiWorkflowForTesting(
                                "synthetic accepted durable save",
                                () =>
                                {
                                    closeRaceDurableAccepted = true;
                                    return Task.CompletedTask;
                                },
                                (acceptedTask, _) =>
                                {
                                    closeRaceDurableObserved = true;
                                    return acceptedTask;
                                }))
                        {
                            harnessAssertion = new InvalidOperationException(
                                "The synthetic durable save was rejected before closing began.");
                        }
                        if (!closeRaceDurableAccepted)
                        {
                            harnessAssertion = new InvalidOperationException(
                                "The durable save was not accepted synchronously before close cancellation.");
                        }
                        initialCloseRequested = true;
                        form.Close();
                        return;
                    }
                    if (initialCloseRequested
                        && !closeRacePendingChecked
                        && form.IsCloseBarrierTimedOutForTesting)
                    {
                        closeRacePendingChecked = true;
                        if (Volatile.Read(ref closeRaceCancellationCallbackCompleted) != 0)
                        {
                            harnessAssertion = new InvalidOperationException(
                                "The close timeout was not surfaced until after a blocking cancellation callback returned.");
                        }
                        if (form.StartUiWorkflowForTesting("must be rejected while close barrier is pending", _ => Task.CompletedTask))
                        {
                            harnessAssertion = new InvalidOperationException(
                                "A UI workflow was accepted while the timed-out close barrier was still pending.");
                        }
                        if (form.StartDurableUiWorkflowForTesting(
                                "durable save must be rejected while close barrier is pending",
                                () =>
                                {
                                    closeRaceRejectedDurableAcceptRan = true;
                                    return Task.CompletedTask;
                                },
                                (acceptedTask, _) => acceptedTask)
                            || closeRaceRejectedDurableAcceptRan)
                        {
                            harnessAssertion = new InvalidOperationException(
                                "A durable save was accepted after the close barrier stopped accepting work.");
                        }
                        release.TrySetResult(true);
                        return;
                    }
                    if (closeRacePendingChecked
                        && !finalCloseRequested
                        && form.IsAcceptingUiWorkflowsForTesting)
                    {
                        finalCloseRequested = true;
                        form.Close();
                        return;
                    }
                    if (DateTime.UtcNow - raceStarted <= TimeSpan.FromSeconds(10)) return;
                    harnessAssertion ??= new TimeoutException("The synthetic close-barrier race did not reach recovery within 10 seconds.");
                    closeRaceEmergencyForce = true;
                    release.TrySetResult(true);
                    finalCloseRequested = true;
                    closeRaceTimer?.Stop();
                    if (!form.IsDisposed) form.Close();
                };
                closeRaceTimer.Start();
            }
            if (options.SlowLoggerDrain)
            {
                form.Shown += (_, _) =>
                {
                    slowLogServices!.Logger.Info("synthetic slow close logger drain");
                    loggerProbeStartedAt = DateTime.UtcNow;
                    loggerDrainTimer = new System.Windows.Forms.Timer { Interval = 25 };
                    loggerDrainTimer.Tick += (_, _) =>
                    {
                        try
                        {
                            if (DateTime.UtcNow - loggerProbeStartedAt > TimeSpan.FromSeconds(10))
                            {
                                throw new TimeoutException("The slow logger close probe did not finish within 10 seconds.");
                            }
                            if (!slowLogWriter!.WriteEntered.IsSet) return;
                            if (!loggerCloseRequested)
                            {
                                loggerCloseRequested = true;
                                form.Close();
                                return;
                            }
                            if (!form.IsServiceDisposalPendingForTesting) return;

                            loggerResponsiveTicks++;
                            if (form.IsDisposed)
                                throw new InvalidOperationException("The form closed before its accepted logger drain completed.");
                            if (loggerResponsiveTicks < 3 || loggerDrainReleased) return;
                            loggerDrainReleased = true;
                            slowLogWriter.ReleaseWrite.Set();
                        }
                        catch (Exception exception)
                        {
                            loggerDrainAssertion ??= exception;
                            TryRelease(slowLogWriter);
                            if (!form.IsDisposed) form.Close();
                        }
                    };
                    loggerDrainTimer.Start();
                };
                form.FormClosed += (_, _) => loggerDrainTimer?.Stop();
            }
            if (options.SlowIoProbe)
            {
                form.Shown += (_, _) =>
                {
                    var settingsControl = SnapshotCapture.DescendantsOf(form).OfType<SettingsControl>().Single();
                    var reviewControl = SnapshotCapture.DescendantsOf(form).OfType<ReviewWorkspaceControl>().Single();
                    var slowGlossary = Path.Combine(root, "slow-glossary.txt");
                    var fastGlossary = Path.Combine(root, "fast-glossary.txt");
                    var slowRmk = Path.Combine(root, "slow-rmk");
                    var fastRmk = Path.Combine(root, "fast-rmk");
                    var workbook = Path.Combine(root, "rmk-reference", "workbook.xlsx");
                    var buildRmk = Path.Combine(root, "build-rmk");

                    settingsControl.SetGlossaryPathForTesting(slowGlossary);
                    reviewControl.RefreshRmkPanelForTesting(slowRmk, workbook);
                    reviewControl.OpenRmkFolderForTesting(Path.Combine(root, "open-rmk"), workbook);
                    ioProbeStartedAt = DateTime.UtcNow;
                    ioProbeTimer = new System.Windows.Forms.Timer { Interval = 15 };
                    ioProbeTimer.Tick += (_, _) =>
                    {
                        try
                        {
                            if (DateTime.UtcNow - ioProbeStartedAt > TimeSpan.FromSeconds(10))
                            {
                                ioProbeAssertion ??= new TimeoutException("The synthetic UI I/O probe did not complete within 10 seconds.");
                                glossaryProbeRelease.Set();
                                rmkProbeRelease.Set();
                                ioProbeTimer.Stop();
                                form.Close();
                                return;
                            }

                            if (ioProbeState == 0
                                && glossaryProbeStarted.IsSet
                                && rmkProbeStarted.IsSet)
                            {
                                if (glossaryProbeRelease.IsSet || rmkProbeRelease.IsSet)
                                    throw new InvalidOperationException("A slow probe was released before the UI responsiveness check.");
                                if (ioProbeThreads["glossary-slow"] == uiThreadId
                                    || ioProbeThreads["rmk-status-slow"] == uiThreadId)
                                    throw new InvalidOperationException("A slow filesystem probe executed on the UI thread.");

                                settingsControl.SetGlossaryPathForTesting(fastGlossary);
                                reviewControl.RefreshRmkPanelForTesting(fastRmk, workbook);
                                ioProbeState = 1;
                                return;
                            }

                            if (ioProbeState == 1
                                && settingsControl.GlossaryStatusForTesting.Contains("fast-glossary.txt", StringComparison.Ordinal)
                                && settingsControl.GlossaryStatusForTesting.Contains("찾을 수 없음", StringComparison.Ordinal)
                                && reviewControl.RmkStatusForTesting.Contains("설정되지 않았습니다", StringComparison.Ordinal))
                            {
                                glossaryProbeRelease.Set();
                                rmkProbeRelease.Set();
                                ioProbeReleasedAt = DateTime.UtcNow;
                                ioProbeState = 2;
                                return;
                            }

                            if (ioProbeState == 2 && DateTime.UtcNow - ioProbeReleasedAt >= TimeSpan.FromMilliseconds(250))
                            {
                                if (!settingsControl.GlossaryStatusForTesting.Contains("fast-glossary.txt", StringComparison.Ordinal)
                                    || !settingsControl.GlossaryStatusForTesting.Contains("찾을 수 없음", StringComparison.Ordinal))
                                    throw new InvalidOperationException("A stale glossary probe overwrote the latest status.");
                                if (!reviewControl.RmkStatusForTesting.Contains("설정되지 않았습니다", StringComparison.Ordinal))
                                    throw new InvalidOperationException("A stale RMK probe overwrote the latest status.");
                                if (!form.StartUiWorkflowForTesting(
                                        "synthetic RMK build probe",
                                        token => form.BuildRmkWorkspaceForTesting(buildRmk, token),
                                        () => ioProbeBuildCompleted = true))
                                    throw new InvalidOperationException("The synthetic RMK build workflow was rejected.");
                                ioProbeState = 3;
                                return;
                            }

                            if (ioProbeState != 3 || !ioProbeBuildCompleted) return;
                            if (!ioProbeThreads.ContainsKey("rmk-build-inner-completed"))
                                throw new InvalidOperationException("The RMK build workflow completed before its delayed inner task.");
                            string[] required =
                            [
                                "glossary-slow", "glossary-fast", "rmk-status-slow", "rmk-status-fast",
                                "rmk-workspace-root", "rmk-directory", "rmk-open", "rmk-build-preflight", "rmk-build",
                                "rmk-build-inner-completed"
                            ];
                            if (required.Any(name => !ioProbeThreads.ContainsKey(name))) return;
                            if (required.Any(name => ioProbeThreads[name] == uiThreadId))
                                throw new InvalidOperationException("A filesystem or RMK build probe executed on the UI thread.");
                            ioProbeCompleted = true;
                            ioProbeTimer.Stop();
                            form.Close();
                        }
                        catch (Exception exception)
                        {
                            ioProbeAssertion ??= exception;
                            glossaryProbeRelease.Set();
                            rmkProbeRelease.Set();
                            ioProbeTimer.Stop();
                            form.Close();
                        }
                    };
                    ioProbeTimer.Start();
                };
            }
            if (options.Width > 0 && options.Height > 0)
            {
                form.Load += (_, _) =>
                {
                    form.WindowState = FormWindowState.Normal;
                    form.ClientSize = new Size(options.Width, options.Height);
                };
            }
            else if (options.Maximize)
            {
                form.Load += (_, _) => form.WindowState = FormWindowState.Maximized;
            }
            if (!string.IsNullOrWhiteSpace(options.SnapshotPath))
            {
                async Task CaptureSnapshotAsync()
                {
                    await SnapshotCapture.WaitForReadyAsync(form, TimeSpan.FromMilliseconds(options.WaitMilliseconds));
                    SnapshotCapture.SelectTopLevelTab(form, options.InitialTab);
                    if (options.OpenFirstProject)
                    {
                        await SnapshotCapture.OpenFirstProjectAsync(form, TimeSpan.FromMilliseconds(options.WaitMilliseconds));
                        SnapshotCapture.SelectWorkspaceTab(form, options.WorkspaceTab);
                        if (!string.IsNullOrWhiteSpace(options.SearchText))
                        {
                            var searchBox = SnapshotCapture.DescendantsOf(form).OfType<TextBox>()
                                .Single(control => control.AccessibleName == "문자열 검색");
                            searchBox.Text = options.SearchText;
                            await Task.Delay(250);
                            await SnapshotCapture.WaitForReadyAsync(form, TimeSpan.FromMilliseconds(options.WaitMilliseconds));
                        }
                        var reviewList = SnapshotCapture.DescendantsOf(form).OfType<ListBox>()
                            .FirstOrDefault(control => control.AccessibleName == "검색된 문자열 목록");
                        if (reviewList is not null && options.SelectIndex >= 0 && options.SelectIndex < reviewList.Items.Count)
                            reviewList.SelectedIndex = options.SelectIndex;
                        if (reviewList is not null && options.TopIndex >= 0 && reviewList.Items.Count > 0)
                            reviewList.TopIndex = Math.Clamp(options.TopIndex, 0, reviewList.Items.Count - 1);
                    }
                    if (!string.IsNullOrWhiteSpace(options.Theme) || options.TextSize != 10)
                    {
                        var previewTheme = ThemeManager.Create(new AppSettingsDocument
                        {
                            ThemeMode = string.IsNullOrWhiteSpace(options.Theme) ? "Light" : options.Theme,
                            DesignPreset = string.IsNullOrWhiteSpace(options.Preset) ? "Professional" : options.Preset,
                            HighContrast = options.HighContrast
                        });
                        ThemeManager.Apply(form, previewTheme, options.TextSize);
                        SnapshotCapture.DescendantsOf(form).OfType<ReviewWorkspaceControl>().FirstOrDefault()?.RefreshThemeState();
                        form.PerformLayout();
                        form.Invalidate(true);
                        form.Update();
                        Application.DoEvents();
                    }
                    Form? previewDialog = null;
                    if (options.Preview.StartsWith("operation", StringComparison.OrdinalIgnoreCase))
                    {
                        var overlay = SnapshotCapture.DescendantsOf(form).OfType<LoadingOverlay>().First();
                        overlay.Show("초벌 번역 실행", "Translating batch 3/12 (40 entries)...", ThemeManager.Current, true);
                        overlay.UpdateProgress("translate", "Translating batch 3/12 (40 entries)...", 3, 12);
                        if (options.Preview.EndsWith("-cancel-requested", StringComparison.OrdinalIgnoreCase))
                            SnapshotCapture.DescendantsOf(overlay).OfType<Button>().Single(button => button.Text == "중지").PerformClick();
                        else if (options.Preview.EndsWith("-restart", StringComparison.OrdinalIgnoreCase))
                        {
                            overlay.Complete("completed", "이전 작업 완료", "이전 작업의 자동 닫기 타이머를 시작합니다.");
                            await Task.Delay(400);
                            overlay.Show("새 작업 실행", "이전 작업 타이머와 독립적으로 실행 중입니다.", ThemeManager.Current, true);
                        }
                        else if (options.Preview.EndsWith("-error", StringComparison.OrdinalIgnoreCase))
                            overlay.Complete("error", "번역 작업에 실패했습니다", "요청 한도를 확인한 뒤 실패한 작업만 다시 시도할 수 있습니다.", true);
                        else if (options.Preview.EndsWith("-cancelled", StringComparison.OrdinalIgnoreCase))
                            overlay.Complete("cancelled", "작업을 중지했습니다", "완료된 배치는 가능한 경우 검수 프로젝트에 남아 있습니다.");
                        else if (options.Preview.EndsWith("-completed", StringComparison.OrdinalIgnoreCase))
                            overlay.Complete("completed", "초벌 번역이 완료되었습니다", "문제 항목을 확인하고 검토를 시작할 수 있습니다.");
                    }
                    else if (options.Preview.Equals("workspace-load", StringComparison.OrdinalIgnoreCase))
                    {
                        SnapshotCapture.DescendantsOf(form).OfType<WorkspaceLoadCover>().First().ShowCover(
                            "프로젝트 구성 중", "문자열과 검수 상태를 한 번에 준비하고 있습니다.", ThemeManager.Current);
                    }
                    else if (options.Preview.Equals("translation", StringComparison.OrdinalIgnoreCase))
                    {
                        previewDialog = new TranslationModeDialog(new TranslationPreflightInfo(
                            "선택한 프로젝트", "Auto", "Google 번역 (키 없음 대체)", "Google Translate",
                            5_000, 125, "Google 번역 · API 토큰 추정 해당 없음", true));
                    }
                    else if (options.Preview.StartsWith("command", StringComparison.OrdinalIgnoreCase))
                    {
                        var includeDisabledCommands = options.Preview.EndsWith("-disabled", StringComparison.OrdinalIgnoreCase);
                        previewDialog = new CommandPaletteDialog([
                            new("프로젝트 목록 열기", "이동", "Ctrl+Home", true, () => { }),
                            new("현재 화면 검색", "이동", "Ctrl+F", !includeDisabledCommands, () => { }),
                            new("선택 문자열 비교 열기", "검수", "Alt+C", !includeDisabledCommands, () => { }),
                            new("프로젝트 품질 센터 열기", "검수", "Alt+Q", !includeDisabledCommands, () => { }),
                            new("이전 문자열", "검수", "Shift+F3", true, () => { }),
                            new("다음 문자열", "검수", "F3", true, () => { }),
                            new("검토 완료 후 다음", "검수", "Ctrl+Enter", true, () => { }),
                            new("검수 내용 저장", "프로젝트", "Ctrl+S", true, () => { }),
                            new("모드 원문 다시 분석", "프로젝트", "F5", true, () => { }),
                            new("AI 초벌 번역 준비", "프로젝트", "F9", true, () => { }),
                            new("개인정보 보호 품질 보고서", "도구", string.Empty, true, () => { }),
                            new("현재 모드 폴더 열기", "도구", string.Empty, true, () => { }),
                            new("API 및 화면 설정 열기", "설정", string.Empty, true, () => { }),
                            new("개인정보 보호 진단 번들 저장", "도구", string.Empty, true, () => { })
                        ]);
                    }
                    else if (options.Preview.Equals("language", StringComparison.OrdinalIgnoreCase))
                    {
                        previewDialog = new SourceLanguageDialog("Sample Translator Test Mod", [
                            new("English", "C:\\fixture\\Languages\\English", "English · XML 7개", 0, 7),
                            new("SimplifiedChinese", "C:\\fixture\\Languages\\SimplifiedChinese", "중국어 간체 · XML 7개", 1, 7),
                            new("Japanese", "C:\\fixture\\Languages\\Japanese", "일본어 · XML 7개", 2, 7)
                        ]);
                    }
                    if (previewDialog is not null)
                    {
                        ThemeManager.Apply(previewDialog, ThemeManager.Current, 10);
                        previewDialog.Show(form);
                        var ownerOrigin = form.PointToScreen(Point.Empty);
                        previewDialog.Location = new Point(
                            ownerOrigin.X + Math.Max(0, (form.ClientSize.Width - previewDialog.Width) / 2),
                            ownerOrigin.Y + Math.Max(0, (form.ClientSize.Height - previewDialog.Height) / 2));
                        previewDialog.Activate();
                    }
                    else if (!string.IsNullOrWhiteSpace(options.FocusAccessibleName))
                    {
                        var target = SnapshotCapture.DescendantsOf(form)
                            .FirstOrDefault(control => control.Visible
                                && control.AccessibleName?.Equals(options.FocusAccessibleName, StringComparison.Ordinal) == true)
                            ?? throw new InvalidOperationException($"Focus target was not found: {options.FocusAccessibleName}");
                        target.Focus();
                    }
                    await Task.Delay(600);
                    SnapshotCapture.Save(form, options.SnapshotPath, previewDialog, fixtureRows > 0);
                    previewDialog?.Close();
                    form.Close();
                }

                form.Shown += (_, _) =>
                {
                    snapshotTask = CaptureSnapshotAsync();
                    snapshotObserver = ObserveSnapshotWorkflowAsync(snapshotTask, form);
                };
            }
            Application.Run(form);
            closeRaceCancellationRegistration.Dispose();
            snapshotObserver?.GetAwaiter().GetResult();
            snapshotTask?.GetAwaiter().GetResult();
            closeRaceTimer?.Stop();
            closeRaceTimer?.Dispose();
            closeRaceTimer = null;
            loggerDrainTimer?.Stop();
            loggerDrainTimer?.Dispose();
            loggerDrainTimer = null;
            if (options.CloseTimeoutRecover)
            {
                if (!closeRacePendingChecked)
                    throw new InvalidOperationException("The close barrier never entered the timed-out pending state.");
                if (forcePromptCount < 1)
                    throw new InvalidOperationException("The force-close confirmation boundary was not reached.");
                if (!closeRaceCompletionObserved)
                    throw new InvalidOperationException("The tracked workflow completion callback was not observed.");
                if (!closeRaceDurableObserved)
                    throw new InvalidOperationException(
                        "The accepted durable save was not observed after close cancellation.");
                if (!closeRaceOperationCompleted)
                    throw new InvalidOperationException(
                        "The synthetic post-cancel operation did not complete without presenting UI.");
                if (harnessAssertion is not null) throw harnessAssertion;
            }
            if (options.SlowIoProbe)
            {
                if (ioProbeAssertion is not null) throw ioProbeAssertion;
                if (!ioProbeCompleted)
                    throw new InvalidOperationException("The synthetic UI I/O probe did not reach completion.");
            }
            if (options.SlowLoggerDrain)
            {
                if (loggerDrainAssertion is not null) throw loggerDrainAssertion;
                if (!loggerCloseRequested)
                    throw new InvalidOperationException("The slow logger probe never requested close.");
                if (loggerResponsiveTicks < 3)
                    throw new InvalidOperationException("The UI timer did not remain responsive while logger disposal was pending.");
                if (!loggerDrainReleased)
                    throw new InvalidOperationException("The form closed without waiting for the slow logger drain release.");
                if (slowLogServices?.Logger.DrainCompletionForTesting.IsCompletedSuccessfully != true)
                    throw new InvalidOperationException("The released logger drain was not complete when the form closed.");
                if (slowLogWriter?.Lines.Any(line => line.Contains("synthetic slow close logger drain", StringComparison.Ordinal)) != true)
                    throw new InvalidOperationException("The accepted slow logger entry was not persisted before close.");
            }
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(errorOutput)!);
                var safeFailure = new
                {
                    schemaVersion = 1,
                    status = "FAILED",
                    exceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                    hResult = ex.HResult,
                    capturedAtUtc = DateTimeOffset.UtcNow,
                    detailPolicy = "Exception messages, stack traces, paths, request text and credentials are intentionally omitted."
                };
                File.WriteAllText(
                    errorOutput,
                    JsonSerializer.Serialize(safeFailure, FailureJsonOptions),
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception sinkFailure)
            {
                System.Diagnostics.Debug.WriteLine($"UI harness error sink unavailable ({sinkFailure.GetType().Name}).");
            }
            throw;
        }
        finally
        {
            closeRaceTimer?.Stop();
            closeRaceTimer?.Dispose();
            ioProbeTimer?.Stop();
            ioProbeTimer?.Dispose();
            loggerDrainTimer?.Stop();
            loggerDrainTimer?.Dispose();
            TryRelease(slowLogWriter);
            try { slowLogServices?.Dispose(); }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Slow logger fixture disposal failed ({exception.GetType().Name}).");
            }
            glossaryProbeRelease.Set();
            rmkProbeRelease.Set();
            glossaryProbeRelease.Dispose();
            rmkProbeRelease.Dispose();
            glossaryProbeStarted.Dispose();
            rmkProbeStarted.Dispose();
            if (ownsRoot)
            {
                try { Directory.Delete(root, true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"UI fixture cleanup skipped ({exception.GetType().Name}).");
                }
            }
            if (ownsDiscoveryRoot)
            {
                try { Directory.Delete(discoveryRoot, true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"UI discovery fixture cleanup skipped ({exception.GetType().Name}).");
                }
            }
        }
    }

    private static void TryRelease(BlockingLogTextWriter? writer)
    {
        if (writer is null) return;
        try { writer.ReleaseWrite.Set(); }
        catch (ObjectDisposedException) { }
    }

    private static void RunProviderUrlSecurityHarness(
        string root,
        RimWorldModDiscoveryService discovery)
    {
        const string originalSecret = "synthetic-original-provider-secret";
        const string editedSecret = "synthetic-edited-provider-secret";
        const string fragmentSecret = "synthetic-fragment-provider-secret";
        const string extensionSecret = "sk-synthetic-extension-secret";
        const string originalUnsafeUrl = "https://provider.invalid/v1?api_key=" + originalSecret;
        const string editedUnsafeUrl = "https://provider.invalid/v1?ACCESS-TOKEN=" + editedSecret;
        const string fragmentUnsafeUrl = "https://provider.invalid/v1#client_secret=" + fragmentSecret;
        const string safeUrl = "https://provider.invalid/v1?format=json&version=1";
        const string storedNetworkGlossary = @"\\phase07.invalid\stored\glossary.txt";
        const string storedNetworkRmk = @"\\phase07.invalid\stored\rmk";

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var paths = new AppDataPaths(root);
        paths.EnsureExists();
        var unsafeStoredSettings = new AppSettingsDocument
        {
            ApiProviderId = "Custom",
            AutoSave = true,
            CustomGlossaryPath = storedNetworkGlossary,
            RmkWorkspaceRoot = storedNetworkRmk,
            ApiProviders = new Dictionary<string, ApiProviderSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["Custom"] = new()
                {
                    Name = "Synthetic Provider",
                    Url = originalUnsafeUrl,
                    Model = "synthetic-model",
                    Temperature = 0.1,
                    ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["futureProviderSetting"] = JsonSerializer.SerializeToElement(new { retained = true }),
                        ["secretKey"] = JsonSerializer.SerializeToElement("synthetic-opaque-provider-secret")
                    }
                }
            },
            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["futureSafeSetting"] = JsonSerializer.SerializeToElement(new { retained = true }),
                ["futureContainer"] = JsonSerializer.SerializeToElement(new
                {
                    safeSibling = "retained",
                    safeKeyboard = new { key = "Ctrl+K", command = "synthetic-command" },
                    apiToken = "synthetic-opaque-root-secret",
                    apiKeyValue = "synthetic-qualified-opaque-root-secret",
                    values = new[] { "visible", extensionSecret }
                })
            }
        };
        File.WriteAllText(
            paths.Settings,
            JsonSerializer.Serialize(unsafeStoredSettings),
            new UTF8Encoding(false));
        var originalSettingsBytes = File.ReadAllBytes(paths.Settings);
        Exception? harnessAssertion = null;
        var storedNetworkProbeCount = 0;
        var ioHooks = new UiIoHooks(
            GlossaryFileExists: _ =>
            {
                Interlocked.Increment(ref storedNetworkProbeCount);
                throw new InvalidOperationException("A stored network glossary reached the filesystem probe hook.");
            },
            DirectoryExists: _ =>
            {
                Interlocked.Increment(ref storedNetworkProbeCount);
                throw new InvalidOperationException("A stored network RMK root reached the filesystem probe hook.");
            });

        using (var form = new MainForm(root, discovery, ioHooks: ioHooks))
        {
            form.Load += (_, _) =>
            {
                form.WindowState = FormWindowState.Normal;
                form.ClientSize = new Size(900, 600);
            };
            form.Shown += async (_, _) =>
            {
                try
                {
                    var settings = SnapshotCapture.DescendantsOf(form).OfType<SettingsControl>().Single();
                    Require(settings.ApiKeyEditorVisibleForTesting
                            && settings.ApiKeyEditorTextForTesting.Length == 0
                            && !settings.HasApiKeyEditButtonForTesting,
                        "The API-key field was not an empty direct editor or retained the obsolete edit button.");
                    Require(Volatile.Read(ref storedNetworkProbeCount) == 0
                            && settings.GlossaryStatusForTesting.Contains("네트워크", StringComparison.Ordinal),
                        "A stored network path was probed automatically or lacked a local-only warning.");
                    var probesBeforeProjectAction = Volatile.Read(ref storedNetworkProbeCount);
                    Require(!form.IsLocalActionDirectoryForTesting(@"\\synthetic.invalid\stored-project")
                            && Volatile.Read(ref storedNetworkProbeCount) == probesBeforeProjectAction,
                        "A stored network project path reached the filesystem probe used by project actions.");
                    Require(settings.StoredSettingsCorrectionRequiredForTesting,
                        "Credential-like legacy extension data was not marked correction-required.");
                    Require(settings.ProviderNoticeForTesting.Contains("인증 설정", StringComparison.Ordinal)
                            && settings.ProviderNoticeForTesting.Contains("settings.json", StringComparison.Ordinal)
                            && settings.ProviderNoticeForTesting.Contains(".bak", StringComparison.Ordinal),
                        "Credential-like legacy extension data did not show the required recovery and cleanup notice.");
                    Require(settings.ProviderNoticeFullyVisibleForTesting,
                        "The stored-settings correction notice was clipped in its visible label bounds.");
                    Require(!settings.ProviderNoticeForTesting.Contains(extensionSecret, StringComparison.Ordinal),
                        "The settings correction notice exposed a legacy extension credential.");
                    Require(settings.HasRootSettingsExtensionForTesting("futureSafeSetting")
                            && settings.HasRootSettingsExtensionForTesting("futureContainer")
                            && settings.HasProviderSettingsExtensionForTesting("Custom", "futureProviderSetting")
                            && !settings.HasProviderSettingsExtensionForTesting("Custom", "secretKey"),
                        "Loading legacy extension data did not preserve safe unknown fields or exclude a credential field.");
                    Require(settings.ProviderSaveBlockedForTesting, "An existing credential-bearing URL was not marked correction-required.");
                    Require(settings.ProviderNoticeForTesting.Contains("제외", StringComparison.Ordinal), "The existing unsafe URL did not show a correction-required notice.");
                    Require(!settings.ProviderNoticeForTesting.Contains(originalSecret, StringComparison.Ordinal), "The provider notice exposed the stored credential.");
                    Require(settings.GetPersistedProviderUrlForTesting("Custom") == originalUnsafeUrl, "The existing unsafe URL was silently overwritten while loading.");
                    Require(File.ReadAllBytes(paths.Settings).SequenceEqual(originalSettingsBytes), "Opening settings rewrote the existing unsafe URL.");

                    settings.SetProviderUrlForTesting("Custom", editedUnsafeUrl);
                    Require(settings.GetDraftProviderUrlForTesting("Custom") == editedUnsafeUrl, "The unsafe provider edit was not captured as a draft.");
                    Require(!settings.SaveProviderForTesting(), "A credential-bearing provider draft was accepted for save.");
                    Require(settings.GetPersistedProviderUrlForTesting("Custom") == originalUnsafeUrl, "An invalid provider edit reached live settings.");
                    Require(File.ReadAllBytes(paths.Settings).SequenceEqual(originalSettingsBytes), "An invalid provider edit changed the settings file.");
                    Require(!settings.ProviderNoticeForTesting.Contains(editedSecret, StringComparison.Ordinal), "The provider notice exposed the edited credential.");

                    var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    settings.SettingsSaved += (_, _) => saved.TrySetResult(true);
                    settings.SetProviderUrlForTesting("Custom", safeUrl);
                    Require(settings.SaveProviderForTesting(), "A safe provider URL was rejected by the UI save boundary.");
                    await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(settings.GetPersistedProviderUrlForTesting("Custom") == safeUrl, "A valid provider draft was not committed to live settings.");
                    var safeSettingsBytes = File.ReadAllBytes(paths.Settings);
                    var safeSettingsText = Encoding.UTF8.GetString(safeSettingsBytes);
                    var persistedSettings = JsonSerializer.Deserialize<AppSettingsDocument>(safeSettingsText);
                    var safeKeyboardPreserved = persistedSettings?.ExtensionData is { } extensionData
                        && extensionData.TryGetValue("futureContainer", out var futureContainer)
                        && futureContainer.GetProperty("safeKeyboard").GetProperty("key").GetString() == "Ctrl+K";
                    Require(persistedSettings?.ApiProviders["Custom"].Url == safeUrl, "The safe provider URL was not persisted.");
                    Require(!safeSettingsText.Contains(originalSecret, StringComparison.Ordinal)
                            && !safeSettingsText.Contains(editedSecret, StringComparison.Ordinal),
                        "A corrected settings save retained an earlier synthetic credential.");
                    Require(!safeSettingsText.Contains(extensionSecret, StringComparison.Ordinal)
                            && !safeSettingsText.Contains("synthetic-opaque-root-secret", StringComparison.Ordinal)
                            && !safeSettingsText.Contains("synthetic-qualified-opaque-root-secret", StringComparison.Ordinal)
                            && !safeSettingsText.Contains("synthetic-opaque-provider-secret", StringComparison.Ordinal),
                        "A corrected settings save retained excluded legacy extension credentials.");
                    Require(safeSettingsText.Contains("futureSafeSetting", StringComparison.Ordinal)
                            && safeSettingsText.Contains("futureProviderSetting", StringComparison.Ordinal)
                            && safeKeyboardPreserved,
                        "A corrected settings save lost safe unknown fields.");
                    Require(settings.StoredSettingsCorrectionRequiredForTesting
                            && File.Exists(paths.Settings + ".bak")
                            && File.ReadAllBytes(paths.Settings + ".bak").SequenceEqual(originalSettingsBytes),
                        "The correction warning cleared while the credential-bearing legacy backup remained.");
                    Require(settings.ProviderNoticeFullyVisibleForTesting
                            && settings.ProviderNoticeForTesting.Contains(".bak", StringComparison.Ordinal),
                        "The remaining-backup cleanup warning was not fully visible after the primary settings save.");

                    const string opaqueEnteredKey = "0123456789abcdef0123456789abcdef";
                    settings.SetProviderModelForTesting("Custom", opaqueEnteredKey);
                    settings.SetApiKeysForTesting("Custom", opaqueEnteredKey);
                    Require(!settings.SaveProviderForTesting()
                            && File.ReadAllBytes(paths.Settings).SequenceEqual(safeSettingsBytes),
                        "An exact opaque API-key draft was persisted through the provider model field.");
                    settings.SetProviderModelForTesting("Custom", "synthetic-model");
                    settings.SetApiKeysForTesting("Custom", string.Empty);

                    File.Delete(paths.Settings + ".bak");
                    var cleanupSaved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    settings.SettingsSaved += (_, _) => cleanupSaved.TrySetResult(true);
                    Require(settings.SetAutoSaveAndSaveForTesting(!settings.PersistedAutoSaveForTesting),
                        "A clean follow-up settings save was rejected after manual backup cleanup.");
                    await cleanupSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    safeSettingsBytes = File.ReadAllBytes(paths.Settings);
                    var cleanBackupText = File.ReadAllText(paths.Settings + ".bak");
                    Require(!settings.StoredSettingsCorrectionRequiredForTesting
                            && !cleanBackupText.Contains(originalSecret, StringComparison.Ordinal)
                            && !cleanBackupText.Contains(extensionSecret, StringComparison.Ordinal),
                        "The correction warning remained after both primary and backup settings were clean.");

                    var persistedAutoSave = settings.PersistedAutoSaveForTesting;
                    settings.SetProviderUrlForTesting("Custom", fragmentUnsafeUrl);
                    Require(!settings.SetAutoSaveAndSaveForTesting(!persistedAutoSave), "An unrelated setting save bypassed an invalid provider draft.");
                    Require(settings.PersistedAutoSaveForTesting == persistedAutoSave, "An unrelated invalid-draft save changed live settings.");
                    Require(settings.GetPersistedProviderUrlForTesting("Custom") == safeUrl, "An unrelated invalid-draft save replaced the previous safe provider URL.");
                    Require(File.ReadAllBytes(paths.Settings).SequenceEqual(safeSettingsBytes), "An unrelated invalid-draft save changed the settings file.");
                    Require(settings.ProviderSaveBlockedForTesting, "A fragment credential did not keep provider saves blocked.");
                    Require(!settings.ProviderNoticeForTesting.Contains(fragmentSecret, StringComparison.Ordinal), "The provider notice exposed a fragment credential.");
                }
                catch (Exception exception)
                {
                    harnessAssertion = exception;
                }
                finally
                {
                    form.Close();
                }
            };
            Application.Run(form);
        }

        if (harnessAssertion is not null) throw harnessAssertion;
        var logText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(paths.Logs, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var sensitive in new[]
                 {
                      originalSecret, editedSecret, fragmentSecret, extensionSecret,
                     originalUnsafeUrl, editedUnsafeUrl, fragmentUnsafeUrl
                 })
        {
            Require(!logText.Contains(sensitive, StringComparison.Ordinal), "Provider URL validation exposed a synthetic credential in logs.");
        }
    }

    private sealed class BlockingLogTextWriter : TextWriter
    {
        private readonly ConcurrentQueue<string> lines = new();

        public override Encoding Encoding => Encoding.UTF8;
        internal ManualResetEventSlim WriteEntered { get; } = new(false);
        internal ManualResetEventSlim ReleaseWrite { get; } = new(false);
        internal IReadOnlyList<string> Lines => lines.ToArray();

        public override void WriteLine(string? value)
        {
            WriteEntered.Set();
            if (!ReleaseWrite.Wait(TimeSpan.FromSeconds(10)))
                throw new IOException("Synthetic slow logger writer was not released.");
            lines.Enqueue(value ?? string.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WriteEntered.Dispose();
                ReleaseWrite.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static void RunBootstrapTransitionFailureHarness(
        string root,
        RimWorldModDiscoveryService discovery)
    {
        AppStartupState? createdState = null;
        MainForm? createdForm = null;
        SettingsControl? createdSettings = null;
        Exception? presentedFailure = null;
        Exception? harnessAssertion = null;
        var transitionHookRan = false;
        var serviceAliveWhileProbeCompleted = 0;
        var expectedRoot = Path.GetFullPath(root);
        var logPath = string.Empty;
        System.Diagnostics.Stopwatch? cleanupStopwatch = null;
        using var probeStarted = new ManualResetEventSlim(false);
        using var probeCompleted = new ManualResetEventSlim(false);

        var ioHooks = new UiIoHooks(
            DirectoryExists: path =>
            {
                try
                {
                    if (!Path.GetFullPath(path).Equals(expectedRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The transition-failure probe escaped its synthetic data root.");
                    probeStarted.Set();
                    Thread.Sleep(250);
                    if (!string.IsNullOrWhiteSpace(logPath) && !CanOpenExclusive(logPath))
                        Interlocked.Exchange(ref serviceAliveWhileProbeCompleted, 1);
                    return true;
                }
                finally
                {
                    probeCompleted.Set();
                }
            });

        Task<AppStartupState> CreateState(CancellationToken cancellationToken)
        {
            createdState = AppStartupState.Create(root, discovery, null, cancellationToken);
            createdState.Services.Settings.RmkWorkspaceRoot = expectedRoot;
            logPath = createdState.Services.Logger.Path;
            return Task.FromResult(createdState);
        }

        var bootstrap = new StartupBootstrapForm(
            CreateState,
            beforeOwnershipTransfer: form =>
            {
                transitionHookRan = true;
                createdForm = form;
                createdSettings = SnapshotCapture.DescendantsOf(form).OfType<SettingsControl>().Single();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (!probeStarted.IsSet && DateTime.UtcNow < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }
                if (!probeStarted.IsSet)
                    throw new InvalidOperationException("The constructor-scheduled transition probe never started.");
                cleanupStopwatch = System.Diagnostics.Stopwatch.StartNew();
                throw new InvalidOperationException("synthetic transition failure");
            },
            startupFailurePresenter: exception =>
            {
                presentedFailure = exception;
                cleanupStopwatch?.Stop();
                if (!probeCompleted.IsSet)
                    harnessAssertion ??= new InvalidOperationException("Startup services were disposed before the in-flight probe drained.");
                if (Volatile.Read(ref serviceAliveWhileProbeCompleted) == 0)
                    harnessAssertion ??= new InvalidOperationException("The logger was released before the constructor-scheduled probe completed.");
                if (cleanupStopwatch is null || cleanupStopwatch.Elapsed >= TimeSpan.FromSeconds(5))
                    harnessAssertion ??= new TimeoutException("Transition-failure workflow cleanup was not bounded.");
                if (createdForm?.HasIncompleteUiWorkflowsForTesting != false)
                    harnessAssertion ??= new InvalidOperationException("A tracked UI workflow remained incomplete after transition failure cleanup.");
                if (createdSettings?.RmkReferenceAsyncUiApplicationCountForTesting != 0)
                    harnessAssertion ??= new InvalidOperationException("A cancelled constructor probe performed a late UI update.");
                if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath) || !CanOpenExclusive(logPath))
                    harnessAssertion ??= new InvalidOperationException("Application services were not released after the failed transition drained.");
            },
            mainFormFactoryForTesting: state => new MainForm(
                state.Services,
                state.ProjectStats,
                state.IsolationAcknowledgementPath,
                ioHooks: ioHooks));
        try
        {
            Application.Run(bootstrap);
        }
        finally
        {
            bootstrap.DisposeAfterRunAsync().AsTask().GetAwaiter().GetResult();
        }

        if (!transitionHookRan)
            throw new InvalidOperationException("The synthetic startup transition failure hook did not run.");
        if (presentedFailure is not InvalidOperationException)
            throw new InvalidOperationException("The startup transition failure was not presented after cleanup.");
        if (bootstrap.TransitionCompletedForTesting)
            throw new InvalidOperationException("A failed startup transition was marked complete.");
        if (!bootstrap.StartupFailed)
            throw new InvalidOperationException("A failed startup transition did not set the process failure state.");
        if (createdState is null || !createdState.WasDisposedForTesting)
            throw new InvalidOperationException("A failed startup transition retained its application services.");
        if (harnessAssertion is not null) throw harnessAssertion;
        AssertStartupResourcesReleased(createdState);
    }

    private static void RunPhase08UiTruthfulnessHarness(
        string root,
        string discoveryRoot,
        RimWorldModDiscoveryService discovery)
    {
        RunStartupPresentationHarness(Path.Combine(root, "startup-presentation"), discovery);
        RunPhaseOneStartupFailureHarness(Path.Combine(root, "phase-one-startup-failure"), discovery);
        RunMainFormStartupFailureHarness(Path.Combine(root, "main-startup-failure"), discovery);
        RunUiActionTruthfulnessHarness(Path.Combine(root, "ui-action-error"), discovery);
        RunSaveCancellationHarness(
            Path.Combine(root, "save-cancellation"),
            Path.Combine(discoveryRoot, "save-cancellation"));
        RunProjectDeleteCacheFailureHarness(
            Path.Combine(root, "delete-cache-failure"),
            discoveryRoot,
            discovery);
        RunCommittedFinalizationHarness();
    }

    private static void RunStartupPresentationHarness(
        string root,
        RimWorldModDiscoveryService discovery)
    {
        Directory.CreateDirectory(root);
        AppStartupState? createdState = null;
        Exception? harnessFailure = null;
        var presentationChecked = false;
        var postRevealControlAdditions = 0;

        Task<AppStartupState> CreateState(CancellationToken cancellationToken)
        {
            createdState = AppStartupState.Create(root, discovery, null, cancellationToken);
            return Task.FromResult(createdState);
        }

        var bootstrap = new StartupBootstrapForm(
            CreateState,
            startupFailurePresenter: exception => harnessFailure ??= exception);
        using var watchdog = new System.Windows.Forms.Timer { Interval = 5_000 };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            harnessFailure ??= new TimeoutException("The startup presentation probe did not complete.");
            bootstrap.Close();
        };
        bootstrap.MainFormShownForTesting += (_, form) =>
        {
            try
            {
                if (bootstrap.Visible || form.Opacity < 1 || !form.Enabled)
                    throw new InvalidOperationException("The first main-form frame was revealed before the bootstrap transition completed.");

                var firstLayout = CaptureVisibleControlLayout(form);
                if (firstLayout.Length < 10)
                    throw new InvalidOperationException("The first main-form frame did not contain the completed dashboard control tree.");

                void RecordControlAddition(object? sender, ControlEventArgs e) =>
                    Interlocked.Increment(ref postRevealControlAdditions);
                foreach (var control in new[] { form }.Concat(SnapshotCapture.DescendantsOf(form)))
                    control.ControlAdded += RecordControlAddition;

                form.BeginInvoke((Action)(() => form.BeginInvoke((Action)(async () =>
                {
                    try
                    {
                        var settledLayout = CaptureVisibleControlLayout(form);
                        if (!firstLayout.SequenceEqual(settledLayout, StringComparer.Ordinal))
                            throw new InvalidOperationException("Visible controls changed size, position, or visibility after the first main-form frame.");
                        if (Volatile.Read(ref postRevealControlAdditions) != 0)
                            throw new InvalidOperationException("Controls were added after the first main-form frame became visible.");
                        presentationChecked = true;
                    }
                    catch (Exception exception)
                    {
                        harnessFailure ??= exception;
                    }
                    finally
                    {
                        watchdog.Stop();
                        await form.DisposeAfterFailedBootstrapAsync();
                        if (!bootstrap.IsDisposed) bootstrap.Close();
                    }
                }))));
            }
            catch (Exception exception)
            {
                harnessFailure ??= exception;
                bootstrap.Close();
            }
        };

        try
        {
            watchdog.Start();
            Application.Run(bootstrap);
        }
        finally
        {
            watchdog.Stop();
            bootstrap.DisposeAfterRunAsync().AsTask().GetAwaiter().GetResult();
        }

        if (harnessFailure is not null) throw harnessFailure;
        if (!presentationChecked || !bootstrap.TransitionCompletedForTesting)
            throw new InvalidOperationException("The completed first-frame startup transition was not observed.");
        if (createdState is null)
            throw new InvalidOperationException("The startup presentation probe did not create application state.");
    }

    private static string[] CaptureVisibleControlLayout(Control root) =>
        new[] { root }
            .Concat(SnapshotCapture.DescendantsOf(root))
            .Where(static control => control.Visible)
            .Select(static (control, index) =>
                $"{index}|{control.GetType().FullName}|{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}|{control.Text}")
            .ToArray();

    private static void RunPhaseOneStartupFailureHarness(
        string root,
        RimWorldModDiscoveryService discovery)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "projects.json"),
            "{ invalid synthetic project state",
            new UTF8Encoding(false));
        Exception? presentedFailure = null;
        Exception? loggingFailure = null;
        var watchdogTriggered = false;

        Task<AppStartupState> CreateState(CancellationToken cancellationToken) =>
            Task.FromResult(AppStartupState.Create(root, discovery, null, cancellationToken));

        var bootstrap = new StartupBootstrapForm(
            CreateState,
            startupFailurePresenter: exception => presentedFailure = exception,
            startupFailureRecorder: exception =>
                loggingFailure = RimWorldAiTranslator.App.Program.RecordStartupFailure(root, exception));
        using var watchdog = new System.Windows.Forms.Timer { Interval = 5_000 };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            watchdogTriggered = true;
            bootstrap.Close();
        };
        try
        {
            watchdog.Start();
            Application.Run(bootstrap);
        }
        finally
        {
            watchdog.Stop();
            bootstrap.DisposeAfterRunAsync().AsTask().GetAwaiter().GetResult();
        }

        if (watchdogTriggered)
            throw new TimeoutException("Phase-one startup failure did not close the bootstrap message loop.");
        if (!bootstrap.StartupFailed || bootstrap.TransitionCompletedForTesting)
            throw new InvalidOperationException("Phase-one startup failure state was not recorded accurately.");
        if (presentedFailure is not (System.Text.Json.JsonException or InvalidDataException)
            || loggingFailure is not null)
            throw new InvalidOperationException("Phase-one startup failure was not presented and recorded safely.");
        var logPath = Path.Combine(root, "logs", "startup-failures.log");
        var log = File.ReadAllText(logPath, Encoding.UTF8);
        if (!log.Contains("Stack=", StringComparison.Ordinal)
            || (!log.Contains(nameof(System.Text.Json.JsonException), StringComparison.Ordinal)
                && !log.Contains(nameof(InvalidDataException), StringComparison.Ordinal))
            || log.Contains("invalid synthetic project state", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Phase-one startup log did not retain only safe failure evidence.");
        }

        var unavailableRoot = Path.Combine(root, "not-a-directory");
        File.WriteAllText(unavailableRoot, "synthetic", new UTF8Encoding(false));
        var unavailable = RimWorldAiTranslator.App.Program.RecordStartupFailure(
            unavailableRoot,
            presentedFailure);
        if (unavailable is null)
            throw new InvalidOperationException("An unavailable startup log path was not reported for type-only fallback.");

        var hardLinkRoot = Path.Combine(root, "startup-hardlink-root");
        var hardLinkPaths = new AppDataPaths(hardLinkRoot);
        hardLinkPaths.EnsureExists();
        var hardLinkSentinel = Path.Combine(root, "startup-hardlink-sentinel.txt");
        File.WriteAllText(hardLinkSentinel, "startup-hardlink-sentinel", new UTF8Encoding(false));
        var hardLinkLog = Path.Combine(hardLinkPaths.Logs, "startup-failures.log");
        CreateHardLinkOrThrow(hardLinkLog, hardLinkSentinel);
        var hardLinkFailure = RimWorldAiTranslator.App.Program.RecordStartupFailure(
            hardLinkRoot,
            presentedFailure);
        if (hardLinkFailure is null
            || File.ReadAllText(hardLinkSentinel, Encoding.UTF8) != "startup-hardlink-sentinel")
        {
            throw new InvalidOperationException(
                "Startup failure logging wrote through a prepositioned hard link.");
        }

        var reparseRoot = Path.Combine(root, "startup-reparse-root");
        var reparsePaths = new AppDataPaths(reparseRoot);
        reparsePaths.EnsureExists();
        var reparseSentinel = Path.Combine(root, "startup-reparse-sentinel.txt");
        File.WriteAllText(reparseSentinel, "startup-reparse-sentinel", new UTF8Encoding(false));
        var reparseLog = Path.Combine(reparsePaths.Logs, "startup-failures.log");
        var reparseFixtureCreated = false;
        try
        {
            File.CreateSymbolicLink(reparseLog, reparseSentinel);
            reparseFixtureCreated = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Startup reparse fixture unavailable ({exception.GetType().Name}).");
        }
        if (reparseFixtureCreated)
        {
            var reparseFailure = RimWorldAiTranslator.App.Program.RecordStartupFailure(
                reparseRoot,
                presentedFailure);
            if (reparseFailure is null
                || File.ReadAllText(reparseSentinel, Encoding.UTF8) != "startup-reparse-sentinel")
            {
                throw new InvalidOperationException(
                    "Startup failure logging wrote through a prepositioned file reparse point.");
            }
        }
    }

    private static void RunMainFormStartupFailureHarness(
        string root,
        RimWorldModDiscoveryService discovery)
    {
        Directory.CreateDirectory(root);
        AppStartupState? createdState = null;
        MainForm? createdForm = null;
        Exception? presentedFailure = null;
        var mainFormShown = false;
        var watchdogTriggered = false;

        Task<AppStartupState> CreateState(CancellationToken cancellationToken)
        {
            createdState = AppStartupState.Create(root, discovery, null, cancellationToken);
            return Task.FromResult(createdState);
        }

        var ioHooks = new UiIoHooks(MainFormStartup: ThrowSyntheticMainFormStartupFailure);
        using var watchdog = new System.Windows.Forms.Timer { Interval = 5_000 };
        var bootstrap = new StartupBootstrapForm(
            CreateState,
            startupFailurePresenter: exception => presentedFailure = exception,
            mainFormFactoryForTesting: state =>
            {
                createdForm = new MainForm(
                    state.Services,
                    state.ProjectStats,
                    state.IsolationAcknowledgementPath,
                    ioHooks: ioHooks);
                return createdForm;
            });
        bootstrap.MainFormShownForTesting += (_, _) => mainFormShown = true;
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            watchdogTriggered = true;
            bootstrap.Close();
        };

        try
        {
            watchdog.Start();
            Application.Run(bootstrap);
        }
        finally
        {
            watchdog.Stop();
            bootstrap.DisposeAfterRunAsync().AsTask().GetAwaiter().GetResult();
        }

        if (watchdogTriggered)
            throw new TimeoutException("The main-form startup failure did not close the application message loop.");
        if (mainFormShown || bootstrap.TransitionCompletedForTesting)
            throw new InvalidOperationException("An incompletely initialized main form was exposed after a startup failure.");
        if (!bootstrap.StartupFailed || createdForm?.StartupFailedForTesting != true)
            throw new InvalidOperationException("Second-stage startup failure did not reach both failure states.");
        if (presentedFailure is not IOException)
            throw new InvalidOperationException("Second-stage startup failure was not presented through the bootstrap.");
        if (createdForm is null || !createdForm.IsDisposed)
            throw new InvalidOperationException("The failed main form remained open.");
        if (createdForm.HasIncompleteUiWorkflowsForTesting)
            throw new InvalidOperationException("A UI workflow remained active after startup failure cleanup.");
        if (createdState is null)
            throw new InvalidOperationException("The second-stage startup state was not created.");

        var logPath = createdState.Services.Logger.Path;
        if (!File.Exists(logPath) || !CanOpenExclusive(logPath))
            throw new InvalidOperationException("Second-stage startup failure did not release its persistent logger.");
        var log = File.ReadAllText(logPath, Encoding.UTF8);
        if (!log.Contains("프로그램 시작 실패", StringComparison.Ordinal)
            || !log.Contains("Stack=", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Second-stage startup failure did not retain a safe method-only stack log.");
        }
        if (log.Contains(SyntheticStartupSecret, StringComparison.Ordinal)
            || log.Contains("private\\source.xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Second-stage startup failure log retained raw exception data.");
        }
    }

    private static void RunUiActionTruthfulnessHarness(
        string root,
        RimWorldModDiscoveryService discovery)
    {
        Directory.CreateDirectory(root);
        string? presented = null;
        var form = new MainForm(
            root,
            discovery,
            ioHooks: new UiIoHooks(ErrorPresenter: message => presented = message));
        form.SuppressStartupForTesting();
        Exception? harnessFailure = null;
        var scenarioCompleted = false;
        using var watchdog = new System.Windows.Forms.Timer { Interval = 5_000 };
        form.Shown += async (_, _) =>
        {
            try
            {
                form.ShowWorkspaceForTesting();
                Application.DoEvents();
                var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var operationTask = form.RunOperationForTesting(
                    async cancellationToken =>
                    {
                        operationStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    },
                    CancellationToken.None);
                await operationStarted.Task;
                var stop = SnapshotCapture.DescendantsOf(form.WorkspaceControlForTesting)
                    .OfType<Button>()
                    .Single(button => button.Text.Equals("중지", StringComparison.Ordinal));
                var safeStopTimer = System.Diagnostics.Stopwatch.StartNew();
                var visualStopTimer = System.Diagnostics.Stopwatch.StartNew();
                stop.PerformClick();
                visualStopTimer.Stop();
                if (visualStopTimer.Elapsed > TimeSpan.FromMilliseconds(250)
                    || stop.Enabled
                    || !stop.Text.Equals("요청됨", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Cancellation visual feedback exceeded 250 ms or was not visible.");
                }
                Application.DoEvents();
                await operationTask;
                safeStopTimer.Stop();
                if (safeStopTimer.Elapsed > TimeSpan.FromSeconds(2))
                    throw new InvalidOperationException("Cancellation did not reach a safe completed state within two seconds.");

                await form.RunUiActionForTestingAsync(
                    "합성 UI 작업",
                    () => Task.FromException(new IOException(SyntheticStartupSecret)));
                if (string.IsNullOrWhiteSpace(presented)
                    || presented.Contains(nameof(IOException), StringComparison.Ordinal)
                    || !presented.Contains("다시 시도", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "UI action failure exposed an internal exception name or omitted the shared safe recovery guidance.");
                }
                if (presented.Contains("기존 데이터는 보존", StringComparison.Ordinal)
                    || presented.Contains(SyntheticStartupSecret, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("UI action failure retained a blanket preservation claim or raw exception message.");
                }
            }
            catch (Exception exception)
            {
                harnessFailure = exception;
            }
            finally
            {
                scenarioCompleted = true;
                watchdog.Stop();
                await form.DisposeAfterFailedBootstrapAsync();
            }
        };
        watchdog.Tick += async (_, _) =>
        {
            watchdog.Stop();
            harnessFailure ??= new TimeoutException("The UI action truthfulness scenario did not complete.");
            await form.DisposeAfterFailedBootstrapAsync();
        };
        watchdog.Start();
        Application.Run(form);
        watchdog.Stop();
        if (!scenarioCompleted && harnessFailure is null)
            throw new InvalidOperationException("The UI action truthfulness scenario ended without completion.");
        if (harnessFailure is not null) throw harnessFailure;
    }

    private static void RunSaveCancellationHarness(string root, string discoveryRoot)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(discoveryRoot);
        File.WriteAllText(
            Path.Combine(discoveryRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
            RimWorldModDiscoveryService.IsolationMarkerContent + Environment.NewLine,
            new UTF8Encoding(false));
        var reviewsRoot = Path.Combine(root, "reviews");
        Directory.CreateDirectory(reviewsRoot);
        var fixture = SyntheticUiFixture.Create(reviewsRoot, discoveryRoot, 3, "dashboard");
        File.Copy(Path.Combine(reviewsRoot, "projects.json"), Path.Combine(root, "projects.json"), overwrite: true);
        var discovery = RimWorldModDiscoveryService.CreateIsolated(discoveryRoot);
        var state = AppStartupState.Create(root, discovery, null, CancellationToken.None);
        var form = new MainForm(state.Services, state.ProjectStats);
        state.TransferOwnershipToMainForm();
        var project = state.Services.ProjectStore.Projects.Single();
        var review = state.Services.Reviews.Load(fixture.ReviewRoot, project);
        review.Dirty = true;
        review.Revision++;
        form.SuppressStartupForTesting();
        var decisionPath = ReviewRepository.GetDecisionPath(fixture.ReviewRoot);
        var before = File.ReadAllBytes(decisionPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception? harnessFailure = null;
        var scenarioCompleted = false;
        using var watchdog = new System.Windows.Forms.Timer { Interval = 5_000 };
        form.Shown += async (_, _) =>
        {
            try
            {
                form.LoadWorkspaceForTesting(project, review);
                try
                {
                    await form.SaveReviewForTestingAsync(cancellation.Token);
                    throw new InvalidOperationException("A pre-cancelled review save completed successfully.");
                }
                catch (OperationCanceledException)
                {
                    // Expected: the UI token must reach the review persistence boundary.
                }
                if (!before.AsSpan().SequenceEqual(File.ReadAllBytes(decisionPath)))
                    throw new InvalidOperationException("A pre-cancelled review save changed the decision file.");
                if (!review.Dirty)
                    throw new InvalidOperationException("A pre-cancelled review save cleared the dirty state.");
            }
            catch (Exception exception)
            {
                harnessFailure = exception;
            }
            finally
            {
                scenarioCompleted = true;
                watchdog.Stop();
                await form.DisposeAfterFailedBootstrapAsync();
            }
        };
        watchdog.Tick += async (_, _) =>
        {
            watchdog.Stop();
            harnessFailure ??= new TimeoutException("The review-save cancellation scenario did not complete.");
            await form.DisposeAfterFailedBootstrapAsync();
        };
        watchdog.Start();
        Application.Run(form);
        watchdog.Stop();
        state.Dispose();
        if (!scenarioCompleted && harnessFailure is null)
            throw new InvalidOperationException("The review-save cancellation scenario ended without completion.");
        if (harnessFailure is not null) throw harnessFailure;
    }

    private static void RunCommittedFinalizationHarness()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var callbackRan = false;
        var result = MainForm.FinalizeCommittedOperationForTestingAsync(
                "RMK에 적용",
                finalizationToken =>
                {
                    callbackRan = true;
                    if (finalizationToken.CanBeCanceled || finalizationToken.IsCancellationRequested)
                        throw new InvalidOperationException("Post-commit metadata inherited the cancelled operation token.");
                    return Task.FromResult(17);
                },
                cancellation.Token)
            .GetAwaiter()
            .GetResult();
        if (!callbackRan || result != 17)
            throw new InvalidOperationException("Post-commit metadata finalization did not complete.");

        try
        {
            MainForm.FinalizeCommittedOperationForTestingAsync<int>(
                    "모드에 적용",
                    _ => Task.FromException<int>(new IOException("synthetic metadata failure")),
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();
            throw new InvalidOperationException("A post-commit metadata failure was not classified.");
        }
        catch (CommittedOperationFinalizationException exception)
            when (exception.InnerException is IOException
                  && exception.OperationLabel.Equals("모드에 적용", StringComparison.Ordinal))
        {
            // Expected: callers can distinguish committed output from metadata failure.
        }
    }

    private static void RunProjectDeleteCacheFailureHarness(
        string root,
        string discoveryRoot,
        RimWorldModDiscoveryService discovery)
    {
        Directory.CreateDirectory(root);
        var modRoot = Path.Combine(discoveryRoot, "synthetic-local-mod");
        Directory.CreateDirectory(modRoot);
        var project = new TranslationProject
        {
            Id = "synthetic-delete-project",
            Name = "Synthetic delete project",
            ModRoot = modRoot,
            SourceKind = "Local",
            PackageId = "synthetic.delete.project",
            SourceLanguageFolder = "English",
            CreatedAt = "2026-01-01T00:00:00.0000000Z",
            UpdatedAt = "2026-01-01T00:00:00.0000000Z"
        };
        var document = new ProjectStoreDocument
        {
            Version = 2,
            UpdatedAt = project.UpdatedAt,
            Projects = [project]
        };
        new AtomicJsonStore().Write(Path.Combine(root, "projects.json"), document);

        var state = AppStartupState.Create(root, discovery, null, CancellationToken.None);
        string? warning = null;
        var cacheSaveAttempted = false;
        var form = new MainForm(
            state.Services,
            state.ProjectStats,
            ioHooks: new UiIoHooks(
                ProjectDeleteConfirmation: _ => DialogResult.Yes,
                SaveProjectStatsCache: _ =>
                {
                    cacheSaveAttempted = true;
                    throw new IOException(SyntheticStartupSecret);
                },
                WarningPresenter: message => warning = message));
        state.TransferOwnershipToMainForm();
        form.SuppressStartupForTesting();
        var deletedReviewRoot = Path.Combine(root, "deleted-review-root");
        var staleWorkspace = new ReviewWorkspace(
            deletedReviewRoot,
            Path.Combine(deletedReviewRoot, "_TranslationAudit", "comparison.json"),
            [],
            extensionData: null,
            quarantinedItems: [],
            new ReviewComparisonEvidence("synthetic-deleted-comparison.json", "synthetic"))
        {
            MigrationPending = true
        };
        Exception? harnessFailure = null;
        var scenarioCompleted = false;
        using var watchdog = new System.Windows.Forms.Timer { Interval = 5_000 };
        form.Shown += async (_, _) =>
        {
            try
            {
                form.LoadWorkspaceForTesting(state.Services.ProjectStore.Projects.Single(), staleWorkspace);
                await form.DeleteProjectForTestingAsync(
                    state.Services.ProjectStore.Projects.Single(),
                    CancellationToken.None);
                if (!cacheSaveAttempted)
                    throw new InvalidOperationException("The derived project-statistics cache was not exercised.");
                if (state.Services.ProjectStore.Projects.Count != 0)
                    throw new InvalidOperationException("The committed project deletion was not retained in memory.");
                if (form.WorkspaceControlForTesting.Project is not null
                    || form.WorkspaceControlForTesting.Workspace is not null)
                {
                    throw new InvalidOperationException(
                        "The deleted project's review workspace remained attached to the MainForm.");
                }
                await form.SaveReviewForTestingAsync(CancellationToken.None);
                var persisted = new ProjectRepository(new AtomicJsonStore(), state.Services.Paths).Load();
                if (persisted.Projects.Count != 0)
                    throw new InvalidOperationException("The committed project deletion was not retained on disk.");
                if (persisted.Revision <= 0
                    || state.Services.ProjectStore.Revision != persisted.Revision)
                {
                    throw new InvalidOperationException(
                        "The MainForm project-store merge lost the persisted concurrency revision.");
                }
                if (string.IsNullOrWhiteSpace(warning)
                    || !warning.Contains("프로젝트 삭제는 완료", StringComparison.Ordinal)
                    || !warning.Contains("다음 통계 새로고침", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A derived cache failure was not presented as post-delete recovery work.");
                }
                if (warning.Contains(SyntheticStartupSecret, StringComparison.Ordinal))
                    throw new InvalidOperationException("The project-delete warning retained a raw exception message.");
            }
            catch (Exception exception)
            {
                harnessFailure = exception;
            }
            finally
            {
                scenarioCompleted = true;
                watchdog.Stop();
                await form.DisposeAfterFailedBootstrapAsync();
            }
        };
        watchdog.Tick += async (_, _) =>
        {
            watchdog.Stop();
            harnessFailure ??= new TimeoutException("The project-delete cache-failure scenario did not complete.");
            await form.DisposeAfterFailedBootstrapAsync();
        };
        watchdog.Start();
        Application.Run(form);
        watchdog.Stop();
        state.Dispose();
        if (!scenarioCompleted && harnessFailure is null)
            throw new InvalidOperationException("The project-delete cache-failure scenario ended without completion.");
        if (harnessFailure is not null) throw harnessFailure;
    }

    private const string SyntheticStartupSecret = "synthetic-startup-secret C:\\private\\source.xml";

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowSyntheticMainFormStartupFailure() =>
        throw new IOException(SyntheticStartupSecret);

    private static void RunBootstrapCleanupTimeoutHarness()
    {
        var neverCompletes = new TaskCompletionSource<AppStartupState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryStarted = 0;

        Task<AppStartupState> NonCooperativeFactory(CancellationToken _)
        {
            Interlocked.Exchange(ref factoryStarted, 1);
            return neverCompletes.Task;
        }

        var bootstrap = new StartupBootstrapForm(
            NonCooperativeFactory,
            shutdownCleanupTimeout: TimeSpan.FromMilliseconds(100));
        using var closeTimer = new System.Windows.Forms.Timer { Interval = 20 };
        var startedAt = DateTime.UtcNow;
        closeTimer.Tick += (_, _) =>
        {
            if (Volatile.Read(ref factoryStarted) != 0)
            {
                closeTimer.Stop();
                bootstrap.Close();
            }
            else if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(5))
            {
                closeTimer.Stop();
                bootstrap.Close();
            }
        };
        closeTimer.Start();
        Application.Run(bootstrap);
        closeTimer.Stop();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bootstrap.DisposeAfterRunAsync().AsTask().GetAwaiter().GetResult();
        stopwatch.Stop();
        if (Volatile.Read(ref factoryStarted) == 0)
            throw new InvalidOperationException("The non-cooperative startup factory was never invoked.");
        if (stopwatch.Elapsed >= TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("A non-cooperative startup factory blocked terminal cleanup without a bound.");
        if (!bootstrap.HasDeferredShutdownObserverForTesting)
            throw new InvalidOperationException("Timed-out startup cleanup was not assigned a deferred observer.");
    }

    private static void RunSingleInstanceProbe(string root)
    {
        var alternateRoot = Path.Combine(root, "alternate-data-root");
        if (!SingleInstanceLease.TryAcquire(root, out var first) || first is null)
            throw new InvalidOperationException("The first synthetic data-root lease was not acquired.");
        using (first)
        {
            if (SingleInstanceLease.TryAcquire(root, out var duplicate) || duplicate is not null)
            {
                duplicate?.Dispose();
                throw new InvalidOperationException("A duplicate lease for the same data root was admitted.");
            }

            if (!SingleInstanceLease.TryAcquire(alternateRoot, out var alternate) || alternate is null)
                throw new InvalidOperationException("An independent data-root lease was rejected.");
            alternate.Dispose();
        }

        if (!SingleInstanceLease.TryAcquire(root, out var reacquired) || reacquired is null)
            throw new InvalidOperationException("A released data-root lease could not be acquired again.");
        reacquired.Dispose();
    }

    private static void RunSlowBootstrapHarness(
        SnapshotOptions options,
        string root,
        RimWorldModDiscoveryService discovery,
        int uiThreadId)
    {
        var releaseFactory = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        AppStartupState? createdState = null;
        Exception? assertion = null;
        var factoryThreadId = 0;
        var stateCreationThreadId = 0;
        var factoryStarted = 0;
        var timerTicksBeforeRelease = 0;
        var releaseRequested = false;
        var mainTransitionObserved = false;
        var startedAt = DateTime.UtcNow;

        async Task<AppStartupState> SlowFactory(CancellationToken cancellationToken)
        {
            factoryThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Exchange(ref factoryStarted, 1);
            await releaseFactory.Task.ConfigureAwait(false);
            // The cancellation scenario intentionally completes late. This verifies that a
            // result produced by non-cooperative initialization is still observed and owned.
            stateCreationThreadId = Environment.CurrentManagedThreadId;
            createdState = AppStartupState.Create(root, discovery, null, CancellationToken.None);
            return createdState;
        }

        var bootstrap = new StartupBootstrapForm(SlowFactory);
        using var responsivenessTimer = new System.Windows.Forms.Timer { Interval = 40 };
        try
        {
            bootstrap.MainFormShownForTesting += (_, form) =>
            {
                try
                {
                    if (options.CloseDuringStartup)
                        throw new InvalidOperationException("A cancelled bootstrap transitioned to the main form.");
                    if (timerTicksBeforeRelease < 3)
                        throw new InvalidOperationException("The bootstrap message loop did not remain responsive before initialization completed.");
                    if (factoryThreadId == uiThreadId)
                        throw new InvalidOperationException("The bootstrap factory executed on the UI thread.");
                    mainTransitionObserved = true;
                    form.BeginInvoke((Action)form.Close);
                }
                catch (Exception exception)
                {
                    assertion ??= exception;
                    try { form.Close(); }
                    catch (Exception closeException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Slow bootstrap harness close skipped ({closeException.GetType().Name}).");
                    }
                }
            };

            responsivenessTimer.Tick += (_, _) =>
            {
                try
                {
                    if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(10))
                        throw new TimeoutException("The slow bootstrap harness did not reach its release boundary.");
                    if (Volatile.Read(ref factoryStarted) == 0) return;
                    timerTicksBeforeRelease++;
                    if (timerTicksBeforeRelease < 3 || releaseRequested) return;
                    if (factoryThreadId == uiThreadId)
                        throw new InvalidOperationException("The bootstrap factory executed on the UI thread.");

                    releaseRequested = true;
                    if (options.CloseDuringStartup) bootstrap.Close();
                    releaseFactory.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    assertion ??= exception;
                    releaseFactory.TrySetResult(true);
                    try { bootstrap.Close(); }
                    catch (Exception closeException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Slow bootstrap failure close skipped ({closeException.GetType().Name}).");
                    }
                }
            };
            responsivenessTimer.Start();
            Application.Run(bootstrap);
            responsivenessTimer.Stop();
            releaseFactory.TrySetResult(true);
            bootstrap.StartupObserverForTesting
                .WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter()
                .GetResult();

            if (assertion is not null) throw assertion;
            if (Volatile.Read(ref factoryStarted) == 0)
                throw new InvalidOperationException("The bootstrap factory was never invoked.");
            if (stateCreationThreadId == uiThreadId)
                throw new InvalidOperationException("Application service creation resumed on the UI thread.");
            if (timerTicksBeforeRelease < 3)
                throw new InvalidOperationException("The bootstrap message loop responsiveness probe did not complete.");
            if (options.CloseDuringStartup)
            {
                if (mainTransitionObserved || bootstrap.TransitionCompletedForTesting)
                    throw new InvalidOperationException("The cancelled bootstrap completed a main-form transition.");
                if (createdState is null)
                    throw new InvalidOperationException("The non-cooperative bootstrap factory did not produce its late result.");
                if (!createdState.WasDisposedForTesting)
                    throw new InvalidOperationException("The late bootstrap result retained its application services.");
            }
            else if (!mainTransitionObserved || !bootstrap.TransitionCompletedForTesting)
            {
                throw new InvalidOperationException("The successful bootstrap did not transition to the main form.");
            }
            if (createdState is null)
                throw new InvalidOperationException("The bootstrap factory did not return its synthetic state.");
            AssertStartupResourcesReleased(createdState);
        }
        finally
        {
            responsivenessTimer.Stop();
            releaseFactory.TrySetResult(true);
            bootstrap.DisposeAfterRunAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void AssertStartupResourcesReleased(AppStartupState state)
    {
        var logPath = state.Services.Logger.Path;
        if (!File.Exists(logPath)) return;
        if (!CanOpenExclusive(logPath))
            throw new InvalidOperationException("A startup logger handle remained open after cleanup.");
    }

    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CreateHardLinkOrThrow(string linkPath, string existingPath)
    {
        if (CreateHardLink(linkPath, existingPath, IntPtr.Zero)) return;
        throw new IOException(
            $"Could not create the startup hard-link fixture ({Marshal.GetLastWin32Error()}).");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static async Task ObserveSnapshotWorkflowAsync(Task task, Form form)
    {
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (task.IsCompletedSuccessfully || form.IsDisposed || !form.IsHandleCreated) return;
        try { form.BeginInvoke((Action)form.Close); }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine($"Snapshot failure close skipped ({exception.GetType().Name}).");
        }
    }
}

internal sealed record SnapshotOptions(
    string DataRoot,
    string DiscoveryRoot,
    string SnapshotPath,
    string ErrorOutput,
    string InitialTab,
    bool OpenFirstProject,
    string WorkspaceTab,
    string SearchText,
    int SelectIndex,
    int TopIndex,
    string FocusAccessibleName,
    string Preview,
    string Theme,
    string Preset,
    string FixtureProfile,
    bool HighContrast,
    int TextSize,
    int Width,
    int Height,
    bool Maximize,
    int SeedRows,
    int WaitMilliseconds,
    bool CloseDuringStartup,
    bool CloseTimeoutRecover,
    bool CloseBehaviorProbe,
    bool UiInteractionProbe,
    bool MetadataAccessibilityProbe,
    bool CommandPaletteProbe,
    bool SingleInstanceProbe,
    string ReportPath,
    bool SlowIoProbe,
    bool SlowBootstrap,
    bool SlowLoggerDrain,
    bool BootstrapTransitionFailure,
    bool Phase08UiTruthfulness,
    bool BootstrapCleanupTimeout,
    bool ProviderUrlSecurity,
    bool DpiUnaware,
    string CompareBaseline,
    string CompareCandidate,
    string CompareOutput,
    string CompareName)
{
    public static SnapshotOptions Parse(IReadOnlyList<string> args)
    {
        string Value(string name, string fallback = "")
        {
            for (var index = 0; index < args.Count - 1; index++)
                if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return fallback;
        }

        static int Number(string value, int fallback) => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
        static int NonNegativeNumber(string value, int fallback) => int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;
        bool Has(string name) => args.Any(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return new SnapshotOptions(
            Value("--data-root"),
            Value("--discovery-root"),
            Value("--snapshot"),
            Value("--error-output"),
            Value("--initial-tab", "projects"),
            Has("--open-first-project"),
            Value("--workspace-tab", "compare"),
            Value("--search-text"),
            NonNegativeNumber(Value("--select-index"), -1),
            NonNegativeNumber(Value("--top-index"), -1),
            Value("--focus-name"),
            Value("--preview"),
            Value("--theme"),
            Value("--preset", "Professional"),
            Value("--fixture-profile", "extended"),
            Has("--high-contrast"),
            Number(Value("--text-size"), 10),
            Number(Value("--width"), 0),
            Number(Value("--height"), 0),
            Has("--maximize"),
            Number(Value("--seed-rows"), 0),
            Number(Value("--wait-ms"), 12_000),
            Has("--close-during-startup"),
            Has("--close-timeout-recover"),
            Has("--close-behavior-probe"),
            Has("--ui-interaction-probe"),
            Has("--metadata-accessibility-probe"),
            Has("--command-palette-probe"),
            Has("--single-instance-probe"),
            Value("--report"),
            Has("--slow-io-probe"),
            Has("--slow-bootstrap"),
            Has("--slow-logger-drain"),
            Has("--bootstrap-transition-failure"),
            Has("--phase08-ui-truthfulness"),
            Has("--bootstrap-cleanup-timeout"),
            Has("--provider-url-security"),
            Has("--dpi-unaware"),
            Value("--compare-baseline"),
            Value("--compare-candidate"),
            Value("--compare-output"),
            Value("--compare-name", "comparison"));
    }
}
