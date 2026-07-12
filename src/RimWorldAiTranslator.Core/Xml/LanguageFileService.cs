using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Core.Xml;

public sealed record LanguageWriteResult(int Applied, int SkippedExisting);

public sealed partial class LanguageFileService
{
    public SortedDictionary<string, string> Read(string path)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return entries;
        }

        var document = SafeXml.Load(path);
        if (document.Root?.Name.LocalName != "LanguageData")
        {
            throw new InvalidDataException($"Target XML is not LanguageData: {path}");
        }

        foreach (var child in document.Root.Elements())
        {
            entries[child.Name.LocalName] = Normalize(child.Value);
        }
        return entries;
    }

    public LanguageWriteResult Write(string path, IReadOnlyDictionary<string, string> updates, bool overwrite)
    {
        var entries = Read(path);
        var applied = 0;
        var skippedExisting = 0;
        foreach (var pair in updates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsValidLocalizationKey(pair.Key))
            {
                throw new InvalidDataException($"Refusing to write an invalid XML localization key: {pair.Key}");
            }

            if (overwrite || !entries.ContainsKey(pair.Key))
            {
                entries[pair.Key] = RemoveInvalidXmlCharacters(pair.Value);
                applied++;
            }
            else
            {
                skippedExisting++;
            }
        }

        if (applied == 0)
        {
            return new LanguageWriteResult(0, skippedExisting);
        }

        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.AppendLine("<LanguageData>");
        foreach (var pair in entries)
        {
            builder.Append("  <").Append(pair.Key).Append('>')
                .Append(SecurityElement.Escape(RemoveInvalidXmlCharacters(pair.Value)))
                .Append("</").Append(pair.Key).AppendLine(">");
        }
        builder.AppendLine("</LanguageData>");
        AtomicFile.WriteUtf8(path, builder.ToString());
        return new LanguageWriteResult(applied, skippedExisting);
    }

    public IReadOnlyDictionary<string, LanguageWriteResult> WriteTransaction(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> outputGroups, bool overwrite)
    {
        var journal = new List<TransactionEntry>();
        var results = new Dictionary<string, LanguageWriteResult>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var group in outputGroups.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var target = Path.GetFullPath(group.Key);
                var existed = File.Exists(target);
                string? snapshot = null;
                if (existed)
                {
                    var directory = Path.GetDirectoryName(target)!;
                    snapshot = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.transaction.bak");
                    AtomicFile.CopyFlushed(target, snapshot);
                }
                journal.Add(new TransactionEntry(target, existed, snapshot));
                results[target] = Write(target, group.Value, overwrite);
            }
        }
        catch (Exception writeError)
        {
            var rollbackErrors = new List<string>();
            for (var index = journal.Count - 1; index >= 0; index--)
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
                throw new IOException($"Translation output failed and rollback was incomplete. Write error: {writeError.Message} Rollback errors: {string.Join(" | ", rollbackErrors)}", writeError);
            }
            throw new IOException($"Translation output failed; all files written by this run were rolled back. {writeError.Message}", writeError);
        }
        finally
        {
            foreach (var entry in journal)
            {
                if (entry.SnapshotPath is not null && File.Exists(entry.SnapshotPath))
                {
                    File.Delete(entry.SnapshotPath);
                }
            }
        }
        return results;
    }

    public static bool IsValidLocalizationKey(string key) => !string.IsNullOrWhiteSpace(key) && ValidKeyRegex().IsMatch(key);

    public static string RemoveInvalidXmlCharacters(string? text) => InvalidXmlCharacterRegex().Replace(text ?? string.Empty, string.Empty);

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    private static void Restore(TransactionEntry entry)
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
            if (File.Exists(entry.Path))
            {
                File.Replace(restore, entry.Path, discard, true);
            }
            else
            {
                File.Move(restore, entry.Path);
            }
        }
        finally
        {
            if (File.Exists(restore)) File.Delete(restore);
            if (File.Exists(discard)) File.Delete(discard);
        }
    }

    private sealed record TransactionEntry(string Path, bool Existed, string? SnapshotPath);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidKeyRegex();

    [GeneratedRegex("[^\\u0009\\u000A\\u000D\\u0020-\\uD7FF\\uE000-\\uFFFD]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidXmlCharacterRegex();
}
