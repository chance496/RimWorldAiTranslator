using System.Xml;
using System.Xml.Linq;

namespace RimWorldAiTranslator.Core.Xml;

public static class SafeXml
{
    public const long DefaultMaximumCharacters = 134_217_728;
    public const int DefaultMaximumDepth = 256;
    public const int DefaultMaximumNodes = 1_000_000;

    public static XDocument Load(string path, long maximumCharacters = DefaultMaximumCharacters)
        => Load(
            path,
            maximumCharacters,
            DefaultMaximumDepth,
            DefaultMaximumNodes,
            LoadOptions.None,
            ignoreComments: true,
            ignoreProcessingInstructions: true,
            null);

    internal static XDocument LoadBounded(
        string path,
        long maximumCharacters,
        int maximumDepth,
        int maximumNodes) =>
        Load(
            path,
            maximumCharacters,
            maximumDepth,
            maximumNodes,
            LoadOptions.None,
            ignoreComments: true,
            ignoreProcessingInstructions: true,
            null);

    internal static XDocument LoadBounded(
        Stream stream,
        long maximumCharacters,
        int maximumDepth = DefaultMaximumDepth,
        int maximumNodes = DefaultMaximumNodes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Load(
            stream,
            "pinned XML output",
            maximumCharacters,
            maximumDepth,
            maximumNodes,
            LoadOptions.None,
            ignoreComments: true,
            ignoreProcessingInstructions: true,
            null);
    }

    public static XDocument LoadPreservingWhitespace(
        string path,
        long maximumCharacters = DefaultMaximumCharacters)
        => Load(
            path,
            maximumCharacters,
            DefaultMaximumDepth,
            DefaultMaximumNodes,
            LoadOptions.PreserveWhitespace,
            ignoreComments: false,
            ignoreProcessingInstructions: false,
            null);

    internal static XDocument LoadWithStructureValidatedHook(string path, Action afterStructureValidated) =>
        Load(
            path,
            DefaultMaximumCharacters,
            DefaultMaximumDepth,
            DefaultMaximumNodes,
            LoadOptions.None,
            ignoreComments: true,
            ignoreProcessingInstructions: true,
            afterStructureValidated);

    private static XDocument Load(
        string path,
        long maximumCharacters,
        int maximumDepth,
        int maximumNodes,
        LoadOptions loadOptions,
        bool ignoreComments,
        bool ignoreProcessingInstructions,
        Action? afterStructureValidated)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Load(
            stream,
            path,
            maximumCharacters,
            maximumDepth,
            maximumNodes,
            loadOptions,
            ignoreComments,
            ignoreProcessingInstructions,
            afterStructureValidated);
    }

    private static XDocument Load(
        Stream stream,
        string description,
        long maximumCharacters,
        int maximumDepth,
        int maximumNodes,
        LoadOptions loadOptions,
        bool ignoreComments,
        bool ignoreProcessingInstructions,
        Action? afterStructureValidated)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNodes);
        if (!stream.CanRead || !stream.CanSeek)
            throw new InvalidDataException("XML input must be readable and seekable.");
        stream.Position = 0;
        if (stream.Length > maximumCharacters)
        {
            throw new InvalidDataException($"XML file is too large: {description}");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = maximumCharacters,
            IgnoreComments = ignoreComments,
            IgnoreProcessingInstructions = ignoreProcessingInstructions
        };
        ValidateStructure(stream, settings, maximumDepth, maximumNodes);
        afterStructureValidated?.Invoke();
        stream.Position = 0;
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, loadOptions);
    }

    private static void ValidateStructure(
        Stream stream,
        XmlReaderSettings settings,
        int maximumDepth,
        int maximumNodes)
    {
        using var reader = XmlReader.Create(stream, settings);
        var nodes = 0;
        while (reader.Read())
        {
            if (++nodes > maximumNodes)
                throw new InvalidDataException($"XML contains more than {maximumNodes:N0} nodes.");
            if (reader.Depth > maximumDepth)
                throw new InvalidDataException($"XML nesting exceeds the supported depth of {maximumDepth:N0}.");
        }
    }
}
