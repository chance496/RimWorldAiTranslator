using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Core.Xml;

public sealed record LanguageWriteResult(int Applied, int SkippedExisting);

public sealed partial class LanguageFileService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
            if (!entries.TryAdd(child.Name.LocalName, Normalize(child.Value)))
            {
                throw new InvalidDataException(
                    $"Target LanguageData XML contains a duplicate localization key '{child.Name.LocalName}': {path}");
            }
        }
        return entries;
    }

    public LanguageWriteResult Write(
        string path,
        IReadOnlyDictionary<string, string> updates,
        bool overwrite,
        CancellationToken cancellationToken = default) =>
        WriteCore(path, updates, overwrite, null, cancellationToken);

    internal LanguageWriteResult Write(
        string path,
        IReadOnlyDictionary<string, string> updates,
        bool overwrite,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken = default) =>
        WriteCore(path, updates, overwrite, writeBoundary, cancellationToken);

    private LanguageWriteResult WriteCore(
        string path,
        IReadOnlyDictionary<string, string> updates,
        bool overwrite,
        PathSafety.TrustedWriteBoundary? writeBoundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileExists = writeBoundary?.TargetExisted(path) ?? File.Exists(path);
        var originalBytes = fileExists
            ? BoundedFileReader.ReadAllBytes(
                path,
                SafeXml.DefaultMaximumCharacters,
                "target language XML",
                cancellationToken: cancellationToken)
            : [];
        var hasUtf8Bom = HasUtf8Bom(originalBytes);
        var newline = fileExists ? DetectNewline(DecodeUtf8(originalBytes, hasUtf8Bom)) : "\n";
        var document = fileExists
            ? SafeXml.LoadPreservingWhitespace(path)
            : new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("LanguageData"));
        var root = document.Root;
        if (root?.Name.LocalName != "LanguageData")
            throw new InvalidDataException($"Target XML is not LanguageData: {path}");
        var existingElements = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in root.Elements())
        {
            if (!existingElements.TryAdd(element.Name.LocalName, element))
                throw new InvalidDataException(
                    $"Target LanguageData XML contains a duplicate localization key '{element.Name.LocalName}': {path}");
        }

        var applied = 0;
        var skippedExisting = 0;
        foreach (var pair in updates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidLocalizationKey(pair.Key))
            {
                throw new InvalidDataException($"Refusing to write an invalid XML localization key: {pair.Key}");
            }

            if (existingElements.TryGetValue(pair.Key, out var existing))
            {
                if (overwrite)
                {
                    existing.Value = RemoveInvalidXmlCharacters(pair.Value);
                    applied++;
                }
                else
                {
                    skippedExisting++;
                }
            }
            else
            {
                var created = new XElement(pair.Key, RemoveInvalidXmlCharacters(pair.Value));
                AppendElement(root, created);
                existingElements.Add(pair.Key, created);
                applied++;
            }
        }

        if (applied == 0)
        {
            return new LanguageWriteResult(0, skippedExisting);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var serialized = SerializeUtf8(document, hasUtf8Bom, newline);
        if (writeBoundary is null)
        {
            AtomicFile.WriteBytesValidated(
                path,
                serialized,
                temporaryPath => ValidateWrittenDocument(temporaryPath, existingElements),
                keepBackup: true);
        }
        else
        {
            AtomicFile.WriteBytesValidated(
                path,
                serialized,
                temporaryPath => ValidateWrittenDocument(temporaryPath, existingElements),
                writeBoundary,
                keepBackup: true);
        }
        return new LanguageWriteResult(applied, skippedExisting);
    }

    public IReadOnlyDictionary<string, LanguageWriteResult> WriteTransaction(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> outputGroups,
        bool overwrite,
        CancellationToken cancellationToken = default) =>
        WriteTransaction(
            outputGroups,
            overwrite,
            File.Delete,
            (source, destination) => CopySnapshotBounded(source, destination, cancellationToken),
            null,
            null,
            cancellationToken);

    internal IReadOnlyDictionary<string, LanguageWriteResult> WriteTransaction(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> outputGroups,
        bool overwrite,
        Action<string> deleteSnapshot,
        CancellationToken cancellationToken = default) =>
        WriteTransaction(
            outputGroups,
            overwrite,
            deleteSnapshot,
            (source, destination) => CopySnapshotBounded(source, destination, cancellationToken),
            null,
            null,
            cancellationToken);

    internal IReadOnlyDictionary<string, LanguageWriteResult> WriteTransaction(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> outputGroups,
        bool overwrite,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken = default) =>
        WriteTransaction(
            outputGroups,
            overwrite,
            File.Delete,
            (source, destination) => CopySnapshotBounded(source, destination, cancellationToken),
            writeBoundary,
            null,
            cancellationToken);

    internal IReadOnlyDictionary<string, LanguageWriteResult> WriteTransaction(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> outputGroups,
        bool overwrite,
        PathSafety.TrustedWriteBoundary writeBoundary,
        FileTransactionRecoverySession recoverySession,
        CancellationToken cancellationToken = default) =>
        WriteTransaction(
            outputGroups,
            overwrite,
            File.Delete,
            (source, destination) => CopySnapshotBounded(source, destination, cancellationToken),
            writeBoundary,
            recoverySession,
            cancellationToken);

    internal IReadOnlyDictionary<string, LanguageWriteResult> WriteTransaction(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> outputGroups,
        bool overwrite,
        Action<string> deleteSnapshot,
        Action<string, string> copySnapshot,
        CancellationToken cancellationToken = default) =>
        WriteTransaction(
            outputGroups,
            overwrite,
            deleteSnapshot,
            copySnapshot,
            null,
            null,
            cancellationToken);

    private IReadOnlyDictionary<string, LanguageWriteResult> WriteTransaction(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> outputGroups,
        bool overwrite,
        Action<string> deleteSnapshot,
        Action<string, string> copySnapshot,
        PathSafety.TrustedWriteBoundary? writeBoundary,
        FileTransactionRecoverySession? suppliedRecoverySession,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTransactionTargetCount(outputGroups.Keys, cancellationToken);
        var groups = outputGroups
            .Select(pair => (Target: Path.GetFullPath(pair.Key), Updates: pair.Value))
            .OrderBy(group => group.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var duplicateTarget = groups
            .GroupBy(group => group.Target, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateTarget is not null)
            throw new InvalidDataException($"Translation output contains duplicate canonical targets: {duplicateTarget.Key}");
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Read(group.Target);
        }
        if (groups.Length == 0)
            return new Dictionary<string, LanguageWriteResult>(StringComparer.OrdinalIgnoreCase);
        var recoverySession = suppliedRecoverySession
                              ?? FileTransactionRecoverySession.CreateDisabled();
        var ownsRecoverySession = suppliedRecoverySession is null;
        FileSnapshotEntry[]? journal = null;
        IDisposable? activation = null;
        var results = new Dictionary<string, LanguageWriteResult>(StringComparer.OrdinalIgnoreCase);
        var preserveSnapshots = false;
        var operationCompleted = false;
        try
        {
            journal = FileSnapshotJournal.Capture(
                groups.Select(group => group.Target),
                "Translation output",
                copySnapshot,
                deleteSnapshot,
                recoverySession,
                cancellationToken);
            activation = recoverySession.Activate();
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[group.Target] = WriteCore(
                    group.Target,
                    group.Updates,
                    overwrite,
                    writeBoundary,
                    cancellationToken);
            }
            activation.Dispose();
            activation = null;
            operationCompleted = true;
            recoverySession.MarkResolved(writeBoundary, CancellationToken.None);
        }
        catch (Exception writeError)
        {
            activation?.Dispose();
            activation = null;
            if (journal is null) throw;
            if (operationCompleted || recoverySession.ShouldDeferRecovery)
            {
                preserveSnapshots = true;
                recoverySession.DetachPreservedEvidence();
                throw new IOException(
                    "Translation output completed but durable resolution was deferred; recovery evidence was preserved.",
                    writeError);
            }
            writeBoundary?.ReleaseWriteLeafLocksForRollback();
            IReadOnlySet<string>? concurrentPaths = writeError is ConcurrentLeafChangeException concurrent
                ? concurrent.PreservedPaths
                : null;
            var rollback = recoverySession.IsEnabled
                ? recoverySession.RollbackDurably(concurrentPaths)
                : FileSnapshotJournal.Rollback(
                    FileSnapshotJournal.CaptureRollbackState(journal, CancellationToken.None),
                    concurrentPaths,
                    CancellationToken.None);
            if (rollback.Errors.Count > 0)
            {
                preserveSnapshots = true;
                recoverySession.DetachPreservedEvidence();
                throw new IOException($"Translation output failed and rollback was incomplete. Write error: {writeError.Message} Rollback errors: {string.Join(" | ", rollback.Errors)} Recovery snapshots were preserved.", writeError);
            }
            if (rollback.ConcurrentPaths.Count > 0)
            {
                preserveSnapshots = true;
                recoverySession.DetachPreservedEvidence();
                throw new ConcurrentLeafChangeException(
                    $"Translation output stopped because rollback detected a concurrent save; current files and recovery snapshots were preserved: {string.Join(" | ", rollback.ConcurrentPaths)}.",
                    writeError,
                    [.. rollback.ConcurrentPaths, .. rollback.RecoveryPaths]);
            }
            if (!recoverySession.IsEnabled)
                recoverySession.MarkRollbackResolved(CancellationToken.None);
            if (writeError is OperationCanceledException) throw;
            throw new IOException($"Translation output failed; all files written by this run were rolled back. {writeError.Message}", writeError);
        }
        finally
        {
            activation?.Dispose();
            if (journal is not null)
            {
                var cleanupFailures = recoverySession.IsEnabled
                    ? Array.Empty<string>()
                    : FileSnapshotJournal.Cleanup(
                        journal,
                        deleteSnapshot,
                        preserveSnapshots,
                        "Language transaction");
                recoverySession.Finish(
                    preserveSnapshots || recoverySession.ShouldDeferRecovery,
                    cleanupFailures);
            }
            if (ownsRecoverySession) recoverySession.Dispose();
        }
        return results;
    }

    internal static void ValidateTransactionTargetCount(
        IEnumerable<string> targetPaths,
        CancellationToken cancellationToken = default)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetPath in targetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.Add(Path.GetFullPath(targetPath))) continue;
            if (targets.Count > FileSnapshotJournal.MaximumSnapshotTargets)
                throw new InvalidDataException(
                    $"Translation output contains more than {FileSnapshotJournal.MaximumSnapshotTargets:N0} distinct target files.");
        }
    }

    public static bool IsValidLocalizationKey(string key) => !string.IsNullOrWhiteSpace(key) && ValidKeyRegex().IsMatch(key);

    public static string RemoveInvalidXmlCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        StringBuilder? sanitized = null;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            var isBasicXmlCharacter = current is '\t' or '\n' or '\r'
                                      || current is >= '\u0020' and <= '\uD7FF'
                                      || current is >= '\uE000' and <= '\uFFFD';
            if (isBasicXmlCharacter)
            {
                sanitized?.Append(current);
                continue;
            }

            if (char.IsHighSurrogate(current)
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                if (sanitized is not null)
                {
                    sanitized.Append(current);
                    sanitized.Append(text[index + 1]);
                }
                index++;
                continue;
            }

            if (sanitized is null)
            {
                sanitized = new StringBuilder(text.Length);
                sanitized.Append(text, 0, index);
            }
        }

        return sanitized?.ToString() ?? text;
    }

    private SortedDictionary<string, string> ReadExpected(string path)
    {
        return Read(path);
    }

    private void ValidateWrittenDocument(string path, IReadOnlyDictionary<string, XElement> expected)
    {
        var reopened = ReadExpected(path);
        if (reopened.Count != expected.Count)
            throw new InvalidDataException("Temporary LanguageData XML changed the number of localization keys.");
        foreach (var pair in expected)
        {
            var expectedValue = Normalize(pair.Value.Value);
            if (!reopened.TryGetValue(pair.Key, out var actual)
                || !actual.Equals(expectedValue, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Temporary LanguageData XML validation failed for localization key '{pair.Key}'.");
            }
        }
    }

    private static void AppendElement(XElement root, XElement element)
    {
        if (root.LastNode is XText trailing && string.IsNullOrWhiteSpace(trailing.Value))
            trailing.Remove();
        root.Add(new XText("\n  "));
        root.Add(element);
        root.Add(new XText("\n"));
    }

    private static byte[] SerializeUtf8(XDocument document, bool emitBom, string newline)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(emitBom, true),
            Indent = false,
            OmitXmlDeclaration = document.Declaration is null,
            NewLineHandling = NewLineHandling.None,
            CloseOutput = false
        };
        using (var writer = XmlWriter.Create(stream, settings)) document.Save(writer);
        var bytes = stream.ToArray();
        if (newline == "\n") return bytes;

        var offset = HasUtf8Bom(bytes) ? 3 : 0;
        var raw = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = newline == "\r\n"
            ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal)
            : normalized.Replace("\n", "\r", StringComparison.Ordinal);
        var body = StrictUtf8.GetBytes(normalized);
        if (!emitBom) return body;
        return [0xEF, 0xBB, 0xBF, .. body];
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string DecodeUtf8(byte[] bytes, bool hasBom) =>
        StrictUtf8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

    private static string DetectNewline(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        if (index < 0) return "\n";
        return text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n'
            ? "\r\n"
            : text[index].ToString();
    }

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    private static void CopySnapshotBounded(string sourcePath, string destinationPath) =>
        CopySnapshotBounded(sourcePath, destinationPath, CancellationToken.None);

    private static void CopySnapshotBounded(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        AtomicFile.CopyFlushedBounded(
            sourcePath,
            destinationPath,
            PathSafety.MaximumTrustedLeafBytes,
            cancellationToken);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidKeyRegex();

}
