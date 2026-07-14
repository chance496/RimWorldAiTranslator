namespace RimWorldAiTranslator.Core.Review;

internal static class ReviewTargetIdentity
{
    private static readonly HashSet<string> ReservedDeviceNames = CreateReservedDeviceNames();

    public static string Canonicalize(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return string.Empty;
        if (Path.IsPathRooted(target))
            throw new InvalidDataException($"Review target identity must be relative: {target}");

        var segments = new List<string>();
        foreach (var segment in target.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
                throw new InvalidDataException($"Review target identity cannot traverse to a parent folder: {target}");
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.EndsWith(' ')
                || segment.EndsWith('.'))
            {
                throw new InvalidDataException($"Review target identity contains an invalid path segment: {target}");
            }
            var deviceStem = segment.Split('.', 2)[0];
            if (ReservedDeviceNames.Contains(deviceStem))
                throw new InvalidDataException($"Review target identity contains a reserved Windows device name: {target}");
            segments.Add(segment);
        }

        if (segments.Count == 0)
            throw new InvalidDataException($"Review target identity has no file path segments: {target}");
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static HashSet<string> CreateReservedDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL"
        };
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }
        return names;
    }
}
