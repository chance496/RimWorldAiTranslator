using RimWorldAiTranslator.Core.Projects;

namespace RimWorldAiTranslator.App.Controls;

internal sealed class ActivityControl : UserControl
{
    private readonly ListView list;

    public ActivityControl()
    {
        Dock = DockStyle.Fill;
        TabStop = false;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        var heading = Ui.Label("활동", 14f, FontStyle.Bold);
        heading.AutoSize = false;
        heading.SetBounds(24, 20, 180, 30);
        list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Malgun Gothic", 9f),
            AccessibleName = "활동 기록 목록",
            AccessibleDescription = "최근 번역, 검수 및 적용 활동을 시간순으로 확인합니다.",
            TabIndex = 0,
            TabStop = true
        };
        list.Columns.Add("시간", 150);
        list.Columns.Add("모드", 320);
        list.Columns.Add("종류", 90);
        list.Columns.Add("내용", 720);
        Controls.AddRange([heading, list]);
        Resize += (_, _) =>
        {
            heading.Height = DeviceDpi > 96 ? 36 : 30;
            list.SetBounds(24, 66, Math.Max(320, ClientSize.Width - 48), Math.Max(220, ClientSize.Height - 90));
        };
        HandleCreated += (_, _) => BeginInvoke((Action)(() => heading.Height = DeviceDpi > 96 ? 36 : 30));
        list.SetBounds(24, 66, 1378, 606);
    }

    public void SetEntries(IEnumerable<ActivityEntry> entries)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var row in entries)
            {
                var time = row.Time == DateTimeOffset.MinValue
                    ? "-"
                    : row.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
                list.Items.Add(new ListViewItem([time, row.Project, row.Kind, row.Text]));
            }
        }
        finally { list.EndUpdate(); }
    }
}
