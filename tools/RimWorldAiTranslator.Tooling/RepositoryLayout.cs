namespace RimWorldAiTranslator.Tooling;

internal sealed class RepositoryLayout
{
    private static readonly string[] RequiredMarkers =
    [
        "RimWorldAiTranslator.sln",
        "NuGet.config",
        "VERSION",
        Path.Combine("src", "RimWorldAiTranslator.App", "RimWorldAiTranslator.App.csproj"),
        Path.Combine("tests", "RimWorldAiTranslator.Tests", "RimWorldAiTranslator.Tests.csproj"),
        Path.Combine("tools", "RimWorldAiTranslator.GlossaryTool", "RimWorldAiTranslator.GlossaryTool.csproj")
    ];

    private RepositoryLayout(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        AssertNoReparsePoint(Root);
        Dist = RequireRepositoryPath(Path.Combine(Root, "dist"));
        Solution = RequireFile("RimWorldAiTranslator.sln");
        VersionFile = RequireFile("VERSION");
        NuGetConfig = RequireFile("NuGet.config");
        AppProject = RequireFile(Path.Combine("src", "RimWorldAiTranslator.App", "RimWorldAiTranslator.App.csproj"));
        TestsProject = RequireFile(Path.Combine("tests", "RimWorldAiTranslator.Tests", "RimWorldAiTranslator.Tests.csproj"));
        GlossaryProject = RequireFile(Path.Combine("tools", "RimWorldAiTranslator.GlossaryTool", "RimWorldAiTranslator.GlossaryTool.csproj"));
    }

    public string Root { get; }
    public string Dist { get; }
    public string Solution { get; }
    public string VersionFile { get; }
    public string NuGetConfig { get; }
    public string AppProject { get; }
    public string TestsProject { get; }
    public string GlossaryProject { get; }

    public static RepositoryLayout Find(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var full = Path.GetFullPath(explicitRoot);
            if (!IsRepositoryRoot(full))
                throw new DirectoryNotFoundException($"The requested repository root is missing required files: {full}");
            return new RepositoryLayout(full);
        }

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo? cursor = new(Path.GetFullPath(start));
            while (cursor is not null)
            {
                if (IsRepositoryRoot(cursor.FullName))
                    return new RepositoryLayout(cursor.FullName);
                cursor = cursor.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the RimWorld AI Translator repository root.");
    }

    public string RequireFile(string relativePath)
    {
        var full = RequireRepositoryPath(Path.Combine(Root, relativePath));
        if (!File.Exists(full))
            throw new FileNotFoundException($"Required repository file is missing: {relativePath}", full);
        AssertNoReparseComponents(full);
        return full;
    }

    public string RequireRepositoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Root, full);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing a path outside the repository: {full}");
        }

        AssertNoReparseComponents(full);
        return full;
    }

    public void AssertNoReparseComponents(string path)
    {
        var full = Path.GetFullPath(path);
        _ = RequireLexicallyInside(full);
        AssertNoReparsePoint(Root);

        var relative = Path.GetRelativePath(Root, full);
        if (relative == ".") return;

        var cursor = Root;
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, part);
            if (File.Exists(cursor) || Directory.Exists(cursor))
                AssertNoReparsePoint(cursor);
        }
    }

    public void AssertNoReparseTree(string directory)
    {
        directory = RequireRepositoryPath(directory);
        if (!Directory.Exists(directory)) return;

        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            AssertNoReparsePoint(current);
            foreach (var child in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                AssertNoReparsePoint(child);
                if (Directory.Exists(child)) pending.Push(child);
            }
        }
    }

    public string CreateOwnedWorkDirectory()
    {
        EnsureDistDirectory();
        var name = "_package-" + Guid.NewGuid().ToString("N");
        var path = RequireRepositoryPath(Path.Combine(Dist, name));
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"Run-unique work path already exists: {path}");
        Directory.CreateDirectory(path);
        AssertNoReparseComponents(path);
        return path;
    }

    public void DeleteOwnedWorkDirectory(string path)
    {
        path = RequireRepositoryPath(path);
        var name = Path.GetFileName(path);
        if (!Path.GetDirectoryName(path)!.Equals(Dist, StringComparison.OrdinalIgnoreCase)
            || name is null
            || !name.StartsWith("_package-", StringComparison.Ordinal)
            || name.Length != "_package-".Length + 32)
        {
            throw new InvalidOperationException($"Refusing to delete a path not owned by this packaging run: {path}");
        }

        if (!Directory.Exists(path)) return;
        AssertNoReparseTree(path);
        Directory.Delete(path, recursive: true);
    }

    public void EnsureDistDirectory()
    {
        _ = RequireRepositoryPath(Dist);
        Directory.CreateDirectory(Dist);
        AssertNoReparseComponents(Dist);
    }

    private string RequireLexicallyInside(string full)
    {
        var relative = Path.GetRelativePath(Root, full);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing a path outside the repository: {full}");
        return full;
    }

    private static bool IsRepositoryRoot(string path) =>
        Directory.Exists(path) && RequiredMarkers.All(marker => File.Exists(Path.Combine(path, marker)));

    private static void AssertNoReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Refusing a reparse-point path: {path}");
    }
}
