using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("RimWorld AI Translator")]
[assembly: AssemblyProduct("RimWorld AI Translator")]
[assembly: AssemblyCompany("chance496")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLayeredWindowAttributes(
        IntPtr windowHandle,
        out uint colorKey,
        out byte alpha,
        out uint flags);

    private const uint LayeredWindowAlpha = 0x2;

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
                StartupForm startup = null;
                Stopwatch startupWatch = Stopwatch.StartNew();
                try
                {
                    while (!process.HasExited)
                    {
                        process.Refresh();
                        IntPtr mainWindow = process.MainWindowHandle;
                        if (IsWindowReady(mainWindow)) break;
                        if (startup == null && startupWatch.ElapsedMilliseconds >= 120)
                        {
                            startup = new StartupForm();
                            startup.Show();
                            startup.Update();
                        }
                        Application.DoEvents();
                        Thread.Sleep(35);
                    }
                }
                finally
                {
                    if (startup != null)
                    {
                        startup.Hide();
                        startup.Close();
                        startup.Dispose();
                    }
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

    private static bool IsWindowReady(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindowVisible(windowHandle)) return false;

        uint colorKey;
        byte alpha;
        uint flags;
        if (GetLayeredWindowAttributes(windowHandle, out colorKey, out alpha, out flags) &&
            (flags & LayeredWindowAlpha) != 0 &&
            alpha == 0)
        {
            return false;
        }

        return true;
    }

    private sealed class StartupForm : Form
    {
        private readonly Font titleFont = new Font("Malgun Gothic", 12.5f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font captionFont = new Font("Malgun Gothic", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font statusFont = new Font("Malgun Gothic", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Stopwatch animationWatch = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer animationTimer;

        public StartupForm()
        {
            Text = "RimWorld AI Translator";
            ClientSize = new Size(460, 154);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(245, 244, 239);
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);

            animationTimer = new System.Windows.Forms.Timer { Interval = 40 };
            animationTimer.Tick += OnAnimationTick;
            animationTimer.Start();
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated || !Visible) return;
            Invalidate(new Rectangle(20, 84, ClientSize.Width - 40, 56));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int DropShadow = 0x00020000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= DropShadow;
                return parameters;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            const int trackX = 24;
            const int trackY = 132;
            const int indicatorWidth = 84;
            int trackWidth = ClientSize.Width - 48;
            double phase = (animationWatch.ElapsedMilliseconds % 1800L) / 1800.0;
            double travel = 0.5 - (Math.Cos(phase * Math.PI * 2.0) * 0.5);
            int indicatorX = trackX + (int)Math.Round((trackWidth - indicatorWidth) * travel);
            int dotCount = 1 + (int)((animationWatch.ElapsedMilliseconds / 320L) % 3L);

            using (SolidBrush headerBrush = new SolidBrush(Color.FromArgb(34, 39, 35)))
            using (SolidBrush accentBrush = new SolidBrush(Color.FromArgb(177, 132, 73)))
            using (SolidBrush accentHighlightBrush = new SolidBrush(Color.FromArgb(218, 186, 139)))
            using (SolidBrush trackBrush = new SolidBrush(Color.FromArgb(220, 218, 209)))
            {
                graphics.FillRectangle(headerBrush, 0, 0, ClientSize.Width, 72);
                graphics.FillRectangle(accentBrush, 0, 69, ClientSize.Width, 3);
                graphics.FillRectangle(trackBrush, trackX, trackY, trackWidth, 3);
                graphics.FillRectangle(accentBrush, indicatorX, trackY, indicatorWidth, 3);
                graphics.FillRectangle(accentHighlightBrush, indicatorX + indicatorWidth - 18, trackY, 18, 3);
            }

            TextRenderer.DrawText(
                graphics,
                "RimWorld AI Translator",
                titleFont,
                new Rectangle(24, 14, ClientSize.Width - 48, 28),
                Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(
                graphics,
                "LOCAL TRANSLATION WORKSPACE",
                captionFont,
                new Rectangle(24, 44, ClientSize.Width - 48, 18),
                Color.FromArgb(168, 177, 168),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(
                graphics,
                "작업공간 준비 중" + new string('.', dotCount),
                statusFont,
                new Rectangle(24, 88, ClientSize.Width - 48, 24),
                Color.FromArgb(49, 53, 48),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(
                graphics,
                "프로젝트와 번역 상태를 불러오고 있습니다.",
                captionFont,
                new Rectangle(24, 110, ClientSize.Width - 48, 18),
                Color.FromArgb(105, 103, 95),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animationTimer.Stop();
                animationTimer.Tick -= OnAnimationTick;
                animationTimer.Dispose();
                titleFont.Dispose();
                captionFont.Dispose();
                statusFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
