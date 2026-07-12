using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Core.Discovery;

public sealed record RimWorldModContainer(string Path, string Source);

public sealed record RimWorldModInfo(
    string Display,
    string Name,
    string Path,
    string Source,
    string Folder,
    string PackageId,
    string WorkshopId,
    string Search);

public sealed record SourceLanguageOption(
    string Folder,
    string Path,
    string Display,
    int Rank,
    int XmlCount);

public sealed partial class RimWorldModDiscoveryService
{
    public Task<IReadOnlyList<RimWorldModInfo>> DiscoverAsync(
        IEnumerable<string>? knownSteamRoots = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Discover(knownSteamRoots, cancellationToken), cancellationToken);

    public IReadOnlyList<RimWorldModInfo> Discover(
        IEnumerable<string>? knownSteamRoots = null,
        CancellationToken cancellationToken = default)
    {
        var mods = new List<RimWorldModInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in GetModContainers(knownSteamRoots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(container.Path, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = GetModInfo(child, container.Source);
                if (info is not null && seen.Add(info.Path)) mods.Add(info);
            }
        }

        return mods
            .OrderBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(mod => mod.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<RimWorldModContainer> GetModContainers(IEnumerable<string>? knownSteamRoots = null)
    {
        var containers = new List<RimWorldModContainer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetSteamLibraryRoots(knownSteamRoots))
        {
            AddContainer(Path.Combine(root, "steamapps", "workshop", "content", "294100"), "Workshop");
            AddContainer(Path.Combine(root, "steamapps", "common", "RimWorld", "Mods"), "Local");
        }
        return containers;

        void AddContainer(string path, string source)
        {
            if (!Directory.Exists(path)) return;
            var full = PathSafety.Normalize(path);
            if (seen.Add(full)) containers.Add(new RimWorldModContainer(full, source));
        }
    }

    public IReadOnlyList<string> GetSteamLibraryRoots(IEnumerable<string>? knownSteamRoots = null)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in knownSteamRoots ?? GetSteamRootCandidates()) AddDirectory(root);
        foreach (var root in roots.ToArray())
        {
            foreach (var library in ReadSteamLibraries(root)) AddDirectory(library);
        }
        return roots;

        void AddDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = PathSafety.Normalize(path);
                if (Directory.Exists(full) && seen.Add(full)) roots.Add(full);
            }
            catch
            {
                // Invalid and inaccessible discovery candidates are ignored.
            }
        }
    }

    public RimWorldModInfo? GetModInfo(string modPath, string source)
    {
        if (!Directory.Exists(modPath)) return null;
        var fullModPath = PathSafety.Normalize(modPath);
        var aboutPath = FindAboutPath(fullModPath);
        var preferredRoot = GetPreferredLoadFolderRoot(fullModPath);
        var effectiveRoot = preferredRoot ?? fullModPath;
        if (aboutPath is not null && preferredRoot is null)
        {
            var aboutRoot = Directory.GetParent(Path.GetDirectoryName(aboutPath)!)?.FullName;
            if (!string.IsNullOrWhiteSpace(aboutRoot) && PathSafety.IsStrictlyInside(aboutRoot, fullModPath))
                effectiveRoot = PathSafety.Normalize(aboutRoot);
        }

        if (aboutPath is null
            && !Directory.Exists(Path.Combine(effectiveRoot, "Defs"))
            && !Directory.Exists(Path.Combine(effectiveRoot, "Languages"))) return null;

        var name = Path.GetFileName(effectiveRoot);
        var packageId = string.Empty;
        if (aboutPath is not null)
        {
            try
            {
                var document = SafeXml.Load(aboutPath);
                var root = document.Root;
                var aboutName = root?.Elements().FirstOrDefault(element => element.Name.LocalName == "name")?.Value.Trim();
                var aboutPackage = root?.Elements().FirstOrDefault(element => element.Name.LocalName == "packageId")?.Value.Trim();
                if (!string.IsNullOrWhiteSpace(aboutName)) name = aboutName;
                if (!string.IsNullOrWhiteSpace(aboutPackage)) packageId = aboutPackage;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
            {
                // A malformed About.xml does not hide an otherwise usable mod.
            }
        }

        var folder = Path.GetFileName(fullModPath);
        var effectiveLeaf = Path.GetFileName(effectiveRoot);
        var workshopId = GetWorkshopId(fullModPath);
        var tag = !string.IsNullOrWhiteSpace(workshopId)
            ? effectiveLeaf.Equals(folder, StringComparison.OrdinalIgnoreCase) ? $"W:{workshopId}" : $"W:{workshopId}\\{effectiveLeaf}"
            : source.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "Local" : source;
        var displayName = name.Length > 44 ? name[..41] + "..." : name;
        var search = string.Join(' ', name, folder, effectiveLeaf, packageId, workshopId, fullModPath, effectiveRoot).ToLowerInvariant();
        return new RimWorldModInfo($"{displayName} [{tag}]", name, effectiveRoot, source, folder, packageId, workshopId, search);
    }

    public string ResolveContentRoot(string modPath)
    {
        var full = PathSafety.Normalize(modPath);
        if (Directory.Exists(Path.Combine(full, "Defs")) || Directory.Exists(Path.Combine(full, "Languages"))) return full;
        return GetPreferredLoadFolderRoot(full) ?? full;
    }

    public IReadOnlyList<SourceLanguageOption> GetSourceLanguageOptions(string modRoot)
    {
        var root = Path.Combine(PathSafety.Normalize(modRoot), "Languages");
        if (!Directory.Exists(root)) return [];
        var result = new List<SourceLanguageOption>();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var folder = Path.GetFileName(directory);
            if (KoreanFolderRegex().IsMatch(folder)) continue;
            int count;
            try { count = Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories).Count(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (count == 0) continue;
            var name = GetLanguageDisplayName(folder);
            result.Add(new SourceLanguageOption(folder, PathSafety.Normalize(directory), $"{name}  ·  {folder}  ·  XML {count}개", GetLanguageRank(folder), count));
        }
        return result.OrderBy(option => option.Rank).ThenBy(option => option.Folder, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> GetSteamRootCandidates()
    {
        var values = new List<string>();
        if (!OperatingSystem.IsWindows()) return values;
        foreach (var (hive, subKey) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam")
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                values.Add(Convert.ToString(key?.GetValue("SteamPath")) ?? string.Empty);
                values.Add(Convert.ToString(key?.GetValue("InstallPath")) ?? string.Empty);
            }
            catch
            {
                // Registry discovery is best effort.
            }
        }
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        values.Add(Path.Combine(programFilesX86, "Steam"));
        values.Add(Path.Combine(programFiles, "Steam"));
        values.Add(Path.Combine(local, "Steam"));
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                values.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
                values.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
            }
            catch
            {
                // A drive can disappear during discovery.
            }
        }
        return values;
    }

    private static IEnumerable<string> ReadSteamLibraries(string steamRoot)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf")
                 })
        {
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (Match match in VdfPathRegex().Matches(text)) yield return match.Groups[1].Value.Replace("\\\\", "\\");
            foreach (Match match in LegacyVdfPathRegex().Matches(text)) yield return match.Groups[1].Value.Replace("\\\\", "\\");
        }
    }

    private static string? FindAboutPath(string modPath)
    {
        var direct = Path.Combine(modPath, "About", "About.xml");
        if (File.Exists(direct)) return direct;
        try
        {
            foreach (var child in Directory.EnumerateDirectories(modPath, "*", SearchOption.TopDirectoryOnly))
            {
                var nested = Path.Combine(child, "About", "About.xml");
                if (File.Exists(nested)) return nested;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    private static string? GetPreferredLoadFolderRoot(string modPath)
    {
        var path = Path.Combine(modPath, "LoadFolders.xml");
        if (!File.Exists(path)) return null;
        try
        {
            var document = SafeXml.Load(path);
            var versions = document.Root?.Elements()
                .Where(element => VersionFolderRegex().IsMatch(element.Name.LocalName))
                .OrderByDescending(element => VersionScore(element.Name.LocalName))
                ?? Enumerable.Empty<XElement>().OrderByDescending(_ => 0);
            foreach (var version in versions)
            {
                foreach (var item in version.Elements().Where(element => element.Name.LocalName == "li"))
                {
                    if (item.Attributes().Any(attribute => attribute.Name.LocalName is "IfModActive" or "IfModNotActive")) continue;
                    var relative = item.Value.Trim();
                    if (string.IsNullOrWhiteSpace(relative)) continue;
                    var candidate = relative is "/" or "\\" or "." ? modPath : Path.Combine(modPath, relative);
                    if (!Directory.Exists(candidate)) continue;
                    var full = PathSafety.Normalize(candidate);
                    if (!full.Equals(modPath, StringComparison.OrdinalIgnoreCase) && !PathSafety.IsStrictlyInside(full, modPath)) continue;
                    if (Directory.Exists(Path.Combine(full, "Defs")) || Directory.Exists(Path.Combine(full, "Languages"))) return full;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            return null;
        }
        return null;
    }

    private static int VersionScore(string value)
    {
        var score = 0;
        foreach (Match number in DigitsRegex().Matches(value)) score = score * 100 + int.Parse(number.Value, System.Globalization.CultureInfo.InvariantCulture);
        return score;
    }

    private static string GetWorkshopId(string path)
    {
        var match = WorkshopPathRegex().Match(path);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static int GetLanguageRank(string name) => name switch
    {
        "English" => 0,
        _ when name.StartsWith("ChineseSimplified", StringComparison.OrdinalIgnoreCase) => 10,
        _ when name.StartsWith("ChineseTraditional", StringComparison.OrdinalIgnoreCase) => 11,
        _ when name.StartsWith("Japanese", StringComparison.OrdinalIgnoreCase) => 20,
        _ when name.StartsWith("Spanish", StringComparison.OrdinalIgnoreCase) => 40,
        _ when name.StartsWith("French", StringComparison.OrdinalIgnoreCase) => 41,
        _ when name.StartsWith("German", StringComparison.OrdinalIgnoreCase) => 42,
        _ when name.StartsWith("Russian", StringComparison.OrdinalIgnoreCase) => 43,
        _ => 100
    };

    private static string GetLanguageDisplayName(string folder) => folder switch
    {
        _ when folder.StartsWith("English", StringComparison.OrdinalIgnoreCase) => "영어",
        _ when folder.StartsWith("ChineseSimplified", StringComparison.OrdinalIgnoreCase) => "중국어 간체",
        _ when folder.StartsWith("ChineseTraditional", StringComparison.OrdinalIgnoreCase) => "중국어 번체",
        _ when folder.StartsWith("Japanese", StringComparison.OrdinalIgnoreCase) => "일본어",
        _ when folder.StartsWith("Spanish", StringComparison.OrdinalIgnoreCase) => "스페인어",
        _ when folder.StartsWith("French", StringComparison.OrdinalIgnoreCase) => "프랑스어",
        _ when folder.StartsWith("German", StringComparison.OrdinalIgnoreCase) => "독일어",
        _ when folder.StartsWith("Russian", StringComparison.OrdinalIgnoreCase) => "러시아어",
        _ when folder.StartsWith("Portuguese", StringComparison.OrdinalIgnoreCase) => "포르투갈어",
        _ when folder.StartsWith("Polish", StringComparison.OrdinalIgnoreCase) => "폴란드어",
        _ => folder
    };

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant)] private static partial Regex VdfPathRegex();
    [GeneratedRegex("(?m)^\\s*\"\\d+\"\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant)] private static partial Regex LegacyVdfPathRegex();
    [GeneratedRegex(@"[\\/]workshop[\\/]content[\\/]294100[\\/](\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex WorkshopPathRegex();
    [GeneratedRegex("^v\\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex VersionFolderRegex();
    [GeneratedRegex("\\d+", RegexOptions.CultureInvariant)] private static partial Regex DigitsRegex();
    [GeneratedRegex("^(Korean|KoreanLegacy|한국)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex KoreanFolderRegex();
}
