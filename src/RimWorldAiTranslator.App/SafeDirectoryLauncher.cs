using System.Diagnostics;

namespace RimWorldAiTranslator.App;

internal static class SafeDirectoryLauncher
{
    public static void Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("The selected folder no longer exists.");

        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorer = Path.Combine(windowsRoot, "explorer.exe");
        if (!File.Exists(explorer))
            throw new FileNotFoundException("Windows Explorer was not found.", explorer);

        var startInfo = new ProcessStartInfo
        {
            FileName = explorer,
            WorkingDirectory = windowsRoot,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(fullPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows Explorer did not start.");
    }
}
