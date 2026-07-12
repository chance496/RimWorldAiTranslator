using System.Text.RegularExpressions;

namespace RimWorldAiTranslator.Core.Logging;

public sealed partial class AppLogger : IDisposable
{
    private readonly object sync = new();
    private readonly StreamWriter writer;
    private bool disposed;

    public AppLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        var path = System.IO.Path.Combine(logDirectory, $"RimWorldAiTranslator-{DateTime.Now:yyyyMMdd}.log");
        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 32 * 1024), new System.Text.UTF8Encoding(false))
        {
            AutoFlush = true
        };
        Path = path;
    }

    public string Path { get; }
    public event EventHandler<string>? MessageWritten;

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var safe = Redact(message);
        var line = $"[{DateTime.Now:HH:mm:ss}] {level,-5} {safe}";
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            writer.WriteLine(line);
        }
        MessageWritten?.Invoke(this, line);
    }

    public static string Redact(string? value)
    {
        var text = value ?? string.Empty;
        text = BearerRegex().Replace(text, "Bearer [REDACTED]");
        text = KeyValueRegex().Replace(text, match => match.Groups[1].Value + "=[REDACTED]");
        text = CerebrasKeyRegex().Replace(text, "csk-[REDACTED]");
        text = OpenAiKeyRegex().Replace(text, "sk-[REDACTED]");
        return text.Replace("\r", " ").Replace("\n", " ");
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            writer.Dispose();
        }
    }

    [GeneratedRegex("(?i)Bearer\\s+[^\\s,;]+", RegexOptions.CultureInvariant)] private static partial Regex BearerRegex();
    [GeneratedRegex("(?i)(api[_-]?key|token|authorization|secret)\\s*=\\s*[^\\s,;]+", RegexOptions.CultureInvariant)] private static partial Regex KeyValueRegex();
    [GeneratedRegex("csk-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)] private static partial Regex CerebrasKeyRegex();
    [GeneratedRegex("sk-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)] private static partial Regex OpenAiKeyRegex();
}
