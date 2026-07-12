using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Core.Diagnostics;

public sealed record DiagnosticBundleOptions(
    string OutputPath,
    AppDataPaths Paths,
    AppSettingsDocument Settings,
    ProjectStoreDocument Projects,
    string ProductRoot,
    IReadOnlyList<string> RuntimeLogLines,
    bool Force = false);

public sealed record DiagnosticBundleResult(string Path, int Entries, long Bytes);

public sealed class DiagnosticBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] ProviderIds =
        ["Cerebras", "OpenAI", "Gemini", "DeepSeek", "Qwen", "Groq", "Mistral", "OpenRouter", "ZAI", "Custom", "Google"];

    public DiagnosticBundleResult Create(DiagnosticBundleOptions options, CancellationToken cancellationToken = default)
    {
        var output = Path.GetFullPath(options.OutputPath);
        if (!Path.GetExtension(output).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("진단 번들은 .zip 확장자를 사용해야 합니다.");
        var parent = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("진단 번들을 저장할 폴더가 없습니다.");
        if (File.Exists(output) && !options.Force)
            throw new IOException("같은 이름의 진단 번들이 이미 있습니다.");

        var tempBase = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var workspace = Path.GetFullPath(Path.Combine(tempBase, "RimWorldAiTranslator-diagnostics-" + Guid.NewGuid().ToString("N")));
        var expectedPrefix = tempBase + Path.DirectorySeparatorChar + "RimWorldAiTranslator-diagnostics-";
        if (!workspace.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("진단 임시 폴더가 안전 경계를 벗어났습니다.");
        Directory.CreateDirectory(workspace);
        var temporaryZip = Path.Combine(parent, Path.GetFileName(output) + ".tmp-" + Guid.NewGuid().ToString("N"));

        try
        {
            var readErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var providerSummaries = options.Settings.ApiProviders
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new
                {
                    id = KnownValue(pair.Key, ProviderIds),
                    url = UrlSummary(pair.Value.Url),
                    modelConfigured = !string.IsNullOrWhiteSpace(pair.Value.Model),
                    modelHash12 = string.IsNullOrWhiteSpace(pair.Value.Model) ? string.Empty : Sha256(pair.Value.Model)[..12],
                    temperature = pair.Value.Temperature
                }).ToArray();
            var settingsSummary = new
            {
                present = true,
                schemaVersion = options.Settings.Version,
                themeMode = KnownValue(options.Settings.ThemeMode, ["System", "Light", "Dark"]),
                designPreset = KnownValue(options.Settings.DesignPreset, ["Professional", "SciFi", "Vivid", "Studio", "Frontier"]),
                textSize = options.Settings.TextSize,
                highContrast = options.Settings.HighContrast,
                autoSave = options.Settings.AutoSave,
                selectedProvider = KnownValue(options.Settings.ApiProviderId, ProviderIds),
                rmkWorkspaceConfigured = !string.IsNullOrWhiteSpace(options.Settings.RmkWorkspaceRoot),
                rmkUseExisting = options.Settings.RmkUseExisting,
                providers = providerSummaries
            };

            var languages = options.Projects.Projects
                .GroupBy(project => LanguageCategory(project.SourceLanguageFolder), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var projectSummary = new
            {
                present = true,
                schemaVersion = options.Projects.Version,
                count = options.Projects.Projects.Count,
                withReview = options.Projects.Projects.Count(project => !string.IsNullOrWhiteSpace(project.LatestReviewRoot)),
                sourceLanguages = languages
            };

            var reviewSummary = BuildReviewSummary(options.Paths.Reviews, readErrors, cancellationToken);
            var integrity = BuildIntegrity(options.ProductRoot, cancellationToken);
            var manifest = new
            {
                schemaVersion = 1,
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                privacy = new
                {
                    includesSourceText = false,
                    includesTranslationText = false,
                    includesLocalizationKeys = false,
                    includesApiKeys = false,
                    includesRawLogs = false,
                    includesAbsolutePaths = false
                },
                runtime = new
                {
                    osVersion = Environment.OSVersion.VersionString,
                    dotnetVersion = Environment.Version.ToString(),
                    process64Bit = Environment.Is64BitProcess,
                    os64Bit = Environment.Is64BitOperatingSystem,
                    culture = CultureInfo.CurrentCulture.Name,
                    uiCulture = CultureInfo.CurrentUICulture.Name
                },
                readErrors
            };

            var entries = new Dictionary<string, object>
            {
                ["manifest.json"] = manifest,
                ["settings-summary.json"] = settingsSummary,
                ["projects-summary.json"] = projectSummary,
                ["reviews-summary.json"] = reviewSummary,
                ["errors-summary.json"] = BuildErrorSummary(options.RuntimeLogLines),
                ["product-integrity.json"] = integrity
            };

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(entry.Value, JsonOptions);
                if (ContainsPrivateContent(json))
                    throw new InvalidDataException($"개인정보 보호 검사에서 {entry.Key} 생성을 차단했습니다.");
                File.WriteAllText(Path.Combine(workspace, entry.Key), json, new UTF8Encoding(false));
            }

            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
            ZipFile.CreateFromDirectory(workspace, temporaryZip, CompressionLevel.Optimal, false);
            using (var archive = ZipFile.OpenRead(temporaryZip))
            {
                var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
                foreach (var required in entries.Keys)
                    if (!names.Contains(required)) throw new InvalidDataException($"진단 번들에 {required} 파일이 없습니다.");
            }

            if (File.Exists(output)) File.Replace(temporaryZip, output, output + ".bak", true);
            else File.Move(temporaryZip, output);
            return new DiagnosticBundleResult(output, entries.Count, new FileInfo(output).Length);
        }
        finally
        {
            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
            if (workspace.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(workspace))
                Directory.Delete(workspace, true);
        }
    }

    private static object BuildReviewSummary(string reviewsRoot, IDictionary<string, string> readErrors, CancellationToken cancellationToken)
    {
        var folders = 0;
        var decisionFiles = 0;
        var items = 0;
        var sourceChanged = 0;
        var unreadable = 0;
        var statuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(reviewsRoot))
        {
            foreach (var folder in Directory.EnumerateDirectories(reviewsRoot, "*", SearchOption.TopDirectoryOnly).Take(50))
            {
                cancellationToken.ThrowIfCancellationRequested();
                folders++;
                var path = Path.Combine(folder, "review-decisions.json");
                if (!File.Exists(path)) continue;
                decisionFiles++;
                try
                {
                    if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("JSONTooLarge");
                    var document = JsonSerializer.Deserialize<ReviewDecisionDocument>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (document is null) throw new InvalidDataException("EmptyJson");
                    foreach (var item in document.Items)
                    {
                        items++;
                        Increment(statuses, KnownValue(item.Status, ["pending", "translated", "approved", "reviewed", "excluded", "failed"], "pending"));
                        Increment(origins, KnownValue(item.TranslationOrigin, ["ai", "local", "rmk", "mod", "existing", "google"]));
                        if (item.SourceChanged) sourceChanged++;
                    }
                }
                catch (Exception ex)
                {
                    unreadable++;
                    readErrors["review-decisions.json"] = ex.GetType().Name;
                }
            }
        }
        return new { folders, decisionFiles, items, sourceChanged, statuses, origins, unreadable };
    }

    private static object[] BuildIntegrity(string productRoot, CancellationToken cancellationToken)
    {
        var names = new[]
        {
            "RimWorldAiTranslator.exe",
            "RimWorldAiTranslator.Core.dll",
            "RimWorldAiTranslator.Native.dll",
            "rimworld-def-field-rules.txt",
            "glossary.generated.ko.json"
        };
        return names.Select(name =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(productRoot, name);
            if (!File.Exists(path)) return (object)new { name, present = false, bytes = 0L, sha256 = string.Empty, fileVersion = string.Empty };
            var file = new FileInfo(path);
            string version;
            try { version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty; }
            catch { version = "unavailable"; }
            return new { name, present = true, bytes = file.Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), fileVersion = version };
        }).ToArray();
    }

    private static Dictionary<string, int> BuildErrorSummary(IReadOnlyList<string> sourceLines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["rateLimit"] = 0, ["timeout"] = 0, ["network"] = 0, ["json"] = 0,
            ["xml"] = 0, ["xlsx"] = 0, ["accessDenied"] = 0, ["path"] = 0,
            ["cancellation"] = 0, ["processExit"] = 0, ["otherError"] = 0,
            ["linesExamined"] = 0, ["linesOmitted"] = 0
        };
        foreach (var raw in sourceLines.Take(20_000))
        {
            var line = raw.Length > 8192 ? raw[..8192] : raw;
            counts["linesExamined"]++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var category = ClassifyError(line);
            if (category is not null) counts[category]++;
        }
        counts["linesOmitted"] = Math.Max(0, sourceLines.Count - counts["linesExamined"]);
        return counts;
    }

    private static string? ClassifyError(string line)
    {
        if (Regex.IsMatch(line, "(?i)(\\b429\\b|rate.?limit|too many requests)")) return "rateLimit";
        if (Regex.IsMatch(line, "(?i)(timeout|timed out|시간\\s*초과)")) return "timeout";
        if (Regex.IsMatch(line, "(?i)(network|connection|dns|socket|http request|네트워크|연결\\s*실패)")) return "network";
        if (Regex.IsMatch(line, "(?i)(json|schema|deserialize|직렬화)")) return "json";
        if (Regex.IsMatch(line, "(?i)(xlsx|workbook|spreadsheet)")) return "xlsx";
        if (Regex.IsMatch(line, "(?i)(xml|languageData)")) return "xml";
        if (Regex.IsMatch(line, "(?i)(access.*denied|unauthorizedaccess|권한|액세스.*거부)")) return "accessDenied";
        if (Regex.IsMatch(line, "(?i)(path|directory|file not found|경로|폴더|파일을\\s*찾)")) return "path";
        if (Regex.IsMatch(line, "(?i)(cancel|cancell|취소|중지\\s*요청)")) return "cancellation";
        if (Regex.IsMatch(line, "(?i)(exit.?code|process.*exit|프로세스\\s*종료|종료\\s*코드)")) return "processExit";
        if (Regex.IsMatch(line, "(?i)(error|failed|failure|exception|오류|실패)")) return "otherError";
        return null;
    }

    private static object UrlSummary(string value)
    {
        var configured = !string.IsNullOrWhiteSpace(value);
        if (!configured || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return new { configured, valid = false, scheme = string.Empty, loopback = false, hasQuery = false };
        return new { configured = true, valid = true, scheme = uri.Scheme, loopback = uri.IsLoopback, hasQuery = !string.IsNullOrWhiteSpace(uri.Query) };
    }

    private static string KnownValue(string? value, IEnumerable<string> allowed, string fallback = "other")
    {
        foreach (var candidate in allowed)
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
        return string.IsNullOrWhiteSpace(value) ? "unspecified" : fallback;
    }

    private static string LanguageCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return "unspecified";
        if (value.StartsWith("English", StringComparison.OrdinalIgnoreCase)) return "english";
        if (value.StartsWith("Chinese", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("SimplifiedChinese", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("TraditionalChinese", StringComparison.OrdinalIgnoreCase)) return "chinese";
        if (value.StartsWith("Japanese", StringComparison.OrdinalIgnoreCase)) return "japanese";
        return "other";
    }

    private static bool ContainsPrivateContent(string value) => Regex.IsMatch(value,
        "(?i)(csk-|sk-[A-Za-z0-9_-]{16,}|[A-Za-z]:\\\\|\\\\\\\\[^\\\\\\r\\n]+\\\\)", RegexOptions.CultureInvariant);

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Increment(IDictionary<string, int> counts, string key) => counts[key] = counts.TryGetValue(key, out var value) ? value + 1 : 1;
}
