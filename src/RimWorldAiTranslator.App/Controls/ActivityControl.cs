using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.App.Controls;

internal sealed class ActivityControl : UserControl
{
    private readonly ListView list;

    public ActivityControl()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(38, 28, 38, 28);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(Ui.Label("활동", 17f, FontStyle.Bold));
        heading.Controls.Add(Ui.Label("최근 번역 실행, 검수 변경, 적용 기록을 프로젝트별로 확인합니다.", 9.5f));
        list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 18, 0, 0)
        };
        list.Columns.Add("시간", 160);
        list.Columns.Add("프로젝트", 260);
        list.Columns.Add("종류", 90);
        list.Columns.Add("내용", 760);
        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(list, 0, 1);
        Controls.Add(root);
    }

    public void SetProjects(IEnumerable<TranslationProject> projects, AtomicJsonStore store)
    {
        var rows = new List<(DateTimeOffset Time, string Project, string Kind, string Text)>();
        foreach (var project in projects)
        {
            foreach (var run in project.Runs)
            {
                if (DateTimeOffset.TryParse(run.CreatedAt, out var time)) rows.Add((time, project.Name, "번역", "검수 작업 생성"));
            }
            if (DateTimeOffset.TryParse(project.LastAppliedAt, out var applied)) rows.Add((applied, project.Name, "적용", "Korean 폴더 반영"));
            if (string.IsNullOrWhiteSpace(project.LatestReviewRoot)) continue;
            var decisionPath = ReviewRepository.GetDecisionPath(project.LatestReviewRoot);
            if (!store.Exists(decisionPath)) continue;
            try
            {
                var document = store.Read<ReviewDecisionDocument>(decisionPath);
                foreach (var decision in document?.Items.Where(item => !string.IsNullOrWhiteSpace(item.UpdatedAt)).OrderByDescending(item => item.UpdatedAt).Take(12) ?? [])
                {
                    if (!DateTimeOffset.TryParse(decision.UpdatedAt, out var time)) continue;
                    var status = decision.SourceChanged ? "업데이트로 변경됨" : decision.Status switch
                    {
                        ReviewStatuses.Approved => "검토됨",
                        ReviewStatuses.Translated => "번역됨",
                        _ => "미번역"
                    };
                    rows.Add((time, project.Name, "검수", $"{decision.Key} -> {status}"));
                }
            }
            catch
            {
                // One damaged project does not hide other activity rows.
            }
        }
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var row in rows.OrderByDescending(row => row.Time).Take(120))
        {
            list.Items.Add(new ListViewItem([row.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), row.Project, row.Kind, row.Text]));
        }
        list.EndUpdate();
    }
}
