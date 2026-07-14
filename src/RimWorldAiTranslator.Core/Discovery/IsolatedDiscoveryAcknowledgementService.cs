using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Core.Discovery;

public sealed class IsolatedDiscoveryAcknowledgementService
{
    public const string Content = "RimWorldAiTranslator isolated discovery active v1\n";

    public void Write(string path, string dataRoot)
    {
        var fullRoot = PathSafety.Normalize(dataRoot);
        var fullPath = PathSafety.Normalize(path);
        if (!PathSafety.IsStrictlyInside(fullPath, fullRoot))
            throw new InvalidDataException("The isolated-discovery acknowledgement must remain inside the isolated data root.");
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new InvalidDataException("The isolated-discovery acknowledgement already exists.");
        PathSafety.EnsureNoReparsePoints(fullPath, fullRoot);
        AtomicFile.WriteUtf8(fullPath, Content, keepBackup: false);
    }
}
