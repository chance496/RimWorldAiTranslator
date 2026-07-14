using System.Globalization;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Rmk;

namespace RimWorldAiTranslator.GlossaryTool;

internal sealed record BuildOptions
{
    public string RimWorldDataRoot { get; init; } = string.Empty;
    public string RmkRoot { get; init; } = string.Empty;
    public string WorkshopRoot { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public string ConflictPath { get; init; } = string.Empty;
    public string DefFieldRulesPath { get; init; } = string.Empty;
    public string GameVersion { get; init; } = "1.6";
    public IReadOnlyList<string> WorkshopIds { get; init; } = [];
    public int MaxSourceChars { get; init; } = 80;
    public int MinRmkOccurrences { get; init; } = 1;
    public int MaxRmkMods { get; init; }
    public bool IncludeSentences { get; init; }
    public bool IncludeRmk { get; init; }
    public bool SkipOfficial { get; init; }
    public bool Discover { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
            throw new GlossaryToolException("--output is required.");
        if (MaxSourceChars is < 1 or > 4096)
            throw new GlossaryToolException("--max-source-chars must be between 1 and 4096.");
        if (MinRmkOccurrences is < 1 or > 1_000_000)
            throw new GlossaryToolException("--min-rmk-occurrences must be between 1 and 1000000.");
        if (MaxRmkMods is < 0 or > 1_000_000)
            throw new GlossaryToolException("--max-rmk-mods must be between 0 and 1000000.");
        if (!Regex.IsMatch(GameVersion, "^[0-9]{1,3}\\.[0-9]{1,3}$", RegexOptions.CultureInvariant))
            throw new GlossaryToolException("--game-version must use major.minor digits.");
        if (WorkshopIds.Any(id => !Regex.IsMatch(id, "^[0-9]{1,20}$", RegexOptions.CultureInvariant)))
            throw new GlossaryToolException("Every --workshop-id must contain digits only.");

        if (!SkipOfficial)
            SafePaths.RequireInputDirectory(RimWorldDataRoot, "RimWorld data root");
        if (IncludeRmk)
        {
            SafePaths.RequireInputDirectory(RmkRoot, "RMK root");
            SafePaths.RequireInputDirectory(WorkshopRoot, "Workshop root");
        }
        if (!SkipOfficial && IncludeRmk && string.Equals(
                SafePaths.Normalize(RimWorldDataRoot), SafePaths.Normalize(RmkRoot), StringComparison.OrdinalIgnoreCase))
            throw new GlossaryToolException("Official and RMK roots must be distinct.");
        if (SkipOfficial && !IncludeRmk)
            throw new GlossaryToolException("No glossary source is enabled.");

        if (!string.IsNullOrWhiteSpace(DefFieldRulesPath))
            SafePaths.RequireInputFile(DefFieldRulesPath, "Def field rules", SafeLimits.RuleFileBytes);

        var inputRoots = new[] { RimWorldDataRoot, RmkRoot, WorkshopRoot }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(SafePaths.Normalize)
            .ToArray();
        var outputs = new[] { OutputPath, ConflictPath, OutputPath + ".bak", ConflictPath + ".bak" }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(SafePaths.Normalize)
            .ToArray();
        if (outputs.Any(output => inputRoots.Any(root => SafePaths.IsInsideOrEqual(output, root)))
            || (!string.IsNullOrWhiteSpace(DefFieldRulesPath)
                && outputs.Any(output => output.Equals(SafePaths.Normalize(DefFieldRulesPath), StringComparison.OrdinalIgnoreCase))))
            throw new GlossaryToolException("Outputs and backups must remain outside every read-only input root and file.");

        SafePaths.PrepareOutputPath(OutputPath, "JSON output");
        SafePaths.PrepareOutputPath(ConflictPath, "CSV output");
        if (string.Equals(SafePaths.Normalize(OutputPath), SafePaths.Normalize(ConflictPath), StringComparison.OrdinalIgnoreCase))
            throw new GlossaryToolException("JSON and CSV output paths must be distinct.");
    }
}

internal static class CommandLine
{
    public static BuildOptions ParseBuild(string[] args)
    {
        string dataRoot = string.Empty;
        string rmkRoot = string.Empty;
        string workshopRoot = string.Empty;
        string output = string.Empty;
        string conflicts = string.Empty;
        string rules = string.Empty;
        string gameVersion = "1.6";
        var workshopIds = new List<string>();
        var maxSourceChars = 80;
        var minRmkOccurrences = 1;
        var maxRmkMods = 0;
        var includeSentences = false;
        var includeRmk = false;
        var skipRmk = false;
        var skipOfficial = false;
        var discover = false;

        for (var index = 0; index < args.Length; index++)
        {
            var (name, inlineValue) = Split(args[index]);
            string Value()
            {
                if (inlineValue is not null) return inlineValue;
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new GlossaryToolException($"Missing value for {name}.");
                return args[index];
            }

            switch (name)
            {
                case "--rimworld-data-root": dataRoot = Value(); break;
                case "--rmk-root": rmkRoot = Value(); break;
                case "--workshop-root": workshopRoot = Value(); break;
                case "--output": output = Value(); break;
                case "--conflicts": conflicts = Value(); break;
                case "--def-field-rules": rules = Value(); break;
                case "--game-version": gameVersion = Value(); break;
                case "--workshop-id": workshopIds.Add(Value()); break;
                case "--max-source-chars": maxSourceChars = Integer(Value(), name); break;
                case "--min-rmk-occurrences": minRmkOccurrences = Integer(Value(), name); break;
                case "--max-rmk-mods": maxRmkMods = Integer(Value(), name); break;
                case "--include-sentences": Flag(inlineValue, name); includeSentences = true; break;
                case "--include-rmk": Flag(inlineValue, name); includeRmk = true; break;
                case "--skip-rmk": Flag(inlineValue, name); skipRmk = true; break;
                case "--skip-official": Flag(inlineValue, name); skipOfficial = true; break;
                case "--discover": Flag(inlineValue, name); discover = true; break;
                case "--include-patches":
                    throw new GlossaryToolException("Patch XML is intentionally unsupported because final game merge state cannot be proven safely.");
                default: throw new GlossaryToolException("An unsupported option was supplied.");
            }
        }

        if (string.IsNullOrWhiteSpace(conflicts) && !string.IsNullOrWhiteSpace(output))
            conflicts = Path.ChangeExtension(output, ".conflicts.csv")
                ?? throw new GlossaryToolException("The conflict output path could not be derived.");

        return new BuildOptions
        {
            RimWorldDataRoot = FullPathOrEmpty(dataRoot),
            RmkRoot = FullPathOrEmpty(rmkRoot),
            WorkshopRoot = FullPathOrEmpty(workshopRoot),
            OutputPath = FullPathOrEmpty(output),
            ConflictPath = FullPathOrEmpty(conflicts),
            DefFieldRulesPath = FullPathOrEmpty(rules),
            GameVersion = gameVersion.Trim(),
            WorkshopIds = workshopIds.Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Order().ToArray(),
            MaxSourceChars = maxSourceChars,
            MinRmkOccurrences = minRmkOccurrences,
            MaxRmkMods = maxRmkMods,
            IncludeSentences = includeSentences,
            IncludeRmk = includeRmk && !skipRmk,
            SkipOfficial = skipOfficial,
            Discover = discover
        };
    }

    private static (string Name, string? Value) Split(string argument)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal))
            throw new GlossaryToolException("An unexpected positional argument was supplied.");
        var separator = argument.IndexOf('=');
        return separator < 0 ? (argument, null) : (argument[..separator], argument[(separator + 1)..]);
    }

    private static void Flag(string? inlineValue, string name)
    {
        if (inlineValue is not null) throw new GlossaryToolException($"{name} does not accept a value.");
    }

    private static int Integer(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new GlossaryToolException($"{name} requires a decimal integer.");

    private static string FullPathOrEmpty(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.GetFullPath(value); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new GlossaryToolException("A supplied path is invalid.");
        }
    }
}

internal static class Discovery
{
    public static BuildOptions ResolveIfRequested(BuildOptions options, CancellationToken cancellationToken)
    {
        if (!options.Discover) return options;
        cancellationToken.ThrowIfCancellationRequested();

        var service = new RimWorldModDiscoveryService();
        var libraries = service.GetSteamLibraryRoots();
        string FirstDirectory(Func<string, string> selector) => libraries
            .Select(selector)
            .FirstOrDefault(Directory.Exists) ?? string.Empty;

        var dataRoot = options.RimWorldDataRoot;
        if (!options.SkipOfficial && string.IsNullOrWhiteSpace(dataRoot))
            dataRoot = FirstDirectory(root => Path.Combine(root, "steamapps", "common", "RimWorld", "Data"));

        var workshopRoot = options.WorkshopRoot;
        if (options.IncludeRmk && string.IsNullOrWhiteSpace(workshopRoot))
            workshopRoot = FirstDirectory(root => Path.Combine(root, "steamapps", "workshop", "content", "294100"));

        var rmkRoot = options.RmkRoot;
        if (options.IncludeRmk && string.IsNullOrWhiteSpace(rmkRoot))
        {
            rmkRoot = new RmkWorkspaceService().FindSubscriptionRoot(libraries) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rmkRoot))
                rmkRoot = FirstDirectory(root => Path.Combine(root, "steamapps", "common", "RimWorld", "Mods", "RMK"));
        }

        return options with
        {
            RimWorldDataRoot = Full(dataRoot),
            WorkshopRoot = Full(workshopRoot),
            RmkRoot = Full(rmkRoot)
        };
    }

    private static string Full(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value);
}
