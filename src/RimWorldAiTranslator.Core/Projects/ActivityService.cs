using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Core.Projects;

public sealed record ActivityEntry(DateTimeOffset Time, string Project, string Kind, string Text);

public sealed class ActivityService
{
    private readonly AtomicJsonStore store;
    private readonly string? trustedReviewsRoot;

    public ActivityService(AtomicJsonStore store, string? trustedReviewsRoot = null)
    {
        this.store = store;
        this.trustedReviewsRoot = trustedReviewsRoot;
    }

    internal Action<string>? BeforeDecisionStoreProbeTestHook { get; set; }

    public Task<IReadOnlyList<ActivityEntry>> LoadAsync(
        IEnumerable<TranslationProject> projects,
        CancellationToken cancellationToken = default)
    {
        var snapshot = projects.ToArray();
        return Task.Run(() => Load(snapshot, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<ActivityEntry> Load(
        IEnumerable<TranslationProject> projects,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<ActivityEntry>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var run in project.Runs)
            {
                if (DateTimeOffset.TryParse(run.CreatedAt, out var time))
                    rows.Add(new ActivityEntry(time, project.Name, "번역", "검수 작업 생성"));
            }
            if (DateTimeOffset.TryParse(project.LastAppliedAt, out var applied))
                rows.Add(new ActivityEntry(applied, project.Name, "적용", "Korean 폴더 반영"));
            if (string.IsNullOrWhiteSpace(project.LatestReviewRoot)) continue;
            try
            {
                var reviewRoot = ReviewRepository.ValidateWritableReviewRoot(
                    project.LatestReviewRoot,
                    trustedReviewsRoot);
                var decisionPath = ReviewRepository.GetDecisionPath(reviewRoot);
                BeforeDecisionStoreProbeTestHook?.Invoke(decisionPath);
                if (!new ReviewRepository(store).TryLoad(
                        reviewRoot,
                        out var document,
                        trustedReviewsRoot,
                        cancellationToken)) continue;
                foreach (var decision in document.Items
                             .Where(item => !string.IsNullOrWhiteSpace(item.UpdatedAt))
                             .OrderByDescending(item => item.UpdatedAt)
                             .Take(12))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!DateTimeOffset.TryParse(decision.UpdatedAt, out var time)) continue;
                    var status = decision.SourceChanged ? "업데이트로 변경됨" : decision.Status switch
                    {
                        ReviewStatuses.Approved => "검토됨",
                        ReviewStatuses.Translated => "번역됨",
                        _ => "미번역"
                    };
                    rows.Add(new ActivityEntry(time, project.Name, "검수", $"{decision.Key} -> {status}"));
                }
            }
            catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or InvalidDataException
                                                   or InvalidOperationException
                                                   or ArgumentException
                                                   or NotSupportedException
                                                   or System.Security.SecurityException
                                                   or System.Text.Json.JsonException)
            {
                rows.Add(new ActivityEntry(DateTimeOffset.MinValue, project.Name, "검수", $"검수 기록 읽기 실패 ({exception.GetType().Name})"));
            }
        }

        return rows.OrderByDescending(row => row.Time).Take(120).ToArray();
    }
}
