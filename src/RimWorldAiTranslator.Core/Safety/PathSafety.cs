namespace RimWorldAiTranslator.Core.Safety;

public static class PathSafety
{
    public static bool IsStrictlyInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Normalize(path);
            var fullRoot = Normalize(root);
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string ResolveInside(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Expected a relative path.");
        }

        var fullRoot = Normalize(root);
        var fullPath = Normalize(Path.Combine(fullRoot, relativePath));
        if (!IsStrictlyInside(fullPath, fullRoot))
        {
            throw new InvalidDataException($"Path escapes the allowed root: {relativePath}");
        }

        return fullPath;
    }

    public static bool ContainsReparsePoint(string path, string stopRoot)
    {
        try
        {
            var stop = Normalize(stopRoot);
            var current = new DirectoryInfo(Normalize(path));
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if (Normalize(current.FullName).Equals(stop, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = current.Parent;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
