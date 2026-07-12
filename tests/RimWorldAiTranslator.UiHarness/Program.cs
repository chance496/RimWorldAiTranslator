using RimWorldAiTranslator.App;

namespace RimWorldAiTranslator.UiHarness;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.Combine(Path.GetTempPath(), "RimWorldAiTranslator-ui-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Application.Run(new MainForm(root));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "RimWorldAiTranslator-ui-harness-error.txt"), ex.ToString());
            throw;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
