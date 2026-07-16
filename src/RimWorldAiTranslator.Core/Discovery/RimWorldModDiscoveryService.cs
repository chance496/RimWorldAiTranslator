using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
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

internal sealed class RimWorldModDiscoveryLimits
{
    public int MaximumKnownSteamRoots { get; init; } = 256;
    public int MaximumSteamRootCandidates { get; init; } = 4_096;
    public int MaximumSteamLibraryEntries { get; init; } = 4_096;
    public long MaximumSteamLibraryManifestBytes { get; init; } = 16L * 1024 * 1024;
    public int MaximumDiscoveredMods { get; init; } = 10_000;
    public int MaximumCandidateDirectories { get; init; } = 100_000;
    public int MaximumNestedAboutDirectories { get; init; } = 2_048;
    public long MaximumAboutXmlBytes { get; init; } = 1 * 1024 * 1024;
    public int MaximumAboutXmlDepth { get; init; } = 32;
    public int MaximumAboutXmlNodes { get; init; } = 10_000;
    public int MaximumAboutRootChildren { get; init; } = 2_048;
    public int MaximumAboutMetadataElements { get; init; } = 16;
    public int MaximumAboutMetadataValueCharacters { get; init; } = 4_096;
    public long MaximumLoadFoldersXmlBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumLoadFoldersXmlDepth { get; init; } = 32;
    public int MaximumLoadFoldersXmlNodes { get; init; } = 50_000;
    public int MaximumLoadFoldersRootChildren { get; init; } = 1_024;
    public int MaximumLoadFolderVersions { get; init; } = 256;
    public int MaximumLoadFolderChildrenPerVersion { get; init; } = 4_096;
    public int MaximumLoadFolderItems { get; init; } = 16_384;
    public int MaximumLoadFolderItemAttributes { get; init; } = 64;
    public int MaximumLoadFolderValueCharacters { get; init; } = 4_096;
    public int MaximumSearchCharacters { get; init; } = 16_384;

    public void Validate()
    {
        foreach (var value in new long[]
                 {
                     MaximumKnownSteamRoots,
                     MaximumSteamRootCandidates,
                     MaximumSteamLibraryEntries,
                     MaximumSteamLibraryManifestBytes,
                     MaximumDiscoveredMods,
                     MaximumCandidateDirectories,
                     MaximumNestedAboutDirectories,
                     MaximumAboutXmlBytes,
                     MaximumAboutXmlDepth,
                     MaximumAboutXmlNodes,
                     MaximumAboutRootChildren,
                     MaximumAboutMetadataElements,
                     MaximumAboutMetadataValueCharacters,
                     MaximumLoadFoldersXmlBytes,
                     MaximumLoadFoldersXmlDepth,
                     MaximumLoadFoldersXmlNodes,
                     MaximumLoadFoldersRootChildren,
                     MaximumLoadFolderVersions,
                     MaximumLoadFolderChildrenPerVersion,
                     MaximumLoadFolderItems,
                     MaximumLoadFolderItemAttributes,
                     MaximumLoadFolderValueCharacters,
                     MaximumSearchCharacters
                 })
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        }
    }
}

public sealed partial class RimWorldModDiscoveryService
{
    private const int MaximumDiscoveryMarkerBytes = 4 * 1024;
    private const int MaximumSourceLanguageDirectories = 256;
    private const int MaximumSourceLanguageTreeDirectories = 10_000;
    private const int MaximumSourceLanguageXmlFiles = 100_000;
    public const string IsolationMarkerFileName = ".rimworld-ai-translator-isolated-discovery";
    public const string IsolationMarkerContent = "RimWorldAiTranslator isolated discovery root v1";

    private readonly string? isolatedSteamRoot;
    private readonly RimWorldModDiscoveryLimits limits;
    internal Action<string>? BeforeSteamRootProbeTestHook { get; set; }

    public RimWorldModDiscoveryService() : this(null, new RimWorldModDiscoveryLimits())
    {
    }

    internal RimWorldModDiscoveryService(RimWorldModDiscoveryLimits limits) : this(null, limits)
    {
    }

    private RimWorldModDiscoveryService(string? isolatedSteamRoot, RimWorldModDiscoveryLimits limits)
    {
        limits.Validate();
        this.isolatedSteamRoot = isolatedSteamRoot;
        this.limits = limits;
    }

    public bool IsIsolated => isolatedSteamRoot is not null;

    public string? IsolatedSteamRoot => isolatedSteamRoot;

    public static RimWorldModDiscoveryService CreateIsolated(string syntheticSteamRoot)
    {
        if (string.IsNullOrWhiteSpace(syntheticSteamRoot) || !Path.IsPathFullyQualified(syntheticSteamRoot))
            throw new InvalidDataException("An absolute synthetic Steam root is required for isolated discovery.");
        if (PathSafety.IsNetworkPath(syntheticSteamRoot))
            throw new InvalidDataException("The isolated discovery root cannot be a network path.");

        var fullRoot = PathSafety.Normalize(syntheticSteamRoot);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"The isolated discovery root does not exist: {fullRoot}");
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The isolated discovery root cannot be a reparse point.");
        fullRoot = PathSafety.GetCanonicalExistingDirectory(fullRoot);

        var marker = Path.Combine(fullRoot, IsolationMarkerFileName);
        if (!File.Exists(marker)
            || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0
            || !Encoding.UTF8.GetString(BoundedFileReader.ReadAllBytes(
                    marker,
                    MaximumDiscoveryMarkerBytes,
                    "isolated discovery marker"))
                .Trim()
                .Equals(IsolationMarkerContent, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The isolated discovery root is missing its valid {IsolationMarkerFileName} marker.");
        }

        return new RimWorldModDiscoveryService(fullRoot, new RimWorldModDiscoveryLimits());
    }

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
        var candidateDirectories = 0;
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
                LogSkipped("container enumeration", ex);
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++candidateDirectories > limits.MaximumCandidateDirectories)
                    throw new InvalidDataException(
                        $"Mod discovery inspected more than {limits.MaximumCandidateDirectories:N0} candidate directories.");
                RimWorldModInfo? info;
                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                    var canonicalChild = PathSafety.GetCanonicalExistingDirectory(child);
                    if (!PathSafety.IsStrictlyInside(canonicalChild, container.Path))
                        throw new InvalidDataException("A discovered mod directory resolves outside its selected container.");
                    info = GetModInfo(canonicalChild, container.Source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    LogSkipped("mod candidate", ex);
                    continue;
                }
                if (info is null || !seen.Add(info.Path)) continue;
                if (mods.Count >= limits.MaximumDiscoveredMods)
                    throw new InvalidDataException(
                        $"Mod discovery found more than {limits.MaximumDiscoveredMods:N0} mods.");
                mods.Add(info);
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
            AddContainer(root, Path.Combine("steamapps", "workshop", "content", "294100"), "Workshop");
            AddContainer(root, Path.Combine("steamapps", "common", "RimWorld", "Mods"), "Local");
        }
        return containers;

        void AddContainer(string root, string relativePath, string source)
        {
            try
            {
                var canonicalRoot = GetCanonicalDiscoveryRoot(root);
                var full = NormalizeDiscoveryPath(Path.Combine(canonicalRoot, relativePath));
                if (!Directory.Exists(full)) return;
                var canonical = GetCanonicalDirectoryInside(full, canonicalRoot, allowRoot: false);
                if (seen.Add(canonical)) containers.Add(new RimWorldModContainer(canonical, source));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                LogSkipped("mod container", ex);
            }
        }
    }

    public IReadOnlyList<string> GetSteamLibraryRoots(IEnumerable<string>? knownSteamRoots = null)
    {
        if (isolatedSteamRoot is not null)
        {
            if (knownSteamRoots is not null)
            {
                var requestedRoots = MaterializeKnownSteamRoots(knownSteamRoots);
                if (requestedRoots.Any(PathSafety.IsNetworkPath))
                    throw new InvalidOperationException("Isolated discovery cannot inspect a network Steam root.");
                var requested = requestedRoots
                    .Select(GetCanonicalDiscoveryRoot)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (requested.Length != 1 || !requested[0].Equals(isolatedSteamRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Isolated discovery cannot inspect a Steam root outside its synthetic root.");
            }
            return [isolatedSteamRoot];
        }

        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateCount = 0;
        if (knownSteamRoots is null)
        {
            foreach (var root in GetSteamRootCandidates()) AddDirectory(root);
        }
        else
        {
            foreach (var root in MaterializeKnownSteamRoots(knownSteamRoots)) AddDirectory(root);
        }
        foreach (var root in roots.ToArray())
        {
            foreach (var library in ReadSteamLibraries(root)) AddDirectory(library);
        }
        return roots;

        void AddDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (++candidateCount > limits.MaximumSteamRootCandidates)
            {
                throw new InvalidDataException(
                    $"Steam discovery contains more than {limits.MaximumSteamRootCandidates:N0} root candidates.");
            }
            if (PathSafety.IsNetworkPath(path)) return;
            BeforeSteamRootProbeTestHook?.Invoke(path);
            try
            {
                var full = GetCanonicalDiscoveryRoot(path);
                if (seen.Add(full)) roots.Add(full);
            }
            catch (Exception ex)
            {
                LogSkipped("candidate normalization", ex);
            }
        }
    }

    public RimWorldModInfo? GetModInfo(string modPath, string source)
    {
        if (isolatedSteamRoot is null && !Directory.Exists(modPath)) return null;
        var fullModPath = GetCanonicalModRoot(modPath);
        if (isolatedSteamRoot is not null && !Directory.Exists(fullModPath)) return null;
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
            && !HasSafeContentDirectory(effectiveRoot, fullModPath, "Defs")
            && !HasSafeContentDirectory(effectiveRoot, fullModPath, "Languages")) return null;

        var name = Path.GetFileName(effectiveRoot);
        var packageId = string.Empty;
        if (aboutPath is not null)
        {
            try
            {
                var document = SafeXml.LoadBounded(
                    aboutPath,
                    limits.MaximumAboutXmlBytes,
                    limits.MaximumAboutXmlDepth,
                    limits.MaximumAboutXmlNodes);
                var root = document.Root;
                var (aboutName, aboutPackage) = root is null
                    ? (null, null)
                    : ReadAboutMetadata(root);
                if (!string.IsNullOrWhiteSpace(aboutName)) name = aboutName;
                if (!string.IsNullOrWhiteSpace(aboutPackage)) packageId = aboutPackage;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
            {
                LogSkipped("About.xml metadata", ex);
            }
        }

        var folder = Path.GetFileName(fullModPath);
        var effectiveLeaf = Path.GetFileName(effectiveRoot);
        var workshopId = GetWorkshopId(fullModPath);
        var tag = !string.IsNullOrWhiteSpace(workshopId)
            ? effectiveLeaf.Equals(folder, StringComparison.OrdinalIgnoreCase) ? $"W:{workshopId}" : $"W:{workshopId} / {effectiveLeaf}"
            : source.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "Local" : source;
        var displayName = name.Length > 44 ? name[..41] + "..." : name;
        var search = BuildBoundedSearch(name, folder, effectiveLeaf, packageId, workshopId, fullModPath, effectiveRoot);
        return new RimWorldModInfo($"{displayName} [{tag}]", name, effectiveRoot, source, folder, packageId, workshopId, search);
    }

    public string ResolveContentRoot(string modPath)
    {
        var full = GetCanonicalModRoot(modPath);
        if (HasSafeContentDirectory(full, full, "Defs")
            || HasSafeContentDirectory(full, full, "Languages")) return full;
        return GetPreferredLoadFolderRoot(full) ?? full;
    }

    public IReadOnlyList<SourceLanguageOption> GetSourceLanguageOptions(string modRoot)
    {
        var normalizedModRoot = GetCanonicalModRoot(modRoot);
        var root = NormalizeDiscoveryPath(Path.Combine(normalizedModRoot, "Languages"));
        if (!Directory.Exists(root)) return [];
        var canonicalModRoot = normalizedModRoot;
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(root);
        if (!PathSafety.IsStrictlyInside(canonicalRoot, canonicalModRoot))
            throw new InvalidDataException("The Languages directory resolves outside the selected mod root.");
        var result = new List<SourceLanguageOption>();
        var languageDirectories = 0;
        foreach (var discoveredDirectory in Directory.EnumerateDirectories(canonicalRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (++languageDirectories > MaximumSourceLanguageDirectories)
                throw new InvalidDataException($"The mod contains more than {MaximumSourceLanguageDirectories:N0} source-language directories.");
            var attributes = File.GetAttributes(discoveredDirectory);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
            var directory = PathSafety.GetCanonicalExistingDirectory(discoveredDirectory);
            if (!PathSafety.IsStrictlyInside(directory, canonicalRoot))
                throw new InvalidDataException("A source-language directory resolves outside the selected Languages root.");
            var folder = Path.GetFileName(directory);
            if (KoreanFolderRegex().IsMatch(folder)) continue;
            int count;
            try { count = CountXmlFilesInside(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                LogSkipped("source-language enumeration", ex);
                continue;
            }
            if (count == 0) continue;
            var name = GetLanguageDisplayName(folder);
            result.Add(new SourceLanguageOption(folder, PathSafety.Normalize(directory), $"{name}  ·  {folder}  ·  XML {count}개", GetLanguageRank(folder), count));
        }
        return result.OrderBy(option => option.Rank).ThenBy(option => option.Folder, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int CountXmlFilesInside(string root)
    {
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(root);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(canonicalRoot);
        var fileCount = 0;
        while (pending.Count > 0)
        {
            var directory = PathSafety.GetCanonicalExistingDirectory(pending.Pop());
            if (!directory.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
                && !PathSafety.IsStrictlyInside(directory, canonicalRoot))
            {
                throw new InvalidDataException("A source-language subtree resolves outside its selected language root.");
            }
            if (!visited.Add(directory)) continue;
            if (visited.Count > MaximumSourceLanguageTreeDirectories)
                throw new InvalidDataException($"A source-language tree contains more than {MaximumSourceLanguageTreeDirectories:N0} directories.");

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                if (!Path.GetExtension(entry).Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                var canonicalFile = PathSafety.GetCanonicalExistingFile(entry);
                if (!PathSafety.IsStrictlyInside(canonicalFile, canonicalRoot))
                    throw new InvalidDataException("A source-language XML file resolves outside its selected language root.");
                if (++fileCount > MaximumSourceLanguageXmlFiles)
                    throw new InvalidDataException($"A source-language tree contains more than {MaximumSourceLanguageXmlFiles:N0} XML files.");
            }
        }
        return fileCount;
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
                values.Add(Convert.ToString(key?.GetValue("SteamPath"), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                values.Add(Convert.ToString(key?.GetValue("InstallPath"), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            }
            catch (Exception ex)
            {
                LogSkipped("registry candidate", ex);
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
            catch (Exception ex)
            {
                LogSkipped("drive candidate", ex);
            }
        }
        return values;
    }

    private IEnumerable<string> ReadSteamLibraries(string steamRoot)
    {
        var entryCount = 0;
        foreach (var path in new[]
                 {
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf")
                 })
        {
            if (!File.Exists(path)) continue;
            string text;
            try
            {
                text = Encoding.UTF8.GetString(BoundedFileReader.ReadAllBytes(
                    path,
                    limits.MaximumSteamLibraryManifestBytes,
                    "Steam library manifest"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSkipped("Steam library manifest", ex);
                continue;
            }
            foreach (Match match in VdfPathRegex().Matches(text))
            {
                ReserveEntry();
                yield return match.Groups[1].Value.Replace("\\\\", "\\");
            }
            foreach (Match match in LegacyVdfPathRegex().Matches(text))
            {
                ReserveEntry();
                yield return match.Groups[1].Value.Replace("\\\\", "\\");
            }
        }

        void ReserveEntry()
        {
            if (++entryCount > limits.MaximumSteamLibraryEntries)
            {
                throw new InvalidDataException(
                    $"Steam library manifests contain more than {limits.MaximumSteamLibraryEntries:N0} path entries.");
            }
        }
    }

    private string? FindAboutPath(string modPath)
    {
        try
        {
            var direct = TryGetCanonicalFileInside(Path.Combine(modPath, "About", "About.xml"), modPath);
            if (direct is not null) return direct;
            var scanned = 0;
            foreach (var discoveredChild in Directory.EnumerateDirectories(modPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (++scanned > limits.MaximumNestedAboutDirectories)
                    throw new InvalidDataException(
                        $"Nested About.xml lookup inspected more than {limits.MaximumNestedAboutDirectories:N0} directories.");
                var attributes = File.GetAttributes(discoveredChild);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                var child = GetCanonicalDirectoryInside(discoveredChild, modPath, allowRoot: false);
                var nested = TryGetCanonicalFileInside(Path.Combine(child, "About", "About.xml"), modPath);
                if (nested is not null) return nested;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogSkipped("nested About.xml lookup", ex);
            return null;
        }
        return null;
    }

    private string? GetPreferredLoadFolderRoot(string modPath)
    {
        try
        {
            var path = TryGetCanonicalFileInside(Path.Combine(modPath, "LoadFolders.xml"), modPath);
            if (path is null) return null;
            var document = SafeXml.LoadBounded(
                path,
                limits.MaximumLoadFoldersXmlBytes,
                limits.MaximumLoadFoldersXmlDepth,
                limits.MaximumLoadFoldersXmlNodes);
            var root = document.Root;
            if (root is null) return null;
            var versions = ReadBoundedLoadFolderVersions(root);
            ValidateLoadFolderEntries(versions);
            foreach (var version in versions)
            {
                foreach (var item in version.Element.Elements())
                {
                    if (!item.Name.LocalName.Equals("li", StringComparison.Ordinal)) continue;
                    if (HasConditionalLoadFolderAttribute(item)) continue;
                    var relative = ReadBoundedText(
                        item,
                        limits.MaximumLoadFolderValueCharacters,
                        "LoadFolders.xml folder entry",
                        rejectChildElements: true);
                    if (string.IsNullOrWhiteSpace(relative)) continue;
                    var full = ResolveSafeLoadFolderCandidate(modPath, relative);
                    if (full is null) continue;
                    if (HasSafeContentDirectory(full, modPath, "Defs")
                        || HasSafeContentDirectory(full, modPath, "Languages")) return full;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            LogSkipped("LoadFolders.xml lookup", ex);
            return null;
        }
        return null;
    }

    private (string? Name, string? PackageId) ReadAboutMetadata(XElement root)
    {
        string? name = null;
        string? packageId = null;
        var childCount = 0;
        var metadataCount = 0;
        foreach (var element in root.Elements())
        {
            if (++childCount > limits.MaximumAboutRootChildren)
                throw new InvalidDataException(
                    $"About.xml contains more than {limits.MaximumAboutRootChildren:N0} root child elements.");
            var localName = element.Name.LocalName;
            if (!localName.Equals("name", StringComparison.Ordinal)
                && !localName.Equals("packageId", StringComparison.Ordinal)) continue;
            if (++metadataCount > limits.MaximumAboutMetadataElements)
                throw new InvalidDataException(
                    $"About.xml contains more than {limits.MaximumAboutMetadataElements:N0} metadata values.");
            var value = ReadBoundedText(
                element,
                limits.MaximumAboutMetadataValueCharacters,
                $"About.xml {localName}",
                rejectChildElements: false);
            if (localName.Equals("name", StringComparison.Ordinal)) name ??= value;
            else packageId ??= value;
        }
        return (name, packageId);
    }

    private IReadOnlyList<(XElement Element, int Score, int Order)> ReadBoundedLoadFolderVersions(XElement root)
    {
        var versions = new List<(XElement Element, int Score, int Order)>();
        var childCount = 0;
        var order = 0;
        foreach (var element in root.Elements())
        {
            if (++childCount > limits.MaximumLoadFoldersRootChildren)
                throw new InvalidDataException(
                    $"LoadFolders.xml contains more than {limits.MaximumLoadFoldersRootChildren:N0} root child elements.");
            var localName = element.Name.LocalName;
            if (!IsVersionFolderElementName(localName)) continue;
            if (versions.Count >= limits.MaximumLoadFolderVersions)
                throw new InvalidDataException(
                    $"LoadFolders.xml contains more than {limits.MaximumLoadFolderVersions:N0} version elements.");
            versions.Add((element, VersionScore(localName), order++));
        }
        versions.Sort(static (left, right) =>
        {
            var score = right.Score.CompareTo(left.Score);
            return score != 0 ? score : left.Order.CompareTo(right.Order);
        });
        return versions;
    }

    private void ValidateLoadFolderEntries(
        IReadOnlyList<(XElement Element, int Score, int Order)> versions)
    {
        var totalItems = 0;
        foreach (var version in versions)
        {
            var childCount = 0;
            foreach (var item in version.Element.Elements())
            {
                if (++childCount > limits.MaximumLoadFolderChildrenPerVersion)
                    throw new InvalidDataException(
                        $"A LoadFolders.xml version contains more than {limits.MaximumLoadFolderChildrenPerVersion:N0} child elements.");
                if (!item.Name.LocalName.Equals("li", StringComparison.Ordinal)) continue;
                if (++totalItems > limits.MaximumLoadFolderItems)
                    throw new InvalidDataException(
                        $"LoadFolders.xml contains more than {limits.MaximumLoadFolderItems:N0} folder entries.");
                _ = HasConditionalLoadFolderAttribute(item);
                _ = ReadBoundedText(
                    item,
                    limits.MaximumLoadFolderValueCharacters,
                    "LoadFolders.xml folder entry",
                    rejectChildElements: true);
            }
        }
    }

    private IReadOnlyList<string> MaterializeKnownSteamRoots(IEnumerable<string> knownSteamRoots)
    {
        var roots = new List<string>(Math.Min(limits.MaximumKnownSteamRoots, 16));
        foreach (var root in knownSteamRoots)
        {
            if (roots.Count >= limits.MaximumKnownSteamRoots)
            {
                throw new InvalidDataException(
                    $"Steam discovery contains more than {limits.MaximumKnownSteamRoots:N0} configured roots.");
            }
            roots.Add(root);
        }
        return roots;
    }

    private bool HasConditionalLoadFolderAttribute(XElement item)
    {
        var count = 0;
        var conditional = false;
        foreach (var attribute in item.Attributes())
        {
            if (++count > limits.MaximumLoadFolderItemAttributes)
                throw new InvalidDataException(
                    $"A LoadFolders.xml entry contains more than {limits.MaximumLoadFolderItemAttributes:N0} attributes.");
            if (attribute.Name.LocalName is "IfModActive" or "IfModNotActive") conditional = true;
        }
        return conditional;
    }

    private static string ReadBoundedText(
        XElement element,
        int maximumCharacters,
        string context,
        bool rejectChildElements)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 256));
        foreach (var node in element.DescendantNodes())
        {
            if (rejectChildElements && node is XElement)
                throw new InvalidDataException($"{context} must contain text only.");
            if (node is not XText text) continue;
            if (text.Value.Length > maximumCharacters - builder.Length)
                throw new InvalidDataException($"{context} exceeds {maximumCharacters:N0} characters.");
            builder.Append(text.Value);
        }
        return builder.ToString().Trim();
    }

    private string? ResolveSafeLoadFolderCandidate(string modPath, string relative)
    {
        try
        {
            string candidate;
            if (relative is "/" or "\\" or ".")
            {
                candidate = modPath;
            }
            else
            {
                if (Path.IsPathFullyQualified(relative) || Path.IsPathRooted(relative)) return null;
                candidate = NormalizeDiscoveryPath(Path.Combine(modPath, relative));
                if (!PathSafety.IsStrictlyInside(candidate, modPath)) return null;
            }
            if (!Directory.Exists(candidate)) return null;
            return GetCanonicalDirectoryInside(candidate, modPath, allowRoot: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            LogSkipped("LoadFolders.xml child path", ex);
            return null;
        }
    }

    private bool HasSafeContentDirectory(string contentRoot, string modRoot, string name)
    {
        var path = NormalizeDiscoveryPath(Path.Combine(contentRoot, name));
        if (!Directory.Exists(path)) return false;
        try
        {
            _ = GetCanonicalDirectoryInside(path, modRoot, allowRoot: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogSkipped("content directory", ex);
            return false;
        }
    }

    private string GetCanonicalModRoot(string path)
    {
        var full = NormalizeDiscoveryPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Mod root not found: {full}");
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("A selected mod root cannot be a redirected directory.");
        var canonical = PathSafety.GetCanonicalExistingDirectory(full);
        if (isolatedSteamRoot is not null
            && !canonical.Equals(isolatedSteamRoot, StringComparison.OrdinalIgnoreCase)
            && !PathSafety.IsStrictlyInside(canonical, isolatedSteamRoot))
        {
            throw new InvalidDataException("A selected mod root resolves outside isolated discovery.");
        }
        return canonical;
    }

    private string GetCanonicalDiscoveryRoot(string path)
    {
        var full = NormalizeDiscoveryPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Discovery root not found: {full}");
        var attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.Device) != 0)
            throw new InvalidDataException("A discovery root cannot be a device path.");
        var canonical = PathSafety.GetCanonicalExistingDirectory(full);
        if (isolatedSteamRoot is not null
            && !canonical.Equals(isolatedSteamRoot, StringComparison.OrdinalIgnoreCase)
            && !PathSafety.IsStrictlyInside(canonical, isolatedSteamRoot))
        {
            throw new InvalidDataException("A discovery root resolves outside isolated discovery.");
        }
        return canonical;
    }

    private string GetCanonicalDirectoryInside(string path, string root, bool allowRoot)
    {
        var full = NormalizeDiscoveryPath(path);
        var fullRoot = NormalizeDiscoveryPath(root);
        if (!(allowRoot && full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            && !PathSafety.IsStrictlyInside(full, fullRoot))
        {
            throw new InvalidDataException("A discovery directory is outside its selected root.");
        }
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Discovery directory not found: {full}");
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("A discovery directory cannot be redirected.");
        var canonical = PathSafety.GetCanonicalExistingDirectory(full);
        if (!canonical.Equals(full, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A discovery directory contains a redirected path component.");
        if (!(allowRoot && canonical.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            && !PathSafety.IsStrictlyInside(canonical, fullRoot))
        {
            throw new InvalidDataException("A discovery directory resolves outside its selected root.");
        }
        return canonical;
    }

    private string? TryGetCanonicalFileInside(string path, string root)
    {
        var full = NormalizeDiscoveryPath(path);
        if (!File.Exists(full)) return null;
        if (!PathSafety.IsStrictlyInside(full, root))
            throw new InvalidDataException("A discovery metadata file is outside its selected mod root.");
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("A discovery metadata file cannot be redirected.");
        var canonical = PathSafety.GetCanonicalExistingFile(full);
        if (!canonical.Equals(full, StringComparison.OrdinalIgnoreCase)
            || !PathSafety.IsStrictlyInside(canonical, root))
        {
            throw new InvalidDataException("A discovery metadata file resolves outside its selected mod root.");
        }
        return canonical;
    }

    private string BuildBoundedSearch(params string[] values)
    {
        var builder = new StringBuilder(Math.Min(limits.MaximumSearchCharacters, 512));
        for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
        {
            if (valueIndex > 0 && builder.Length < limits.MaximumSearchCharacters) builder.Append(' ');
            foreach (var character in values[valueIndex])
            {
                if (builder.Length >= limits.MaximumSearchCharacters) return builder.ToString();
                builder.Append(char.ToLowerInvariant(character));
            }
        }
        return builder.ToString();
    }

    private string NormalizeDiscoveryPath(string path)
    {
        var full = PathSafety.Normalize(path);
        if (isolatedSteamRoot is null) return full;
        if (!full.Equals(isolatedSteamRoot, StringComparison.OrdinalIgnoreCase)
            && !PathSafety.IsStrictlyInside(full, isolatedSteamRoot))
        {
            throw new InvalidOperationException("Isolated discovery refused a path outside its synthetic Steam root.");
        }

        EnsureNoReparseComponents(full, isolatedSteamRoot);
        return full;
    }

    private static void LogSkipped(string operation, Exception exception) =>
        Debug.WriteLine($"Discovery {operation} skipped ({exception.GetType().Name}).");

    private static void EnsureNoReparseComponents(string path, string root)
    {
        var cursor = root;
        if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Isolated discovery refused a reparse point.");

        var relative = Path.GetRelativePath(root, path);
        if (relative.Equals(".", StringComparison.Ordinal)) return;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            if (!Directory.Exists(cursor) && !File.Exists(cursor)) break;
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Isolated discovery refused a reparse point.");
        }
    }

    private static bool IsVersionFolderElementName(string value) =>
        value.Length is >= 2 and <= 64
        && value[0] is 'v' or 'V'
        && char.IsDigit(value[1]);

    private static int VersionScore(string value)
    {
        long score = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsAsciiDigit(value[index])) continue;
            var number = 0;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
            {
                number = Math.Min(9_999, number * 10 + value[index] - '0');
                index++;
            }
            index--;
            score = Math.Min(int.MaxValue, score * 100 + number);
        }
        return (int)score;
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
    [GeneratedRegex("^(Korean|KoreanLegacy|한국)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex KoreanFolderRegex();
}
