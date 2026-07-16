using System.Diagnostics;

namespace RimWorldAiTranslator.App.Controls;

internal sealed class UiRenderDiagnostics : IDisposable
{
    private readonly bool enabled;
    private readonly Action<string> sink;
    private readonly HashSet<Control> observed = [];
    private readonly Dictionary<Control, RenderCounts> counts = [];
    private readonly Stopwatch stopwatch = new();
    private bool capturing;

    public UiRenderDiagnostics(Action<string> sink)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        enabled = Environment.GetEnvironmentVariable("RIMWORLD_TRANSLATOR_UI_TRACE")
            ?.Equals("1", StringComparison.Ordinal) == true;
    }

    internal string LastReportForTesting { get; private set; } = string.Empty;

    public void Begin(Control root)
    {
        if (!enabled) return;
        Attach(root);
        counts.Clear();
        stopwatch.Restart();
        capturing = true;
    }

    public void End()
    {
        if (!enabled || !capturing) return;
        capturing = false;
        stopwatch.Stop();
        var busiest = counts
            .Select(pair => new
            {
                Name = pair.Key.GetType().Name,
                pair.Value.Paint,
                pair.Value.Layout,
                pair.Value.Resize,
                Total = pair.Value.Paint + pair.Value.Layout + pair.Value.Resize
            })
            .Where(item => item.Total > 0)
            .OrderByDescending(item => item.Total)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(8)
            .Select(item => $"{item.Name}: paint {item.Paint}, layout {item.Layout}, resize {item.Resize}")
            .ToArray();
        LastReportForTesting = $"UI window-drag trace · {stopwatch.Elapsed.TotalMilliseconds:F1} ms"
            + (busiest.Length == 0 ? " · no control activity" : " · " + string.Join(" | ", busiest));
        Debug.WriteLine(LastReportForTesting);
        sink(LastReportForTesting);
    }

    private void Attach(Control control)
    {
        if (!observed.Add(control)) return;
        control.Paint += ControlPaint;
        control.Layout += ControlLayout;
        control.Resize += ControlResize;
        control.ControlAdded += ControlAdded;
        control.Disposed += ControlDisposed;
        foreach (Control child in control.Controls) Attach(child);
    }

    private void ControlPaint(object? sender, PaintEventArgs e) => Increment(sender, static value => value.Paint++);
    private void ControlLayout(object? sender, LayoutEventArgs e) => Increment(sender, static value => value.Layout++);
    private void ControlResize(object? sender, EventArgs e) => Increment(sender, static value => value.Resize++);

    private void Increment(object? sender, Action<RenderCounts> update)
    {
        if (!capturing || sender is not Control control) return;
        if (!counts.TryGetValue(control, out var value))
        {
            value = new RenderCounts();
            counts[control] = value;
        }
        update(value);
    }

    private void ControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control is not null) Attach(e.Control);
    }

    private void ControlDisposed(object? sender, EventArgs e)
    {
        if (sender is not Control control || !observed.Remove(control)) return;
        control.Paint -= ControlPaint;
        control.Layout -= ControlLayout;
        control.Resize -= ControlResize;
        control.ControlAdded -= ControlAdded;
        control.Disposed -= ControlDisposed;
        counts.Remove(control);
    }

    public void Dispose()
    {
        foreach (var control in observed.ToArray()) ControlDisposed(control, EventArgs.Empty);
        counts.Clear();
    }

    private sealed class RenderCounts
    {
        public int Paint;
        public int Layout;
        public int Resize;
    }
}
