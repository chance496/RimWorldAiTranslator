using System.Text.Json;
using System.Runtime.InteropServices;
using RimWorldAiTranslator.App.Controls;

namespace RimWorldAiTranslator.UiHarness;

internal static class SnapshotCapture
{
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const string FixturePathReplacement = @"C:\Fixture\review";
    private static readonly JsonSerializerOptions AccessibilityJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task WaitForReadyAsync(Form form, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            var loading = Descendants(form).OfType<LoadingOverlay>().FirstOrDefault();
            var workspaceLoading = Descendants(form).OfType<WorkspaceLoadCover>().FirstOrDefault();
            var workspace = Descendants(form).OfType<ReviewWorkspaceControl>().FirstOrDefault(control => control.Visible);
            if (form.Opacity >= 1
                && loading is { Visible: false }
                && workspaceLoading is { Visible: false }
                && workspace is not { UiDataWorkPendingForTesting: true }) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The C# UI did not reach its stable first screen before the snapshot timeout.");
    }

    public static void SelectTopLevelTab(Form form, string tab)
    {
        var label = tab.ToLowerInvariant() switch
        {
            "activity" => "\uD65C\uB3D9",
            "settings" => "\uC124\uC815",
            _ => "\uD504\uB85C\uC81D\uD2B8"
        };
        var button = Descendants(form).OfType<Button>().FirstOrDefault(item => item.Text == label)
            ?? throw new InvalidOperationException($"Top-level tab was not found: {label}");
        button.PerformClick();
        form.PerformLayout();
        form.Invalidate(true);
        form.Update();
        Application.DoEvents();
    }

    public static async Task OpenFirstProjectAsync(Form form, TimeSpan timeout)
    {
        var open = Descendants(form).OfType<Button>()
            .FirstOrDefault(item => item.Visible && item.Text == "\uC5F4\uAE30")
            ?? throw new InvalidOperationException("The first project open button was not found.");
        open.PerformClick();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            var loading = Descendants(form).OfType<LoadingOverlay>().FirstOrDefault();
            var workspaceLoading = Descendants(form).OfType<WorkspaceLoadCover>().FirstOrDefault();
            var workspace = Descendants(form).OfType<ReviewWorkspaceControl>().FirstOrDefault();
            if (workspace is { Visible: true }
                && loading is { Visible: false }
                && workspaceLoading is { Visible: false }
                && !workspace.UiDataWorkPendingForTesting) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The C# review workspace did not open before the snapshot timeout.");
    }

    public static void SelectWorkspaceTab(Form form, string tab)
    {
        var label = tab.ToLowerInvariant() switch
        {
            "terms" => "\uC6A9\uC5B4",
            "memo" => "\uBA54\uBAA8",
            "rmk" => "RMK",
            "issues" or "quality" => "\uD488\uC9C8",
            "log" => "\uB85C\uADF8",
            _ => "\uBE44\uAD50"
        };
        var page = Descendants(form).OfType<TabPage>().FirstOrDefault(item => item.Text == label);
        if (page?.Parent is TabControl tabs)
        {
            tabs.SelectedTab = page;
            tabs.Update();
            Application.DoEvents();
        }
    }

    public static void Save(Form form, string path, Form? dialog = null, bool allowSyntheticFixtureText = false)
    {
        var labelReplacements = new List<LabelTextReplacement>();
        try
        {
            ReplacePathLikeLabels(form, dialog, labelReplacements);
            SaveCore(form, path, dialog, labelReplacements.Select(replacement => replacement.Path).ToArray(), allowSyntheticFixtureText);
        }
        finally
        {
            for (var index = labelReplacements.Count - 1; index >= 0; index--)
                labelReplacements[index].Control.Text = labelReplacements[index].OriginalText;
        }
    }

    private static void SaveCore(
        Form form,
        string path,
        Form? dialog,
        IReadOnlyList<string> labelPathSubstitutions,
        bool allowSyntheticFixtureText)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var restoreDialog = dialog is { Visible: true };
        var dialogLocation = restoreDialog ? dialog!.Location : Point.Empty;
        var dialogActiveControl = restoreDialog ? dialog!.ActiveControl : null;
        if (restoreDialog)
        {
            dialog!.Hide();
            Application.DoEvents();
        }
        form.Activate();
        form.BringToFront();
        form.PerformLayout();
        form.Invalidate(true);
        form.Update();
        Application.DoEvents();

        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        bitmap.SetResolution(Math.Max(96, form.DeviceDpi), Math.Max(96, form.DeviceDpi));
        using (var frame = new Bitmap(form.Width, form.Height))
        {
            CaptureWindow(form, frame);
            var clientOrigin = form.PointToScreen(Point.Empty);
            var clientOffset = new Point(clientOrigin.X - form.Left, clientOrigin.Y - form.Top);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(form.BackColor);
            graphics.DrawImage(
                frame,
                new Rectangle(Point.Empty, bitmap.Size),
                new Rectangle(clientOffset, bitmap.Size),
                GraphicsUnit.Pixel);
        }
        var overlay = Descendants(form).OfType<LoadingOverlay>().FirstOrDefault(control => control.Visible);
        if (overlay is not null) CompositeControl(form, overlay, bitmap);
        var workspaceCover = Descendants(form).OfType<WorkspaceLoadCover>().FirstOrDefault(control => control.Visible);
        if (workspaceCover is not null) CompositeControl(form, workspaceCover, bitmap);
        var visualRedactions = RedactSensitiveVisibleData(form, "owner", bitmap, Point.Empty, allowSyntheticFixtureText);
        if (restoreDialog)
        {
            dialog!.Show(form);
            dialog.Location = dialogLocation;
            dialog.Activate();
            dialog.BringToFront();
            dialogActiveControl?.Focus();
            dialog.PerformLayout();
            dialog.Invalidate(true);
            dialog.Update();
            Application.DoEvents();
            CompositeDialog(form, dialog, bitmap);
            var dialogOrigin = OwnerClientLocation(form, dialog);
            visualRedactions.AddRange(RedactSensitiveVisibleData(dialog, "dialog", bitmap, dialogOrigin, allowSyntheticFixtureText));
        }
        bitmap.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);

        var rows = CaptureTree(form, "owner").ToList();
        if (dialog is not null) rows.AddRange(CaptureTree(dialog, "dialog"));
        var accessibilityPath = Path.ChangeExtension(fullPath, ".accessibility.json");
        File.WriteAllText(
            accessibilityPath,
            JsonSerializer.Serialize(rows, AccessibilityJsonOptions),
            new System.Text.UTF8Encoding(false));

        var missingAccessibleNames = rows
            .Where(row => row.Visible && row.Interactive && string.IsNullOrWhiteSpace(row.Name))
            .Select(row => row.Path)
            .ToArray();
        var clippedControls = rows
            .Where(row => row.Visible && row.Clipped && (row.Interactive || row.TextPresent || row.ChildCount > 0))
            .Select(row => row.Path)
            .ToArray();
        var decorativeClipping = rows
            .Where(row => row.Visible && row.Clipped && !row.Interactive && !row.TextPresent && row.ChildCount == 0)
            .Select(row => row.Path)
            .ToArray();
        var clippedText = rows
            .Where(row => row.Visible && row.TextClipped)
            .Select(row => row.Path)
            .ToArray();
        var evidence = new
        {
            schemaVersion = 2,
            capturedAtUtc = DateTimeOffset.UtcNow,
            image = Path.GetFileName(fullPath),
            target = new
            {
                formType = form.GetType().FullName,
                clientWidth = form.ClientSize.Width,
                clientHeight = form.ClientSize.Height,
                windowState = form.WindowState.ToString(),
                deviceDpi = form.DeviceDpi,
                dialogPresent = dialog is not null
            },
            summary = new
            {
                controlCount = rows.Count,
                visibleControlCount = rows.Count(row => row.Visible),
                interactiveControlCount = rows.Count(row => row.Visible && row.Interactive),
                missingAccessibleNameCount = missingAccessibleNames.Length,
                clippedControlCount = clippedControls.Length,
                decorativeClippingCount = decorativeClipping.Length,
                textClippedControlCount = clippedText.Length,
                labelPathSubstitutionCount = labelPathSubstitutions.Count,
                visualRedactionCount = visualRedactions.Count
            },
            checks = new
            {
                accessibleNamesPassed = missingAccessibleNames.Length == 0,
                controlClippingPassed = clippedControls.Length == 0,
                textClippingPassed = clippedText.Length == 0,
                missingAccessibleNames,
                clippedControls,
                decorativeClipping,
                clippedText,
                labelPathSubstitutions,
                visualRedactions
            },
            privacy = new
            {
                fixturePolicy = "synthetic-only",
                inputValuesRecorded = false,
                dynamicVisibleTextRecorded = allowSyntheticFixtureText,
                syntheticFixtureTextVisible = allowSyntheticFixtureText,
                hostPathsRecorded = false,
                fixturePathReplacement = FixturePathReplacement,
                note = allowSyntheticFixtureText
                    ? "Only allowlisted run-owned synthetic fixture text is left visible. Path-like content and every non-allowlisted input value remain pixel-masked."
                    : "Path-like Label text is replaced before drawing and restored in finally. Other path-like visible content and non-empty input values are pixel-masked."
            }
        };
        File.WriteAllText(
            Path.ChangeExtension(fullPath, ".evidence.json"),
            JsonSerializer.Serialize(evidence, AccessibilityJsonOptions),
            new System.Text.UTF8Encoding(false));

        var failures = new List<string>();
        if (missingAccessibleNames.Length > 0)
            failures.Add($"missing accessible names: {string.Join(", ", missingAccessibleNames)}");
        if (clippedControls.Length > 0)
            failures.Add($"clipped controls: {string.Join(", ", clippedControls)}");
        if (clippedText.Length > 0)
            failures.Add($"clipped text: {string.Join(", ", clippedText)}");
        if (failures.Count > 0)
            throw new InvalidDataException($"Snapshot UI audit failed ({string.Join("; ", failures)}). Evidence was written next to the PNG.");
    }

    private static void ReplacePathLikeLabels(
        Form form,
        Form? dialog,
        List<LabelTextReplacement> replacements)
    {
        ReplacePathLikeLabels(form, $"owner/{form.GetType().Name}[0]", replacements);
        if (dialog is not null)
            ReplacePathLikeLabels(dialog, $"dialog/{dialog.GetType().Name}[0]", replacements);
    }

    private static void ReplacePathLikeLabels(
        Control control,
        string path,
        List<LabelTextReplacement> replacements)
    {
        if (control is Label label && LooksSensitive(label.Text))
        {
            replacements.Add(new LabelTextReplacement(label, label.Text, path));
            label.Text = FixturePathReplacement;
        }

        for (var index = 0; index < control.Controls.Count; index++)
        {
            var child = control.Controls[index];
            ReplacePathLikeLabels(
                child,
                $"{path}/{child.GetType().Name}[{index}]",
                replacements);
        }
    }

    private static Point OwnerClientLocation(Form owner, Form dialog) =>
        owner.PointToClient(dialog.PointToScreen(Point.Empty));

    private static List<string> RedactSensitiveVisibleData(
        Control root,
        string scope,
        Bitmap target,
        Point rootOffset,
        bool allowSyntheticFixtureText)
    {
        var redactions = new List<string>();
        using var graphics = Graphics.FromImage(target);
        var rootPath = $"{scope}/{root.GetType().Name}[0]";
        RedactControl(root, root, rootPath, target, graphics, rootOffset, redactions, allowSyntheticFixtureText);
        return redactions;
    }

    private static void RedactControl(
        Control root,
        Control control,
        string path,
        Bitmap target,
        Graphics graphics,
        Point rootOffset,
        List<string> redactions,
        bool allowSyntheticFixtureText)
    {
        if (control.Visible && RequiresVisualRedaction(control, allowSyntheticFixtureText))
        {
            var location = root.PointToClient(control.PointToScreen(Point.Empty));
            location.Offset(rootOffset);
            var bounds = Rectangle.Intersect(
                new Rectangle(location, control.Size),
                new Rectangle(Point.Empty, target.Size));
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                using var background = new SolidBrush(Color.FromArgb(43, 49, 53));
                graphics.FillRectangle(background, bounds);
                TextRenderer.DrawText(
                    graphics,
                    "[redacted]",
                    SystemFonts.MessageBoxFont,
                    bounds,
                    Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                redactions.Add(path);
            }
        }

        for (var index = 0; index < control.Controls.Count; index++)
        {
            var child = control.Controls[index];
            RedactControl(
                root,
                child,
                $"{path}/{child.GetType().Name}[{index}]",
                target,
                graphics,
                rootOffset,
                redactions,
                allowSyntheticFixtureText);
        }
    }

    private static bool RequiresVisualRedaction(Control control, bool allowSyntheticFixtureText)
    {
        var visibleText = control.Text ?? string.Empty;
        if (control is TextBoxBase && visibleText.Length > 0)
            return !allowSyntheticFixtureText || !IsAllowlistedSyntheticFixtureText(visibleText);
        if (LooksSensitive(visibleText)
            && (control is not Label || !visibleText.Equals(FixturePathReplacement, StringComparison.Ordinal))) return true;
        if (control is ListBox listBox)
            return listBox.Items.Cast<object>().Any(item => LooksSensitive(listBox.GetItemText(item) ?? string.Empty));
        if (control is ListView listView) return ContainsSensitiveListViewText(listView);
        if (control is TreeView treeView) return ContainsSensitiveTreeNode(treeView.Nodes);
        if (control is DataGridView grid)
            return grid.Rows.Cast<DataGridViewRow>()
                .Where(row => row.Displayed)
                .SelectMany(row => row.Cells.Cast<DataGridViewCell>())
                .Any(cell => LooksSensitive(Convert.ToString(cell.FormattedValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
        return false;
    }

    private static bool IsAllowlistedSyntheticFixtureText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || LooksSensitive(value)) return false;
        string[] markers =
        [
            "Synthetic source", "needle source", "Repeated source", "Safe synthetic source",
            "日本語 中文 Ελληνικά emoji", "번역문 ", "로컬 메모리 번역", "Synthetic.Entry.",
            "로컬 번역 메모리", "관련 용어 또는 번역 메모리 없음", "Def Class : Keyed"
        ];
        return markers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
    }

    private static bool ContainsSensitiveListViewText(ListView listView)
    {
        var count = listView.VirtualMode ? listView.VirtualListSize : listView.Items.Count;
        for (var index = 0; index < count; index++)
        {
            var item = listView.Items[index];
            foreach (ListViewItem.ListViewSubItem subItem in item.SubItems)
                if (LooksSensitive(subItem.Text)) return true;
        }
        return false;
    }

    private static bool ContainsSensitiveTreeNode(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            if (LooksSensitive(node.Text) || ContainsSensitiveTreeNode(node.Nodes)) return true;
        }
        return false;
    }

    private static IEnumerable<CapturedControl> CaptureTree(Control root, string scope)
    {
        var rootPath = $"{scope}/{root.GetType().Name}[0]";
        yield return Capture(root, rootPath, null, 0);
        foreach (var row in CaptureChildren(root, rootPath, 1)) yield return row;
    }

    private static IEnumerable<CapturedControl> CaptureChildren(Control parent, string parentPath, int depth)
    {
        for (var index = 0; index < parent.Controls.Count; index++)
        {
            var child = parent.Controls[index];
            var path = $"{parentPath}/{child.GetType().Name}[{index}]";
            yield return Capture(child, path, parentPath, depth);
            foreach (var row in CaptureChildren(child, path, depth + 1)) yield return row;
        }
    }

    private static CapturedControl Capture(Control control, string path, string? parentPath, int depth)
    {
        var name = SanitizeMetadata(control.AccessibleName);
        var description = SanitizeMetadata(control.AccessibleDescription);
        var text = CaptureText(control);
        var textMeasurement = MeasureVisibleText(control);
        return new CapturedControl(
            path,
            parentPath,
            depth,
            control.GetType().FullName ?? control.GetType().Name,
            name.Value,
            name.Redacted,
            description.Value,
            description.Redacted,
            control.AccessibilityObject.Role.ToString(),
            control.AccessibleRole.ToString(),
            text.Value,
            text.Present,
            text.Length,
            text.Redacted,
            new CapturedRectangle(control.Bounds.X, control.Bounds.Y, control.Bounds.Width, control.Bounds.Height),
            control.Parent is null ? null : new CapturedSize(control.Parent.ClientSize.Width, control.Parent.ClientSize.Height),
            control.Visible,
            control.Enabled,
            IsInteractive(control),
            control.Controls.Count,
            control.TabIndex,
            control.TabStop,
            control.Focused,
            control.ContainsFocus,
            control.DeviceDpi,
            IsClipped(control),
            textMeasurement,
            textMeasurement?.Clipped ?? false,
            new CapturedFont(control.Font.Name, control.Font.Size, control.Font.Style.ToString()),
            ColorTranslator.ToHtml(control.ForeColor),
            ColorTranslator.ToHtml(control.BackColor));
    }

    private static SanitizedValue SanitizeMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new SanitizedValue(string.Empty, false);
        if (value.Length > 512 || LooksSensitive(value)) return new SanitizedValue("[redacted]", true);
        return new SanitizedValue(value, false);
    }

    private static CapturedText CaptureText(Control control)
    {
        var value = control.Text ?? string.Empty;
        if (value.Length == 0) return new CapturedText(string.Empty, false, 0, false);
        if (IsContentBearing(control)) return new CapturedText(string.Empty, true, null, true);
        if (control is ButtonBase or TabPage && !LooksSensitive(value) && value.Length <= 160)
            return new CapturedText(value, true, value.Length, false);
        return new CapturedText(string.Empty, true, value.Length, true);
    }

    private static bool LooksSensitive(string value) =>
        value.Contains(":\\", StringComparison.Ordinal)
        || value.Contains(":/", StringComparison.Ordinal)
        || value.StartsWith("\\\\", StringComparison.Ordinal)
        || value.Contains("://", StringComparison.Ordinal);

    private static bool IsContentBearing(Control control) =>
        control is TextBoxBase
            or ListControl
            or ListView
            or TreeView
            or DataGridView
            or UpDownBase;

    private static bool IsInteractive(Control control) =>
        control is ButtonBase
            or TextBoxBase
            or ListControl
            or ListView
            or TreeView
            or DataGridView
            or UpDownBase
            or TrackBar
            or ScrollBar
            or TabControl
            or LinkLabel
            or DateTimePicker
            or MonthCalendar
            or PropertyGrid
            || (control.TabStop && control.CanSelect);

    private static bool IsClipped(Control control)
    {
        if (!control.Visible || control.Parent is null) return false;
        if (control.Parent is ScrollableControl { AutoScroll: true }) return false;
        var parentClient = new Rectangle(Point.Empty, control.Parent.ClientSize);
        return control.Bounds.Left < parentClient.Left
            || control.Bounds.Top < parentClient.Top
            || control.Bounds.Right > parentClient.Right
            || control.Bounds.Bottom > parentClient.Bottom;
    }

    private static CapturedTextMeasurement? MeasureVisibleText(Control control)
    {
        if (!control.Visible || string.IsNullOrWhiteSpace(control.Text) || control.Font is null) return null;

        var availableWidth = control.ClientSize.Width - control.Padding.Horizontal;
        var availableHeight = control.ClientSize.Height - control.Padding.Vertical;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var mode = "single-line";
        switch (control)
        {
            case CheckBox or RadioButton:
                availableWidth -= 22;
                availableHeight -= 4;
                break;
            case ButtonBase:
                availableWidth -= 12;
                availableHeight -= 8;
                break;
            case ComboBox:
                availableWidth -= 30;
                availableHeight -= 6;
                break;
            case Label { AutoSize: false, AutoEllipsis: false }:
                flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
                mode = "word-wrap";
                break;
            default:
                return null;
        }

        if (availableWidth <= 0 || availableHeight <= 0)
            return new CapturedTextMeasurement(availableWidth, availableHeight, 0, 0, mode, true);

        var measured = TextRenderer.MeasureText(
            control.Text,
            control.Font,
            new Size(availableWidth, int.MaxValue),
            flags);
        var clipped = measured.Width > availableWidth + 1 || measured.Height > availableHeight + 1;
        return new CapturedTextMeasurement(
            availableWidth,
            availableHeight,
            measured.Width,
            measured.Height,
            mode,
            clipped);
    }

    private static void CompositeDialog(Form owner, Form dialog, Bitmap target)
    {
        dialog.PerformLayout();
        dialog.Invalidate(true);
        dialog.Update();
        Application.DoEvents();
        using var frame = new Bitmap(dialog.Width, dialog.Height);
        using (var background = Graphics.FromImage(frame)) background.Clear(dialog.BackColor);
        dialog.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
        using (var printed = new Bitmap(dialog.Width, dialog.Height))
        {
            CaptureWindow(dialog, printed);
            var clientOrigin = dialog.PointToScreen(Point.Empty);
            var clientOffset = new Point(clientOrigin.X - dialog.Left, clientOrigin.Y - dialog.Top);
            var clientBounds = new Rectangle(clientOffset, dialog.ClientSize);
            using var clientGraphics = Graphics.FromImage(frame);
            clientGraphics.DrawImage(printed, clientBounds, clientBounds, GraphicsUnit.Pixel);
        }
        var destination = new Rectangle(owner.PointToClient(dialog.PointToScreen(Point.Empty)), frame.Size);
        using var graphics = Graphics.FromImage(target);
        graphics.DrawImageUnscaled(frame, destination.Location);
    }

    private static void CaptureWindow(Form form, Bitmap target)
    {
        using var graphics = Graphics.FromImage(target);
        graphics.Clear(form.BackColor);
        var hdc = graphics.GetHdc();
        var printed = false;
        try
        {
            printed = PrintWindow(form.Handle, hdc, PrintWindowRenderFullContent);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        if (!printed)
            form.DrawToBitmap(target, new Rectangle(Point.Empty, target.Size));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    private static void CompositeControl(Control root, Control control, Bitmap target)
    {
        using var frame = new Bitmap(control.Width, control.Height);
        using (var background = Graphics.FromImage(frame)) background.Clear(control.BackColor);
        control.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
        var location = Point.Empty;
        for (Control? cursor = control; cursor is not null && !ReferenceEquals(cursor, root); cursor = cursor.Parent)
            location.Offset(cursor.Left, cursor.Top);
        using var graphics = Graphics.FromImage(target);
        graphics.DrawImageUnscaled(frame, location);
    }

    public static IEnumerable<Control> DescendantsOf(Control parent) => Descendants(parent);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed record SanitizedValue(string Value, bool Redacted);
    private sealed record LabelTextReplacement(Label Control, string OriginalText, string Path);
    private sealed record CapturedText(string Value, bool Present, int? Length, bool Redacted);
    private sealed record CapturedRectangle(int X, int Y, int Width, int Height);
    private sealed record CapturedSize(int Width, int Height);
    private sealed record CapturedFont(string Name, float Size, string Style);
    private sealed record CapturedTextMeasurement(
        int AvailableWidth,
        int AvailableHeight,
        int MeasuredWidth,
        int MeasuredHeight,
        string Mode,
        bool Clipped);
    private sealed record CapturedControl(
        string Path,
        string? ParentPath,
        int Depth,
        string Type,
        string Name,
        bool NameRedacted,
        string Description,
        bool DescriptionRedacted,
        string Role,
        string DeclaredRole,
        string Text,
        bool TextPresent,
        int? TextLength,
        bool TextRedacted,
        CapturedRectangle Bounds,
        CapturedSize? ParentClientSize,
        bool Visible,
        bool Enabled,
        bool Interactive,
        int ChildCount,
        int TabIndex,
        bool TabStop,
        bool Focused,
        bool ContainsFocus,
        int DeviceDpi,
        bool Clipped,
        CapturedTextMeasurement? TextMeasurement,
        bool TextClipped,
        CapturedFont Font,
        string Foreground,
        string Background);

}
