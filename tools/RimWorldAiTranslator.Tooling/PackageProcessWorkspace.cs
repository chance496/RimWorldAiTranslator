namespace RimWorldAiTranslator.Tooling;

internal sealed class PackageProcessWorkspace : IDisposable
{
    internal const string DirectoryPrefix = "RwatPkg-";
    internal const int MaximumRootLength = 96;

    private readonly string temporaryRoot;
    private bool disposed;

    private PackageProcessWorkspace(string root, string temporaryRoot)
    {
        Root = root;
        this.temporaryRoot = temporaryRoot;
    }

    public string Root { get; }

    public static PackageProcessWorkspace Create()
    {
        var temporaryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        AssertTemporaryRoot(temporaryRoot);

        var name = DirectoryPrefix + Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(Path.Combine(temporaryRoot, name));
        AssertOwnedRoot(root, temporaryRoot);
        if (root.Length > MaximumRootLength)
        {
            throw new PathTooLongException(
                $"The package-process workspace root is too long for legacy Windows child tools ({root.Length} > {MaximumRootLength}).");
        }
        if (File.Exists(root) || Directory.Exists(root))
            throw new IOException($"The package-process workspace already exists: {root}");

        Directory.CreateDirectory(root);
        try
        {
            AssertNoReparseTree(root);
            if (Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly).Any())
                throw new IOException("The new package-process workspace was not empty.");
            return new PackageProcessWorkspace(root, temporaryRoot);
        }
        catch (Exception creationFailure)
        {
            try
            {
                if (Directory.Exists(root)
                    && !File.Exists(root)
                    && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
                {
                    AssertNoReparseTree(root);
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Package-process workspace creation and exact-root cleanup both failed.",
                    creationFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    public string CreateDirectory(string name) => CreateDirectoryPath(name);

    internal string CreateDirectoryPath(params string[] components)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (components.Length is 0 or > 8
            || components.Any(component =>
                string.IsNullOrWhiteSpace(component)
                || component is "." or ".."
                || !string.Equals(Path.GetFileName(component), component, StringComparison.Ordinal)
                || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidOperationException("A package-process child directory name is unsafe.");
        }

        var path = Path.GetFullPath(components.Aggregate(Root, Path.Combine));
        var relative = Path.GetRelativePath(Root, path);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A package-process child directory escaped its owned root.");
        }
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"The package-process child directory already exists: {path}");

        Directory.CreateDirectory(path);
        AssertNoReparseTree(Root);
        return path;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        AssertTemporaryRoot(temporaryRoot);
        AssertOwnedRoot(Root, temporaryRoot);
        if (File.Exists(Root))
            throw new InvalidOperationException(
                $"The package-process workspace root became a file and was preserved: {Root}");
        if (!Directory.Exists(Root)) return;
        AssertNoReparseTree(Root);
        Directory.Delete(Root, recursive: true);
        if (File.Exists(Root) || Directory.Exists(Root))
            throw new IOException($"The package-process workspace remained after deletion: {Root}");
    }

    private static void AssertTemporaryRoot(string temporaryRoot)
    {
        if (!Directory.Exists(temporaryRoot)
            || File.Exists(temporaryRoot)
            || (File.GetAttributes(temporaryRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"The package-process temporary root is missing or redirected: {temporaryRoot}");
        }
    }

    private static void AssertOwnedRoot(string root, string temporaryRoot)
    {
        var relative = Path.GetRelativePath(temporaryRoot, root);
        if (Path.IsPathRooted(relative)
            || relative.Contains(Path.DirectorySeparatorChar)
            || relative.Contains(Path.AltDirectorySeparatorChar)
            || !relative.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
            || relative.Length != DirectoryPrefix.Length + 32
            || !Guid.TryParseExact(relative[DirectoryPrefix.Length..], "N", out _))
        {
            throw new InvalidOperationException(
                $"Refusing an unverified package-process workspace: {root}");
        }
    }

    private static void AssertNoReparseTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    $"The package-process workspace contains a reparse point: {current}");
            if (!Directory.Exists(current)) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                pending.Push(child);
            }
        }
    }
}
