using RimWorldAiTranslator.Core.Diagnostics;
using RimWorldAiTranslator.App.Controls;

namespace RimWorldAiTranslator.App;

internal sealed class StartupBootstrapForm : Form
{
    private static readonly TimeSpan DefaultShutdownCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly object stateSync = new();
    private readonly Func<CancellationToken, Task<AppStartupState>> startupFactory;
    private readonly TimeSpan shutdownCleanupTimeout;
    private readonly Action<MainForm>? beforeOwnershipTransfer;
    private readonly Action<Exception>? startupFailurePresenter;
    private readonly Action<Exception>? startupFailureRecorder;
    private readonly Func<AppStartupState, MainForm>? mainFormFactoryForTesting;
    private readonly CancellationTokenSource startupCancellation = new();
    private Task<AppStartupState>? startupTask;
    private Task? startupObserver;
    private Task? transitionTask;
    private Task? transitionObserver;
    private Task? mainFormFailureTask;
    private Task? mainFormFailureObserver;
    private Task? pendingStateDisposalTask;
    private Task? deferredShutdownObserver;
    private AppStartupState? pendingState;
    private MainForm? mainForm;
    private Exception? startupFailure;
    private bool closeRequested;
    private bool transitionCompleted;
    private bool loadingSurfaceCoveredMain;

    internal StartupBootstrapForm(
        Func<CancellationToken, Task<AppStartupState>> startupFactory,
        TimeSpan? shutdownCleanupTimeout = null,
        Action<MainForm>? beforeOwnershipTransfer = null,
        Action<Exception>? startupFailurePresenter = null,
        Func<AppStartupState, MainForm>? mainFormFactoryForTesting = null,
        Action<Exception>? startupFailureRecorder = null)
    {
        this.startupFactory = startupFactory ?? throw new ArgumentNullException(nameof(startupFactory));
        var configuredShutdownTimeout = shutdownCleanupTimeout ?? DefaultShutdownCleanupTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuredShutdownTimeout, TimeSpan.Zero);
        this.shutdownCleanupTimeout = configuredShutdownTimeout;
        this.beforeOwnershipTransfer = beforeOwnershipTransfer;
        this.startupFailurePresenter = startupFailurePresenter;
        this.mainFormFactoryForTesting = mainFormFactoryForTesting;
        this.startupFailureRecorder = startupFailureRecorder;
        Text = "RimWorld AI Translator - 시작 중";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        Bounds = Screen.FromPoint(Cursor.Position).WorkingArea;
        BackColor = Color.FromArgb(29, 38, 42);
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint,
            true);
        SuspendLayout();

        var card = new BufferedPanel
        {
            AutoSize = false,
            Size = new Size(520, 184),
            BackColor = Color.FromArgb(245, 247, 248)
        };
        var title = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(38, 28, 444, 34),
            Font = new Font("Malgun Gothic", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(19, 30, 35),
            Text = "RimWorld AI Translator",
            TextAlign = ContentAlignment.MiddleLeft
        };
        var status = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(38, 72, 444, 54),
            Font = new Font("Malgun Gothic", 9f),
            ForeColor = Color.FromArgb(66, 85, 94),
            Text = "RimWorld AI Translator를 준비하고 있습니다.",
            TextAlign = ContentAlignment.MiddleLeft
        };
        var progress = new ProgressBar
        {
            Bounds = new Rectangle(38, 142, 444, 8),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 24
        };
        card.Controls.AddRange([title, status, progress]);
        Controls.Add(card);
        void CenterCard() => card.Location = new Point(
            Math.Max(0, (ClientSize.Width - card.Width) / 2),
            Math.Max(0, (ClientSize.Height - card.Height) / 2));
        Resize += (_, _) => CenterCard();
        CenterCard();
        Shown += BootstrapShown;
        FormClosing += BootstrapClosing;
        ResumeLayout(false);
    }

    internal event EventHandler<MainForm>? MainFormShownForTesting;
    internal bool TransitionCompletedForTesting => transitionCompleted;
    internal bool LoadingSurfaceCoveredMainForTesting => loadingSurfaceCoveredMain;
    internal bool StartupFailed
    {
        get
        {
            lock (stateSync) return startupFailure is not null;
        }
    }
    internal Task StartupObserverForTesting => startupObserver ?? Task.CompletedTask;
    internal bool HasDeferredShutdownObserverForTesting =>
        deferredShutdownObserver is { IsCompleted: false };

    private void BootstrapShown(object? sender, EventArgs e)
    {
        try
        {
            BeginInvoke((Action)StartBootstrapSafely);
        }
        catch (Exception exception)
        {
            ShowStartupFailure(exception);
        }
    }

    private void StartBootstrapSafely()
    {
        try
        {
            StartBootstrap();
        }
        catch (Exception exception)
        {
            ShowStartupFailure(exception);
        }
    }

    private void StartBootstrap()
    {
        lock (stateSync)
        {
            if (startupTask is not null || closeRequested) return;
            startupTask = Task.Run(
                async () => await startupFactory(startupCancellation.Token).ConfigureAwait(false),
                CancellationToken.None);
            startupObserver = ObserveStartupAsync(startupTask);
        }
    }

    private async Task ObserveStartupAsync(Task<AppStartupState> task)
    {
        try
        {
            var state = await task.ConfigureAwait(false)
                ?? throw new InvalidOperationException("The startup factory returned no state.");
            var disposeInstead = false;
            lock (stateSync)
            {
                if (closeRequested) disposeInstead = true;
                else pendingState = state;
            }

            if (disposeInstead)
            {
                await state.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (!TryPost(StartCompleteBootstrap))
            {
                var abandoned = TakePendingState();
                if (abandoned is not null)
                    await abandoned.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (IsCloseRequested())
        {
            // Closing the bootstrap window is the expected cancellation boundary.
        }
        catch (Exception exception)
        {
            if (!IsCloseRequested()) TryPost(() => ShowStartupFailure(exception));
        }
    }

    private bool TryPost(Action action)
    {
        lock (stateSync)
        {
            if (closeRequested) return false;
            try
            {
                BeginInvoke(action);
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine($"Startup UI notification skipped ({exception.GetType().Name}).");
                return false;
            }
        }
    }

    private void StartCompleteBootstrap()
    {
        var transition = CompleteBootstrapAsync();
        lock (stateSync)
        {
            transitionTask = transition;
            transitionObserver = ObserveTransitionAsync(transition);
        }
    }

    private static async Task ObserveTransitionAsync(Task transition)
    {
        await transition.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (transition.IsFaulted && transition.Exception is not null)
            System.Diagnostics.Debug.WriteLine($"Startup transition failed ({transition.Exception.GetBaseException().GetType().Name}).");
    }

    private async Task CompleteBootstrapAsync()
    {
        var state = TakePendingState();
        if (state is null) return;
        if (IsCloseRequested())
        {
            await state.DisposeAsync();
            return;
        }

        MainForm? created = null;
        var transferred = false;
        try
        {
            created = mainFormFactoryForTesting?.Invoke(state)
                ?? new MainForm(
                    state.Services,
                    state.ProjectStats,
                    state.IsolationAcknowledgementPath);
            created.StartupFailed += MainFormStartupFailed;
            beforeOwnershipTransfer?.Invoke(created);
            state.TransferOwnershipToMainForm();
            transferred = true;
            mainForm = created;
            created.FormClosed += MainFormClosed;
            await created.PrepareForFirstShowAsync(startupCancellation.Token);
            if (IsCloseRequested())
            {
                await created.DisposeAfterFailedBootstrapAsync();
                mainForm = null;
                return;
            }
            created.Enabled = false;
            created.Show();
            TopMost = true;
            BringToFront();
            Activate();
            loadingSurfaceCoveredMain = Visible && Bounds.Contains(Screen.FromControl(created).WorkingArea);
            try
            {
                await created.RevealPreparedFirstFrameAsync();
                Hide();
            }
            finally
            {
                TopMost = false;
            }
            created.Activate();
            transitionCompleted = true;
            NotifyMainFormShown(created);
            created.CompleteFirstShow();
        }
        catch (Exception exception)
        {
            if (created is not null)
            {
                created.StartupFailed -= MainFormStartupFailed;
                await created.DisposeAfterFailedBootstrapAsync();
            }
            if (!transferred) await state.DisposeAsync();
            ShowStartupFailure(exception);
        }
        finally
        {
            await state.DisposeAsync();
        }
    }

    private void NotifyMainFormShown(MainForm form)
    {
        try
        {
            MainFormShownForTesting?.Invoke(this, form);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Startup transition observer skipped ({exception.GetType().Name}).");
        }
    }

    private void MainFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is MainForm form) form.StartupFailed -= MainFormStartupFailed;
        try
        {
            if (!IsDisposed) Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine($"Bootstrap close skipped ({exception.GetType().Name}).");
        }
    }

    private void ShowStartupFailure(Exception exception)
    {
        if (IsCloseRequested() || IsDisposed) return;
        if (!TryRecordStartupFailure(exception)) return;
        try
        {
            RecordStartupFailure(exception);
            PresentStartupFailure(exception);
        }
        finally
        {
            try { Close(); }
            catch (Exception closeException)
            {
                System.Diagnostics.Debug.WriteLine($"Startup failure close skipped ({closeException.GetType().Name}).");
            }
        }
    }

    private void MainFormStartupFailed(object? sender, MainFormStartupFailureEventArgs e)
    {
        if (sender is not MainForm form || IsCloseRequested() || IsDisposed) return;
        if (!TryRecordStartupFailure(e.Exception)) return;
        var shutdown = CompleteMainFormStartupFailureAsync(form, e.Exception);
        lock (stateSync)
        {
            mainFormFailureTask = shutdown;
            mainFormFailureObserver = ObserveTransitionAsync(shutdown);
        }
    }

    private async Task CompleteMainFormStartupFailureAsync(MainForm form, Exception exception)
    {
        // Let the MainForm's currently executing tracked startup workflow leave its
        // event callback before draining all tracked work. Awaiting that workflow
        // from inside itself would otherwise deadlock the failure shutdown.
        await Task.Yield();
        form.StartupFailed -= MainFormStartupFailed;
        form.FormClosed -= MainFormClosed;
        try
        {
            await form.DisposeAfterFailedBootstrapAsync();
        }
        catch (Exception cleanupException)
        {
            System.Diagnostics.Debug.WriteLine($"Main-form startup cleanup failed ({cleanupException.GetType().Name}).");
        }
        finally
        {
            lock (stateSync)
            {
                if (ReferenceEquals(mainForm, form)) mainForm = null;
            }
        }

        RecordStartupFailure(exception);
        PresentStartupFailure(exception);
        if (IsDisposed) return;
        try { Close(); }
        catch (Exception closeException) when (closeException is InvalidOperationException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine($"Main-form startup failure close skipped ({closeException.GetType().Name}).");
        }
    }

    private bool TryRecordStartupFailure(Exception exception)
    {
        lock (stateSync)
        {
            if (startupFailure is not null) return false;
            startupFailure = exception;
            return true;
        }
    }

    private void RecordStartupFailure(Exception exception)
    {
        if (startupFailureRecorder is null) return;
        try { startupFailureRecorder(exception); }
        catch (Exception recordException)
        {
            System.Diagnostics.Debug.WriteLine($"Startup failure recording unavailable ({recordException.GetType().Name}).");
        }
    }

    private void PresentStartupFailure(Exception exception)
    {
        try
        {
            if (startupFailurePresenter is not null)
            {
                startupFailurePresenter(exception);
                return;
            }

            MessageBox.Show(
                this,
                "RimWorld AI Translator를 시작하지 못했습니다.\n\n"
                + OperationErrorPresentation.CreateUserDetail(exception),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1);
        }
        catch (Exception dialogException)
        {
            System.Diagnostics.Debug.WriteLine($"Startup failure dialog unavailable ({dialogException.GetType().Name}).");
            try
            {
                MessageBox.Show(
                    "RimWorld AI Translator를 시작하지 못했습니다. 입력 데이터와 저장 위치를 확인하세요.",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1);
            }
            catch (Exception fallbackException)
            {
                System.Diagnostics.Debug.WriteLine($"Startup failure fallback unavailable ({fallbackException.GetType().Name}).");
            }
        }
    }

    private void BootstrapClosing(object? sender, FormClosingEventArgs e) => RequestCancellation();

    private void RequestCancellation()
    {
        AppStartupState? disposeState;
        lock (stateSync)
        {
            if (closeRequested) return;
            closeRequested = true;
            disposeState = pendingState;
            pendingState = null;
        }
        if (disposeState is not null)
        {
            var disposal = DisposePendingStateAsync(disposeState);
            lock (stateSync) pendingStateDisposalTask = disposal;
        }
        try { startupCancellation.Cancel(); }
        catch (ObjectDisposedException)
        {
            // The observed startup task may already have released its token source.
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Startup cancellation callback failed ({exception.GetType().Name}).");
        }
    }

    private static async Task DisposePendingStateAsync(AppStartupState state)
    {
        try { await state.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Pending startup disposal failed ({exception.GetType().Name}).");
        }
    }

    private AppStartupState? TakePendingState()
    {
        lock (stateSync)
        {
            var state = pendingState;
            pendingState = null;
            return state;
        }
    }

    private bool IsCloseRequested()
    {
        lock (stateSync) return closeRequested;
    }

    internal async ValueTask DisposeAfterRunAsync()
    {
        RequestCancellation();
        Task[] cleanupTasks;
        lock (stateSync)
        {
            cleanupTasks = new[]
                {
                    startupObserver,
                    transitionTask,
                    transitionObserver,
                    mainFormFailureTask,
                    mainFormFailureObserver,
                    pendingStateDisposalTask
                }
                .Where(static task => task is not null)
                .Cast<Task>()
                .ToArray();
        }
        try { mainForm?.Dispose(); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Main form disposal after bootstrap failed ({exception.GetType().Name}).");
        }
        try { Dispose(); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Bootstrap form disposal failed ({exception.GetType().Name}).");
        }
        if (cleanupTasks.Length == 0)
        {
            startupCancellation.Dispose();
            return;
        }

        var cleanup = Task.WhenAll(cleanupTasks);
        var cleanupCompleted = false;
        try
        {
            await cleanup.WaitAsync(shutdownCleanupTimeout).ConfigureAwait(false);
            cleanupCompleted = true;
        }
        catch (TimeoutException)
        {
            System.Diagnostics.Debug.WriteLine("Startup cleanup exceeded the shutdown time limit and will be observed in the background.");
            deferredShutdownObserver = ObserveDeferredShutdownCleanupAsync(cleanup, startupCancellation);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Startup cleanup failed ({exception.GetType().Name}).");
            cleanupCompleted = true;
        }
        if (cleanupCompleted) startupCancellation.Dispose();
    }

    private static async Task ObserveDeferredShutdownCleanupAsync(
        Task cleanup,
        CancellationTokenSource cancellationSource)
    {
        try
        {
            await cleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (cleanup.IsFaulted && cleanup.Exception is not null)
                System.Diagnostics.Debug.WriteLine($"Deferred startup cleanup failed ({cleanup.Exception.GetBaseException().GetType().Name}).");
        }
        finally
        {
            cancellationSource.Dispose();
        }
    }
}
