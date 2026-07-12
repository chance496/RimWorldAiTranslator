namespace RimWorldAiTranslator.Core.Storage;

public static class FileTransaction
{
    public static void Execute(IEnumerable<string> paths, Action action, string operationName)
    {
        var journal = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(Capture).ToArray();
        try
        {
            action();
        }
        catch (Exception operationError)
        {
            var rollbackErrors = new List<string>();
            for (var index = journal.Length - 1; index >= 0; index--)
            {
                try
                {
                    Restore(journal[index]);
                }
                catch (Exception ex)
                {
                    rollbackErrors.Add($"{journal[index].Path}: {ex.Message}");
                }
            }
            if (rollbackErrors.Count > 0)
            {
                throw new IOException($"{operationName} failed and rollback was incomplete. Error: {operationError.Message} Rollback errors: {string.Join(" | ", rollbackErrors)}", operationError);
            }
            throw new IOException($"{operationName} failed; all files written by this run were rolled back. {operationError.Message}", operationError);
        }
        finally
        {
            foreach (var entry in journal)
            {
                if (entry.SnapshotPath is not null && File.Exists(entry.SnapshotPath)) File.Delete(entry.SnapshotPath);
            }
        }
    }

    private static Entry Capture(string path)
    {
        var exists = File.Exists(path);
        string? snapshot = null;
        if (exists)
        {
            var directory = Path.GetDirectoryName(path)!;
            snapshot = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.transaction.bak");
            AtomicFile.CopyFlushed(path, snapshot);
        }
        return new Entry(path, exists, snapshot);
    }

    private static void Restore(Entry entry)
    {
        if (!entry.Existed)
        {
            if (File.Exists(entry.Path)) File.Delete(entry.Path);
            return;
        }
        var directory = Path.GetDirectoryName(entry.Path)!;
        var restore = Path.Combine(directory, $".{Path.GetFileName(entry.Path)}.{Guid.NewGuid():N}.restore.tmp");
        var discard = Path.Combine(directory, $".{Path.GetFileName(entry.Path)}.{Guid.NewGuid():N}.failed.tmp");
        try
        {
            AtomicFile.CopyFlushed(entry.SnapshotPath!, restore);
            if (File.Exists(entry.Path)) File.Replace(restore, entry.Path, discard, true);
            else File.Move(restore, entry.Path);
        }
        finally
        {
            if (File.Exists(restore)) File.Delete(restore);
            if (File.Exists(discard)) File.Delete(discard);
        }
    }

    private sealed record Entry(string Path, bool Existed, string? SnapshotPath);
}
