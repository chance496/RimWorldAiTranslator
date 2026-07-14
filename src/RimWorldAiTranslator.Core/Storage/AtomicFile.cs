using System.Text;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    internal static Action<string>? BeforeTemporaryFlushTestHook { get; set; }
    internal static Action<string>? AfterTemporaryValidationBeforeCommitTestHook { get; set; }
    internal static Action<string>? AfterCopyChunkWrittenTestHook { get; set; }

    public static void WriteUtf8(string path, string text, bool keepBackup = true) =>
        WriteBytes(path, Utf8NoBom.GetBytes(text), keepBackup);

    public static void WriteUtf8Validated(
        string path,
        string text,
        Action<string> validateTemporaryFile,
        bool keepBackup = true)
    {
        ArgumentNullException.ThrowIfNull(validateTemporaryFile);
        WriteBytes(path, Utf8NoBom.GetBytes(text), keepBackup, validateTemporaryFile);
    }

    public static void WriteBytes(string path, byte[] bytes, bool keepBackup = true) =>
        WriteBytes(path, bytes, keepBackup, null);

    public static void WriteBytesValidated(
        string path,
        byte[] bytes,
        Action<string> validateTemporaryFile,
        bool keepBackup = true)
    {
        ArgumentNullException.ThrowIfNull(validateTemporaryFile);
        WriteBytes(path, bytes, keepBackup, validateTemporaryFile);
    }

    internal static void WriteBytesValidated(
        string path,
        byte[] bytes,
        Action<string> validateTemporaryFile,
        PathSafety.TrustedWriteBoundary writeBoundary,
        bool keepBackup = true)
    {
        ArgumentNullException.ThrowIfNull(validateTemporaryFile);
        ArgumentNullException.ThrowIfNull(writeBoundary);
        WriteBytes(path, bytes, keepBackup, validateTemporaryFile, writeBoundary);
    }

    internal static void CopyFlushedBounded(
        string sourcePath,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        var completed = false;
        try
        {
            using var input = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            if (input.Length > maximumBytes)
                throw new InvalidDataException(
                    $"Snapshot source exceeds the {maximumBytes:N0}-byte limit: {Path.GetFileName(source)}");
            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total = checked(total + read);
                if (total > maximumBytes)
                    throw new InvalidDataException("Snapshot source grew beyond its size limit.");
                output.Write(buffer, 0, read);
                AfterCopyChunkWrittenTestHook?.Invoke(destination);
            }
            output.Flush(flushToDisk: true);
            completed = true;
        }
        finally
        {
            if (!completed && File.Exists(destination)) File.Delete(destination);
        }
    }

    public static void CopyFlushed(string sourcePath, string destinationPath) =>
        CopyFlushedBounded(sourcePath, destinationPath, long.MaxValue);

    private static void WriteBytes(
        string path,
        byte[] bytes,
        bool keepBackup,
        Action<string>? validateTemporaryFile,
        PathSafety.TrustedWriteBoundary? writeBoundary = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("An atomic output has no parent directory.");
        var boundaryRoot = writeBoundary is null ? FindNearestExistingDirectory(directory) : null;
        using var localBoundary = writeBoundary is null
            ? PathSafety.AcquireTrustedWriteBoundary(boundaryRoot!, [fullPath])
            : null;
        var activeBoundary = writeBoundary ?? localBoundary
            ?? throw new InvalidOperationException("An atomic write has no output boundary.");
        if (Directory.Exists(fullPath))
            throw new IOException("Refusing to replace a directory with a file.");
        if (activeBoundary.TargetExisted(fullPath)
            && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReadOnly))
        {
            throw new UnauthorizedAccessException("Refusing to replace a read-only file.");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                BeforeTemporaryFlushTestHook?.Invoke(temporaryPath);
                stream.Flush(flushToDisk: true);
            }
            validateTemporaryFile?.Invoke(temporaryPath);
            AfterTemporaryValidationBeforeCommitTestHook?.Invoke(temporaryPath);
            activeBoundary.CommitPreparedFile(temporaryPath, fullPath, keepBackup);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string FindNearestExistingDirectory(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException(
                    "No existing ancestor could be found for the atomic output directory.");
            }
            current = parent;
        }
        return current;
    }
}
