using System.Text;
using RimWorldAiTranslator.App;
using RimWorldAiTranslator.App.Controls;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.UiHarness;

internal static class CloseBehaviorProbe
{
    private const string EditedTranslation = "합성 종료 동작 검증 번역";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    public static void Run(string baseRoot)
    {
        RunDirtyScenario(baseRoot, "dirty-cancel-no", DialogResult.Cancel, cancelThenDiscard: true);
        RunDirtyScenario(baseRoot, "dirty-no", DialogResult.No, cancelThenDiscard: false);
        RunDirtyScenario(baseRoot, "dirty-yes", DialogResult.Yes, cancelThenDiscard: false);
        RunActiveOperationScenario(baseRoot);
    }

    private static void RunDirtyScenario(
        string baseRoot,
        string name,
        DialogResult initialAnswer,
        bool cancelThenDiscard)
    {
        var roots = CreateScenarioRoots(baseRoot, name);
        var fixture = SyntheticUiFixture.Create(roots.DataRoot, roots.DiscoveryRoot, 1);
        var decisionPath = ReviewRepository.GetDecisionPath(fixture.ReviewRoot);
        var answer = initialAnswer;
        var promptCount = 0;
        var discovery = RimWorldModDiscoveryService.CreateIsolated(roots.DiscoveryRoot);
        using var form = new MainForm(
            roots.DataRoot,
            discovery,
            unsavedCloseConfirmation: () =>
            {
                promptCount++;
                return answer;
            });

        RunFormScenario(
            form,
            async () =>
            {
                await WaitForStartupAsync(form);
                await SnapshotCapture.OpenFirstProjectAsync(form, UiTimeout);
                if (File.Exists(decisionPath))
                    throw new InvalidOperationException($"{name}: opening a review wrote a decision file.");

                var translation = SnapshotCapture.DescendantsOf(form)
                    .OfType<RichTextBox>()
                    .Single(control => control.AccessibleName == "번역문 편집");
                var saveStatus = SnapshotCapture.DescendantsOf(form)
                    .OfType<Label>()
                    .Single(control => control.AccessibleName == "검수 저장 상태");
                var workspace = SnapshotCapture.DescendantsOf(form)
                    .OfType<ReviewWorkspaceControl>()
                    .Single();
                workspace.SetSaveInProgress(automatic: true);
                AssertSaveStatus(saveStatus, "자동 저장 중", name);
                workspace.SetSaveFailed(automatic: true);
                AssertSaveStatus(saveStatus, "자동 저장 실패", name);
                workspace.SetSaveSucceeded(automatic: true);
                if (!saveStatus.Text.StartsWith("자동 저장됨 ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{name}: the successful auto-save state was not visible (actual: '{saveStatus.Text}').");
                }

                translation.Text = EditedTranslation;
                if (!saveStatus.Text.Equals("자동 저장 대기", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{name}: the dirty editor did not expose the pending auto-save state (actual: '{saveStatus.Text}').");
                }

                form.Close();
                if (cancelThenDiscard)
                {
                    if (form.IsDisposed || !form.Visible)
                        throw new InvalidOperationException("dirty-cancel-no: Cancel did not keep the form open.");
                    if (promptCount != 1)
                        throw new InvalidOperationException("dirty-cancel-no: Cancel did not stop after one dirty prompt.");
                    if (File.Exists(decisionPath))
                        throw new InvalidOperationException("dirty-cancel-no: Cancel wrote the review decision file.");

                    answer = DialogResult.No;
                    form.Close();
                }
            },
            () => answer = DialogResult.No);

        var expectedPrompts = cancelThenDiscard ? 2 : 1;
        if (promptCount != expectedPrompts)
            throw new InvalidOperationException($"{name}: expected {expectedPrompts} dirty prompts, observed {promptCount}.");

        if (initialAnswer != DialogResult.Yes)
        {
            if (File.Exists(decisionPath))
                throw new InvalidOperationException($"{name}: discarding changes wrote the review decision file.");
            return;
        }

        if (!File.Exists(decisionPath))
            throw new InvalidOperationException("dirty-yes: saving on close did not create the review decision file.");
        var saved = new ReviewRepository(new AtomicJsonStore()).Load(fixture.ReviewRoot);
        if (!saved.Items.Any(item => item.Text.Equals(EditedTranslation, StringComparison.Ordinal)))
            throw new InvalidOperationException("dirty-yes: the edited translation was not persisted on close.");
    }

    private static void RunActiveOperationScenario(string baseRoot)
    {
        var roots = CreateScenarioRoots(baseRoot, "active-operation-cancel-ok");
        var answer = DialogResult.Cancel;
        var promptCount = 0;
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = RimWorldModDiscoveryService.CreateIsolated(roots.DiscoveryRoot);
        using var form = new MainForm(
            roots.DataRoot,
            discovery,
            activeOperationCloseConfirmation: () =>
            {
                promptCount++;
                return answer;
            },
            unsavedCloseConfirmation: () => DialogResult.No);

        RunFormScenario(
            form,
            async () =>
            {
                await WaitForStartupAsync(form);
                if (!form.StartUiWorkflowForTesting(
                        "synthetic active close prompt",
                        lifetimeToken => form.RunOperationForTesting(
                            async operationToken =>
                            {
                                operationStarted.TrySetResult();
                                try
                                {
                                    await Task.Delay(Timeout.InfiniteTimeSpan, operationToken);
                                }
                                catch (OperationCanceledException)
                                {
                                    cancellationObserved.TrySetResult();
                                    throw;
                                }
                            },
                            lifetimeToken)))
                {
                    throw new InvalidOperationException("active-operation-cancel-ok: the synthetic operation was rejected.");
                }

                await operationStarted.Task.WaitAsync(UiTimeout);
                form.Close();
                if (form.IsDisposed || !form.Visible)
                    throw new InvalidOperationException("active-operation-cancel-ok: Cancel did not keep the form open.");
                if (promptCount != 1)
                    throw new InvalidOperationException("active-operation-cancel-ok: Cancel did not stop after one active-operation prompt.");
                if (cancellationObserved.Task.IsCompleted)
                    throw new InvalidOperationException("active-operation-cancel-ok: Cancel signaled operation cancellation.");

                answer = DialogResult.OK;
                form.Close();
            },
            () => answer = DialogResult.OK);

        if (promptCount != 2)
            throw new InvalidOperationException($"active-operation-cancel-ok: expected 2 prompts, observed {promptCount}.");
        if (!cancellationObserved.Task.IsCompletedSuccessfully)
            throw new InvalidOperationException("active-operation-cancel-ok: accepted close did not cancel the operation.");
    }

    private static void AssertSaveStatus(Label saveStatus, string expected, string scenario)
    {
        if (!saveStatus.Text.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{scenario}: expected save status '{expected}', observed '{saveStatus.Text}'.");
        }
    }

    private static async Task WaitForStartupAsync(MainForm form)
    {
        await SnapshotCapture.WaitForReadyAsync(form, UiTimeout);
        var deadline = DateTime.UtcNow + UiTimeout;
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (!form.HasIncompleteUiWorkflowsForTesting) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The C# UI startup workflow did not complete before the close behavior probe timeout.");
    }

    private static void RunFormScenario(MainForm form, Func<Task> scenario, Action prepareTeardown)
    {
        Exception? failure = null;
        Task? scenarioTask = null;
        form.Shown += (_, _) => scenarioTask = RunGuardedAsync();
        Application.Run(form);
        scenarioTask?.GetAwaiter().GetResult();
        if (scenarioTask is null)
            throw new InvalidOperationException("The close behavior scenario did not start.");
        if (failure is not null)
            throw new InvalidOperationException("The close behavior scenario failed.", failure);

        async Task RunGuardedAsync()
        {
            try
            {
                await scenario();
            }
            catch (Exception exception)
            {
                failure = exception;
                prepareTeardown();
                if (!form.IsDisposed) form.Close();
            }
        }
    }

    private static ScenarioRoots CreateScenarioRoots(string baseRoot, string name)
    {
        var scenarioRoot = Path.Combine(Path.GetFullPath(baseRoot), "close-behavior-probe", name);
        var dataRoot = Path.Combine(scenarioRoot, "data");
        var discoveryRoot = Path.Combine(scenarioRoot, "discovery");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(discoveryRoot);
        File.WriteAllText(
            Path.Combine(discoveryRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
            RimWorldModDiscoveryService.IsolationMarkerContent + Environment.NewLine,
            new UTF8Encoding(false));
        return new ScenarioRoots(dataRoot, discoveryRoot);
    }

    private sealed record ScenarioRoots(string DataRoot, string DiscoveryRoot);
}
