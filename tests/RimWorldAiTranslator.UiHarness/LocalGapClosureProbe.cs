using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.App;
using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.App.Dialogs;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;

namespace RimWorldAiTranslator.UiHarness;

internal static class LocalGapClosureProbe
{
    private const string Mode = "--local-gap-closure-probe";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };

    internal static bool TryRun(IReadOnlyList<string> args, out int exitCode)
    {
        if (!args.Any(value => value.Equals(Mode, StringComparison.OrdinalIgnoreCase)))
        {
            exitCode = 0;
            return false;
        }

        var reportPath = ReadValue(args, "--report");
        var root = Path.Combine(
            Path.GetTempPath(),
            "RimWorldAiTranslator-local-gap-probe-" + Guid.NewGuid().ToString("N"));
        var report = new LocalGapReport
        {
            SchemaVersion = "local-gap-closure-v1",
            SyntheticFixture = true,
            UserDataUsed = false,
            ExternalNetworkUsed = false
        };
        try
        {
            Directory.CreateDirectory(root);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            report.SourceLanguageCancel = VerifySourceLanguageCancel(Path.Combine(root, "source-language"));
            report.SettingsActivityRestart = VerifySettingsActivityRestart(Path.Combine(root, "restart"));
            report.Status = "PASS";
            report.Cleanup = "pending";
            exitCode = 0;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Local gap closure probe failed ({exception.GetType().Name}).");
            report.Status = "FAIL";
            report.FailureType = exception.GetType().Name;
            report.FailureMessage = Bound(exception.Message, 512);
            exitCode = 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                report.Cleanup = Directory.Exists(root) ? "FAILED" : "PASS";
                if (report.Cleanup != "PASS") exitCode = 1;
            }
            catch (Exception exception)
            {
                report.Cleanup = "FAILED:" + exception.GetType().Name;
                exitCode = 1;
            }

            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                var fullReportPath = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullReportPath)!);
                File.WriteAllText(
                    fullReportPath,
                    JsonSerializer.Serialize(report, ReportJsonOptions),
                    new UTF8Encoding(false));
            }
        }

        return true;
    }

    private static SourceLanguageCancelReport VerifySourceLanguageCancel(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var discoveryRoot = Path.Combine(root, "steam");
        var modRoot = Path.Combine(
            discoveryRoot,
            "steamapps",
            "common",
            "RimWorld",
            "Mods",
            "LanguageChoiceFixture");
        Directory.CreateDirectory(Path.Combine(modRoot, "About"));
        Directory.CreateDirectory(Path.Combine(modRoot, "Languages", "English", "Keyed"));
        Directory.CreateDirectory(Path.Combine(modRoot, "Languages", "Japanese", "Keyed"));
        File.WriteAllText(
            Path.Combine(discoveryRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
            RimWorldModDiscoveryService.IsolationMarkerContent + Environment.NewLine,
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(modRoot, "About", "About.xml"),
            "<ModMetaData><name>Language Choice Fixture</name><packageId>fixture.language.choice</packageId></ModMetaData>",
            new UTF8Encoding(false));
        foreach (var language in new[] { "English", "Japanese" })
        {
            File.WriteAllText(
                Path.Combine(modRoot, "Languages", language, "Keyed", "Fixture.xml"),
                "<LanguageData><Fixture.Start>Start</Fixture.Start></LanguageData>",
                new UTF8Encoding(false));
        }

        Directory.CreateDirectory(dataRoot);
        var discovery = RimWorldModDiscoveryService.CreateIsolated(discoveryRoot);
        var mod = discovery.GetModInfo(modRoot, "Local")
            ?? throw new InvalidOperationException("The source-language fixture mod was not discovered.");
        var choices = discovery.GetSourceLanguageOptions(mod.Path);
        Require(choices.Count == 2 && choices[0].Folder == "English" && choices[1].Folder == "Japanese",
            "The source-language choices were not ranked deterministically.");

        using var form = new MainForm(dataRoot, discovery);
        form.SuppressStartupForTesting();
        Exception? failure = null;
        var scenarioCompleted = false;
        var cancelClicked = false;
        var observedChoiceCount = 0;
        using var cancelTimer = new System.Windows.Forms.Timer { Interval = 20 };
        using var watchdog = new System.Windows.Forms.Timer { Interval = checked((int)UiTimeout.TotalMilliseconds) };
        cancelTimer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.OfType<SourceLanguageDialog>().SingleOrDefault();
            if (dialog is null) return;
            var list = SnapshotCapture.DescendantsOf(dialog).OfType<ListBox>().Single();
            observedChoiceCount = list.Items.Count;
            Require(list.SelectedItem is SourceLanguageOption selected && selected.Folder == "English",
                "The source-language dialog did not select the ranked first option.");
            cancelClicked = true;
            cancelTimer.Stop();
            ((Button)dialog.CancelButton!).PerformClick();
        };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            failure ??= new TimeoutException("The source-language cancel workflow timed out.");
            foreach (var dialog in Application.OpenForms.OfType<SourceLanguageDialog>().ToArray()) dialog.Close();
            form.Close();
        };
        form.Shown += async (_, _) =>
        {
            try
            {
                cancelTimer.Start();
                watchdog.Start();
                var method = typeof(MainForm).GetMethod(
                    "CreateProjectAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(typeof(MainForm).FullName, "CreateProjectAsync");
                var task = (Task?)method.Invoke(form, [mod, CancellationToken.None])
                    ?? throw new InvalidOperationException("CreateProjectAsync returned no task.");
                await task;
                Require(cancelClicked && observedChoiceCount == 2,
                    "The source-language cancel action was not observed.");
                var projectsPath = Path.Combine(dataRoot, "projects.json");
                var reviewRoot = Path.Combine(dataRoot, "reviews");
                Require(!File.Exists(projectsPath),
                    "Cancelling source-language selection wrote the project store.");
                Require(!Directory.EnumerateFileSystemEntries(reviewRoot, "*", SearchOption.AllDirectories).Any(),
                    "Cancelling source-language selection wrote a review artifact.");
                scenarioCompleted = true;
            }
            catch (Exception exception)
            {
                failure = exception is TargetInvocationException { InnerException: not null }
                    ? exception.InnerException
                    : exception;
            }
            finally
            {
                cancelTimer.Stop();
                watchdog.Stop();
                form.Close();
            }
        };
        Application.Run(form);
        Require(scenarioCompleted && failure is null,
            failure?.Message ?? "The source-language cancel scenario did not complete.");
        return new SourceLanguageCancelReport
        {
            Status = "PASS",
            DiscoveredChoiceCount = choices.Count,
            DialogChoiceCount = observedChoiceCount,
            RankedFirstFolder = choices[0].Folder,
            CancelClicked = cancelClicked,
            ProjectStoreWritten = false,
            ReviewArtifactWritten = false
        };
    }

    private static SettingsActivityRestartReport VerifySettingsActivityRestart(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var discoveryRoot = Path.Combine(root, "discovery");
        Directory.CreateDirectory(discoveryRoot);
        File.WriteAllText(
            Path.Combine(discoveryRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
            RimWorldModDiscoveryService.IsolationMarkerContent + Environment.NewLine,
            new UTF8Encoding(false));
        var syntheticModRoot = Path.Combine(discoveryRoot, "synthetic-mod");
        Directory.CreateDirectory(syntheticModRoot);
        var discovery = RimWorldModDiscoveryService.CreateIsolated(discoveryRoot);
        const string memoryOnlyKey = "synthetic-memory-only-key-never-persist";
        var runTime = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.FromHours(9));

        using (var first = new AppServices(dataRoot, discovery))
        {
            first.Settings.ThemeMode = "Dark";
            first.Settings.DesignPreset = "SciFi";
            first.Settings.TextSize = 12;
            first.Settings.AutoSave = false;
            first.Settings.ApiProviderId = "Custom";
            first.Settings.ApiProviders["Custom"] = new ApiProviderSettings
            {
                Name = "Synthetic Custom",
                Url = "https://example.invalid/v1/chat/completions",
                Model = "synthetic-model"
            };
            first.ApiKeys["Custom"] = memoryOnlyKey;
            first.SaveSettingsAsync(first.Settings).GetAwaiter().GetResult();
            first.ProjectStore.Projects.Add(new TranslationProject
            {
                Id = "restart-project",
                Name = "Restart Fixture",
                ModRoot = syntheticModRoot,
                SourceKind = "Folder",
                SourceLanguageFolder = "English",
                Runs =
                [
                    new ProjectRun
                    {
                        CreatedAt = runTime.ToString("O")
                    }
                ]
            });
            first.ProjectRepository.Save(first.ProjectStore);
        }

        var settingsPath = Path.Combine(dataRoot, "settings.json");
        var settingsBytes = File.ReadAllBytes(settingsPath);
        Require(!Encoding.UTF8.GetString(settingsBytes).Contains(memoryOnlyKey, StringComparison.Ordinal),
            "A memory-only API key was persisted before restart.");
        File.Copy(settingsPath, settingsPath + ".bak", overwrite: true);
        File.WriteAllText(settingsPath, "{ invalid synthetic settings", new UTF8Encoding(false));

        int recoveryNoticeCount;
        int secondDrainCount;
        ActivityEntry[] activityEntries;
        using (var second = new AppServices(dataRoot, discovery))
        {
            Require(second.Settings.ThemeMode == "Dark"
                    && second.Settings.DesignPreset == "SciFi"
                    && second.Settings.TextSize == 12
                    && !second.Settings.AutoSave
                    && second.Settings.ApiProviderId == "Custom"
                    && second.Settings.ApiProviders["Custom"].Model == "synthetic-model",
                "Non-secret settings did not survive restart and backup recovery.");
            Require(second.ApiKeys.Count == 0,
                "A memory-only API key survived process-service reconstruction.");
            Require(second.ProjectStore.Projects is
                    [
                        {
                            Id: "restart-project",
                            Name: "Restart Fixture",
                            SourceLanguageFolder: "English",
                            Runs.Count: 1
                        }
                    ],
                "The project and run history did not survive restart.");
            activityEntries = second.Activity.Load(second.ProjectStore.Projects).ToArray();
            Require(activityEntries.Any(entry => entry.Project == "Restart Fixture" && entry.Time == runTime),
                "Persisted run history did not reappear in Activity.");
            var notices = second.DrainRecoveryNotices();
            recoveryNoticeCount = notices.Count;
            Require(notices.Count == 1
                    && Path.GetFileName(notices[0].StorePath) == "settings.json"
                    && !string.IsNullOrWhiteSpace(notices[0].PreservedCorruptPath),
                "Restart recovery did not queue the exact settings recovery notice.");
            secondDrainCount = second.DrainRecoveryNotices().Count;
            Require(secondDrainCount == 0,
                "The recovery notice queue did not drain exactly once.");
        }

        Require(File.ReadAllBytes(settingsPath).SequenceEqual(settingsBytes),
            "Backup recovery did not restore the exact persisted settings bytes.");
        Require(!Encoding.UTF8.GetString(File.ReadAllBytes(settingsPath)).Contains(memoryOnlyKey, StringComparison.Ordinal),
            "A memory-only API key appeared after restart recovery.");

        var uiSettingsBound = false;
        var uiActivityRows = 0;
        var uiScenarioCompleted = false;
        Exception? uiFailure = null;
        var form = new MainForm(dataRoot, discovery);
        form.SuppressStartupForTesting();
        using var uiWatchdog = new System.Windows.Forms.Timer
        {
            Interval = checked((int)UiTimeout.TotalMilliseconds)
        };
        uiWatchdog.Tick += (_, _) =>
        {
            uiWatchdog.Stop();
            uiFailure ??= new TimeoutException("The restarted Settings/Activity UI probe timed out.");
            form.Dispose();
        };
        form.Shown += async (_, _) =>
        {
            try
            {
                var settingsControl = SnapshotCapture.DescendantsOf(form).OfType<SettingsControl>().Single();
                var themeMode = GetPrivateField<ComboBox>(settingsControl, "themeMode");
                var designPreset = GetPrivateField<ComboBox>(settingsControl, "designPreset");
                var textSize = GetPrivateField<ComboBox>(settingsControl, "textSize");
                var autoSave = GetPrivateField<CheckBox>(settingsControl, "autoSave");
                var selection = settingsControl.GetSelection();
                uiSettingsBound = SettingChoiceValue(themeMode) == "Dark"
                                  && SettingChoiceValue(designPreset) == "SciFi"
                                  && Convert.ToString(
                                      textSize.SelectedItem,
                                      System.Globalization.CultureInfo.InvariantCulture) == "12"
                                  && !autoSave.Checked
                                  && selection.Profile.Id == "Custom"
                                  && selection.Settings.Model == "synthetic-model"
                                  && selection.Keys.Count == 0;
                Require(uiSettingsBound,
                    "The restarted Settings UI did not bind the persisted non-secret values.");

                var activityControl = SnapshotCapture.DescendantsOf(form).OfType<ActivityControl>().Single();
                activityControl.SetEntries(activityEntries);
                var activityList = SnapshotCapture.DescendantsOf(activityControl).OfType<ListView>().Single();
                uiActivityRows = activityList.Items.Count;
                Require(activityList.Items.Cast<ListViewItem>().Any(item =>
                        item.SubItems.Count >= 2 && item.SubItems[1].Text == "Restart Fixture"),
                    "The restarted Activity UI did not bind the persisted project run.");
                uiScenarioCompleted = true;
            }
            catch (Exception exception)
            {
                uiFailure = exception;
            }
            finally
            {
                uiWatchdog.Stop();
                await form.DisposeAfterFailedBootstrapAsync();
            }
        };
        uiWatchdog.Start();
        Application.Run(form);
        Require(uiScenarioCompleted && uiFailure is null,
            uiFailure?.Message ?? "The restarted Settings/Activity UI scenario did not complete.");

        return new SettingsActivityRestartReport
        {
            Status = "PASS",
            SettingsRecoveredExactly = true,
            ProjectRunReopened = true,
            ActivityEntryCount = activityEntries.Length,
            RecoveryNoticeCount = recoveryNoticeCount,
            RecoveryNoticeSecondDrainCount = secondDrainCount,
            MemoryOnlyApiKeyPersisted = false,
            UiSettingsBound = uiSettingsBound,
            UiActivityRowCount = uiActivityRows
        };
    }

    private static T GetPrivateField<T>(object instance, string name) where T : class =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
        ?? throw new MissingFieldException(instance.GetType().FullName, name);

    private static string SettingChoiceValue(ComboBox combo)
    {
        var item = combo.SelectedItem
            ?? throw new InvalidOperationException("A persisted setting has no selected UI item.");
        return Convert.ToString(
                   item.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item),
                   System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static string ReadValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }
        return string.Empty;
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class LocalGapReport
    {
        public string SchemaVersion { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool SyntheticFixture { get; set; }
        public bool UserDataUsed { get; set; }
        public bool ExternalNetworkUsed { get; set; }
        public SourceLanguageCancelReport? SourceLanguageCancel { get; set; }
        public SettingsActivityRestartReport? SettingsActivityRestart { get; set; }
        public string Cleanup { get; set; } = string.Empty;
        public string? FailureType { get; set; }
        public string? FailureMessage { get; set; }
    }

    private sealed class SourceLanguageCancelReport
    {
        public string Status { get; set; } = string.Empty;
        public int DiscoveredChoiceCount { get; set; }
        public int DialogChoiceCount { get; set; }
        public string RankedFirstFolder { get; set; } = string.Empty;
        public bool CancelClicked { get; set; }
        public bool ProjectStoreWritten { get; set; }
        public bool ReviewArtifactWritten { get; set; }
    }

    private sealed class SettingsActivityRestartReport
    {
        public string Status { get; set; } = string.Empty;
        public bool SettingsRecoveredExactly { get; set; }
        public bool ProjectRunReopened { get; set; }
        public int ActivityEntryCount { get; set; }
        public int RecoveryNoticeCount { get; set; }
        public int RecoveryNoticeSecondDrainCount { get; set; }
        public bool MemoryOnlyApiKeyPersisted { get; set; }
        public bool UiSettingsBound { get; set; }
        public int UiActivityRowCount { get; set; }
    }
}
