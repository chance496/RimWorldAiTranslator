using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace RimWorldAiTranslator.Core.Storage;

public sealed record JsonRecoveryNotice(string StorePath, string? PreservedCorruptPath);

public sealed class AtomicJsonStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private readonly ConcurrentDictionary<string, byte> blockedWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions options;

    public AtomicJsonStore(JsonSerializerOptions? options = null)
    {
        this.options = options ?? new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }

    public event EventHandler<JsonRecoveryNotice>? RecoveredFromBackup;

    public bool Exists(string path)
    {
        var fullPath = Normalize(path);
        return File.Exists(fullPath) || File.Exists(fullPath + ".bak");
    }

    public T? Read<T>(string path, bool allowMissing = false)
    {
        var fullPath = Normalize(path);
        var found = false;
        var errors = new List<string>();

        foreach (var candidate in new[] { fullPath, fullPath + ".bak" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            found = true;
            try
            {
                var raw = File.ReadAllText(candidate, Utf8NoBom);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    throw new JsonException("JSON file is empty.");
                }

                var value = JsonSerializer.Deserialize<T>(raw, options);
                if (value is null)
                {
                    throw new JsonException("JSON root value is null.");
                }

                if (!candidate.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    var preserved = RestoreFromBackup(fullPath, candidate);
                    RecoveredFromBackup?.Invoke(this, new JsonRecoveryNotice(fullPath, preserved));
                }

                Unblock(fullPath);
                return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                errors.Add($"{candidate} : {ex.Message}");
            }
        }

        if (!found && allowMissing)
        {
            return default;
        }

        if (!found)
        {
            throw new FileNotFoundException($"JSON file was not found: {fullPath}", fullPath);
        }

        Block(fullPath);
        throw new InvalidDataException($"JSON file and its backup could not be read. {string.Join(" | ", errors)}");
    }

    public void Write<T>(string path, T value)
    {
        var fullPath = Normalize(path);
        if (blockedWrites.ContainsKey(fullPath))
        {
            throw new InvalidOperationException($"Refusing to overwrite a JSON store that could not be read: {fullPath}");
        }

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"JSON path has no parent directory: {fullPath}");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(value, options);
        var bytes = Utf8NoBom.GetBytes(json);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = fullPath + ".bak";

        try
        {
            WriteFlushed(tempPath, bytes, FileMode.CreateNew);
            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, backupPath, true);
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

    public void Block(string path) => blockedWrites[Normalize(path)] = 0;

    public void Unblock(string path) => blockedWrites.TryRemove(Normalize(path), out _);

    public bool IsBlocked(string path) => blockedWrites.ContainsKey(Normalize(path));

    private static string? RestoreFromBackup(string path, string backupPath)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"JSON path has no parent directory: {path}");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.restore.tmp");
        string? corruptPath = null;

        try
        {
            WriteFlushed(tempPath, File.ReadAllBytes(backupPath), FileMode.CreateNew);
            if (File.Exists(path))
            {
                corruptPath = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
                corruptPath = corruptPath[..^24];
                File.Replace(tempPath, path, corruptPath, true);
            }
            else
            {
                File.Move(tempPath, path);
            }

            return corruptPath;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void WriteFlushed(string path, byte[] bytes, FileMode mode)
    {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
