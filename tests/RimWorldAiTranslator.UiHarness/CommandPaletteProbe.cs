using System.Reflection;
using System.Text.Json;
using RimWorldAiTranslator.App.Dialogs;

namespace RimWorldAiTranslator.UiHarness;

internal static class CommandPaletteProbe
{
    private const string UnavailableMarker = "\uD604\uC7AC \uC0AC\uC6A9 \uBD88\uAC00";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Run(string reportPath)
    {
        var checks = new List<CommandPaletteProbeCheck>();
        var disabledExecutionCount = 0;
        var enabledExecutionCount = 0;
        CommandPaletteDialog? dialog = null;

        try
        {
            var disabledAction = new CommandPaletteAction(
                "Disabled synthetic command",
                "Synthetic",
                "Ctrl+D",
                false,
                () => disabledExecutionCount++);
            var enabledAction = new CommandPaletteAction(
                "Enabled synthetic command",
                "Synthetic",
                "Ctrl+E",
                true,
                () => enabledExecutionCount++);

            dialog = new CommandPaletteDialog([disabledAction, enabledAction])
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000)
            };
            dialog.Show();
            Application.DoEvents();

            var list = DescendantsOf(dialog).OfType<ListView>().Single();
            var run = DescendantsOf(dialog)
                .OfType<Button>()
                .Single(button => !ReferenceEquals(button, dialog.CancelButton));

            Select(list, 0);
            Check(checks, "disabled.run-button", !run.Enabled,
                $"enabled={run.Enabled}");
            Check(checks, "disabled.visible-name",
                list.Items[0].Text.Contains(UnavailableMarker, StringComparison.Ordinal),
                $"text={list.Items[0].Text}");

            var accessibleNames = CollectAccessibleNames(list.AccessibilityObject);
            Check(checks, "disabled.accessible-name",
                accessibleNames.Any(name =>
                    name.Contains("Disabled synthetic command", StringComparison.Ordinal)
                    && name.Contains(UnavailableMarker, StringComparison.Ordinal)),
                $"names={string.Join(" | ", accessibleNames)}");

            RaiseEnter(list);
            Application.DoEvents();
            Check(checks, "disabled.enter-keeps-dialog-open",
                !dialog.IsDisposed && dialog.Visible && dialog.DialogResult == DialogResult.None,
                $"disposed={dialog.IsDisposed};visible={dialog.Visible};result={dialog.DialogResult}");
            Check(checks, "disabled.enter-no-selection",
                dialog.SelectedAction is null,
                $"selected={dialog.SelectedAction?.Name ?? "<null>"}");
            Check(checks, "disabled.enter-no-execution",
                disabledExecutionCount == 0 && enabledExecutionCount == 0,
                $"disabled={disabledExecutionCount};enabled={enabledExecutionCount}");

            Select(list, 1);
            Check(checks, "enabled.run-button", run.Enabled,
                $"enabled={run.Enabled}");

            RaiseEnter(list);
            Application.DoEvents();
            Check(checks, "enabled.enter-selects-action",
                ReferenceEquals(dialog.SelectedAction, enabledAction)
                && dialog.DialogResult == DialogResult.OK,
                $"selected={dialog.SelectedAction?.Name ?? "<null>"};result={dialog.DialogResult}");

            dialog.SelectedAction?.Execute();
            Check(checks, "enabled.host-execution",
                enabledExecutionCount == 1 && disabledExecutionCount == 0,
                $"disabled={disabledExecutionCount};enabled={enabledExecutionCount}");
        }
        catch (Exception exception)
        {
            Check(checks, "probe.exception", false, exception.GetType().Name);
        }
        finally
        {
            if (dialog is not null && !dialog.IsDisposed)
            {
                dialog.Close();
                dialog.Dispose();
            }
            Application.DoEvents();
        }

        var passed = checks.All(check => check.Passed);
        WriteReport(reportPath, new CommandPaletteProbeReport(
            "phase06-command-palette-v1",
            passed ? "PASS" : "FAIL",
            checks));
        if (!passed)
            throw new InvalidOperationException(
                "Command palette probe failed: "
                + string.Join(", ", checks.Where(check => !check.Passed).Select(check => check.Name)));
    }

    private static void Select(ListView list, int index)
    {
        foreach (ListViewItem item in list.Items)
        {
            item.Selected = false;
            item.Focused = false;
        }
        list.Items[index].Selected = true;
        list.Items[index].Focused = true;
        list.Select();
        Application.DoEvents();
    }

    private static void RaiseEnter(Control control)
    {
        var onKeyDown = typeof(Control).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Control).FullName, "OnKeyDown");
        onKeyDown.Invoke(control, [new KeyEventArgs(Keys.Enter)]);
    }

    private static List<string> CollectAccessibleNames(AccessibleObject root)
    {
        var names = new List<string>();
        Visit(root, 0);
        return names;

        void Visit(AccessibleObject current, int depth)
        {
            if (!string.IsNullOrWhiteSpace(current.Name)) names.Add(current.Name);
            if (depth >= 4) return;
            var childCount = Math.Clamp(current.GetChildCount(), 0, 100);
            for (var index = 0; index < childCount; index++)
            {
                var child = current.GetChild(index);
                if (child is not null) Visit(child, depth + 1);
            }
        }
    }

    private static IEnumerable<Control> DescendantsOf(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in DescendantsOf(child)) yield return descendant;
        }
    }

    private static void Check(
        ICollection<CommandPaletteProbeCheck> checks,
        string name,
        bool passed,
        string detail) =>
        checks.Add(new CommandPaletteProbeCheck(name, passed, detail));

    private static void WriteReport(string reportPath, CommandPaletteProbeReport report)
    {
        if (string.IsNullOrWhiteSpace(reportPath)) return;
        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(report, JsonOptions),
            new System.Text.UTF8Encoding(false));
    }

    private sealed record CommandPaletteProbeCheck(string Name, bool Passed, string Detail);
    private sealed record CommandPaletteProbeReport(
        string SchemaVersion,
        string Result,
        IReadOnlyList<CommandPaletteProbeCheck> Checks);
}
