namespace RimWorldAiTranslator.App.Controls;

internal sealed class CommandToolTipService : IDisposable, IMessageFilter
{
    private const int MouseMoveMessage = 0x0200;
    private const int NonClientMouseMoveMessage = 0x00A0;
    private readonly ToolTip toolTip = new()
    {
        AutomaticDelay = 500,
        InitialDelay = 600,
        ReshowDelay = 100,
        AutoPopDelay = 7_000,
        ShowAlways = true
    };
    private readonly Dictionary<Control, Registration> registrations = [];
    private Control? manuallyShownFor;
    private bool disposed;

    public CommandToolTipService()
    {
        Application.AddMessageFilter(this);
    }

    public void Register(
        Control control,
        UiCommand command,
        Func<string?>? disabledReason = null,
        Func<string>? description = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(control);
        if (registrations.TryGetValue(control, out var existing))
        {
            existing.Command = command;
            existing.DisabledReason = disabledReason;
            existing.Description = description;
            Refresh(control);
            return;
        }

        var registration = new Registration(command, disabledReason, description);
        registrations.Add(control, registration);
        control.EnabledChanged += ControlStateChanged;
        control.VisibleChanged += ControlStateChanged;
        control.Disposed += ControlDisposed;
        Refresh(control);
    }

    public void Refresh(Control control)
    {
        if (disposed || control.IsDisposed || !registrations.TryGetValue(control, out var registration)) return;
        var text = BuildText(registration, control.Enabled);
        toolTip.SetToolTip(control, text);
        control.AccessibleDescription = text;
    }

    public void RefreshAll()
    {
        foreach (var control in registrations.Keys.ToArray()) Refresh(control);
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (disposed || message.Msg is not (MouseMoveMessage or NonClientMouseMoveMessage)) return false;
        var cursor = Control.MousePosition;
        var disabled = registrations.Keys.FirstOrDefault(control =>
            !control.IsDisposed
            && control.Visible
            && !control.Enabled
            && control.FindForm()?.Visible == true
            && control.RectangleToScreen(control.ClientRectangle).Contains(cursor));
        if (ReferenceEquals(disabled, manuallyShownFor)) return false;
        if (manuallyShownFor is not null && !manuallyShownFor.IsDisposed) toolTip.Hide(manuallyShownFor);
        manuallyShownFor = disabled;
        if (disabled is not null)
        {
            Refresh(disabled);
            toolTip.Show(toolTip.GetToolTip(disabled), disabled, disabled.Width / 2, disabled.Height + 2, toolTip.AutoPopDelay);
        }
        return false;
    }

    internal string GetText(Control control) => toolTip.GetToolTip(control) ?? string.Empty;
    internal int RegistrationCount(Control control) => registrations.ContainsKey(control) ? 1 : 0;

    internal bool FitsWorkingAreaForTesting(Control control, int dpi, Size workingArea)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        if (workingArea.Width <= 0 || workingArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workingArea));
        var scale = dpi / 96f;
        using var font = new Font(control.Font.FontFamily, control.Font.Size * scale, control.Font.Style);
        var lines = GetText(control).Split(["\r\n", "\n"], StringSplitOptions.None);
        var sizes = lines.Select(line => TextRenderer.MeasureText(line, font, Size.Empty, TextFormatFlags.NoPadding)).ToArray();
        return sizes.All(size => size.Width + 32 <= workingArea.Width)
            && sizes.Sum(size => size.Height) + 24 <= workingArea.Height;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Application.RemoveMessageFilter(this);
        foreach (var control in registrations.Keys.ToArray()) Detach(control);
        registrations.Clear();
        toolTip.Dispose();
    }

    private static string BuildText(Registration registration, bool enabled)
    {
        var definition = UiCommandCatalog.Get(registration.Command);
        var lines = new List<string> { registration.Description?.Invoke() ?? definition.Description };
        if (!string.IsNullOrWhiteSpace(definition.ShortcutText)) lines.Add($"단축키: {definition.ShortcutText}");
        if (!enabled)
        {
            var reason = registration.DisabledReason?.Invoke();
            lines.Add(string.IsNullOrWhiteSpace(reason) ? "현재 상태에서는 사용할 수 없습니다." : reason);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void ControlStateChanged(object? sender, EventArgs e)
    {
        if (sender is Control control) Refresh(control);
    }

    private void ControlDisposed(object? sender, EventArgs e)
    {
        if (sender is not Control control) return;
        Detach(control);
        registrations.Remove(control);
    }

    private void Detach(Control control)
    {
        control.EnabledChanged -= ControlStateChanged;
        control.VisibleChanged -= ControlStateChanged;
        control.Disposed -= ControlDisposed;
        if (!control.IsDisposed) toolTip.SetToolTip(control, null);
    }

    private sealed class Registration(
        UiCommand command,
        Func<string?>? disabledReason,
        Func<string>? description)
    {
        public UiCommand Command { get; set; } = command;
        public Func<string?>? DisabledReason { get; set; } = disabledReason;
        public Func<string>? Description { get; set; } = description;
    }
}
