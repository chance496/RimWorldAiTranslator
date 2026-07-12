using System.Text;

namespace RimWorldAiTranslator.Core.Storage;

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    public static void WriteUtf8(string path, string text, bool keepBackup = true)
    {
        WriteBytes(path, Utf8NoBom.GetBytes(text), keepBackup);
    }

    public static void WriteBytes(string path, byte[] bytes, bool keepBackup = true)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException($"Path has no parent directory: {fullPath}");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteNewFlushed(tempPath, bytes);
            if (File.Exists(fullPath))
            {
                var backup = keepBackup ? fullPath + ".bak" : Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.discard.tmp");
                try
                {
                    File.Replace(tempPath, fullPath, backup, true);
                }
                finally
                {
                    if (!keepBackup)
                    {
                        TryDelete(backup);
                    }
                }
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static void CopyFlushed(string sourcePath, string destinationPath)
    {
        WriteNewFlushed(destinationPath, File.ReadAllBytes(sourcePath));
    }

    private static void WriteNewFlushed(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
