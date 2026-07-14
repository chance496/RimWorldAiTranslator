using System.Security.Cryptography;
using System.Text;

namespace RimWorldAiTranslator.Core.Utilities;

public static class StableIdentity
{
    public static string Sha256(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ProjectId(string modRoot, string? packageId, string? workshopId)
    {
        var basis = !string.IsNullOrWhiteSpace(packageId)
            ? $"pkg:{packageId}"
            : !string.IsNullOrWhiteSpace(workshopId)
                ? $"ws:{workshopId}"
                : Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Sha256(basis)[..16];
    }

    public static string ReviewRowId(string target, string key) => Sha256($"{target}|{key}")[..16];
}
