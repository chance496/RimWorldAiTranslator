using RimWorldAiTranslator.App;
using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;

namespace RimWorldAiTranslator.UiHarness;

internal static class CommandToolTipProbe
{
    private static readonly int[] DpiValues = [96, 120, 144];

    public static void Run(string dataRoot, string discoveryRoot)
    {
        using var services = new AppServices(
            dataRoot,
            RimWorldModDiscoveryService.CreateIsolated(discoveryRoot));
        var project = services.ProjectStore.Projects.Single();
        var review = services.Reviews.Load(project.LatestReviewRoot, project);
        var ioHooks = new UiIoHooks(
            GlossaryFileExists: _ => false,
            DirectoryExists: _ => false,
            IsRmkWorkspaceRoot: _ => false,
            IsWritableRmkWorkspace: _ => false,
            OpenDirectory: _ => { });
        using var form = new MainForm(
            services,
            services.ProjectStats.Load(),
            closeBarrierTimeout: TimeSpan.FromSeconds(5),
            forceCloseConfirmation: () => false,
            ioHooks: ioHooks,
            activeOperationCloseConfirmation: () => DialogResult.OK,
            unsavedCloseConfirmation: () => DialogResult.No)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20_000, -20_000),
            ClientSize = new Size(1_600, 900)
        };
        form.SuppressStartupForTesting();
        form.Show();
        Application.DoEvents();
        form.LoadWorkspaceForTesting(project, review);
        Require(
            PumpUntil(() => !form.WorkspaceControlForTesting.UiDataWorkPendingForTesting, TimeSpan.FromSeconds(5)),
            "The synthetic workspace did not become ready.");

        var workspace = form.WorkspaceControlForTesting;
        var dashboard = form.DashboardControlForTesting;
        var mainControls = SnapshotCapture.DescendantsOf(form)
            .Where(control => !string.IsNullOrWhiteSpace(form.CommandToolTipTextForTesting(control)))
            .ToArray();
        var dashboardControls = SnapshotCapture.DescendantsOf(dashboard)
            .Where(control => !string.IsNullOrWhiteSpace(dashboard.CommandToolTipTextForTesting(control)))
            .ToArray();
        var workspaceControls = SnapshotCapture.DescendantsOf(workspace)
            .Where(control => !string.IsNullOrWhiteSpace(workspace.CommandToolTipTextForTesting(control)))
            .ToArray();

        Require(mainControls.Length == 4, $"Expected four main command ToolTips; actual={mainControls.Length}.");
        Require(dashboardControls.Length >= 5, $"Dashboard command ToolTips are incomplete; actual={dashboardControls.Length}.");
        Require(workspaceControls.Length >= 35, $"Workspace command ToolTips are incomplete; actual={workspaceControls.Length}.");
        Require(
            mainControls.All(control => form.CommandToolTipRegistrationCountForTesting(control) == 1)
            && dashboardControls.All(control => dashboard.CommandToolTipRegistrationCountForTesting(control) == 1)
            && workspaceControls.All(control => workspace.CommandToolTipRegistrationCountForTesting(control) == 1),
            "A command control was registered more than once.");
        Require(
            mainControls.All(control => control.AccessibleDescription == form.CommandToolTipTextForTesting(control))
            && dashboardControls.All(control => control.AccessibleDescription == dashboard.CommandToolTipTextForTesting(control))
            && workspaceControls.All(control => control.AccessibleDescription == workspace.CommandToolTipTextForTesting(control)),
            "AccessibleDescription and ToolTip text diverged.");

        var ai = FindButton(workspaceControls, "AI 번역");
        var save = FindButton(workspaceControls, "저장");
        var approveAndNext = FindButton(workspaceControls, "완료 후 다음");
        var commandPalette = FindButton(mainControls, "명령");
        Require(HasShortcut(workspace.CommandToolTipTextForTesting(ai), UiCommand.AiTranslate), "AI shortcut hint is incorrect.");
        Require(HasShortcut(workspace.CommandToolTipTextForTesting(save), UiCommand.SaveReview), "Save shortcut hint is incorrect.");
        Require(HasShortcut(workspace.CommandToolTipTextForTesting(approveAndNext), UiCommand.MarkApprovedAndNext), "Approve-and-next shortcut hint is incorrect.");
        Require(HasShortcut(form.CommandToolTipTextForTesting(commandPalette), UiCommand.CommandPalette), "Command-palette shortcut hint is incorrect.");
        Require(
            UiCommandCatalog.Matches(UiCommand.AiTranslate, Keys.F9)
            && UiCommandCatalog.Matches(UiCommand.MarkApprovedAndNext, Keys.Control | Keys.Enter),
            "Displayed shortcut policy does not match key routing.");

        workspace.SetRunning(true, "합성 ToolTip 작업");
        Application.DoEvents();
        var disabledText = workspace.CommandToolTipTextForTesting(ai);
        workspace.RefreshThemeState();
        Require(
            disabledText.Contains("현재 작업이 실행 중", StringComparison.Ordinal)
            && workspace.CommandToolTipTextForTesting(ai).Equals(disabledText, StringComparison.Ordinal),
            "Disabled reason was absent or lost after theme refresh.");
        workspace.SetRunning(false, "준비됨");
        Application.DoEvents();
        Require(
            !workspace.CommandToolTipTextForTesting(ai).Contains("현재 작업이 실행 중", StringComparison.Ordinal),
            "Disabled reason remained after the operation ended.");

        Require(
            DpiValues.All(dpi =>
                mainControls.All(control => form.CommandToolTipFitsForTesting(control, dpi, new Size(1280, 720)))
                && dashboardControls.All(control => dashboard.CommandToolTipFitsForTesting(control, dpi, new Size(1280, 720)))
                && workspaceControls.All(control => workspace.CommandToolTipFitsForTesting(control, dpi, new Size(1280, 720)))),
            "ToolTip text exceeded a 1280x720 working area at 100%, 125%, or 150% DPI.");

        dashboard.SetData(
            [new RimWorldModInfo(project.Name, project.Name, project.ModRoot, project.SourceKind, Path.GetFileName(project.ModRoot), project.PackageId, project.WorkshopId, project.Name)],
            [project]);
        Require(form.DispatchShortcutForTesting(Keys.Control | Keys.Home), "Project-list shortcut was not handled.");
        Application.DoEvents();
        var dynamicDashboardToolTips = SnapshotCapture.DescendantsOf(dashboard)
            .Count(control => !string.IsNullOrWhiteSpace(dashboard.CommandToolTipTextForTesting(control)));
        Require(dashboard.Visible && dynamicDashboardToolTips >= 7, "Dynamic project-card ToolTips were not restored after screen transition.");
        form.LoadWorkspaceForTesting(project, review);
        Require(
            PumpUntil(() => workspace.Visible && !workspace.UiDataWorkPendingForTesting, TimeSpan.FromSeconds(5))
            && HasShortcut(workspace.CommandToolTipTextForTesting(ai), UiCommand.AiTranslate),
            "Workspace ToolTips were lost after workspace reload.");

        var target = review.Items.First(item => !string.IsNullOrWhiteSpace(item.Decision.Text) && !item.IsWarning);
        var targetIndex = review.Items.FindIndex(item => ReferenceEquals(item, target));
        Require(targetIndex >= 0 && targetIndex + 1 < review.Items.Count, "Approve-and-next fixture row is unavailable.");
        FindControl<ListBox>(workspace, "검색된 문자열 목록").SelectedIndex = targetIndex;
        Require(PumpUntil(() => approveAndNext.Enabled, TimeSpan.FromSeconds(2)), "Approve-and-next button did not become enabled.");
        approveAndNext.PerformClick();
        Require(
            PumpUntil(
                () => target.EffectiveStatus.Equals(ReviewStatuses.Approved, StringComparison.Ordinal)
                    && workspace.CurrentOriginalIndexForTesting == review.Items[targetIndex + 1].OriginalIndex,
                TimeSpan.FromSeconds(5)),
            "Approve-and-next did not retain its next selection after asynchronous filtering.");

        workspace.SuspendBackgroundUiWorkForClose();
        Console.Out.WriteLine("[PASS] UI.CommandToolTips (main=4, dashboard>=7, workspace>=35, DPI=100/125/150, approve-next)");
    }

    private static T FindControl<T>(Control root, string accessibleName) where T : Control =>
        SnapshotCapture.DescendantsOf(root)
            .OfType<T>()
            .Single(control => control.AccessibleName == accessibleName);

    private static Button FindButton(IEnumerable<Control> controls, string text) =>
        controls.OfType<Button>().Single(button => button.Text.Equals(text, StringComparison.Ordinal));

    private static bool HasShortcut(string text, UiCommand command) =>
        text.Contains($"단축키: {UiCommandCatalog.Get(command).ShortcutText}", StringComparison.Ordinal);

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            Application.DoEvents();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Application.DoEvents();
        return condition();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
