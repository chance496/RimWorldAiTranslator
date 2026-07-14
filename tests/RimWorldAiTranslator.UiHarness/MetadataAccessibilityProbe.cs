using System.Reflection;
using System.Text.Json;
using RimWorldAiTranslator.App;
using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Rmk;

namespace RimWorldAiTranslator.UiHarness;

internal static class MetadataAccessibilityProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Run(string dataRoot, string discoveryRoot, string reportPath)
    {
        var checks = new List<MetadataProbeCheck>();
        MainForm? form = null;
        AppServices? services = null;

        try
        {
            services = new AppServices(dataRoot, RimWorldModDiscoveryService.CreateIsolated(discoveryRoot));
            var project = services.ProjectStore.Projects.Single();
            var review = services.Reviews.Load(project.LatestReviewRoot, project);
            Check(checks, "fixture.rows", review.Items.Count == 3);

            var ioHooks = new UiIoHooks(
                GlossaryFileExists: _ => false,
                DirectoryExists: _ => false,
                IsRmkWorkspaceRoot: _ => false,
                IsWritableRmkWorkspace: _ => false,
                OpenDirectory: _ => { },
                BuildRmkWorkspace: (_, cancellationToken) =>
                    Task.FromCanceled<RmkBuildResult>(
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
                ClientSize = new Size(1_280, 720)
            };
            form.SuppressStartupForTesting();
            form.Show();
            Application.DoEvents();
            form.LoadWorkspaceForTesting(project, review);
            form.PerformLayout();
            var workspace = form.WorkspaceControlForTesting;
            var reviewList = Find<ListBox>(workspace, "검색된 문자열 목록");
            var workspaceReady = PumpUntil(
                () => workspace.FilteredReviewCountForTesting == 3
                    && reviewList.Items.Count == 3
                    && !workspace.UiDataWorkPendingForTesting,
                TimeSpan.FromSeconds(5));
            Check(checks, "workspace.ready", workspaceReady,
                $"filtered={workspace.FilteredReviewCountForTesting};listItems={reviewList.Items.Count};filterPending={workspace.FilterWorkPendingForTesting};qualityPending={workspace.QualityWorkPendingForTesting}");
            if (!workspaceReady) throw new InvalidOperationException("Synthetic workspace did not become ready.");

            reviewList.SelectedIndex = 0;
            Application.DoEvents();

            var metadata = Find<RichTextBox>(workspace, "문자열 정보");
            var source = Find<RichTextBox>(workspace, "선택 문자열 원문");
            var existing = Find<RichTextBox>(workspace, "기존 번역");
            var candidate = Find<RichTextBox>(workspace, "AI 번역 후보");
            var requiredTokens = new[] { "Def Class", "Node", "파일", "ID", "출처", "단어", "안전 적용" };

            Check(checks, "metadata.read-only-focusable",
                metadata.ReadOnly && metadata.TabStop && metadata.CanSelect && metadata.ShortcutsEnabled);
            Check(checks, "metadata.accessibility",
                string.Equals(metadata.AccessibilityObject.Name, "문자열 정보", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(metadata.AccessibilityObject.Description)
                && requiredTokens.All(token => metadata.Text.Contains(token, StringComparison.Ordinal)));
            Check(checks, "metadata.accessible-value",
                string.Equals(
                    NormalizeNewlines(metadata.AccessibilityObject.Value).TrimEnd('\n'),
                    NormalizeNewlines(metadata.Text).TrimEnd('\n'),
                    StringComparison.Ordinal),
                $"valueLength={metadata.AccessibilityObject.Value?.Length ?? -1};textLength={metadata.TextLength}");
            Check(checks, "metadata.logical-tab-order",
                metadata.TabIndex == 2
                && existing.TabIndex > metadata.TabIndex
                && candidate.TabIndex == existing.TabIndex + 1
                && SnapshotCapture.DescendantsOf(workspace)
                    .OfType<Button>()
                    .Where(button => ReferenceEquals(button.Parent, metadata.Parent))
                    .All(button => button.TabIndex > metadata.TabIndex));

            Clipboard.Clear();
            var focusAccepted = metadata.Focus();
            Application.DoEvents();
            metadata.SelectAll();
            metadata.Copy();
            Check(checks, "metadata.keyboard-copy-command",
                focusAccepted
                && metadata.Focused
                && metadata.SelectionLength == metadata.TextLength
                && string.Equals(NormalizeNewlines(Clipboard.GetText()), NormalizeNewlines(metadata.Text), StringComparison.Ordinal),
                $"focusAccepted={focusAccepted};focused={metadata.Focused};selection={metadata.SelectionLength};text={metadata.TextLength};clipboard={Clipboard.GetText().Length}");

            VerifyDoubleClickCopy(checks, "metadata.double-click-copy", metadata);
            VerifyDoubleClickCopy(checks, "source.double-click-copy", source);
            VerifyDoubleClickCopy(checks, "existing.double-click-copy", existing);
            VerifyDoubleClickCopy(checks, "candidate.double-click-copy", candidate);

            reviewList.SelectedIndex = 2;
            Application.DoEvents();
            var status = SnapshotCapture.DescendantsOf(workspace)
                .OfType<Label>()
                .Single(label => label.AccessibleName?.StartsWith("문자열 상태:", StringComparison.Ordinal) == true);
            var update = SnapshotCapture.DescendantsOf(workspace)
                .OfType<Label>()
                .Single(label => string.Equals(label.Text, "업데이트로 변경됨", StringComparison.Ordinal));
            Check(checks, "status.dynamic-accessibility",
                (status.AccessibleName?.Length ?? 0) > "문자열 상태:".Length
                && status.AccessibleDescription?.Contains("검색 결과", StringComparison.Ordinal) == true
                && status.AccessibleDescription?.Contains("주의 항목", StringComparison.Ordinal) == true);
            Check(checks, "update.dynamic-accessibility",
                update.Visible
                && string.Equals(update.AccessibleName, "원문 업데이트 감지", StringComparison.Ordinal)
                && update.AccessibleDescription?.Contains("다시 검토", StringComparison.Ordinal) == true);
        }
        catch (Exception exception)
        {
            Check(checks, "probe.exception", false, exception.GetType().Name);
        }
        finally
        {
            if (form is not null)
            {
                try
                {
                    form.WorkspaceControlForTesting.SuspendBackgroundUiWorkForClose();
                    Application.DoEvents();
                    form.Dispose();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Check(checks, "probe.cleanup", false, exception.GetType().Name);
                }
                Application.DoEvents();
            }
            services?.Dispose();
        }

        var passed = checks.All(check => check.Passed);
        WriteReport(reportPath, new MetadataProbeReport(
            "phase06-metadata-accessibility-v1",
            passed ? "PASS" : "FAIL",
            checks));
        if (!passed)
            throw new InvalidOperationException(
                "Metadata accessibility probe failed: "
                + string.Join(", ", checks.Where(check => !check.Passed).Select(check => check.Name)));
    }

    private static void VerifyDoubleClickCopy(
        ICollection<MetadataProbeCheck> checks,
        string name,
        RichTextBox control)
    {
        Clipboard.Clear();
        var onDoubleClick = typeof(Control).GetMethod(
            "OnDoubleClick",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Control).FullName, "OnDoubleClick");
        onDoubleClick.Invoke(control, [EventArgs.Empty]);
        Check(checks, name,
            control.TextLength > 0
            && control.SelectionLength == control.TextLength
            && string.Equals(NormalizeNewlines(Clipboard.GetText()), NormalizeNewlines(control.Text), StringComparison.Ordinal),
            $"selection={control.SelectionLength};text={control.TextLength};clipboard={Clipboard.GetText().Length}");
    }

    private static T Find<T>(Control root, string accessibleName) where T : Control =>
        SnapshotCapture.DescendantsOf(root)
            .OfType<T>()
            .Single(control => string.Equals(control.AccessibleName, accessibleName, StringComparison.Ordinal));

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            Application.DoEvents();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Application.DoEvents();
        return condition();
    }

    private static string NormalizeNewlines(string? value) =>
        (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static void Check(
        ICollection<MetadataProbeCheck> checks,
        string name,
        bool passed,
        string detail = "") =>
        checks.Add(new MetadataProbeCheck(name, passed, detail));

    private static void WriteReport(string reportPath, MetadataProbeReport report)
    {
        if (string.IsNullOrWhiteSpace(reportPath)) return;
        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, JsonOptions), new System.Text.UTF8Encoding(false));
    }

    private sealed record MetadataProbeCheck(string Name, bool Passed, string Detail);
    private sealed record MetadataProbeReport(
        string SchemaVersion,
        string Result,
        IReadOnlyList<MetadataProbeCheck> Checks);
}
