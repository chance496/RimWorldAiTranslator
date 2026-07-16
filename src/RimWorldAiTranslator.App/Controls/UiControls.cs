namespace RimWorldAiTranslator.App.Controls;

internal static class Ui
{
    public static Label Label(string text, float size = 9f, FontStyle style = FontStyle.Regular) => new()
    {
        AutoSize = true,
        Text = text,
        Font = new Font("Malgun Gothic", size, style),
        Margin = new Padding(0, 3, 0, 3)
    };

    public static Button Button(string text, string? role = null, int width = 104)
    {
        var button = new Button
        {
            Text = text,
            AccessibleName = text,
            Tag = role,
            AutoSize = false,
            Size = new Size(width, 38),
            Margin = new Padding(5),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
        button.EnabledChanged += (_, _) => ThemeManager.Apply(button, ThemeManager.Current, ThemeManager.CurrentTextSize);
        return button;
    }

    public static TextBox TextBox(string placeholder = "", bool multiline = false) => new()
    {
        PlaceholderText = placeholder,
        AccessibleName = string.IsNullOrWhiteSpace(placeholder) ? null : placeholder,
        Multiline = multiline,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(4),
        ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
    };

    public static Panel Divider(Color color) => new() { Height = 1, Dock = DockStyle.Top, BackColor = color };
}

internal class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}

internal sealed class BufferedListBox : ListBox
{
    public BufferedListBox()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
}

internal sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
}

internal sealed class LayoutSplitContainer : SplitContainer
{
    public LayoutSplitContainer()
    {
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TabStop = false;
    }
}

internal sealed class LoadingOverlay : BufferedPanel
{
    private readonly Label title;
    private readonly Label detail;
    private readonly Label stage;
    private readonly Label count;
    private readonly OperationSpinner spinner;
    private readonly ProgressBar progress;
    private readonly Button cancel;
    private readonly Button retry;
    private readonly Button review;
    private readonly Button close;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer dismissTimer;
    private int frame;
    private bool spinnerActive;

    public LoadingOverlay()
    {
        Dock = DockStyle.None;
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Height = 82;
        Visible = false;
        TabStop = false;
        BorderStyle = BorderStyle.FixedSingle;
        var accent = new Panel { Width = 4, Tag = "accent" };
        spinner = new OperationSpinner { TabStop = false };
        title = Ui.Label("번역 작업", 10f, FontStyle.Bold); title.AutoSize = false;
        stage = Ui.Label("작업공간 확인", 9.5f, FontStyle.Bold); stage.AutoSize = false;
        detail = Ui.Label("실제 작업 로그를 기다리는 중입니다.", 8.3f); detail.AutoSize = false;
        count = Ui.Label("작업 응답 대기 중", 8f, FontStyle.Bold); count.AutoSize = false;
        progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 28,
            Height = 7,
            TabStop = false,
            AccessibleName = "작업 진행 상태",
            AccessibleDescription = "현재 작업의 진행률 또는 응답 대기 상태를 표시합니다."
        };
        cancel = Ui.Button("중지", "danger", 72);
        cancel.Visible = false;
        cancel.Click += (_, _) =>
        {
            if (!cancel.Enabled) return;
            cancel.Enabled = false;
            cancel.Text = "요청됨";
            cancel.AccessibleName = "취소 요청됨";
            stage.Text = "취소 요청됨";
            detail.Text = "현재 단계가 안전하게 중단될 때까지 기다리는 중입니다.";
            count.Text = "완료분 보존 대기 중";
            CancelRequested?.Invoke(this, EventArgs.Empty);
        };
        retry = Ui.Button("다시 시도", "primary", 96); retry.Visible = false; retry.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        review = Ui.Button("검수 화면", "success", 96); review.Visible = false; review.Click += (_, _) => ReviewRequested?.Invoke(this, EventArgs.Empty);
        close = Ui.Button("닫기", null, 72); close.Visible = false; close.Click += (_, _) => HideOverlay();
        Controls.AddRange([accent, spinner, title, stage, detail, count, progress, cancel, retry, review, close]);
        Resize += (_, _) => LayoutOverlay(accent);
        timer = new System.Windows.Forms.Timer { Interval = 90 };
        timer.Tick += (_, _) =>
        {
            frame = (frame + 1) % 20;
            spinner.Pulse = frame;
            spinner.Invalidate();
        };
        dismissTimer = new System.Windows.Forms.Timer { Interval = 900 };
        dismissTimer.Tick += (_, _) => { dismissTimer.Stop(); HideOverlay(); };
    }

    public event EventHandler? CancelRequested;
    public event EventHandler? RetryRequested;
    public event EventHandler? ReviewRequested;

    public void Show(string heading, string message, ThemePalette theme, bool cancellable = false)
    {
        dismissTimer.Stop();
        spinnerActive = true;
        spinner.Visible = true;
        if (Parent is not null)
        {
            var workspace = Parent.Controls.OfType<ReviewWorkspaceControl>().FirstOrDefault(control => control.Visible);
            SetBounds(0, workspace?.WorkspaceHeaderHeight ?? 70, Parent.ClientSize.Width, 82);
        }
        title.Text = heading;
        stage.Text = "작업공간 확인";
        detail.Text = message;
        count.Text = "작업 응답 대기 중";
        cancel.Visible = cancellable;
        cancel.Enabled = cancellable;
        cancel.Text = "중지";
        cancel.AccessibleName = "중지";
        retry.Visible = false;
        review.Visible = false;
        close.Visible = false;
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 28;
        BackColor = theme.Surface;
        ForeColor = theme.Text;
        spinner.AccentColor = theme.Accent;
        spinner.BorderColor = ThemeManager.ReadableForeground(theme.Surface, theme.Border, theme.Text);
        title.ForeColor = theme.Text;
        stage.ForeColor = theme.Text;
        detail.ForeColor = ThemeManager.ReadableForeground(theme.Surface, theme.Muted, theme.Text);
        count.ForeColor = ThemeManager.ReadableForeground(theme.Surface, theme.Muted, theme.Text);
        foreach (var child in Controls.OfType<Button>())
        {
            var role = child.Tag as string;
            child.BackColor = role switch
            {
                "danger" => theme.Danger,
                "primary" => theme.Accent,
                "success" => theme.Success,
                _ => theme.Surface
            };
            child.ForeColor = ThemeManager.ReadableForeground(
                child.BackColor,
                role switch
                {
                    "danger" or "success" => Color.White,
                    "primary" => theme.AccentText,
                    _ => theme.Text
                },
                theme.Text);
            child.FlatAppearance.BorderColor = ThemeManager.ReadableForeground(
                child.BackColor,
                theme.Border,
                child.ForeColor);
        }
        Visible = true;
        LayoutOverlay(Controls.OfType<Panel>().First(panel => panel.Tag as string == "accent"));
        BringToFront();
        timer.Start();
    }

    public void UpdateMessage(string heading, string message)
    {
        stage.Text = heading;
        detail.Text = message;
    }

    public void UpdateProgress(string stageName, string message, int current, int total)
    {
        stage.Text = stageName switch
        {
            "translate" => "번역 배치 처리",
            "retry" => "번역 배치 재시도",
            "rate-limit" => "요청 한도 대기",
            "scan" or "source" => "원문 분석",
            "prepare" => "번역 준비",
            _ => "작업 처리"
        };
        detail.Text = message;
        if (total > 0)
        {
            var boundedCurrent = Math.Clamp(current, 0, total);
            var percent = Math.Clamp((int)Math.Round(boundedCurrent * 100d / total), 0, 100);
            count.Text = $"{boundedCurrent:N0} / {total:N0} 항목  ·  {percent}%";
            progress.Style = ProgressBarStyle.Continuous;
            progress.MarqueeAnimationSpeed = 0;
            progress.Minimum = 0;
            progress.Maximum = 100;
            progress.Value = percent;
        }
        else
        {
            count.Text = "작업 응답 대기 중";
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 28;
        }
    }

    public void Complete(string kind, string heading, string message, bool canRetry = false, bool canReview = false)
    {
        dismissTimer.Stop();
        timer.Stop();
        spinnerActive = false;
        spinner.Visible = false;
        title.Text = heading;
        stage.Text = kind switch
        {
            "completed" => "완료",
            "cancelled" => "중단됨",
            _ => "실패"
        };
        detail.Text = message;
        count.Text = kind switch
        {
            "completed" => "결과 저장됨",
            "cancelled" => "완료분 보존",
            _ when canRetry => "실패 · 다시 시도 가능",
            _ => "실패 · 입력 확인 필요"
        };
        progress.Style = ProgressBarStyle.Continuous;
        progress.MarqueeAnimationSpeed = 0;
        progress.Minimum = 0;
        progress.Maximum = 100;
        progress.Value = kind == "completed" ? 100 : 0;
        cancel.Visible = false;
        retry.Visible = kind == "error" && canRetry;
        review.Visible = canReview;
        close.Visible = kind == "error";
        LayoutOverlay(Controls.OfType<Panel>().First(panel => panel.Tag as string == "accent"));
        Invalidate(true);
        if (kind is "completed" or "cancelled")
        {
            dismissTimer.Stop();
            dismissTimer.Start();
        }
    }

    internal string TitleForTesting => title.Text;

    internal string StageForTesting => stage.Text;

    internal string CountForTesting => count.Text;

    internal bool SpinnerVisibleForTesting => spinnerActive;

    public void HideOverlay()
    {
        dismissTimer.Stop();
        timer.Stop();
        spinnerActive = false;
        cancel.Visible = false;
        retry.Visible = false;
        review.Visible = false;
        close.Visible = false;
        Visible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            dismissTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void LayoutOverlay(Control accent)
    {
        accent.SetBounds(0, 0, 4, Math.Max(1, ClientSize.Height));
        var buttonX = ClientSize.Width - 16;
        foreach (var (button, width) in new[] { (close, 72), (review, 96), (retry, 96), (cancel, 72) })
        {
            if (!button.Visible) continue;
            buttonX -= width;
            button.SetBounds(buttonX, 24, width, 34);
            buttonX -= 8;
        }
        var contentRight = Math.Max(520, buttonX - 8);
        spinner.SetBounds(18, 19, 42, 42);
        title.SetBounds(72, 10, 168, 24);
        count.SetBounds(72, 48, 168, 18);
        stage.SetBounds(250, 8, Math.Max(220, contentRight - 250), 22);
        detail.SetBounds(250, 29, Math.Max(220, contentRight - 250), 20);
        progress.SetBounds(250, 56, Math.Max(220, contentRight - 250), 7);
    }
}

internal sealed class WorkspaceLoadCover : BufferedPanel
{
    private readonly Label title;
    private readonly Label detail;
    private readonly ProgressBar progress;

    public WorkspaceLoadCover()
    {
        Dock = DockStyle.None;
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Visible = false;
        TabStop = false;
        Tag = "surface";
        title = Ui.Label("프로젝트 구성 중", 11f, FontStyle.Bold);
        title.AutoSize = false;
        title.TextAlign = ContentAlignment.MiddleCenter;
        detail = Ui.Label("문자열과 검수 상태를 한 번에 준비하고 있습니다.", 8.5f);
        detail.AutoSize = false;
        detail.TextAlign = ContentAlignment.MiddleCenter;
        detail.Tag = "muted";
        progress = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Height = 5, TabStop = false };
        progress.AccessibleName = "프로젝트 로드 진행 상태";
        Controls.AddRange([title, detail, progress]);
        Resize += (_, _) => LayoutContent();
    }

    public void ShowCover(string heading, string message, ThemePalette theme)
    {
        if (Parent is null) return;
        var workspace = Parent.Controls.OfType<ReviewWorkspaceControl>().FirstOrDefault(control => control.Visible);
        var top = workspace?.WorkspaceHeaderHeight ?? 70;
        SetBounds(0, top, Parent.ClientSize.Width, Math.Max(1, Parent.ClientSize.Height - top));
        title.Text = heading;
        detail.Text = message;
        BackColor = theme.Background;
        title.ForeColor = theme.Text;
        detail.ForeColor = theme.Muted;
        progress.MarqueeAnimationSpeed = 24;
        Visible = true;
        LayoutContent();
        BringToFront();
        Update();
    }

    public void HideCover()
    {
        progress.MarqueeAnimationSpeed = 0;
        Visible = false;
    }

    private void LayoutContent()
    {
        var contentWidth = Math.Min(480, Math.Max(280, ClientSize.Width - 48));
        var x = Math.Max(24, (ClientSize.Width - contentWidth) / 2);
        var y = Math.Max(48, (ClientSize.Height - 86) / 2);
        title.SetBounds(x, y, contentWidth, 28);
        detail.SetBounds(x, y + 32, contentWidth, 22);
        progress.SetBounds(x, y + 64, contentWidth, 5);
    }
}

internal sealed class OperationSpinner : Control
{
    public Color AccentColor { get; set; } = Color.FromArgb(183, 131, 66);
    public Color BorderColor { get; set; } = Color.FromArgb(90, 96, 90);
    public int Pulse { get; set; }

    public OperationSpinner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var diameter = Math.Max(12, Math.Min(ClientSize.Width, ClientSize.Height) - 8);
        var x = (ClientSize.Width - diameter) / 2;
        var y = (ClientSize.Height - diameter) / 2;
        using var border = new Pen(BorderColor, 1);
        using var accent = new Pen(AccentColor, 3);
        using var dot = new SolidBrush(AccentColor);
        e.Graphics.DrawEllipse(border, x, y, diameter, diameter);
        var start = Pulse * 18 % 360;
        const int sweep = 72;
        e.Graphics.DrawArc(accent, x, y, diameter, diameter, start, sweep);
        var radians = (start + sweep) * Math.PI / 180d;
        var radius = diameter / 2d;
        var dotX = x + radius + Math.Cos(radians) * radius;
        var dotY = y + radius + Math.Sin(radians) * radius;
        e.Graphics.FillEllipse(dot, (float)(dotX - 2.5), (float)(dotY - 2.5), 5, 5);
    }
}
