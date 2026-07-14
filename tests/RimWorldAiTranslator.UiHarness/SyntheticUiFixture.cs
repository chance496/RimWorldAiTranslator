using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Utilities;

namespace RimWorldAiTranslator.UiHarness;

internal sealed record SyntheticUiFixturePaths(
    string ModRoot,
    string ReviewRoot,
    string ComparisonPath,
    string ProjectId);

internal static class SyntheticUiFixture
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static SyntheticUiFixturePaths Create(
        string dataRoot,
        string discoveryRoot,
        int rowCount,
        string profile = "extended")
    {
        if (rowCount is < 1 or > 20_000)
            throw new ArgumentOutOfRangeException(nameof(rowCount));

        var fullDataRoot = Path.GetFullPath(dataRoot);
        var fullDiscoveryRoot = Path.GetFullPath(discoveryRoot);
        var normalizedProfile = string.IsNullOrWhiteSpace(profile) ? "extended" : profile.Trim().ToLowerInvariant();
        if (normalizedProfile is not ("extended" or "golden" or "dashboard" or "memory"))
            throw new ArgumentException($"Unknown synthetic UI fixture profile: {profile}", nameof(profile));
        if (normalizedProfile == "memory" && rowCount != 2)
            throw new ArgumentException("The memory fixture profile requires exactly two rows.", nameof(rowCount));
        var modRoot = Path.Combine(
            fullDiscoveryRoot,
            "steamapps",
            "workshop",
            "content",
            "294100",
            "1234567890");
        var reviewName = normalizedProfile switch
        {
            "golden" => "review",
            "memory" => "memory-review",
            _ => "synthetic-review"
        };
        var reviewRoot = Path.Combine(fullDataRoot, "reviews", reviewName);
        var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
        var koreanRoot = Path.Combine(reviewRoot, "Languages", "Korean", "Keyed");
        var comparisonPath = Path.Combine(
            auditRoot,
            normalizedProfile == "memory" ? "memory-comparison.json" : "synthetic-comparison.json");
        var targetPath = Path.Combine(
            koreanRoot,
            normalizedProfile == "memory" ? "Memory.xml" : "Synthetic.xml");
        var now = "2025-01-15T12:00:00.0000000Z";

        Directory.CreateDirectory(Path.Combine(modRoot, "About"));
        Directory.CreateDirectory(Path.Combine(modRoot, "Languages", "English", "Keyed"));
        Directory.CreateDirectory(auditRoot);
        Directory.CreateDirectory(koreanRoot);

        WriteUtf8(
            Path.Combine(modRoot, "About", "About.xml"),
            "<ModMetaData><name>Frontier Furniture Expansion</name>" +
            "<packageId>codex.fixture.frontier</packageId>" +
            "<description>Synthetic isolated UI fixture.</description></ModMetaData>");
        WriteUtf8(
            Path.Combine(modRoot, "Languages", "English", "Keyed", "Synthetic.xml"),
            "<LanguageData><Synthetic.Sample>Safe synthetic source</Synthetic.Sample></LanguageData>");

        var rows = new List<ReviewComparisonRow>(rowCount);
        for (var index = 0; index < rowCount; index++)
        {
            var source = normalizedProfile switch
            {
                "memory" => "Repeated source for local memory",
                "golden" or "dashboard" when index % 17 == 0 =>
                    $"needle source {index} with a deliberately long localization sentence for layout stress",
                "golden" or "dashboard" => $"Synthetic source {index}",
                _ => index switch
                {
                    0 or 1 => "Repeated source for local translation memory",
                    _ when index % 29 == 0 => $"日本語 中文 Ελληνικά emoji 😀 source {index}",
                    _ when index % 17 == 0 =>
                        $"needle source {index} with a deliberately long localization sentence for layout stress",
                    _ => $"Synthetic source {index}"
                }
            };
            var candidate = normalizedProfile == "memory"
                ? index == 1 ? "로컬 메모리 번역" : string.Empty
                : index % 3 == 0 ? $"번역문 {index}" : string.Empty;
            var existing = normalizedProfile == "memory"
                ? string.Empty
                : index % 5 == 0 ? $"번역문 existing {index}" : string.Empty;
            var id = normalizedProfile == "memory"
                ? index == 0 ? "M000002" : "M000001"
                : $"E{index + 1:D6}";
            var key = normalizedProfile == "memory"
                ? index == 0 ? "Memory.Two" : "Memory.One"
                : $"Synthetic.Entry.{index}";

            rows.Add(new ReviewComparisonRow
            {
                Id = id,
                Key = key,
                Kind = "Keyed",
                DefClass = "Keyed",
                Node = key,
                Field = "text",
                Target = targetPath,
                Source = source,
                Existing = existing,
                Candidate = candidate,
                ExistingOrigin = string.IsNullOrEmpty(existing) ? string.Empty : "mod",
                TranslationOrigin = string.IsNullOrEmpty(candidate)
                    ? string.IsNullOrEmpty(existing) ? string.Empty : "mod"
                    : normalizedProfile == "memory" ? "local" : "ai",
                ExistingPresent = !string.IsNullOrEmpty(existing),
                ExistingHasKorean = !string.IsNullOrEmpty(existing),
                CandidateHasKorean = !string.IsNullOrEmpty(candidate),
                CandidateBlank = string.IsNullOrEmpty(candidate),
                SafeToApply = !string.IsNullOrEmpty(candidate)
            });
        }
        WriteUtf8(comparisonPath, JsonSerializer.Serialize(rows, JsonOptions));

        if (normalizedProfile is "dashboard" or "memory")
            WriteDecisions(reviewRoot, comparisonPath, targetPath, rows, normalizedProfile, now);

        const string projectId = "fixture-frontier-furniture";
        var projects = new ProjectStoreDocument
        {
            Version = 2,
            UpdatedAt = now,
            Projects =
            [
                new TranslationProject
                {
                    Id = projectId,
                    Name = normalizedProfile switch
                    {
                        "golden" => "review",
                        "memory" => "memory-review",
                        _ => "Frontier Furniture Expansion"
                    },
                    ModRoot = modRoot,
                    SourceKind = "Workshop",
                    PackageId = "codex.fixture.frontier",
                    WorkshopId = "1234567890",
                    SourceLanguageFolder = "English",
                    LatestReviewRoot = reviewRoot,
                    LatestReviewAt = now,
                    LastAppliedAt = string.Empty,
                    CreatedAt = "2025-01-13T12:00:00.0000000Z",
                    UpdatedAt = now,
                    Runs =
                    [
                        new ProjectRun
                        {
                            ReviewRoot = reviewRoot,
                            CreatedAt = now,
                            Provider = "Synthetic"
                        }
                    ]
                }
            ]
        };
        WriteUtf8(Path.Combine(fullDataRoot, "projects.json"), JsonSerializer.Serialize(projects, JsonOptions));

        return new SyntheticUiFixturePaths(modRoot, reviewRoot, comparisonPath, projectId);
    }

    private static void WriteUtf8(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void WriteDecisions(
        string reviewRoot,
        string comparisonPath,
        string targetPath,
        IReadOnlyList<ReviewComparisonRow> rows,
        string profile,
        string timestamp)
    {
        var selected = profile == "memory" ? rows.Skip(1).Take(1) : rows.Take(3);
        var decisions = selected.Select((row, index) =>
        {
            var sourceText = profile == "dashboard" && index == 2
                ? "Synthetic source before update"
                : row.Source;
            var text = profile == "memory"
                ? "로컬 메모리 번역"
                : index switch
                {
                    0 => "번역문 0",
                    1 => "번역문 1",
                    _ => "번역문 old"
                };
            return new ReviewDecision
            {
                Id = row.Id,
                Key = row.Key,
                Target = Path.Combine("Keyed", Path.GetFileName(targetPath)),
                DefClass = row.DefClass,
                Node = row.Node,
                Status = profile == "memory" || index == 0
                    ? ReviewStatuses.Approved
                    : index == 1 ? ReviewStatuses.Translated : ReviewStatuses.Pending,
                Text = text,
                TranslationOrigin = profile == "memory" || index == 0 ? "local" : index == 1 ? "ai" : "rmk",
                TranslationUpdatedAt = timestamp,
                SourceHash = StableIdentity.Sha256(sourceText),
                SourceText = sourceText,
                PreviousSourceText = profile == "dashboard" && index == 2 ? sourceText : string.Empty,
                SourceChanged = profile == "dashboard" && index == 2,
                UpdatedAt = timestamp
            };
        }).ToList();
        var document = new ReviewDecisionDocument
        {
            Version = ReviewDecisionDocument.CurrentVersion,
            Sparse = true,
            ReviewRoot = reviewRoot,
            Comparison = comparisonPath,
            ComparisonSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(comparisonPath))).ToLowerInvariant(),
            UpdatedAt = timestamp,
            Items = decisions
        };
        WriteUtf8(Path.Combine(reviewRoot, "review-decisions.json"), JsonSerializer.Serialize(document, JsonOptions));
    }
}
