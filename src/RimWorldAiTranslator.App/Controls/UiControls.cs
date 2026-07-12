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

    public static Button Button(string text, string? role = null, int width = 104) => new()
    {
        Text = text,
        Tag = role,
        AutoSize = false,
        Size = new Size(width, 38),
        Margin = new Padding(5),
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false
    };

    public static TextBox TextBox(string placeholder = "", bool multiline = false) => new()
    {
        PlaceholderText = placeholder,
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

internal sealed class LoadingOverlay : BufferedPanel
{
    private readonly Label title;
    private readonly Label detail;
    private readonly Label activity;
    private readonly ProgressBar progress;
    private readonly Button cancel;
    private readonly System.Windows.Forms.Timer timer;
    private int frame;

    public LoadingOverlay()
    {
        Dock = DockStyle.Fill;
        Visible = false;
        BringToFront();
        var card = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(30),
            Anchor = AnchorStyles.None,
            MinimumSize = new Size(430, 190)
        };
        title = Ui.Label("작업 준비 중", 14f, FontStyle.Bold);
        detail = Ui.Label("잠시만 기다려 주세요.", 9.5f);
        detail.MaximumSize = new Size(520, 0);
        activity = Ui.Label("●  ○  ○", 12f, FontStyle.Bold);
        progress = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24, Height = 5, Dock = DockStyle.Top };
        cancel = Ui.Button("중지", "danger", 88);
        cancel.Anchor = AnchorStyles.Left;
        cancel.Visible = false;
        cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        card.Controls.Add(title, 0, 0);
        card.Controls.Add(detail, 0, 1);
        card.Controls.Add(activity, 0, 2);
        card.Controls.Add(progress, 0, 3);
        card.Controls.Add(cancel, 0, 4);
        Controls.Add(card);
        card.Location = new Point((Width - card.Width) / 2, (Height - card.Height) / 2);
        Resize += (_, _) => card.Location = new Point(Math.Max(0, (Width - card.Width) / 2), Math.Max(0, (Height - card.Height) / 2));
        timer = new System.Windows.Forms.Timer { Interval = 220 };
        timer.Tick += (_, _) =>
        {
            frame = (frame + 1) % 3;
            activity.Text = frame switch { 0 => "●  ○  ○", 1 => "○  ●  ○", _ => "○  ○  ●" };
        };
    }

    public event EventHandler? CancelRequested;

    public void Show(string heading, string message, ThemePalette theme, bool cancellable = false)
    {
        title.Text = heading;
        detail.Text = message;
        cancel.Visible = cancellable;
        cancel.Enabled = cancellable;
        BackColor = Color.FromArgb(theme.Dark ? 245 : 238, theme.Background);
        foreach (Control child in Controls)
        {
            child.BackColor = theme.Surface;
            child.ForeColor = theme.Text;
            foreach (Control nested in child.Controls)
            {
                nested.BackColor = theme.Surface;
                nested.ForeColor = theme.Text;
            }
        }
        Visible = true;
        BringToFront();
        timer.Start();
    }

    public void UpdateMessage(string heading, string message)
    {
        title.Text = heading;
        detail.Text = message;
    }

    public void HideOverlay()
    {
        timer.Stop();
        cancel.Visible = false;
        Visible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
