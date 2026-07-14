using RimWorldAiTranslator.Core.Models;
using System.Runtime.CompilerServices;

namespace RimWorldAiTranslator.App;

internal sealed record ThemePalette(
    Color Background,
    Color Surface,
    Color SurfaceAlt,
    Color Header,
    Color HeaderText,
    Color Text,
    Color Muted,
    Color Border,
    Color Accent,
    Color AccentText,
    Color Success,
    Color Danger,
    Color Warning,
    Color Selection,
    bool Dark);

internal static class ThemeManager
{
    private sealed class FontBaseline(float size) { public float Size { get; } = size; }
    private static readonly ConditionalWeakTable<Control, FontBaseline> FontBaselines = new();
    public static float CurrentTextSize { get; private set; } = 10f;
    public static ThemePalette Current { get; private set; } = new(
        Color.FromArgb(242, 244, 245), Color.FromArgb(251, 252, 252), Color.FromArgb(230, 234, 236),
        Color.FromArgb(32, 38, 42), Color.White, Color.FromArgb(32, 39, 43), Color.FromArgb(89, 102, 109),
        Color.FromArgb(184, 193, 198), Color.FromArgb(53, 115, 138), Color.White,
        Color.FromArgb(61, 122, 89), Color.FromArgb(166, 75, 75), Color.FromArgb(169, 111, 47),
        Color.FromArgb(220, 233, 237), false);
    public static ThemePalette Create(AppSettingsDocument settings)
    {
        var dark = settings.ThemeMode.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            || settings.ThemeMode.Equals("System", StringComparison.OrdinalIgnoreCase) && IsSystemDark();
        if (settings.HighContrast || SystemInformation.HighContrast)
        {
            // The accessibility palette is physically dark regardless of the saved
            // light/dark preference. Keep Dark=true so every downstream contrast
            // choice (diff highlights, warnings and selection text) follows the
            // actual black surfaces when Windows high contrast is active.
            return Palette("#000000", "#000000", "#202020", "#000000", "#FFFFFF", "#FFFFFF", "#E6E6E6", "#FFFFFF", "#FFD166", "#000000", "#76E39B", "#FF7B72", "#FFD166", "#343434", true);
        }
        var key = $"{settings.DesignPreset}-{(dark ? "Dark" : "Light")}".ToLowerInvariant();
        return key switch
        {
            "professional-dark" => Palette("#161B1E", "#1D2428", "#303A3F", "#111619", "#F1F4F5", "#F1F4F5", "#B6C0C5", "#4A565C", "#5AA0B7", "#0E181C", "#66AB7E", "#D16D68", "#D7A457", "#2E444C", true),
            "scifi-light" => Palette("#EEF2F3", "#FFFFFF", "#DCE6E8", "#172329", "#F5F2E9", "#1D292E", "#5B6C72", "#AFC0C5", "#087F99", "#FFFFFF", "#27866B", "#B44B50", "#B97A29", "#D2EEF2", false),
            "scifi-dark" => Palette("#0E1519", "#131E23", "#263940", "#081014", "#EAF7F9", "#EAF7F9", "#A8BDC2", "#3C5961", "#24BCD2", "#071416", "#49B997", "#E06A70", "#E0AD52", "#183D46", true),
            "vivid-light" => Palette("#F3F5F4", "#FFFFFF", "#E6EBE9", "#263238", "#F5F2E9", "#242B2E", "#626D70", "#B8C1C0", "#D85C54", "#FFFFFF", "#27846B", "#B64049", "#B77921", "#F3DED9", false),
            "vivid-dark" => Palette("#191D1E", "#22282A", "#394244", "#111617", "#F5F3EF", "#F5F3EF", "#BDC4C3", "#4D595A", "#F0786E", "#1E1110", "#52B596", "#EE7078", "#E2AD55", "#493634", true),
            "studio-light" => Palette("#F4F5F3", "#FFFFFF", "#E8ECEA", "#2B3030", "#F5F2E9", "#292D2C", "#666E6B", "#BDC3C0", "#A65F6B", "#FFFFFF", "#3E8066", "#A84F56", "#B07A38", "#F1E2E4", false),
            "studio-dark" => Palette("#1A1D1D", "#232827", "#39413F", "#121515", "#F2F2EE", "#F2F2EE", "#BFC4C1", "#4E5754", "#D28490", "#211214", "#67AA89", "#D36E73", "#D9A65C", "#49393D", true),
            "frontier-light" => Palette("#EFEEE8", "#FAF9F4", "#E3E7E1", "#252A27", "#F5F2E9", "#20251F", "#636C65", "#B7BDB6", "#B78342", "#FFFFFF", "#3E7A55", "#A74A45", "#A96E28", "#DCE8DF", false),
            "frontier-dark" => Palette("#151A18", "#1D2421", "#323A35", "#101512", "#F3F0E7", "#F3F0E7", "#B8B9AF", "#4A524C", "#C08B46", "#17130E", "#5FA577", "#C7655F", "#D6A34D", "#34443B", true),
            _ => Palette("#F2F4F5", "#FBFCFC", "#E6EAEC", "#20262A", "#F5F2E9", "#20272B", "#59666D", "#B8C1C6", "#35738A", "#FFFFFF", "#3D7A59", "#A64B4B", "#A96F2F", "#DCE9ED", false)
        };

        static ThemePalette Palette(string background, string surface, string surfaceAlt, string header, string headerText,
            string text, string muted, string border, string accent, string accentText, string success, string danger,
            string warning, string selection, bool isDark) => new(
                ColorTranslator.FromHtml(background), ColorTranslator.FromHtml(surface), ColorTranslator.FromHtml(surfaceAlt),
                ColorTranslator.FromHtml(header), ColorTranslator.FromHtml(headerText), ColorTranslator.FromHtml(text),
                ColorTranslator.FromHtml(muted), ColorTranslator.FromHtml(border), ColorTranslator.FromHtml(accent),
                ColorTranslator.FromHtml(accentText), ColorTranslator.FromHtml(success), ColorTranslator.FromHtml(danger),
                ColorTranslator.FromHtml(warning), ColorTranslator.FromHtml(selection), isDark);
    }

    public static void Apply(Control root, ThemePalette theme, float fontSize)
    {
        Current = theme;
        CurrentTextSize = fontSize;
        ApplyControl(root, theme, fontSize);
        foreach (Control child in root.Controls) Apply(child, theme, fontSize);
    }

    private static void ApplyControl(Control control, ThemePalette theme, float fontSize)
    {
        var baseline = FontBaselines.GetValue(control, item => new FontBaseline(item.Font.Size));
        control.Font = new Font("Malgun Gothic", Math.Max(8f, baseline.Size + fontSize - 10f), control.Font.Style, GraphicsUnit.Point);
        switch (control)
        {
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = theme.Border;
                button.FlatAppearance.BorderSize = 1;
                if (!button.Enabled)
                {
                    button.BackColor = theme.SurfaceAlt;
                    button.ForeColor = ReadableForeground(theme.SurfaceAlt, theme.Muted, theme.Text);
                    button.FlatAppearance.BorderColor = ReadableForeground(theme.SurfaceAlt, theme.Border, button.ForeColor);
                }
                else if (button.Tag as string == "primary") { button.BackColor = theme.Accent; button.ForeColor = ReadableForeground(theme.Accent, theme.AccentText, theme.Text); }
                else if (button.Tag as string == "success") { button.BackColor = theme.Success; button.ForeColor = ReadableForeground(theme.Success, Color.White, theme.Text); }
                else if (button.Tag as string == "danger") { button.BackColor = theme.Danger; button.ForeColor = ReadableForeground(theme.Danger, Color.White, theme.Text); }
                else if (button.Tag as string == "header-button") { button.BackColor = theme.Header; button.ForeColor = theme.HeaderText; }
                else if (button.Tag as string == "accent-soft") { button.BackColor = theme.Selection; button.ForeColor = theme.Text; }
                else if (button.Tag as string == "warm-soft") { button.BackColor = theme.SurfaceAlt; button.ForeColor = theme.Text; }
                else { button.BackColor = theme.Surface; button.ForeColor = theme.Text; }
                if (button.Enabled)
                    button.FlatAppearance.BorderColor = ReadableForeground(
                        button.BackColor,
                        theme.Border,
                        button.ForeColor);
                break;
            case TextBoxBase text when control.Tag as string == "readonly":
                text.BackColor = theme.SurfaceAlt;
                text.ForeColor = theme.Text;
                break;
            case TextBoxBase text when control.Tag as string == "log":
                text.BackColor = Color.FromArgb(17, 23, 29);
                text.ForeColor = Color.FromArgb(214, 224, 234);
                break;
            case TextBoxBase text:
                text.BackColor = theme.Surface;
                text.ForeColor = theme.Text;
                text.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox combo:
                combo.BackColor = theme.Surface;
                combo.ForeColor = theme.Text;
                break;
            case ListView list:
                list.BackColor = theme.Surface;
                list.ForeColor = theme.Text;
                break;
            case TabControl:
                control.BackColor = theme.Surface;
                control.ForeColor = theme.Text;
                break;
            case Form when control.Tag as string == "surface":
                control.BackColor = theme.Surface;
                control.ForeColor = theme.Text;
                break;
            case Label when control.Tag as string == "accent-label":
                control.ForeColor = ReadableForeground(EffectiveBackground(control, theme.Background), theme.Accent, theme.Text);
                break;
            case Label when control.Tag as string == "success-label":
                control.ForeColor = ReadableForeground(EffectiveBackground(control, theme.Background), theme.Success, theme.Text);
                break;
            case Label when control.Tag as string == "header-text":
                control.ForeColor = theme.HeaderText;
                break;
            case Label when control.Tag as string == "header-warning":
                control.ForeColor = ReadableForeground(theme.Header, theme.Warning, theme.HeaderText);
                break;
            case Label when control.Tag as string == "header-danger":
                control.ForeColor = ReadableForeground(theme.Header, theme.Danger, theme.HeaderText);
                break;
            case Label when control.Tag as string == "header-muted":
                control.ForeColor = theme.Dark ? Color.FromArgb(184, 185, 175) : Color.FromArgb(196, 201, 194);
                break;
            case Label when control.Tag as string == "muted":
                control.ForeColor = ReadableForeground(EffectiveBackground(control, theme.Background), theme.Muted, theme.Text);
                break;
            case Label when control.Tag as string == "faint":
                // "Faint" text is still user-facing status text. Keep it at the
                // palette's readable secondary-text contrast instead of creating a
                // low-contrast third tier that disappears at small Korean font sizes.
                control.ForeColor = ReadableForeground(EffectiveBackground(control, theme.Background), theme.Muted, theme.Text);
                break;
            case Label when control.Tag as string == "warning":
                control.ForeColor = ReadableForeground(EffectiveBackground(control, theme.Background), theme.Warning, theme.Text);
                break;
            case Label when control.Tag as string == "danger-text":
                control.ForeColor = ReadableForeground(EffectiveBackground(control, theme.Background), theme.Danger, theme.Text);
                break;
            case Label when control.Tag as string == "selection":
                control.BackColor = theme.Selection;
                control.ForeColor = ReadableForeground(theme.Selection, theme.Accent, theme.Text);
                break;
            case Label:
                control.ForeColor = theme.Text;
                break;
            case Panel panel when panel.Tag as string == "accent":
                panel.BackColor = theme.Accent;
                break;
            case Panel panel when panel.Tag as string == "divider":
                panel.BackColor = theme.Border;
                break;
            case Panel panel when panel.Tag as string == "progress-track":
                panel.BackColor = theme.SurfaceAlt;
                break;
            case Panel panel when panel.Tag as string == "surface-alt":
                panel.BackColor = theme.SurfaceAlt;
                panel.ForeColor = theme.Text;
                break;
            case Panel panel when panel.Tag as string == "selection-panel":
                panel.BackColor = theme.Selection;
                panel.ForeColor = theme.Text;
                break;
            case CheckBox when control.Tag as string == "header-check":
                control.BackColor = theme.Header;
                control.ForeColor = theme.HeaderText;
                break;
            case Panel panel when panel.Tag as string == "header":
                panel.BackColor = theme.Header;
                panel.ForeColor = theme.HeaderText;
                break;
            case Panel panel when panel.Tag as string == "surface":
                panel.BackColor = theme.Surface;
                panel.ForeColor = theme.Text;
                break;
            case TabPage page when page.Tag as string == "surface":
                page.BackColor = theme.Surface;
                page.ForeColor = theme.Text;
                break;
            default:
                control.BackColor = theme.Background;
                control.ForeColor = theme.Text;
                break;
        }
    }

    internal static Color ReadableForeground(Color background, params Color[] preferred)
    {
        var candidates = preferred.Concat([Color.Black, Color.White]).Distinct().ToArray();
        foreach (var candidate in candidates)
            if (ContrastRatio(background, candidate) >= 4.5) return candidate;
        return candidates
            .OrderByDescending(candidate => ContrastRatio(background, candidate))
            .First();
    }

    private static Color EffectiveBackground(Control control, Color fallback)
    {
        if (control.BackColor != Color.Transparent && control.BackColor.A > 0) return control.BackColor;
        return control.Parent?.BackColor is { A: > 0 } parent ? parent : fallback;
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                var normalized = value / 255d;
                return normalized <= 0.04045
                    ? normalized / 12.92
                    : Math.Pow((normalized + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }

        var high = Math.Max(Luminance(first), Luminance(second));
        var low = Math.Min(Luminance(first), Luminance(second));
        return (high + 0.05) / (low + 0.05);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme") ?? 1, System.Globalization.CultureInfo.InvariantCulture) == 0;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"System theme detection fell back to light ({exception.GetType().Name}).");
            return false;
        }
    }
}
