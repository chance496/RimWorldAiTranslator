using System.Text;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.App;

internal sealed record RecoveryNoticeMessages(string UserMessage, string LogMessage);

internal static class RecoveryNoticePresentation
{
    internal const int MaximumFileNameLength = 120;
    internal const int MaximumUserMessageLength = 4_096;
    internal const int MaximumLogMessageLength = 2_048;
    private const int MaximumDetailedNoticeCount = 12;

    internal static RecoveryNoticeMessages Create(IReadOnlyList<JsonRecoveryNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(notices);

        if (notices.Count == 0)
        {
            return new RecoveryNoticeMessages(
                "복구된 저장 데이터가 없습니다.\n\n손상 원본 보존본 없음",
                "백업 복구 알림 · 0개 · 손상 원본 보존본 없음");
        }

        var entries = notices.Select(CreateEntry).ToArray();
        var preservedCount = entries.Count(entry => entry.PreservedFileName is not null);
        var missingPreservedCount = entries.Length - preservedCount;

        var userMessage = new StringBuilder()
            .Append("저장 데이터 ")
            .Append(entries.Length)
            .Append("개를 검증된 백업에서 복구했습니다.\n\n손상 원본 보존본 ");
        AppendPreservationSummary(userMessage, preservedCount, missingPreservedCount);
        if (preservedCount > 0)
            userMessage.Append("\n보존본은 각 대상 파일과 같은 폴더에 있습니다.");
        userMessage.Append("\n\n복구 항목:");

        foreach (var entry in entries.Take(MaximumDetailedNoticeCount))
        {
            userMessage
                .Append("\n- 대상: ")
                .Append(entry.StoreFileName)
                .Append(" · 손상 원본 보존본 ")
                .Append(entry.PreservedFileName ?? "없음");
        }
        AppendOmittedCount(userMessage, entries.Length);

        var logMessage = new StringBuilder()
            .Append("백업 복구 완료 · ")
            .Append(entries.Length)
            .Append("개 · 손상 원본 보존본 ");
        AppendPreservationSummary(logMessage, preservedCount, missingPreservedCount);
        logMessage.Append(" · 대상 ");

        var displayedEntries = entries.Take(MaximumDetailedNoticeCount).ToArray();
        for (var index = 0; index < displayedEntries.Length; index++)
        {
            if (index > 0) logMessage.Append(", ");
            var entry = displayedEntries[index];
            logMessage
                .Append(entry.StoreFileName)
                .Append(" -> ")
                .Append(entry.PreservedFileName ?? "보존본 없음");
        }
        AppendOmittedCount(logMessage, entries.Length);

        return new RecoveryNoticeMessages(
            Limit(userMessage.ToString(), MaximumUserMessageLength),
            Limit(logMessage.ToString(), MaximumLogMessageLength));
    }

    private static RecoveryNoticeEntry CreateEntry(JsonRecoveryNotice notice) => new(
        ExtractSafeFileName(notice.StorePath, "저장 데이터"),
        string.IsNullOrWhiteSpace(notice.PreservedCorruptPath)
            ? null
            : ExtractSafeFileName(notice.PreservedCorruptPath, "보존된 손상 원본"));

    private static string ExtractSafeFileName(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path)) return fallback;

        var lastSeparator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        var candidate = path[(lastSeparator + 1)..];
        var sanitized = new StringBuilder(Math.Min(candidate.Length, MaximumFileNameLength));
        var truncated = false;

        foreach (var character in candidate)
        {
            if (char.IsControl(character) || character is '\u2028' or '\u2029') continue;
            if (sanitized.Length >= MaximumFileNameLength - 1)
            {
                truncated = true;
                break;
            }
            sanitized.Append(character);
        }

        var value = sanitized.ToString().Trim();
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..") return fallback;
        return truncated ? value + "…" : value;
    }

    private static void AppendPreservationSummary(
        StringBuilder builder,
        int preservedCount,
        int missingPreservedCount)
    {
        if (preservedCount == 0)
        {
            builder.Append("없음");
            return;
        }

        builder.Append(preservedCount).Append('개');
        if (missingPreservedCount > 0)
            builder.Append(" · 보존본 없음 ").Append(missingPreservedCount).Append('개');
    }

    private static void AppendOmittedCount(StringBuilder builder, int totalCount)
    {
        var omittedCount = totalCount - MaximumDetailedNoticeCount;
        if (omittedCount > 0) builder.Append(" · 그 외 ").Append(omittedCount).Append('개');
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private sealed record RecoveryNoticeEntry(string StoreFileName, string? PreservedFileName);
}
