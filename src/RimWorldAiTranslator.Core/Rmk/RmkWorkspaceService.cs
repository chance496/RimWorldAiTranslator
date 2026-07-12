using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Rmk;

public sealed record RmkModListEntry(string WorkshopId, string ModName, string RelativeLocation, string PackageId);

public sealed record RmkTarget(
    string Root,
    string YamlPath,
    string LanguageRoot,
    string WorkbookPath,
    string WorkshopId,
    string ModName,
    string Version,
    string SourceRoot,
    string SourceKind);

public sealed partial class RmkWorkspaceService
{
    public const string WorkshopId = "3079466972";

    public bool IsWorkspaceRoot(string root, bool requireGit = true)
    {
        if (!Directory.Exists(root)) return false;
        var full = PathSafety.Normalize(root);
        return Directory.Exists(Path.Combine(full, "Data"))
            && File.Exists(Path.Combine(full, "ModList.tsv"))
            && (!requireGit || Directory.Exists(Path.Combine(full, ".git")) || File.Exists(Path.Combine(full, ".git")));
    }

    public IReadOnlyList<RmkModListEntry> ReadModList(string root)
    {
        if (!IsWorkspaceRoot(root, false)) return [];
        var path = Path.Combine(PathSafety.Normalize(root), "ModList.tsv");
        var entries = new List<RmkModListEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var columns = line.Split('\t');
            if (columns.Length < 4) continue;
            entries.Add(new RmkModListEntry(columns[0].Trim(), columns[1].Trim(), columns[2].Trim(), columns[3].Trim()));
        }
        return entries;
    }

    public string FindSubscriptionRoot(IEnumerable<string> steamLibraryRoots)
    {
        foreach (var steamRoot in steamLibraryRoots)
        {
            var candidate = Path.Combine(steamRoot, "steamapps", "workshop", "content", "294100", WorkshopId);
            if (IsWorkspaceRoot(candidate, false)) return PathSafety.Normalize(candidate);
        }
        return string.Empty;
    }

    public string FindWritableWorkspace(IEnumerable<string> localModContainers)
    {
        foreach (var container in localModContainers)
        {
            if (!Directory.Exists(container)) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(container, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                if (IsWorkspaceRoot(child, true)) return PathSafety.Normalize(child);
            }
        }
        return string.Empty;
    }

    public IReadOnlyList<RmkTarget> FindTargets(string workspaceRoot, TranslationProject project, string sourceKind = "작업 클론")
    {
        if (!IsWorkspaceRoot(workspaceRoot, false)) return [];
        var root = PathSafety.Normalize(workspaceRoot);
        var rows = ReadModList(root);
        var hasIndex = rows.Count > 0;
        var matches = !string.IsNullOrWhiteSpace(project.WorkshopId)
            ? rows.Where(row => row.WorkshopId.Equals(project.WorkshopId, StringComparison.Ordinal)).ToArray()
            : [];
        if (matches.Length == 0 && !string.IsNullOrWhiteSpace(project.PackageId))
            matches = rows.Where(row => row.PackageId.Equals(project.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hasIndex && matches.Length == 0) return [];

        var yamlPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in matches)
        {
            var relative = row.RelativeLocation.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string location;
            try { location = PathSafety.ResolveInside(root, relative); }
            catch { continue; }
            if (!Directory.Exists(location)) continue;
            var candidates = Directory.EnumerateDirectories(location, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.IsNullOrWhiteSpace(project.WorkshopId)
                    || Path.GetFileName(path).EndsWith(" - " + project.WorkshopId, StringComparison.OrdinalIgnoreCase));
            var expected = Path.Combine(location, $"{row.ModName} - {project.WorkshopId}");
            if (Directory.Exists(expected)) candidates = candidates.Prepend(expected);
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var yaml in Directory.EnumerateFiles(candidate, "LoadFolders.Build.yaml", SearchOption.AllDirectories)) yamlPaths.Add(yaml);
            }
        }

        if (yamlPaths.Count == 0 && !hasIndex)
        {
            var dataRoot = Path.Combine(root, "Data");
            foreach (var yaml in Directory.EnumerateFiles(dataRoot, "LoadFolders.Build.yaml", SearchOption.AllDirectories))
            {
                if (!string.IsNullOrWhiteSpace(project.WorkshopId)
                    && yaml.Contains(" - " + project.WorkshopId + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    yamlPaths.Add(yaml);
                else if (string.IsNullOrWhiteSpace(project.WorkshopId) && !string.IsNullOrWhiteSpace(project.PackageId))
                {
                    try { if (File.ReadAllText(yaml).Contains(project.PackageId, StringComparison.OrdinalIgnoreCase)) yamlPaths.Add(yaml); }
                    catch { }
                }
            }
        }

        var result = new List<RmkTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var yaml in yamlPaths)
        {
            var target = ReadTarget(yaml, root, sourceKind);
            if (target is null) continue;
            var idMatch = !string.IsNullOrWhiteSpace(project.WorkshopId) && target.WorkshopId.Equals(project.WorkshopId, StringComparison.Ordinal);
            var packageMatch = !string.IsNullOrWhiteSpace(project.PackageId) && File.ReadAllText(yaml).Contains(project.PackageId, StringComparison.OrdinalIgnoreCase);
            if ((idMatch || packageMatch) && seen.Add(target.Root)) result.Add(target);
        }
        return result;
    }

    public RmkTarget? SelectTarget(IEnumerable<RmkTarget> targets, string version)
    {
        var rows = targets.ToArray();
        return rows.FirstOrDefault(row => row.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(row => string.IsNullOrWhiteSpace(row.Version))
            ?? rows.OrderByDescending(row => ParseVersion(row.Version)).FirstOrDefault();
    }

    public RmkTarget CreateTarget(string workspaceRoot, TranslationProject project, string version)
    {
        if (!IsWorkspaceRoot(workspaceRoot, true)) throw new InvalidDataException("RMK Git workspace is invalid.");
        if (string.IsNullOrWhiteSpace(project.WorkshopId) || string.IsNullOrWhiteSpace(project.PackageId))
            throw new InvalidDataException("A Workshop ID and Package ID are required to create a new RMK target.");
        var root = PathSafety.Normalize(workspaceRoot);
        var dataRoot = Path.Combine(root, "Data");
        var safeName = SafeFolderName(project.Name);
        var targetRoot = PathSafety.ResolveInside(dataRoot, Path.Combine($"{safeName} - {project.WorkshopId}", version));
        var languageRoot = Path.Combine(targetRoot, "Languages", "Korean (한국어)");
        Directory.CreateDirectory(languageRoot);
        var yamlPath = Path.Combine(targetRoot, "LoadFolders.Build.yaml");
        if (!File.Exists(yamlPath))
        {
            var yaml = $"""
                BuildRule:
                  Binding:
                    PackageID: ["{Yaml(project.PackageId)}"]
                    Mode: "None"
                    Dependency: "Independent"
                  Order:
                    After:
                    Before:
                  Version:
                    Default: "{Yaml(version)}"
                    LeftBoundary:
                    RightBoundary:
                    Designate:
                    Ban:
                Metadata:
                  WorkshopID: "{Yaml(project.WorkshopId)}"
                  ModName: "{Yaml(project.Name)}"
                """;
            File.WriteAllText(yamlPath, yaml, new System.Text.UTF8Encoding(false));
        }
        return ReadTarget(yamlPath, root, "작업 클론") ?? throw new InvalidDataException("Created RMK target could not be read.");
    }

    public string GetRimWorldVersion(string modRoot, IEnumerable<string> steamLibraryRoots)
    {
        var fullMod = PathSafety.Normalize(modRoot);
        foreach (var steam in steamLibraryRoots)
        {
            var containers = new[]
            {
                Path.Combine(steam, "steamapps", "workshop", "content", "294100"),
                Path.Combine(steam, "steamapps", "common", "RimWorld", "Mods")
            };
            if (!containers.Any(container => Directory.Exists(container) && PathSafety.IsStrictlyInside(fullMod, container))) continue;
            var versionFile = Path.Combine(steam, "steamapps", "common", "RimWorld", "Version.txt");
            if (!File.Exists(versionFile)) continue;
            var match = VersionRegex().Match(File.ReadAllText(versionFile));
            if (match.Success) return match.Groups[1].Value;
        }
        return "1.6";
    }

    private static RmkTarget? ReadTarget(string yamlPath, string sourceRoot, string sourceKind)
    {
        try
        {
            var text = File.ReadAllText(yamlPath);
            var workshop = ValueRegex("WorkshopID").Match(text).Groups[1].Value.Trim();
            var name = ValueRegex("ModName").Match(text).Groups[1].Value.Trim().Trim('"', '\'');
            var defaultVersion = ValueRegex("Default").Match(text).Groups[1].Value.Trim();
            var root = PathSafety.Normalize(Path.GetDirectoryName(yamlPath)!);
            var leaf = Path.GetFileName(root);
            var version = VersionOnlyRegex().IsMatch(leaf) ? leaf : defaultVersion;
            var workbook = Directory.EnumerateFiles(root, "*.xlsx", SearchOption.TopDirectoryOnly)
                .OrderBy(path => !string.IsNullOrWhiteSpace(workshop) && Path.GetFileNameWithoutExtension(path).Contains(workshop, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? string.Empty;
            return new RmkTarget(root, Path.GetFullPath(yamlPath), Path.Combine(root, "Languages", "Korean (한국어)"), workbook, workshop, name, version, sourceRoot, sourceKind);
        }
        catch
        {
            return null;
        }
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    private static string SafeFolderName(string value)
    {
        var result = string.Concat((value ?? string.Empty).Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(result)) result = "RimWorld Mod";
        return result.Length > 100 ? result[..100].TrimEnd('.', ' ') : result;
    }

    private static string Yaml(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    private static Regex ValueRegex(string key) => new($"(?mi)^\\s*{Regex.Escape(key)}:\\s*[\"']?([^\"'\\s#]+|.+?)[\"']?\\s*$", RegexOptions.CultureInvariant);

    [GeneratedRegex("(\\d+\\.\\d+)", RegexOptions.CultureInvariant)] private static partial Regex VersionRegex();
    [GeneratedRegex("^\\d+\\.\\d+$", RegexOptions.CultureInvariant)] private static partial Regex VersionOnlyRegex();
}
