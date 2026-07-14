using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Extraction;

namespace RimWorldAiTranslator.GlossaryTool;

internal sealed record LanguageEntry(
    string Identity,
    string Key,
    string Text,
    string Kind,
    string TypeName,
    string Field);

internal sealed class LanguageEntryMap
{
    private readonly Dictionary<string, LanguageEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);

    public int Count => entries.Count;
    public IEnumerable<string> Identities => entries.Keys;

    public bool TryGetValue(string identity, out LanguageEntry entry) => entries.TryGetValue(identity, out entry!);

    public void Add(LanguageEntry entry)
    {
        if (entries.Count + ambiguous.Count >= SafeLimits.TranslationEntries)
            throw new InputDataException("An input contains too many translation entries.");
        if (ambiguous.Contains(entry.Identity)) return;
        if (entries.TryGetValue(entry.Identity, out var existing))
        {
            if (existing.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)
                && existing.Text.Equals(entry.Text, StringComparison.Ordinal)
                && existing.Kind.Equals(entry.Kind, StringComparison.OrdinalIgnoreCase)
                && existing.TypeName.Equals(entry.TypeName, StringComparison.OrdinalIgnoreCase)
                && existing.Field.Equals(entry.Field, StringComparison.OrdinalIgnoreCase))
                return;
            entries.Remove(entry.Identity);
            ambiguous.Add(entry.Identity);
            return;
        }
        entries[entry.Identity] = entry;
    }

    public void Merge(LanguageEntryMap source)
    {
        foreach (var identity in source.Identities.Order(StringComparer.OrdinalIgnoreCase))
            if (source.TryGetValue(identity, out var entry)) Add(entry);
    }
}

internal sealed class LanguageSources
{
    private static readonly Regex VersionDirectoryRegex = new("^[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);
    private readonly SourceExtractor sourceSafety;

    public LanguageSources(string rulesPath)
    {
        sourceSafety = new SourceExtractor(string.IsNullOrWhiteSpace(rulesPath) ? null : rulesPath);
    }

    public static LanguageEntryMap ReadLanguageRoot(string languageRoot, CancellationToken cancellationToken)
    {
        var root = SafePaths.RequireInputDirectory(languageRoot, "Language root");
        var map = new LanguageEntryMap();
        foreach (var file in SafeFileTree.Files(root, path => string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafePaths.RequireInputFile(file, "Language XML", SafeLimits.XmlFileBytes);
            var (kind, typeName) = NamespaceFromRelativePath(Path.GetRelativePath(root, file));
            if (kind.Length == 0) continue;
            IReadOnlyList<RimWorldTranslatorRawSourceEntry> rawEntries;
            try { rawEntries = RimWorldTranslatorRmkXlsxReader.ReadLanguageData(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
            {
                throw new InputDataException("A language XML file failed safe parsing.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var raw in rawEntries)
            {
                var key = (raw.Key ?? string.Empty).Trim();
                if (key.Length == 0) continue;
                var text = GlossaryFilter.Normalize(raw.Text ?? string.Empty);
                if (!GlossaryFilter.HasHumanText(text)) continue;
                map.Add(new LanguageEntry(Identity(kind, typeName, key), key, text, kind, typeName, raw.Field ?? string.Empty));
            }
        }
        return map;
    }

    public LanguageEntryMap ReadModSource(string modRoot, string gameVersion, CancellationToken cancellationToken)
    {
        var map = new LanguageEntryMap();
        foreach (var contentRoot in ContentRoots(modRoot, gameVersion, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var english = Path.Combine(contentRoot, "Languages", "English");
            if (Directory.Exists(english))
            {
                SafePaths.RejectReparseBetween(contentRoot, english, "Workshop language root");
                map.Merge(ReadLanguageRoot(english, cancellationToken));
            }

            var defs = Path.Combine(contentRoot, "Defs");
            if (!Directory.Exists(defs)) continue;
            SafePaths.RejectReparseBetween(contentRoot, defs, "Workshop Defs root");
            foreach (var file in SafeFileTree.Files(defs, path => string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase), cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SafePaths.RequireInputFile(file, "Defs XML", SafeLimits.XmlFileBytes);
                RimWorldTranslatorDefReadResult result;
                try { result = RimWorldTranslatorRmkXlsxReader.ReadDefsDetailed(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
                {
                    throw new InputDataException("A Defs XML file failed safe parsing.");
                }
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var raw in result.Entries)
                {
                    var key = (raw.Key ?? string.Empty).Trim();
                    var typeName = (raw.TypeName ?? string.Empty).Trim();
                    var field = (raw.Field ?? string.Empty).Trim();
                    var text = GlossaryFilter.Normalize(raw.Text ?? string.Empty);
                    if (key.Length == 0 || typeName.Length == 0 || !GlossaryFilter.HasHumanText(text)) continue;
                    if (sourceSafety.GetInternalIdentifierReason(key, "DefInjected", typeName, field) is not null) continue;
                    map.Add(new LanguageEntry(Identity("DefInjected", typeName, key), key, text, "DefInjected", typeName, field));
                }
            }
        }
        return map;
    }

    private static List<string> ContentRoots(string modRoot, string gameVersion, CancellationToken cancellationToken)
    {
        var root = SafePaths.RequireInputDirectory(modRoot, "Workshop mod root");
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string candidate)
        {
            if (!Directory.Exists(candidate)) return;
            var full = SafePaths.Normalize(candidate);
            if (!SafePaths.IsInsideOrEqual(full, root)) throw new InputDataException("A mod content root escaped its Workshop directory.");
            SafePaths.RejectReparseBetween(root, full, "Mod content root");
            if (!Directory.Exists(Path.Combine(full, "Defs")) && !Directory.Exists(Path.Combine(full, "Languages"))) return;
            if (seen.Add(full)) result.Add(full);
        }

        Add(root);
        foreach (var name in new[] { "Common", gameVersion }) Add(Path.Combine(root, name));
        return result;
    }

    private static (string Kind, string TypeName) NamespaceFromRelativePath(string relativePath)
    {
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0].Equals("Keyed", StringComparison.OrdinalIgnoreCase))
            return ("Keyed", string.Empty);
        if (parts.Length > 1 && parts[0].Equals("DefInjected", StringComparison.OrdinalIgnoreCase))
            return ("DefInjected", parts[1]);
        return (string.Empty, string.Empty);
    }

    private static string Identity(string kind, string typeName, string key) =>
        kind.Equals("Keyed", StringComparison.OrdinalIgnoreCase)
            ? $"Keyed|{key}"
            : $"DefInjected|{typeName}|{key}";
}
