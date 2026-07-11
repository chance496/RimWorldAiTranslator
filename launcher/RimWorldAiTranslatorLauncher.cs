using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [STAThread]
    private static int Main(string[] arguments)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

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
                "Start-RimWorldAiReviewGui.ps1 \ud30c\uc77c\uc744 \ucc3e\uc744 \uc218 \uc5c6\uc2b5\ub2c8\ub2e4.\nEXE\uc640 \uc2a4\ud06c\ub9bd\ud2b8\ub97c \uac19\uc740 \ud3f4\ub354\uc5d0 \ub450\uc138\uc694.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }
        if (!File.Exists(powershellPath))
        {
            MessageBox.Show(
                "Windows PowerShell\uc744 \uc2dc\uc2a4\ud15c \ud3f4\ub354\uc5d0\uc11c \ucc3e\uc744 \uc218 \uc5c6\uc2b5\ub2c8\ub2e4.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 3;
        }

        try
        {
            string childArguments = "-WindowStyle Hidden -NoProfile -STA -ExecutionPolicy Bypass -File " + Quote(scriptPath);
            foreach (string argument in arguments) childArguments += " " + Quote(argument);
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = childArguments,
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process process = Process.Start(startInfo))
            {
                if (process == null) throw new InvalidOperationException("PowerShell process could not be started.");

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                using (StartupForm startup = new StartupForm())
                {
                    startup.Show();
                    startup.Refresh();
                    while (!process.HasExited)
                    {
                        process.Refresh();
                        IntPtr mainWindow = process.MainWindowHandle;
                        if (mainWindow != IntPtr.Zero && IsWindowVisible(mainWindow)) break;
                        Application.DoEvents();
                        Thread.Sleep(35);
                    }
                    startup.Close();
                }

                process.WaitForExit();
                string output = outputTask.Result;
                string error = errorTask.Result;
                if (process.ExitCode != 0)
                {
                    string details = (error + Environment.NewLine + output).Trim();
                    if (details.Length > 4000) details = details.Substring(0, 4000);
                    MessageBox.Show(
                        "\ubc88\uc5ed\uae30\ub97c \uc2dc\uc791\ud558\uc9c0 \ubabb\ud588\uc2b5\ub2c8\ub2e4.\n\n" + details,
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
                "GUI \uc2e4\ud589\uc5d0 \uc2e4\ud328\ud588\uc2b5\ub2c8\ub2e4.\n\n" + ex.Message,
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string Quote(string value)
    {
        if (value == null) return "\"\"";
        StringBuilder result = new StringBuilder(value.Length + 8);
        result.Append('\"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '\"')
            {
                result.Append('\\', (backslashes * 2) + 1);
                result.Append('\"');
                backslashes = 0;
                continue;
            }
            if (backslashes > 0)
            {
                result.Append('\\', backslashes);
                backslashes = 0;
            }
            result.Append(character);
        }
        if (backslashes > 0) result.Append('\\', backslashes * 2);
        result.Append('\"');
        return result.ToString();
    }

    private sealed class StartupForm : Form
    {
        public StartupForm()
        {
            Text = "RimWorld AI Translator";
            ClientSize = new Size(430, 116);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(244, 243, 238);

            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 54,
                BackColor = Color.FromArgb(34, 39, 35)
            };
            Label title = new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(18, 12, 390, 30),
                Text = "RimWorld AI Translator",
                ForeColor = Color.White,
                Font = new Font("Malgun Gothic", 11.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Panel accent = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 3,
                BackColor = Color.FromArgb(177, 132, 73)
            };
            header.Controls.Add(title);
            header.Controls.Add(accent);

            Label status = new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(18, 67, 394, 20),
                Text = "\ud504\ub85c\uc81d\ud2b8\uc640 \ubc88\uc5ed \uae30\ub85d\uc744 \uc900\ube44\ud558\ub294 \uc911...",
                ForeColor = Color.FromArgb(74, 70, 63),
                Font = new Font("Malgun Gothic", 9f, FontStyle.Regular)
            };
            ProgressBar progress = new ProgressBar
            {
                Bounds = new Rectangle(18, 92, 394, 7),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 24
            };

            Controls.Add(header);
            Controls.Add(status);
            Controls.Add(progress);
        }
    }
}
