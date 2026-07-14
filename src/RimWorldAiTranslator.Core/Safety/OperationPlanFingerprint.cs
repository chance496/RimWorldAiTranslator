using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RimWorldAiTranslator.Core.Safety;

internal sealed class OperationPlanFingerprint : IDisposable
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool completed;

    public void Add(string? value)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        if (value is null)
        {
            AppendLength(-1);
            return;
        }

        var maximumByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        Span<byte> bytes = maximumByteCount <= 1024
            ? stackalloc byte[maximumByteCount]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maximumByteCount));
        try
        {
            var written = Encoding.UTF8.GetBytes(value, bytes);
            AppendLength(written);
            hash.AppendData(bytes[..written]);
        }
        finally
        {
            if (rented is not null)
            {
                CryptographicOperations.ZeroMemory(rented);
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public void Add(bool value) => Add(value ? "1" : "0");

    public void Add(int value) => Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void AddPath(string path) => Add(
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant());

    public string Complete()
    {
        ObjectDisposedException.ThrowIf(completed, this);
        try
        {
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            completed = true;
            hash.Dispose();
        }
    }

    public static void RequireExpected(string? expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected)) return;

        Span<byte> expectedBytes = stackalloc byte[32];
        Span<byte> actualBytes = stackalloc byte[32];
        if (expected.Length != 64
            || !TryDecodeSha256(expected, expectedBytes)
            || !TryDecodeSha256(actual, actualBytes)
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw new InvalidOperationException(
                "The approved preview expired because its exact plan or output baseline changed. Preview the operation again before writing.");
        }
    }

    private static bool TryDecodeSha256(string value, Span<byte> destination)
    {
        try
        {
            var decoded = Convert.FromHexString(value);
            if (decoded.Length != destination.Length) return false;
            decoded.CopyTo(destination);
            CryptographicOperations.ZeroMemory(decoded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (completed) return;
        completed = true;
        hash.Dispose();
    }

    private void AppendLength(int value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value);
        hash.AppendData(length);
    }
}
