using System.Globalization;

namespace RimWorldAiTranslator.Tooling;

internal static class PackageLayout
{
    public const string Configuration = "Release";
    public const string RuntimeIdentifier = "win-x64";
    public const string ApplicationFileName = "RimWorldAiTranslator.exe";
    public const string DataRootEnvironmentVariable = "RIMWORLD_TRANSLATOR_DATA_ROOT";
    public const string DiscoveryRootEnvironmentVariable = "RIMWORLD_TRANSLATOR_DISCOVERY_ROOT";
    public const string DiscoveryAckEnvironmentVariable = "RIMWORLD_TRANSLATOR_DISCOVERY_ACK_PATH";
    public const string DiscoveryMarkerFileName = ".rimworld-ai-translator-isolated-discovery";
    public const string DiscoveryMarkerContent = "RimWorldAiTranslator isolated discovery root v1";
    public const string DiscoveryAckContent = "RimWorldAiTranslator isolated discovery active v1";

    public static readonly IReadOnlySet<string> RuntimeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ApplicationFileName,
        "rimworld-def-field-rules.txt",
        "glossary.generated.ko.json"
    };

    public static readonly IReadOnlyDictionary<string, string> DocumentationSourceFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["README.md"] = "README.md",
            ["RELEASE_NOTES.md"] = Path.Combine("docs", "RELEASE_NOTES.md"),
            ["sample-glossary.txt"] = Path.Combine("docs", "sample-glossary.txt"),
            ["VERSION"] = "VERSION",
            ["LICENSE"] = "LICENSE",
            ["SECURITY.md"] = Path.Combine(".github", "SECURITY.md"),
            ["PRIVACY.md"] = Path.Combine("docs", "PRIVACY.md"),
            ["THIRD_PARTY_NOTICES.md"] = Path.Combine("docs", "THIRD_PARTY_NOTICES.md"),
            ["DOTNET_RUNTIME_LICENSE.txt"] = Path.Combine("docs", "DOTNET_RUNTIME_LICENSE.txt"),
            ["DOTNET_RUNTIME_THIRD_PARTY_NOTICES.txt"] = Path.Combine("docs", "DOTNET_RUNTIME_THIRD_PARTY_NOTICES.txt"),
            ["DOTNET_WINDOWSDESKTOP_LICENSE.txt"] = Path.Combine("docs", "DOTNET_WINDOWSDESKTOP_LICENSE.txt"),
            ["DOTNET_ASPNETCORE_THIRD_PARTY_NOTICES.txt"] = Path.Combine("docs", "DOTNET_ASPNETCORE_THIRD_PARTY_NOTICES.txt")
        };

    public static IReadOnlySet<string> DocumentationFiles { get; } = new HashSet<string>(
        DocumentationSourceFiles.Keys,
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> AllFiles { get; } = new HashSet<string>(
        RuntimeFiles.Concat(DocumentationFiles),
        StringComparer.OrdinalIgnoreCase);

    public static string ArchiveName(SemanticVersion version) =>
        $"RimWorldAiTranslator-v{version}-win-x64.zip";

    public static string ManifestName(SemanticVersion version) =>
        $"RimWorldAiTranslator-v{version}-win-x64.manifest.json";
}

internal sealed record SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string? Prerelease,
    string? BuildMetadata,
    string Original)
{
    public string NumericFileVersion => $"{Major}.{Minor}.{Patch}.0";

    public static SemanticVersion Read(string path)
    {
        var value = File.ReadAllText(path, System.Text.Encoding.ASCII).Trim();
        return Parse(value);
    }

    public static SemanticVersion Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Equals(value.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException("VERSION must contain one trimmed SemVer value.");

        var plus = value.IndexOf('+');
        var withoutBuild = plus >= 0 ? value[..plus] : value;
        var build = plus >= 0 ? value[(plus + 1)..] : null;
        if (plus >= 0 && (string.IsNullOrEmpty(build) || build.Contains('+')))
            throw new InvalidDataException($"VERSION has invalid build metadata: {value}");

        var dash = withoutBuild.IndexOf('-');
        if (dash == 0 || dash == withoutBuild.Length - 1)
            throw new InvalidDataException($"VERSION has an invalid prerelease identifier: {value}");

        var coreText = dash >= 0 ? withoutBuild[..dash] : withoutBuild;
        var core = coreText.Split('.');
        var prerelease = dash >= 0 ? withoutBuild[(dash + 1)..] : null;
        if (core.Length != 3)
            throw new InvalidDataException($"VERSION must have major.minor.patch: {value}");

        var major = ParseNumericIdentifier(core[0], "major", value);
        var minor = ParseNumericIdentifier(core[1], "minor", value);
        var patch = ParseNumericIdentifier(core[2], "patch", value);
        if (prerelease is not null)
            ValidateIdentifiers(prerelease, numericLeadingZeroForbidden: true, "prerelease", value);
        if (build is not null) ValidateIdentifiers(build, numericLeadingZeroForbidden: false, "build metadata", value);
        return new SemanticVersion(major, minor, patch, prerelease, build, value);
    }

    public override string ToString() => Original;

    private static int ParseNumericIdentifier(string identifier, string name, string original)
    {
        if (identifier.Length == 0
            || (identifier.Length > 1 && identifier[0] == '0')
            || !identifier.All(char.IsAsciiDigit)
            || !int.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value > ushort.MaxValue)
        {
            throw new InvalidDataException($"VERSION {name} is not a valid numeric PE-version component: {original}");
        }
        return value;
    }

    private static void ValidateIdentifiers(
        string value,
        bool numericLeadingZeroForbidden,
        string name,
        string original)
    {
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0
                || !identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
                || (numericLeadingZeroForbidden
                    && identifier.Length > 1
                    && identifier.All(char.IsAsciiDigit)
                    && identifier[0] == '0'))
            {
                throw new InvalidDataException($"VERSION has invalid {name}: {original}");
            }
        }
    }
}
