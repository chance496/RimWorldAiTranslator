namespace RimWorldAiTranslator.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show("RimWorld AI Translator를 시작하지 못했습니다.\n\n" + ex.Message, "RimWorld AI Translator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
