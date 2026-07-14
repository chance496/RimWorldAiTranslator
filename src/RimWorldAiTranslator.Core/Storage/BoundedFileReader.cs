namespace RimWorldAiTranslator.Core.Storage;

internal static class BoundedFileReader
{
    internal static Action<long>? AfterChunkReadTestHook { get; set; }

    public static byte[] ReadAllBytes(
        string path,
        long maximumBytes,
        string description,
        Action<long>? lengthObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "The in-memory file limit must fit in a managed byte array.");

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return ReadAllBytesCore(stream, maximumBytes, description, lengthObserver, cancellationToken);
    }

    internal static byte[] ReadAllBytes(
        FileStream pinnedStream,
        long maximumBytes,
        string description,
        Action<long>? lengthObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pinnedStream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "The in-memory file limit must fit in a managed byte array.");
        if (!pinnedStream.CanRead || !pinnedStream.CanSeek)
            throw new InvalidDataException($"{description} is not a readable regular file.");

        cancellationToken.ThrowIfCancellationRequested();
        pinnedStream.Position = 0;
        return ReadAllBytesCore(pinnedStream, maximumBytes, description, lengthObserver, cancellationToken);
    }

    private static byte[] ReadAllBytesCore(
        FileStream stream,
        long maximumBytes,
        string description,
        Action<long>? lengthObserver,
        CancellationToken cancellationToken)
    {
        if (stream.Length > maximumBytes)
            throw new InvalidDataException($"{description} exceeds the {maximumBytes:N0}-byte limit.");
        lengthObserver?.Invoke(stream.Length);

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
            if (read == 0) throw new EndOfStreamException($"{description} changed while it was being read.");
            offset += read;
            AfterChunkReadTestHook?.Invoke(offset);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1)
            throw new InvalidDataException($"{description} changed while it was being read.");
        return bytes;
    }
}
