using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using RimWorldAiTranslator.Core.Validation;

namespace RimWorldAiTranslator.Core.Quality;

public sealed record QualityEntry(
    int Index,
    string Key,
    string Target,
    string DefClass,
    string Source,
    string Translation,
    string Existing,
    string Status,
    bool SourceChanged,
    bool SafeToApply,
    bool? TokenOrTagIssue = null);

public sealed record QualityIssue(
    int Index,
    string Key,
    string Target,
    string DefClass,
    string Category,
    string Severity,
    string Detail);

public sealed class QualityPrivacy
{
    [JsonPropertyName("includesSourceText")]
    public bool IncludesSourceText { get; init; }

    [JsonPropertyName("includesTranslationText")]
    public bool IncludesTranslationText { get; init; }

    [JsonPropertyName("includesApiKeys")]
    public bool IncludesApiKeys { get; init; }

    [JsonPropertyName("includesAbsolutePaths")]
    public bool IncludesAbsolutePaths { get; init; }
}

public sealed class QualityReportModel
{
    public string Product { get; init; } = "RimWorld AI Translator";
    public int ReportVersion { get; init; } = 1;
    public string GeneratedUtc { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public QualityPrivacy Privacy { get; init; } = new();
    public int EntryCount { get; init; }
    public int IssueCount { get; init; }
    public IReadOnlyDictionary<string, int> Statuses { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> IssueCategories { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Severities { get; init; } = new Dictionary<string, int>();
}

public sealed record QualityReportResult(string Path, string? BackupPath, QualityReportModel Model);

public static class QualityService
{
    public static IReadOnlyList<QualityIssue> FindIssues(IEnumerable<QualityEntry> sourceEntries)
    {
        var entries = sourceEntries.ToArray();
        var issues = new List<QualityIssue>();
        var identities = new Dictionary<string, List<QualityEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var identity = entry.Target.ToLowerInvariant() + "\u001f" + entry.Key;
            if (!identities.TryGetValue(identity, out var matches))
            {
                matches = [];
                identities[identity] = matches;
            }
            matches.Add(entry);

            if (string.IsNullOrWhiteSpace(entry.Translation))
            {
                issues.Add(NewIssue(entry, "Missing", "warning", "번역문이 비어 있습니다."));
                continue;
            }

            if (entry.SourceChanged)
            {
                issues.Add(NewIssue(entry, "SourceChanged", "warning", "번역 이후 원문이 변경되었습니다."));
            }

            if (!entry.SafeToApply)
            {
                issues.Add(NewIssue(entry, "Unsafe", "error", "안전 검사에 통과하지 못했습니다."));
            }

            var tokenIssue = entry.TokenOrTagIssue ?? !HasMatchingTokenStructure(entry.Source, entry.Translation);
            if (tokenIssue)
            {
                issues.Add(NewIssue(entry, "TokenOrTag", "error", "보호 토큰 또는 태그의 종류나 개수가 다릅니다."));
            }

            if (entry.Source.Length >= 3 && entry.Source.Trim().Equals(entry.Translation.Trim(), StringComparison.Ordinal))
            {
                issues.Add(NewIssue(entry, "SameAsSource", "info", "번역문이 원문과 같습니다."));
            }

            if (entry.Source.Length >= 20 && entry.Translation.Length >= 1)
            {
                var ratio = entry.Translation.Length / (double)entry.Source.Length;
                if (ratio < 0.18)
                {
                    issues.Add(NewIssue(entry, "TooShort", "warning", "번역문이 원문에 비해 매우 짧습니다."));
                }
                else if (ratio > 4.0)
                {
                    issues.Add(NewIssue(entry, "TooLong", "warning", "번역문이 원문에 비해 매우 깁니다."));
                }
            }

            if (!string.IsNullOrEmpty(entry.Existing) && !entry.Existing.Equals(entry.Translation, StringComparison.Ordinal))
            {
                issues.Add(NewIssue(entry, "ExistingChanged", "info", "기존 번역과 현재 번역이 다릅니다."));
            }
        }

        foreach (var duplicate in identities.Values.Where(group => group.Count > 1).SelectMany(group => group))
        {
            issues.Add(NewIssue(duplicate, "DuplicateIdentity", "error", "같은 대상 파일과 키가 둘 이상 존재합니다."));
        }

        return issues;
    }

    public static QualityReportModel CreateReport(IEnumerable<QualityEntry> sourceEntries, IEnumerable<QualityIssue> sourceIssues)
    {
        var entries = sourceEntries.ToArray();
        var issues = sourceIssues.ToArray();
        return new QualityReportModel
        {
            GeneratedUtc = DateTimeOffset.UtcNow.ToString("O"),
            EntryCount = entries.Length,
            IssueCount = issues.Length,
            Statuses = entries.GroupBy(entry => string.IsNullOrWhiteSpace(entry.Status) ? "unknown" : entry.Status)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            IssueCategories = issues.GroupBy(issue => issue.Category)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            Severities = issues.GroupBy(issue => issue.Severity)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public static QualityReportResult ExportHtml(string path, IEnumerable<QualityEntry> entries, IEnumerable<QualityIssue> issues)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Quality reports must use the .html extension.");
        }

        var model = CreateReport(entries, issues);
        var html = ToHtml(model);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Report path has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = fullPath + ".bak";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(html);
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, backupPath, true);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return new QualityReportResult(fullPath, File.Exists(backupPath) ? backupPath : null, model);
    }

    public static string ToHtml(QualityReportModel model)
    {
        static string Encode(object? value) => WebUtility.HtmlEncode(Convert.ToString(value) ?? string.Empty);
        static string Rows(IReadOnlyDictionary<string, int> values) => string.Concat(values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"<tr><td>{Encode(pair.Key)}</td><td>{pair.Value}</td></tr>"));

        return $$$"""
            <!doctype html>
            <html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>번역 품질 보고서</title><style>
            body{font-family:"Malgun Gothic",sans-serif;margin:0;background:#efeee8;color:#20251f}main{max-width:920px;margin:40px auto;padding:0 24px}
            h1{font-size:26px;margin:0 0 8px}.meta{color:#636c65;margin-bottom:24px}.summary{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;margin-bottom:24px}
            .metric{background:#fff;border:1px solid #b7bdb6;border-top:3px solid #b78342;padding:18px}.metric strong{display:block;font-size:28px;margin-top:8px}
            section{background:#faf9f4;border:1px solid #d4d8d2;margin:12px 0;padding:18px}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:9px;border-bottom:1px solid #d4d8d2}
            .privacy{font-size:13px;color:#636c65}@media(max-width:640px){.summary{grid-template-columns:1fr}main{margin:20px auto}}
            </style></head><body><main><h1>번역 품질 보고서</h1><div class="meta">생성 시각: {{{Encode(model.GeneratedUtc)}}}</div>
            <div class="summary"><div class="metric">전체 문자열<strong>{{{model.EntryCount}}}</strong></div><div class="metric">검사 항목<strong>{{{model.IssueCount}}}</strong></div></div>
            <section><h2>번역 상태</h2><table><thead><tr><th>상태</th><th>개수</th></tr></thead><tbody>{{{Rows(model.Statuses)}}}</tbody></table></section>
            <section><h2>품질 검사</h2><table><thead><tr><th>분류</th><th>개수</th></tr></thead><tbody>{{{Rows(model.IssueCategories)}}}</tbody></table></section>
            <p class="privacy">이 보고서는 집계 수치만 포함합니다. 원문, 번역문, API 키, 절대 경로는 포함하지 않습니다.</p>
            </main></body></html>
            """;
    }

    private static bool HasMatchingTokenStructure(string source, string translation)
    {
        var result = TranslationValidator.Validate(source, translation);
        return result.MissingTokens.Count == 0
            && result.UnexpectedTokens.Count == 0
            && result.TokenCountMismatches.Count == 0
            && !result.GrammarPrefixMoved;
    }

    private static QualityIssue NewIssue(QualityEntry entry, string category, string severity, string detail) =>
        new(entry.Index, entry.Key, entry.Target, entry.DefClass, category, severity, detail);
}
