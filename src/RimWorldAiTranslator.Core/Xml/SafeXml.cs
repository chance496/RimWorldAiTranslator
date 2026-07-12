using System.Xml;
using System.Xml.Linq;

namespace RimWorldAiTranslator.Core.Xml;

public static class SafeXml
{
    public const long DefaultMaximumCharacters = 134_217_728;

    public static XDocument Load(string path, long maximumCharacters = DefaultMaximumCharacters)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("XML file was not found.", path);
        }

        if (info.Length > maximumCharacters)
        {
            throw new InvalidDataException($"XML file is too large: {path}");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = maximumCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }
}
