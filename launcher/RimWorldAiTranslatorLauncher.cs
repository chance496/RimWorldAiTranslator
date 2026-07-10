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
        string scriptPath = Path.Combine(baseDir, "Start-RimWorldAiReviewGui.ps1");
        string powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(scriptPath))
        {
            MessageBox.Show(
                "Start-RimWorldAiReviewGui.ps1 파일을 찾을 수 없습니다.\nEXE와 스크립트를 같은 폴더에 두세요.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }
        if (!File.Exists(powershellPath))
        {
            MessageBox.Show(
                "Windows PowerShell을 시스템 폴더에서 찾을 수 없습니다.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 3;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = "-WindowStyle Hidden -NoProfile -STA -ExecutionPolicy Bypass -File " + Quote(scriptPath),
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("PowerShell process could not be started.");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();

                string output = outputTask.Result;
                string error = errorTask.Result;
                if (process.ExitCode != 0)
                {
                    string details = (error + Environment.NewLine + output).Trim();
                    if (details.Length > 4000)
                    {
                        details = details.Substring(0, 4000);
                    }

                    MessageBox.Show(
                        "The translator could not be started.\n\n" + details,
                        "RimWorld AI Translator",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return process.ExitCode;
                }
            }

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
