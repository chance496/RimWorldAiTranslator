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
    public static ThemePalette Create(AppSettingsDocument settings)
    {
        var dark = settings.ThemeMode.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            || settings.ThemeMode.Equals("System", StringComparison.OrdinalIgnoreCase) && IsSystemDark();
        var accent = settings.DesignPreset.ToLowerInvariant() switch
        {
            "scifi" => Color.FromArgb(43, 151, 181),
            "vivid" => Color.FromArgb(194, 86, 66),
            "studio" => Color.FromArgb(73, 115, 156),
            "frontier" => Color.FromArgb(174, 126, 65),
            _ => Color.FromArgb(48, 126, 151)
        };
        if (settings.HighContrast)
        {
            return dark
                ? new ThemePalette(Color.Black, Color.FromArgb(15, 15, 15), Color.FromArgb(28, 28, 28), Color.Black, Color.White, Color.White, Color.Gainsboro, Color.White, Color.Cyan, Color.Black, Color.Lime, Color.Red, Color.Yellow, Color.FromArgb(40, 80, 80), true)
                : new ThemePalette(Color.White, Color.White, Color.FromArgb(242, 242, 242), Color.Black, Color.White, Color.Black, Color.FromArgb(48, 48, 48), Color.Black, Color.FromArgb(0, 92, 160), Color.White, Color.FromArgb(0, 110, 45), Color.FromArgb(170, 20, 20), Color.FromArgb(155, 100, 0), Color.FromArgb(218, 236, 247), false);
        }
        return dark
            ? new ThemePalette(Color.FromArgb(22, 28, 31), Color.FromArgb(30, 37, 41), Color.FromArgb(38, 47, 52), Color.FromArgb(24, 30, 33), Color.WhiteSmoke, Color.FromArgb(235, 239, 237), Color.FromArgb(165, 178, 176), Color.FromArgb(75, 88, 91), accent, Color.White, Color.FromArgb(64, 157, 102), Color.FromArgb(180, 70, 70), Color.FromArgb(202, 145, 58), Color.FromArgb(45, 73, 82), true)
            : new ThemePalette(Color.FromArgb(241, 243, 241), Color.FromArgb(252, 252, 249), Color.FromArgb(231, 235, 231), Color.FromArgb(31, 38, 40), Color.White, Color.FromArgb(30, 37, 37), Color.FromArgb(99, 109, 106), Color.FromArgb(194, 201, 196), accent, Color.White, Color.FromArgb(43, 139, 79), Color.FromArgb(166, 67, 64), Color.FromArgb(177, 123, 52), Color.FromArgb(219, 235, 239), false);
    }

    public static void Apply(Control root, ThemePalette theme, float fontSize)
    {
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
                if (button.Tag as string == "primary") { button.BackColor = theme.Accent; button.ForeColor = theme.AccentText; }
                else if (button.Tag as string == "success") { button.BackColor = theme.Success; button.ForeColor = Color.White; }
                else if (button.Tag as string == "danger") { button.BackColor = theme.Danger; button.ForeColor = Color.White; }
                else { button.BackColor = theme.Surface; button.ForeColor = theme.Text; }
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
            case Label:
                control.ForeColor = theme.Text;
                break;
            case Panel panel when panel.Tag as string == "header":
                panel.BackColor = theme.Header;
                panel.ForeColor = theme.HeaderText;
                break;
            case Panel panel when panel.Tag as string == "surface":
                panel.BackColor = theme.Surface;
                panel.ForeColor = theme.Text;
                break;
            default:
                control.BackColor = theme.Background;
                control.ForeColor = theme.Text;
                break;
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme") ?? 1) == 0;
        }
        catch { return false; }
    }
}
