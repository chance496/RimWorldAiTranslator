using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string scriptPath = Path.Combine(baseDir, "Start-RimWorldAiTranslatorGui.ps1");
        if (!File.Exists(scriptPath))
        {
            MessageBox.Show(
                "Start-RimWorldAiTranslatorGui.ps1 파일을 찾을 수 없습니다.\nEXE와 스크립트를 같은 폴더에 두세요.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-WindowStyle Hidden -NoProfile -STA -ExecutionPolicy Bypass -File " + Quote(scriptPath),
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "GUI 실행에 실패했습니다.\n\n" + ex.Message,
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
