using System.Globalization;
using System.Text.RegularExpressions;

namespace RimWorldAiTranslator.GlossaryTool;

internal sealed record GlossaryObservation(
    string Source,
    string SourceKey,
    string Korean,
    string KoreanKey,
    string Key,
    string Kind,
    string TypeName,
    int Priority,
    string Origin,
    string WorkshopId);

internal sealed record RmkStats(int Observations, int ScannedMods, int PairedMods, int MissingSource, int MissingKorean);

internal sealed class GlossaryBuilder(BuildOptions options)
{
    private static readonly Regex WorkshopSegmentRegex = new(" - (?<id>[0-9]{6,20})$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionDirectoryRegex = new("^[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);
    private readonly LanguageSources sources = new(options.DefFieldRulesPath);

    public GlossaryBuildResult Build(CancellationToken cancellationToken)
    {
        var observations = new List<GlossaryObservation>();
        var officialCount = options.SkipOfficial ? 0 : ImportOfficial(observations, cancellationToken);
        var rmk = options.IncludeRmk ? ImportRmk(observations, cancellationToken) : new RmkStats(0, 0, 0, 0, 0);
        var (terms, conflicts) = Merge(observations, cancellationToken);
        var categories = new List<string>();
        if (!options.SkipOfficial) categories.AddRange(["official-core", "official-dlc"]);
        if (options.IncludeRmk) categories.Add("rmk");
        var priorities = new List<GlossaryPriority>
        {
            new() { Priority = 0, Origin = "official-core" },
            new() { Priority = 10, Origin = "official-dlc-*" }
        };
        if (options.IncludeRmk) priorities.Add(new GlossaryPriority { Priority = 100, Origin = "rmk" });

        var document = new GlossaryDocument
        {
            GameVersion = options.GameVersion,
            Sources = new GlossarySourceMetadata
            {
                Official = !options.SkipOfficial,
                Rmk = options.IncludeRmk,
                DiscoveryRequested = options.Discover,
                Categories = categories,
                CustomDefFieldRules = !string.IsNullOrWhiteSpace(options.DefFieldRulesPath)
            },
            PriorityOrder = priorities,
            Filters = new GlossaryFilterMetadata
            {
                MaxSourceChars = options.MaxSourceChars,
                MinRmkOccurrences = options.MinRmkOccurrences,
                IncludeSentences = options.IncludeSentences,
                IncludeRmk = options.IncludeRmk
            },
            Stats = new GlossaryStats
            {
                Observations = observations.Count,
                OfficialObservations = officialCount,
                RmkObservations = rmk.Observations,
                RmkScannedMods = rmk.ScannedMods,
                RmkPairedMods = rmk.PairedMods,
                RmkMissingSourceMods = rmk.MissingSource,
                RmkMissingKoreanMods = rmk.MissingKorean,
                Terms = terms.Count,
                Conflicts = conflicts.Count
            },
            Terms = terms
        };
        return new GlossaryBuildResult(document, conflicts);
    }

    private int ImportOfficial(List<GlossaryObservation> observations, CancellationToken cancellationToken)
    {
        var before = observations.Count;
        foreach (var packRoot in SafeFileTree.ImmediateDirectories(options.RimWorldDataRoot, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var languagesRoot = Path.Combine(packRoot, "Languages");
            var englishRoot = Path.Combine(languagesRoot, "English");
            if (!Directory.Exists(englishRoot) || !Directory.Exists(languagesRoot)) continue;
            SafePaths.RejectReparseBetween(packRoot, englishRoot, "Official English language root");

            string? koreanTar;
            try
            {
                koreanTar = new DirectoryInfo(languagesRoot).EnumerateFiles("Korean*.tar", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(file =>
                    {
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new InputDataException("An official Korean archive is a reparse point.");
                        return file.FullName;
                    })
                    .FirstOrDefault();
            }
            catch (GlossaryToolException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InputDataException("Official language archives could not be enumerated safely.");
            }
            if (koreanTar is null) continue;

            using var extracted = TemporaryDirectory.Create();
            SafeTarExtractor.Extract(koreanTar, extracted.Path, cancellationToken);
            var sourceMap = LanguageSources.ReadLanguageRoot(englishRoot, cancellationToken);
            var koreanMap = LanguageSources.ReadLanguageRoot(extracted.Path, cancellationToken);
            var packName = Path.GetFileName(packRoot) ?? string.Empty;
            var isCore = packName.Equals("Core", StringComparison.OrdinalIgnoreCase);
            AddPairs(sourceMap, koreanMap, observations, isCore ? "official-core" : "official-dlc-" + SafeCategory(packName),
                isCore ? 0 : 10, string.Empty, cancellationToken);
        }
        return observations.Count - before;
    }

    private RmkStats ImportRmk(List<GlossaryObservation> observations, CancellationToken cancellationToken)
    {
        var before = observations.Count;
        var rows = ReadModList(cancellationToken);
        var koreanRoots = IndexRmkKoreanRoots(cancellationToken);
        var wanted = options.WorkshopIds.ToHashSet(StringComparer.Ordinal);
        var scanned = 0;
        var paired = 0;
        var missingSource = 0;
        var missingKorean = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (wanted.Count > 0 && !wanted.Contains(row.WorkshopId)) continue;
            if (options.MaxRmkMods > 0 && scanned >= options.MaxRmkMods) break;
            scanned++;
            var modRoot = Path.Combine(options.WorkshopRoot, row.WorkshopId);
            if (!Directory.Exists(modRoot))
            {
                missingSource++;
                continue;
            }
            SafePaths.RejectReparseBetween(options.WorkshopRoot, modRoot, "Workshop mod root");
            if (!koreanRoots.TryGetValue(row.WorkshopId, out var roots))
            {
                missingKorean++;
                continue;
            }

            var koreanMap = new LanguageEntryMap();
            foreach (var root in roots.Order(StringComparer.OrdinalIgnoreCase))
                koreanMap.Merge(LanguageSources.ReadLanguageRoot(root, cancellationToken));
            if (koreanMap.Count == 0) continue;
            var sourceMap = sources.ReadModSource(modRoot, options.GameVersion, cancellationToken);
            if (sourceMap.Count == 0) continue;
            var pairBefore = observations.Count;
            AddPairs(sourceMap, koreanMap, observations, "rmk", 100, row.WorkshopId, cancellationToken);
            if (observations.Count > pairBefore) paired++;
        }

        return new RmkStats(observations.Count - before, scanned, paired, missingSource, missingKorean);
    }

    private List<RmkRow> ReadModList(CancellationToken cancellationToken)
    {
        var path = SafePaths.RequireInputFile(Path.Combine(options.RmkRoot, "ModList.tsv"), "RMK ModList.tsv", SafeLimits.ModListBytes);
        var rows = new List<RmkRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length > 64 * 1024) throw new InputDataException("RMK ModList.tsv contains an overlong row.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var id = parts[0].Trim();
            if (!Regex.IsMatch(id, "^[0-9]{1,20}$", RegexOptions.CultureInvariant)) continue;
            if (!seen.Add(id)) continue;
            rows.Add(new RmkRow(id));
            if (rows.Count > SafeLimits.FileSystemEntries) throw new InputDataException("RMK ModList.tsv has too many rows.");
        }
        return rows;
    }

    private Dictionary<string, List<string>> IndexRmkKoreanRoots(CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var xmlFiles = SafeFileTree.Files(options.RmkRoot,
            path => string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase), cancellationToken);
        foreach (var file in xmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(options.RmkRoot, file);
            var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var koreanIndex = -1;
            for (var indexPart = 1; indexPart < parts.Length; indexPart++)
            {
                if (parts[indexPart - 1].Equals("Languages", StringComparison.OrdinalIgnoreCase)
                    && parts[indexPart].StartsWith("Korean", StringComparison.OrdinalIgnoreCase))
                {
                    koreanIndex = indexPart;
                    break;
                }
            }
            if (koreanIndex < 0) continue;
            var idPart = parts.Take(koreanIndex).Select((segment, position) => (Match: WorkshopSegmentRegex.Match(segment), Position: position))
                .FirstOrDefault(value => value.Match.Success);
            var id = idPart.Match?.Groups["id"].Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var version = parts.Skip(idPart.Position + 1).Take(koreanIndex - idPart.Position - 1)
                .FirstOrDefault(segment => VersionDirectoryRegex.IsMatch(segment));
            if (!string.IsNullOrWhiteSpace(version) && !version.Equals(options.GameVersion, StringComparison.Ordinal)) continue;
            var root = Path.Combine(options.RmkRoot, Path.Combine(parts[..(koreanIndex + 1)]));
            if (!index.TryGetValue(id, out var roots))
            {
                roots = [];
                index[id] = roots;
            }
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
        }
        return index;
    }

    private void AddPairs(
        LanguageEntryMap sourceMap,
        LanguageEntryMap koreanMap,
        List<GlossaryObservation> observations,
        string origin,
        int priority,
        string workshopId,
        CancellationToken cancellationToken)
    {
        foreach (var identity in sourceMap.Identities.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceMap.TryGetValue(identity, out var source) || !koreanMap.TryGetValue(identity, out var korean)) continue;
            var sourceText = GlossaryFilter.Normalize(source.Text);
            var koreanText = GlossaryFilter.Normalize(korean.Text);
            if (!GlossaryFilter.IsPair(sourceText, koreanText, options.MaxSourceChars, options.IncludeSentences)) continue;
            if (observations.Count >= SafeLimits.TranslationEntries)
                throw new InputDataException("Glossary inputs contain too many paired observations.");
            observations.Add(new GlossaryObservation(
                sourceText,
                sourceText.ToLowerInvariant(),
                koreanText,
                koreanText.ToLowerInvariant(),
                source.Key,
                source.Kind,
                source.TypeName,
                priority,
                origin,
                workshopId));
        }
    }

    private (IReadOnlyList<GlossaryTermOutput> Terms, IReadOnlyList<GlossaryConflict> Conflicts) Merge(
        IReadOnlyList<GlossaryObservation> observations,
        CancellationToken cancellationToken)
    {
        var terms = new List<GlossaryTermOutput>();
        var conflicts = new List<GlossaryConflict>();
        foreach (var sourceGroup in observations.GroupBy(item => item.SourceKey, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = sourceGroup.ToArray();
            var choices = items.GroupBy(item => item.KoreanKey, StringComparer.Ordinal)
                .Select(group => CreateChoice(group.ToArray()))
                .ToArray();
            if (choices.Length == 0) continue;
            var phrase = WordCount(items[0].Source) > 1;
            var ordered = choices.OrderBy(choice => choice.Priority)
                .ThenByDescending(choice => choice.Count);
            var best = (phrase
                    ? ordered.ThenByDescending(choice => choice.Korean.Length)
                    : ordered.ThenBy(choice => choice.Korean.Length))
                .ThenBy(choice => choice.Korean, StringComparer.Ordinal)
                .First();
            if (best.Priority >= 100 && best.Count < options.MinRmkOccurrences) continue;

            var source = items.OrderBy(item => item.Priority).ThenBy(item => item.Source.Length)
                .ThenBy(item => item.Source, StringComparer.Ordinal).First().Source;
            var alternatives = choices.Where(choice => !choice.Korean.Equals(best.Korean, StringComparison.Ordinal))
                .OrderBy(choice => choice.Priority).ThenByDescending(choice => choice.Count)
                .ThenBy(choice => choice.Korean, StringComparer.Ordinal).Take(8)
                .Select(choice => new GlossaryAlternative
                {
                    Korean = choice.Korean,
                    Count = choice.Count,
                    Priority = choice.Priority,
                    Origins = choice.Origins
                }).ToArray();
            var confidence = best.Priority switch
            {
                0 => 1.00,
                < 100 => 0.95,
                _ when best.Count >= 5 && alternatives.Length == 0 => 0.88,
                _ when best.Count >= 2 => 0.80,
                _ => 0.70
            };
            if (alternatives.Length > 0 && confidence > 0.75) confidence -= 0.08;

            terms.Add(new GlossaryTermOutput
            {
                Source = source,
                Korean = best.Korean,
                Priority = best.Priority,
                Origin = string.Join(",", best.Origins),
                Confidence = Math.Round(confidence, 2, MidpointRounding.AwayFromZero),
                Count = best.Count,
                Keys = best.Keys,
                WorkshopIds = best.WorkshopIds,
                Alternatives = alternatives
            });
            if (alternatives.Length > 0)
            {
                conflicts.Add(new GlossaryConflict
                {
                    Source = source,
                    ChosenKorean = best.Korean,
                    ChosenPriority = best.Priority,
                    ChosenCount = best.Count,
                    Alternatives = string.Join(" | ", alternatives.Select(item => $"{item.Korean} [{item.Count.ToString(CultureInfo.InvariantCulture)}]"))
                });
            }
        }

        return (
            terms.OrderBy(term => term.Priority).ThenBy(term => term.Source, StringComparer.Ordinal).ToArray(),
            conflicts.OrderBy(conflict => conflict.Source, StringComparer.Ordinal).ToArray());
    }

    private static Choice CreateChoice(GlossaryObservation[] items)
    {
        var sample = items.OrderBy(item => item.Priority).ThenBy(item => item.Korean.Length)
            .ThenBy(item => item.Korean, StringComparer.Ordinal).First();
        return new Choice(
            sample.Korean,
            items.Length,
            items.Min(item => item.Priority),
            items.Select(item => item.Origin).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(12).ToArray(),
            items.Where(item => item.WorkshopId.Length > 0).Select(item => item.WorkshopId)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(12).ToArray());
    }

    private static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string SafeCategory(string value)
    {
        var sanitized = new string(value.Take(64).Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray());
        return sanitized.Length == 0 ? "unknown" : sanitized;
    }

    private sealed record Choice(
        string Korean,
        int Count,
        int Priority,
        IReadOnlyList<string> Origins,
        IReadOnlyList<string> Keys,
        IReadOnlyList<string> WorkshopIds);

    private sealed record RmkRow(string WorkshopId);
}

internal static partial class GlossaryFilter
{
    public static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n').Trim();

    public static bool HasHumanText(string value) => value.Any(char.IsLetter);

    public static bool IsPair(string source, string korean, int maxSourceChars, bool includeSentences)
    {
        if (!HasHumanText(source) || !ContainsKorean(korean) || source.Equals(korean, StringComparison.Ordinal)) return false;
        if (LooksLikeCodeOrPath(source) || LooksLikeCodeOrPath(korean)) return false;
        if (source.Length > maxSourceChars || korean.Length > Math.Max(120, maxSourceChars * 2)) return false;
        if (source.Contains('\n') || korean.Contains('\n') || ProtectedTokenRegex().IsMatch(source) || ProtectedTokenRegex().IsMatch(korean)) return false;
        if (!includeSentences)
        {
            if (source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 10) return false;
            if (SentenceEndRegex().IsMatch(source) || KoreanSentenceEndRegex().IsMatch(korean)) return false;
        }
        return true;
    }

    private static bool ContainsKorean(string value) => value.Any(character => character is >= '\uAC00' and <= '\uD7AF');

    private static bool LooksLikeCodeOrPath(string value)
    {
        var text = value.Trim();
        return Path.IsPathRooted(text)
            || NumberOnlyRegex().IsMatch(text)
            || DottedIdentifierRegex().IsMatch(text)
            || FilePathRegex().IsMatch(text)
            || SlashPathRegex().IsMatch(text)
            || BracedIdentifierRegex().IsMatch(text);
    }

    [GeneratedRegex("^[0-9\\s.,:+\\-/%°]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberOnlyRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.]*\\.[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedIdentifierRegex();

    [GeneratedRegex("^[A-Za-z0-9_./\\\\:-]+\\.(png|jpg|jpeg|dds|tga|wav|ogg|mp3|dll|asset|shader|xml)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilePathRegex();

    [GeneratedRegex("^[A-Za-z0-9_./\\\\:-]+/[A-Za-z0-9_./\\\\:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SlashPathRegex();

    [GeneratedRegex("^\\{[A-Za-z0-9_:.-]+\\}$", RegexOptions.CultureInvariant)]
    private static partial Regex BracedIdentifierRegex();

    [GeneratedRegex("\\{[A-Za-z0-9_:.-]+\\}|\\[[A-Za-z0-9_:.-]+\\]|\\$[A-Za-z_]|<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedTokenRegex();


    [GeneratedRegex("[.!?]\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEndRegex();

    [GeneratedRegex("[.!?。！？]\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex KoreanSentenceEndRegex();
}
