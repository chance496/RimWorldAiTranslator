namespace RimWorldAiTranslator.Core.Storage;

public sealed class AppDataPaths
{
    public AppDataPaths(string? root = null)
    {
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimWorldAiTranslator")
            : root);
    }

    public string Root { get; }
    public string Projects => Path.Combine(Root, "projects.json");
    public string Settings => Path.Combine(Root, "settings.json");
    public string ModCatalog => Path.Combine(Root, "mod-catalog.json");
    public string ProjectStats => Path.Combine(Root, "project-stats.json");
    public string Reviews => Path.Combine(Root, "reviews");
    public string Logs => Path.Combine(Root, "logs");
    public string Temp => Path.Combine(Root, "temp");

    public void EnsureExists()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Reviews);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Temp);
    }
}
