using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.App;
using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Quality;
using RimWorldAiTranslator.Core.Review;

namespace RimWorldAiTranslator.UiHarness;

internal static class UiInteractionProbe
{
    private const int ExpectedRows = 5_000;
    private static readonly int[] ToolTipDpiValues = [96, 120, 144];
    private static readonly ProbeThresholds Thresholds = new(
        WorkspaceInitializationMilliseconds: 5_000,
        QualityCalculationMilliseconds: 2_000,
        SearchFilterMilliseconds: 200,
        StatusFilterMilliseconds: 200,
        NextSelectionMilliseconds: 500,
        StopFeedbackMilliseconds: 250);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static void Run(string dataRoot, string discoveryRoot, string reportPath, string fixtureProfile)
    {
        var checks = new List<ProbeCheck>();
        var timings = new ProbeTimings(-1, -1, -1, -1);
        MainForm? form = null;
        AppServices? services = null;

        try
        {
            var discovery = RimWorldModDiscoveryService.CreateIsolated(discoveryRoot);
            services = new AppServices(dataRoot, discovery);
            var project = services.ProjectStore.Projects.Single();
            var review = services.Reviews.Load(project.LatestReviewRoot, project);
            Check(checks, "fixture.row-count", review.Items.Count == ExpectedRows,
                $"expected={ExpectedRows};actual={review.Items.Count}");

            var ioHooks = new UiIoHooks(
                GlossaryFileExists: _ => false,
                DirectoryExists: _ => false,
                IsRmkWorkspaceRoot: _ => false,
                IsWritableRmkWorkspace: _ => false,
                OpenDirectory: _ => { },
                BuildRmkWorkspace: (_, cancellationToken) =>
                    Task.FromCanceled<RimWorldAiTranslator.Core.Rmk.RmkBuildResult>(
                        cancellationToken.IsCancellationRequested
                            ? cancellationToken
                            : new CancellationToken(canceled: true)));
            form = new MainForm(
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
                WindowState = FormWindowState.Normal,
                ClientSize = new Size(1_600, 900)
            };
            form.SuppressStartupForTesting();
            form.Show();
            Application.DoEvents();

            var initializationTimer = Stopwatch.StartNew();
            form.LoadWorkspaceForTesting(project, review);
            form.PerformLayout();
            var backgroundWorkCompleted = PumpUntil(
                () => !form.WorkspaceControlForTesting.UiDataWorkPendingForTesting
                    && form.WorkspaceControlForTesting.FilteredReviewCountForTesting == review.Items.Count,
                TimeSpan.FromMilliseconds(Thresholds.WorkspaceInitializationMilliseconds));
            initializationTimer.Stop();

            var workspace = form.WorkspaceControlForTesting;
            var expectedIssues = QualityService.FindIssues(workspace.GetQualityEntries());
            timings = timings with
            {
                WorkspaceInitializationMilliseconds = Milliseconds(initializationTimer.Elapsed),
                QualityCalculationMilliseconds = Milliseconds(workspace.LastQualityBuildDurationForTesting)
            };
            Check(checks, "performance.workspace-initialization",
                backgroundWorkCompleted
                && initializationTimer.Elapsed.TotalMilliseconds <= Thresholds.WorkspaceInitializationMilliseconds,
                $"thresholdMs={Thresholds.WorkspaceInitializationMilliseconds};backgroundComplete={backgroundWorkCompleted};filtered={workspace.FilteredReviewCountForTesting};filterPending={workspace.FilterWorkPendingForTesting};qualityPending={workspace.QualityWorkPendingForTesting}");
            Check(checks, "responsiveness.filter-worker-thread",
                workspace.LastFilterUsedWorkerThreadForTesting,
                "bulkFilterMustRunOutsideUiThread=true");
            Check(checks, "responsiveness.quality-worker-thread",
                workspace.LastQualityUsedWorkerThreadForTesting,
                "qualityAnalysisMustRunOutsideUiThread=true");
            Check(checks, "performance.quality-calculation",
                workspace.LastQualityBuildDurationForTesting.TotalMilliseconds <= Thresholds.QualityCalculationMilliseconds,
                $"thresholdMs={Thresholds.QualityCalculationMilliseconds}");
            Check(checks, "quality.virtual-mode", workspace.QualityListUsesVirtualizationForTesting,
                "required=true");
            Check(checks, "quality.issue-count",
                workspace.VisibleQualityIssueCountForTesting == expectedIssues.Count,
                $"expected={expectedIssues.Count};actual={workspace.VisibleQualityIssueCountForTesting}");

            var qualityList = FindControl<ListView>(workspace, "품질 문제 목록");
            Check(checks, "quality.virtual-list-size",
                qualityList.VirtualListSize == expectedIssues.Count,
                $"expected={expectedIssues.Count};actual={qualityList.VirtualListSize}");
            VerifyQualityJump(checks, workspace, qualityList, expectedIssues);

            var search = FindControl<TextBox>(workspace, "문자열 검색");
            var searchField = FindControl<ComboBox>(workspace, "검색 대상");
            var statusFilter = FindControl<ComboBox>(workspace, "검수 상태 필터");
            var reviewList = FindControl<ListBox>(workspace, "검색된 문자열 목록");
            var translation = FindControl<RichTextBox>(workspace, "번역문 편집");
            var sideTabs = FindControl<TabControl>(workspace, "검수 도구 탭");

            SetComboSelection(searchField, "텍스트/키");
            SetComboSelection(statusFilter, "전체");
            search.Clear();
            PumpUntil(
                () => workspace.FilteredReviewCountForTesting == review.Items.Count
                    && !workspace.UiDataWorkPendingForTesting,
                TimeSpan.FromSeconds(2));

            var middleIndex = 2_500;
            reviewList.SelectedIndex = middleIndex;
            reviewList.TopIndex = Math.Max(0, middleIndex - 2);
            Application.DoEvents();
            var retainedOriginalIndex = workspace.CurrentOriginalIndexForTesting;
            var expectedSearchCount = services.Reviews.Query(
                review,
                new ReviewQuery("Synthetic source", ReviewSearchField.All)).Count;
            var searchTimer = Stopwatch.StartNew();
            search.Text = "Synthetic source";
            var searchCompleted = PumpUntil(
                () => workspace.FilteredReviewCountForTesting == expectedSearchCount,
                TimeSpan.FromMilliseconds(Thresholds.SearchFilterMilliseconds));
            searchTimer.Stop();
            timings = timings with { SearchFilterMilliseconds = Milliseconds(searchTimer.Elapsed) };
            Check(checks, "performance.search-filter",
                searchCompleted && searchTimer.Elapsed.TotalMilliseconds <= Thresholds.SearchFilterMilliseconds,
                $"thresholdMs={Thresholds.SearchFilterMilliseconds}");
            Check(checks, "selection.retained-after-search",
                workspace.CurrentOriginalIndexForTesting == retainedOriginalIndex,
                $"expectedIndex={retainedOriginalIndex};actualIndex={workspace.CurrentOriginalIndexForTesting}");
            var visibleRows = Math.Max(1, reviewList.ClientSize.Height / Math.Max(1, reviewList.ItemHeight));
            var selectedVisible = reviewList.SelectedIndex >= reviewList.TopIndex
                && reviewList.SelectedIndex < reviewList.TopIndex + visibleRows;
            Check(checks, "scroll.selected-row-visible-after-search",
                selectedVisible && reviewList.TopIndex > 0,
                "selectedRowMustRemainVisibleWithoutResettingToTop");

            search.Clear();
            PumpUntil(
                () => workspace.FilteredReviewCountForTesting == review.Items.Count
                    && !workspace.UiDataWorkPendingForTesting,
                TimeSpan.FromSeconds(2));
            var expectedPendingCount = services.Reviews.Query(
                review,
                new ReviewQuery(Status: ReviewStatusFilter.Pending)).Count;
            var filterTimer = Stopwatch.StartNew();
            SetComboSelection(statusFilter, "미번역");
            var statusFilterCompleted = PumpUntil(
                () => workspace.FilteredReviewCountForTesting == expectedPendingCount
                    && !workspace.UiDataWorkPendingForTesting,
                TimeSpan.FromMilliseconds(Thresholds.StatusFilterMilliseconds));
            filterTimer.Stop();
            timings = timings with { StatusFilterMilliseconds = Milliseconds(filterTimer.Elapsed) };
            Check(checks, "performance.status-filter",
                statusFilterCompleted
                && workspace.FilteredReviewCountForTesting == expectedPendingCount
                && filterTimer.Elapsed.TotalMilliseconds <= Thresholds.StatusFilterMilliseconds,
                $"thresholdMs={Thresholds.StatusFilterMilliseconds};expectedCount={expectedPendingCount};actualCount={workspace.FilteredReviewCountForTesting}");
            SetComboSelection(statusFilter, "전체");
            PumpUntil(
                () => workspace.FilteredReviewCountForTesting == review.Items.Count
                    && !workspace.UiDataWorkPendingForTesting,
                TimeSpan.FromMilliseconds(Thresholds.StatusFilterMilliseconds));

            reviewList.SelectedIndex = middleIndex;
            reviewList.TopIndex = Math.Max(0, middleIndex - 2);
            Application.DoEvents();
            var topBeforeNext = reviewList.TopIndex;
            var nextTimer = Stopwatch.StartNew();
            var nextHandled = form.DispatchShortcutForTesting(Keys.F3);
            nextTimer.Stop();
            timings = timings with { NextSelectionMilliseconds = Milliseconds(nextTimer.Elapsed) };
            Check(checks, "keyboard.f3-next",
                nextHandled && workspace.CurrentOriginalIndexForTesting == middleIndex + 1,
                $"expectedIndex={middleIndex + 1};actualIndex={workspace.CurrentOriginalIndexForTesting}");
            Check(checks, "performance.next-selection",
                nextTimer.Elapsed.TotalMilliseconds <= Thresholds.NextSelectionMilliseconds,
                $"thresholdMs={Thresholds.NextSelectionMilliseconds}");
            var previousHandled = form.DispatchShortcutForTesting(Keys.Shift | Keys.F3);
            Check(checks, "keyboard.shift-f3-previous",
                previousHandled && workspace.CurrentOriginalIndexForTesting == middleIndex,
                $"expectedIndex={middleIndex};actualIndex={workspace.CurrentOriginalIndexForTesting}");
            Check(checks, "scroll.retained-during-adjacent-navigation",
                reviewList.TopIndex > 0 && Math.Abs(reviewList.TopIndex - topBeforeNext) <= 3,
                $"initialTopIndex={topBeforeNext};actualTopIndex={reviewList.TopIndex};maximumDrift=3");

            VerifyKeyboardRouting(checks, form, workspace, review, search, statusFilter, reviewList, translation, sideTabs);
            VerifyApproveAndNextButton(checks, workspace, review, reviewList);
            VerifyProvenanceWorkflow(checks, workspace, review, reviewList, translation);
            timings = timings with
            {
                StopFeedbackMilliseconds = VerifyImmediateStopFeedback(checks, workspace)
            };
            VerifyCommandToolTips(checks, form, workspace, project, review);
            VerifyDashboardEscape(checks, form);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var target = exception.TargetSite is null
                ? "unknown"
                : $"{exception.TargetSite.DeclaringType?.Name}.{exception.TargetSite.Name}";
            var parameter = exception is ArgumentException argument ? argument.ParamName ?? "none" : "none";
            Check(checks, "probe.completed", false,
                $"exceptionType={exception.GetType().Name};target={target};parameter={parameter}");
        }
        finally
        {
            if (form is not null)
            {
                try
                {
                    form.WorkspaceControlForTesting.SuspendBackgroundUiWorkForClose();
                    Application.DoEvents();
                    if (!form.IsDisposed) form.Dispose();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Check(checks, "probe.cleanup", false,
                        $"exceptionType={exception.GetType().Name};target={exception.TargetSite?.DeclaringType?.Name}.{exception.TargetSite?.Name}");
                }
            }
            services?.Dispose();
        }

        if (!checks.Any(check => check.Name.Equals("probe.completed", StringComparison.Ordinal)))
            Check(checks, "probe.completed", true, "allChecksExecuted=true");
        var passed = checks.All(check => check.Passed);
        var report = new ProbeReport(
            SchemaVersion: "phase06-ui-interaction-v1",
            Result: passed ? "PASS" : "FAIL",
            FixtureProfile: fixtureProfile,
            RowCount: ExpectedRows,
            Thresholds,
            timings,
            checks);
        WriteReport(report, reportPath);
        if (!passed)
        {
            var failedNames = string.Join(", ", checks.Where(check => !check.Passed).Select(check => check.Name));
            throw new InvalidOperationException("UI interaction probe failed: " + failedNames);
        }
    }

    private static void VerifyQualityJump(
        ICollection<ProbeCheck> checks,
        ReviewWorkspaceControl workspace,
        ListView qualityList,
        IReadOnlyList<QualityIssue> expectedIssues)
    {
        if (expectedIssues.Count == 0)
        {
            Check(checks, "quality.selection-jump", false, "expectedAtLeastOneSyntheticIssue=true");
            return;
        }

        var issuePosition = expectedIssues.Count - 1;
        workspace.SelectSideTab("품질");
        Application.DoEvents();
        qualityList.SelectedIndices.Clear();
        qualityList.Focus();
        qualityList.Items[issuePosition].Selected = true;
        qualityList.EnsureVisible(issuePosition);
        Application.DoEvents();
        var jump = SnapshotCapture.DescendantsOf(workspace)
            .OfType<Button>()
            .Single(button => button.Text.Equals("문자열로 이동", StringComparison.Ordinal));
        jump.PerformClick();
        var completed = PumpUntil(
            () => workspace.CurrentOriginalIndexForTesting == expectedIssues[issuePosition].Index
                && !workspace.UiDataWorkPendingForTesting,
            TimeSpan.FromSeconds(2));
        Check(checks, "quality.selection-jump",
            completed
            && workspace.CurrentOriginalIndexForTesting == expectedIssues[issuePosition].Index
            && workspace.CurrentKeyForTesting.Equals(expectedIssues[issuePosition].Key, StringComparison.Ordinal),
            $"expectedIndex={expectedIssues[issuePosition].Index};actualIndex={workspace.CurrentOriginalIndexForTesting}");
    }

    private static void VerifyCommandToolTips(
        ICollection<ProbeCheck> checks,
        MainForm form,
        ReviewWorkspaceControl workspace,
        TranslationProject project,
        ReviewWorkspace review)
    {
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

        Check(checks, "tooltip.major-command-coverage",
            mainControls.Length >= 4 && dashboardControls.Length >= 5 && workspaceControls.Length >= 30,
            $"main={mainControls.Length};dashboard={dashboardControls.Length};workspace={workspaceControls.Length}");
        Check(checks, "tooltip.single-registration-per-control",
            mainControls.All(control => form.CommandToolTipRegistrationCountForTesting(control) == 1)
            && dashboardControls.All(control => dashboard.CommandToolTipRegistrationCountForTesting(control) == 1)
            && workspaceControls.All(control => workspace.CommandToolTipRegistrationCountForTesting(control) == 1),
            "expectedRegistrationCount=1");
        Check(checks, "tooltip.accessible-description-shared-source",
            mainControls.All(control => control.AccessibleDescription == form.CommandToolTipTextForTesting(control))
            && dashboardControls.All(control => control.AccessibleDescription == dashboard.CommandToolTipTextForTesting(control))
            && workspaceControls.All(control => control.AccessibleDescription == workspace.CommandToolTipTextForTesting(control)),
            "accessibleDescriptionMustEqualToolTipText=true");

        var ai = workspaceControls.OfType<Button>().Single(button => button.Text.Equals("AI 번역", StringComparison.Ordinal));
        var save = workspaceControls.OfType<Button>().Single(button => button.Text.Equals("저장", StringComparison.Ordinal));
        var next = workspaceControls.OfType<Button>().Single(button => button.Text.Equals("완료 후 다음", StringComparison.Ordinal));
        var command = mainControls.OfType<Button>().Single(button => button.Text.Equals("명령", StringComparison.Ordinal));
        Check(checks, "tooltip.shortcut-policy-exact",
            HasCatalogShortcut(workspace.CommandToolTipTextForTesting(ai), UiCommand.AiTranslate)
            && HasCatalogShortcut(workspace.CommandToolTipTextForTesting(save), UiCommand.SaveReview)
            && HasCatalogShortcut(workspace.CommandToolTipTextForTesting(next), UiCommand.MarkApprovedAndNext)
            && HasCatalogShortcut(form.CommandToolTipTextForTesting(command), UiCommand.CommandPalette)
            && UiCommandCatalog.Matches(UiCommand.AiTranslate, Keys.F9)
            && UiCommandCatalog.Matches(UiCommand.MarkApprovedAndNext, Keys.Control | Keys.Enter),
            "tooltipAndKeyRoutingUseUiCommandCatalog=true");

        workspace.SetRunning(true, "합성 ToolTip 작업");
        Application.DoEvents();
        var disabledText = workspace.CommandToolTipTextForTesting(ai);
        workspace.RefreshThemeState();
        var themedText = workspace.CommandToolTipTextForTesting(ai);
        workspace.SetRunning(false, "준비됨");
        Application.DoEvents();
        var enabledText = workspace.CommandToolTipTextForTesting(ai);
        Check(checks, "tooltip.disabled-reason-lifecycle",
            disabledText.Contains("현재 작업이 실행 중", StringComparison.Ordinal)
            && themedText.Equals(disabledText, StringComparison.Ordinal)
            && !enabledText.Contains("현재 작업이 실행 중", StringComparison.Ordinal),
            "disabledReasonAddedThenRemoved=true;themeRefreshPreserved=true");

        var allControls = mainControls
            .Select(control => (Control: control, Fits: (Func<int, bool>)(dpi => form.CommandToolTipFitsForTesting(control, dpi, new Size(1280, 720)))))
            .Concat(dashboardControls.Select(control => (Control: control, Fits: (Func<int, bool>)(dpi => dashboard.CommandToolTipFitsForTesting(control, dpi, new Size(1280, 720))))))
            .Concat(workspaceControls.Select(control => (Control: control, Fits: (Func<int, bool>)(dpi => workspace.CommandToolTipFitsForTesting(control, dpi, new Size(1280, 720))))))
            .ToArray();
        Check(checks, "tooltip.dpi-100-125-150-fit",
            ToolTipDpiValues.All(dpi => allControls.All(item => item.Fits(dpi))),
            "workingArea=1280x720;dpi=96,120,144");

        dashboard.SetData(
            [new RimWorldModInfo(project.Name, project.Name, project.ModRoot, project.SourceKind, Path.GetFileName(project.ModRoot), project.PackageId, project.WorkshopId, project.Name)],
            [project]);
        form.DispatchShortcutForTesting(Keys.Control | Keys.Home);
        Application.DoEvents();
        var dashboardSearch = FindControl<TextBox>(dashboard, "프로젝트 검색");
        var visibleDashboardControls = SnapshotCapture.DescendantsOf(dashboard)
            .Count(control => !string.IsNullOrWhiteSpace(dashboard.CommandToolTipTextForTesting(control)));
        Check(checks, "tooltip.screen-transition-preserved",
            dashboard.Visible
            && visibleDashboardControls >= 7
            && !string.IsNullOrWhiteSpace(dashboard.CommandToolTipTextForTesting(dashboardSearch))
            && form.CommandToolTipTextForTesting(command).Contains(UiCommandCatalog.Get(UiCommand.CommandPalette).ShortcutText, StringComparison.Ordinal),
            $"dashboardAndMainToolTipsPresentAfterTransition=true;dashboard={visibleDashboardControls}");
        form.LoadWorkspaceForTesting(project, review);
        Application.DoEvents();
        Check(checks, "tooltip.workspace-reload-preserved",
            workspace.Visible
            && workspace.CommandToolTipTextForTesting(ai).Contains(UiCommandCatalog.Get(UiCommand.AiTranslate).ShortcutText, StringComparison.Ordinal),
            "workspaceToolTipsPresentAfterReload=true");
    }

    private static bool HasCatalogShortcut(string toolTipText, UiCommand command) =>
        toolTipText.Contains($"단축키: {UiCommandCatalog.Get(command).ShortcutText}", StringComparison.Ordinal);

    private static void VerifyApproveAndNextButton(
        ICollection<ProbeCheck> checks,
        ReviewWorkspaceControl workspace,
        ReviewWorkspace review,
        ListBox reviewList)
    {
        var index = review.Items.FindIndex(item =>
            item.OriginalIndex < review.Items.Count - 1
            && !string.IsNullOrWhiteSpace(item.Decision.Text)
            && !item.IsWarning);
        if (index < 0)
        {
            Check(checks, "button.approve-and-next", false, "safeTranslatedFixtureRowRequired=true");
            return;
        }

        reviewList.SelectedIndex = index;
        Application.DoEvents();
        var target = review.Items[index];
        var expectedNextIndex = review.Items[index + 1].OriginalIndex;
        var button = SnapshotCapture.DescendantsOf(workspace)
            .OfType<Button>()
            .Single(candidate => candidate.Text.Equals("완료 후 다음", StringComparison.Ordinal));
        var becameEnabled = PumpUntil(() => button.Enabled, TimeSpan.FromSeconds(2));
        button.PerformClick();
        var moved = PumpUntil(
            () => target.EffectiveStatus.Equals(ReviewStatuses.Approved, StringComparison.Ordinal)
                && workspace.CurrentOriginalIndexForTesting == expectedNextIndex,
            TimeSpan.FromSeconds(2));
        Check(checks, "button.approve-and-next",
            becameEnabled && moved,
            $"enabled={becameEnabled};approved={target.EffectiveStatus};expectedNextIndex={expectedNextIndex};actualIndex={workspace.CurrentOriginalIndexForTesting}");
    }

    private static void VerifyKeyboardRouting(
        ICollection<ProbeCheck> checks,
        MainForm form,
        ReviewWorkspaceControl workspace,
        ReviewWorkspace review,
        TextBox search,
        ComboBox statusFilter,
        ListBox reviewList,
        RichTextBox translation,
        TabControl sideTabs)
    {
        search.Clear();
        SetComboSelection(statusFilter, "전체");
        PumpUntil(
            () => workspace.FilteredReviewCountForTesting == review.Items.Count
                && !workspace.UiDataWorkPendingForTesting,
            TimeSpan.FromSeconds(2));

        var ctrlFHandled = form.DispatchShortcutForTesting(Keys.Control | Keys.F);
        Application.DoEvents();
        Check(checks, "keyboard.ctrl-f", ctrlFHandled && search.Focused, "focus=workspaceSearch");

        var f2Handled = form.DispatchShortcutForTesting(Keys.F2);
        Application.DoEvents();
        Check(checks, "keyboard.f2", f2Handled && translation.Focused, "focus=translationEditor");

        var safeIndex = review.Items.FindIndex(item =>
            !string.IsNullOrWhiteSpace(item.Decision.Text) && !item.IsWarning);
        if (safeIndex < 0)
        {
            Check(checks, "keyboard.ctrl-1-2-3", false, "safeSyntheticTranslationRequired=true");
        }
        else
        {
            reviewList.SelectedIndex = safeIndex;
            Application.DoEvents();
            var target = review.Items[safeIndex];
            var pendingHandled = form.DispatchShortcutForTesting(Keys.Control | Keys.D1);
            Application.DoEvents();
            var pending = target.EffectiveStatus.Equals(ReviewStatuses.Pending, StringComparison.Ordinal);
            var translatedHandled = form.DispatchShortcutForTesting(Keys.Control | Keys.D2);
            Application.DoEvents();
            var translated = target.EffectiveStatus.Equals(ReviewStatuses.Translated, StringComparison.Ordinal);
            var approvedHandled = form.DispatchShortcutForTesting(Keys.Control | Keys.D3);
            Application.DoEvents();
            var approved = target.EffectiveStatus.Equals(ReviewStatuses.Approved, StringComparison.Ordinal);
            Check(checks, "keyboard.ctrl-1-2-3",
                pendingHandled && translatedHandled && approvedHandled && pending && translated && approved,
                "statusSequence=pending,translated,approved");
        }

        var qualityHandled = form.DispatchShortcutForTesting(Keys.Alt | Keys.Q);
        Application.DoEvents();
        var qualitySelected = workspace.SelectedSideTabForTesting.Equals("품질", StringComparison.Ordinal);
        var compareHandled = form.DispatchShortcutForTesting(Keys.Alt | Keys.C);
        Application.DoEvents();
        var compareSelected = workspace.SelectedSideTabForTesting.Equals("비교", StringComparison.Ordinal);
        Check(checks, "keyboard.alt-q-alt-c",
            qualityHandled && qualitySelected && compareHandled && compareSelected,
            "tabSequence=quality,compare");

        search.Clear();
        SetComboSelection(statusFilter, "전체");
        translation.Focus();
        Application.DoEvents();
        var defaultEscapeHandled = form.DispatchShortcutForTesting(Keys.Escape);
        Check(checks, "keyboard.escape-default-not-consumed",
            !defaultEscapeHandled && search.TextLength == 0 && statusFilter.SelectedIndex == 0,
            "expectedHandled=false");
        search.Text = "needle";
        var activeEscapeHandled = form.DispatchShortcutForTesting(Keys.Escape);
        Application.DoEvents();
        Check(checks, "keyboard.escape-clears-active-filter",
            activeEscapeHandled && search.TextLength == 0 && statusFilter.SelectedIndex == 0 && search.Focused,
            "expectedHandled=true");

        form.DispatchShortcutForTesting(Keys.Control | Keys.F);
        Application.DoEvents();
        var f6ToEditor = form.DispatchShortcutForTesting(Keys.F6);
        Application.DoEvents();
        var editorFocused = translation.Focused;
        var f6ToTools = form.DispatchShortcutForTesting(Keys.F6);
        Application.DoEvents();
        var toolsFocused = sideTabs.Focused || sideTabs.ContainsFocus;
        var shiftF6ToEditor = form.DispatchShortcutForTesting(Keys.Shift | Keys.F6);
        Application.DoEvents();
        Check(checks, "keyboard.f6-regions",
            f6ToEditor && editorFocused && f6ToTools && toolsFocused && shiftF6ToEditor && translation.Focused,
            "sequence=search,editor,tools,editor");
    }

    private static double VerifyImmediateStopFeedback(
        ICollection<ProbeCheck> checks,
        ReviewWorkspaceControl workspace)
    {
        workspace.SetRunning(true, "synthetic operation");
        var stop = SnapshotCapture.DescendantsOf(workspace)
            .OfType<Button>()
            .Single(button => button.Text.Equals("중지", StringComparison.Ordinal));
        var feedbackTimer = Stopwatch.StartNew();
        stop.PerformClick();
        Application.DoEvents();
        feedbackTimer.Stop();
        var statusVisible = SnapshotCapture.DescendantsOf(workspace)
            .OfType<Label>()
            .Any(label => label.Text.Contains("취소 요청됨", StringComparison.Ordinal));
        Check(checks, "operation.stop-immediate-feedback",
            !stop.Enabled
            && stop.Text.Equals("요청됨", StringComparison.Ordinal)
            && string.Equals(stop.AccessibleName, "취소 요청됨", StringComparison.Ordinal)
            && statusVisible
            && feedbackTimer.Elapsed.TotalMilliseconds <= Thresholds.StopFeedbackMilliseconds,
            $"buttonState=disabled-requested;thresholdMs={Thresholds.StopFeedbackMilliseconds};actualMs={Milliseconds(feedbackTimer.Elapsed)}");
        workspace.SetRunning(false, "synthetic operation complete");
        return Milliseconds(feedbackTimer.Elapsed);
    }

    private static void VerifyProvenanceWorkflow(
        ICollection<ProbeCheck> checks,
        ReviewWorkspaceControl workspace,
        ReviewWorkspace review,
        ListBox reviewList,
        RichTextBox translation)
    {
        var ready = PumpUntil(
            () => workspace.FilteredReviewCountForTesting == review.Items.Count
                && !workspace.UiDataWorkPendingForTesting,
            TimeSpan.FromSeconds(2));
        var target = review.Items.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.Row.Candidate)
            && !string.IsNullOrWhiteSpace(item.Row.Existing));
        if (!ready || target is null)
        {
            Check(checks, "provenance.fixture", false, "candidateAndExistingRequired=true");
            return;
        }

        var targetIndex = reviewList.Items.IndexOf(target);
        if (targetIndex < 0)
        {
            Check(checks, "provenance.fixture", false, "targetMustBeVisible=true");
            return;
        }

        reviewList.SelectedIndex = targetIndex;
        Application.DoEvents();
        var candidateButton = SnapshotCapture.DescendantsOf(workspace)
            .OfType<Button>()
            .FirstOrDefault(button => button.Text.Equals("AI 후보", StringComparison.Ordinal));
        var existingButton = SnapshotCapture.DescendantsOf(workspace)
            .OfType<Button>()
            .FirstOrDefault(button => button.Text.Equals("기존", StringComparison.Ordinal));
        var undoButton = SnapshotCapture.DescendantsOf(workspace)
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.AccessibleName, "되돌리기", StringComparison.Ordinal));
        var memo = SnapshotCapture.DescendantsOf(workspace)
            .OfType<TextBox>()
            .FirstOrDefault(control => string.Equals(control.AccessibleName, "검수 메모", StringComparison.Ordinal));
        if (candidateButton is null || existingButton is null || undoButton is null || memo is null)
        {
            Check(checks, "provenance.controls", false,
                $"candidate={candidateButton is not null};existing={existingButton is not null};undo={undoButton is not null};memo={memo is not null}");
            return;
        }

        translation.Text = "합성 로컬 편집";
        workspace.SaveCurrentEditor(false);
        PumpUntil(() => !workspace.UiDataWorkPendingForTesting, TimeSpan.FromSeconds(2));
        Check(checks, "provenance.manual-edit-local",
            target.Decision.TranslationOrigin.Equals("local", StringComparison.Ordinal)
            && target.Decision.Text.Equals("합성 로컬 편집", StringComparison.Ordinal),
            "expectedOrigin=local");

        candidateButton.PerformClick();
        Check(checks, "provenance.ai-pending-origin",
            workspace.TranslationTouchedForTesting
            && workspace.PendingEditOriginForTesting.Equals("ai", StringComparison.Ordinal),
            "expectedPendingOrigin=ai");
        workspace.SaveCurrentEditor(false);
        PumpUntil(() => !workspace.UiDataWorkPendingForTesting, TimeSpan.FromSeconds(2));
        Check(checks, "provenance.ai-save-origin",
            target.Decision.TranslationOrigin.Equals("ai", StringComparison.Ordinal)
            && target.Decision.Text.Equals(target.Row.Candidate, StringComparison.Ordinal),
            "expectedOrigin=ai");

        translation.Text = "합성 수동 재편집";
        workspace.SaveCurrentEditor(false);
        PumpUntil(() => !workspace.UiDataWorkPendingForTesting, TimeSpan.FromSeconds(2));
        Check(checks, "provenance.manual-overrides-to-local",
            target.Decision.TranslationOrigin.Equals("local", StringComparison.Ordinal),
            "expectedOrigin=local");

        existingButton.PerformClick();
        workspace.SaveCurrentEditor(false);
        PumpUntil(() => !workspace.UiDataWorkPendingForTesting, TimeSpan.FromSeconds(2));
        var expectedExistingOrigin = string.IsNullOrWhiteSpace(target.Row.ExistingOrigin)
            ? "existing"
            : target.Row.ExistingOrigin.ToLowerInvariant();
        Check(checks, "provenance.existing-save-origin",
            target.Decision.TranslationOrigin.Equals(expectedExistingOrigin, StringComparison.Ordinal)
            && target.Decision.Text.Equals(target.Row.Existing, StringComparison.Ordinal),
            $"expectedOrigin={expectedExistingOrigin}");

        memo.Text = "합성 메모 전용 변경";
        Check(checks, "provenance.memo-does-not-mark-translation",
            !workspace.TranslationTouchedForTesting,
            "translationTouched=false");
        workspace.SaveCurrentEditor(true);
        PumpUntil(() => !workspace.UiDataWorkPendingForTesting, TimeSpan.FromSeconds(2));
        Check(checks, "provenance.memo-preserves-origin",
            target.Decision.TranslationOrigin.Equals(expectedExistingOrigin, StringComparison.Ordinal)
            && target.Decision.Note.Equals("합성 메모 전용 변경", StringComparison.Ordinal),
            $"expectedOrigin={expectedExistingOrigin};duplicatePrompt=false");

        translation.Text = "저장하지 않을 합성 편집";
        undoButton.PerformClick();
        Check(checks, "provenance.undo-restores-baseline",
            !workspace.HasPendingEditorChanges
            && !workspace.TranslationTouchedForTesting
            && workspace.PendingEditOriginForTesting.Equals(expectedExistingOrigin, StringComparison.Ordinal)
            && translation.Text.Equals(target.Row.Existing, StringComparison.Ordinal),
            $"expectedOrigin={expectedExistingOrigin}");
    }

    private static void VerifyDashboardEscape(ICollection<ProbeCheck> checks, MainForm form)
    {
        var homeHandled = form.DispatchShortcutForTesting(Keys.Control | Keys.Home);
        Application.DoEvents();
        var dashboard = form.DashboardControlForTesting;
        var search = FindControl<TextBox>(dashboard, "프로젝트 검색");
        search.Text = "Frontier";
        var activeHandled = form.DispatchShortcutForTesting(Keys.Escape);
        Application.DoEvents();
        var activeCleared = activeHandled && search.TextLength == 0 && search.Focused;
        var defaultHandled = form.DispatchShortcutForTesting(Keys.Escape);
        Check(checks, "keyboard.dashboard-escape",
            homeHandled && dashboard.Visible && activeCleared && !defaultHandled,
            "activeHandled=true;defaultHandled=false");
    }

    private static T FindControl<T>(Control root, string accessibleName) where T : Control =>
        SnapshotCapture.DescendantsOf(root)
            .OfType<T>()
            .Single(control => string.Equals(control.AccessibleName, accessibleName, StringComparison.Ordinal));

    private static void SetComboSelection(ComboBox combo, string text)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (!string.Equals(combo.Items[index]?.ToString(), text, StringComparison.Ordinal)) continue;
            combo.SelectedIndex = index;
            return;
        }
        throw new InvalidOperationException("Synthetic combo selection was unavailable.");
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            Application.DoEvents();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Application.DoEvents();
        return condition();
    }

    private static double Milliseconds(TimeSpan elapsed) => Math.Round(elapsed.TotalMilliseconds, 3);

    private static void Check(ICollection<ProbeCheck> checks, string name, bool passed, string detail) =>
        checks.Add(new ProbeCheck(name, passed, detail));

    private static void WriteReport(ProbeReport report, string reportPath)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            Console.Out.WriteLine(json);
            return;
        }

        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed record ProbeThresholds(
        int WorkspaceInitializationMilliseconds,
        int QualityCalculationMilliseconds,
        int SearchFilterMilliseconds,
        int StatusFilterMilliseconds,
        int NextSelectionMilliseconds,
        int StopFeedbackMilliseconds);

    private sealed record ProbeTimings(
        double WorkspaceInitializationMilliseconds,
        double QualityCalculationMilliseconds,
        double SearchFilterMilliseconds,
        double StatusFilterMilliseconds,
        double NextSelectionMilliseconds = -1,
        double StopFeedbackMilliseconds = -1);

    private sealed record ProbeCheck(string Name, bool Passed, string Detail);

    private sealed record ProbeReport(
        string SchemaVersion,
        string Result,
        string FixtureProfile,
        int RowCount,
        ProbeThresholds Thresholds,
        ProbeTimings Timings,
        IReadOnlyList<ProbeCheck> Checks);
}
