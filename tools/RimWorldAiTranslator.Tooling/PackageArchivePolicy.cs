using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace RimWorldAiTranslator.Tooling;

internal static class PackageArchivePolicy
{
    private const long Mebibyte = 1024 * 1024;
    private const long MaximumArchiveBytes = 160 * Mebibyte;
    private const long MaximumAggregateUncompressedBytes = 160 * Mebibyte;
    private const long MaximumCompressionRatio = 100;
    private const int EndOfCentralDirectoryMinimumBytes = 22;
    private const int MaximumZipCommentBytes = ushort.MaxValue;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
    private const uint CentralDirectoryFileHeaderSignature = 0x02014b50;
    private const ushort Zip64ExtraFieldIdentifier = 0x0001;
    private const uint MaximumCentralDirectoryBytes = 64 * 1024;

    private static readonly Dictionary<string, long> MaximumUncompressedBytesByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [PackageLayout.ApplicationFileName] = 160 * Mebibyte,
            ["rimworld-def-field-rules.txt"] = 2 * Mebibyte,
            ["PACKAGE_README.txt"] = 2 * Mebibyte,
            ["RELEASE_NOTES.md"] = 4 * Mebibyte,
            ["sample-glossary.txt"] = 2 * Mebibyte,
            ["VERSION"] = 4 * 1024,
            ["LICENSE"] = 2 * Mebibyte,
            ["SECURITY.md"] = 2 * Mebibyte,
            ["PRIVACY.md"] = 2 * Mebibyte,
            ["THIRD_PARTY_NOTICES.md"] = 2 * Mebibyte,
            ["DOTNET_RUNTIME_LICENSE.txt"] = 2 * Mebibyte,
            ["DOTNET_RUNTIME_THIRD_PARTY_NOTICES.txt"] = 2 * Mebibyte,
            ["DOTNET_WINDOWSDESKTOP_LICENSE.txt"] = 2 * Mebibyte,
            ["DOTNET_ASPNETCORE_THIRD_PARTY_NOTICES.txt"] = 2 * Mebibyte
        };

    public static void ValidateArchiveFile(string path)
    {
        var info = new FileInfo(path);
        var length = info.Length;
        if (length <= 0 || length > MaximumArchiveBytes)
            throw new InvalidDataException($"Package archive size is outside the allowed range: {length} bytes.");
        ValidateEndOfCentralDirectory(path, length);
    }

    private static void ValidateEndOfCentralDirectory(string path, long length)
    {
        var tailLength = checked((int)Math.Min(
            length,
            EndOfCentralDirectoryMinimumBytes + MaximumZipCommentBytes));
        var tail = new byte[tailLength];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);

        var eocdInTail = -1;
        for (var offset = tail.Length - EndOfCentralDirectoryMinimumBytes; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset, sizeof(uint)))
                != EndOfCentralDirectorySignature)
            {
                continue;
            }
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 20, sizeof(ushort)));
            if (offset + EndOfCentralDirectoryMinimumBytes + commentLength != tail.Length) continue;
            eocdInTail = offset;
            break;
        }
        if (eocdInTail < 0)
            throw new InvalidDataException("Package archive has no bounded end-of-central-directory record.");

        var eocd = tail.AsSpan(eocdInTail, EndOfCentralDirectoryMinimumBytes);
        var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]);
        var centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        var centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        if (diskNumber != 0
            || centralDirectoryDisk != 0
            || entriesOnDisk == ushort.MaxValue
            || totalEntries == ushort.MaxValue
            || centralDirectorySize == uint.MaxValue
            || centralDirectoryOffset == uint.MaxValue)
        {
            throw new InvalidDataException("Package archive uses unsupported split-disk or Zip64 metadata.");
        }
        if (entriesOnDisk != PackageLayout.AllFiles.Count || totalEntries != PackageLayout.AllFiles.Count)
        {
            throw new InvalidDataException(
                $"Package archive declares {totalEntries} entries instead of exactly {PackageLayout.AllFiles.Count}.");
        }
        if (centralDirectorySize == 0 || centralDirectorySize > MaximumCentralDirectoryBytes)
        {
            throw new InvalidDataException(
                $"Package archive central directory is outside the {MaximumCentralDirectoryBytes}-byte limit.");
        }

        var eocdOffset = checked(length - tailLength + eocdInTail);
        var centralDirectoryEnd = checked((long)centralDirectoryOffset + centralDirectorySize);
        if (centralDirectoryEnd != eocdOffset)
            throw new InvalidDataException("Package archive central-directory bounds do not end at the EOCD record.");

        if (eocdOffset >= 20)
        {
            Span<byte> locatorSignature = stackalloc byte[sizeof(uint)];
            stream.Position = eocdOffset - 20;
            stream.ReadExactly(locatorSignature);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locatorSignature) == Zip64EndOfCentralDirectoryLocatorSignature)
                throw new InvalidDataException("Package archive uses unsupported Zip64 metadata.");
        }
        ValidateCentralDirectory(stream, centralDirectoryOffset, centralDirectorySize, totalEntries);
    }

    private static void ValidateCentralDirectory(
        FileStream stream,
        uint centralDirectoryOffset,
        uint centralDirectorySize,
        ushort totalEntries)
    {
        var bytes = new byte[checked((int)centralDirectorySize)];
        stream.Position = centralDirectoryOffset;
        stream.ReadExactly(bytes);
        var offset = 0;
        for (var index = 0; index < totalEntries; index++)
        {
            const int fixedHeaderBytes = 46;
            if (offset > bytes.Length - fixedHeaderBytes
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)))
                != CentralDirectoryFileHeaderSignature)
            {
                throw new InvalidDataException("Package archive central directory has an invalid file header.");
            }

            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 20, sizeof(uint)));
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 24, sizeof(uint)));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28, sizeof(ushort)));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 30, sizeof(ushort)));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 32, sizeof(ushort)));
            var diskStart = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 34, sizeof(ushort)));
            var localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 42, sizeof(uint)));
            if (compressedSize == uint.MaxValue
                || uncompressedSize == uint.MaxValue
                || diskStart == ushort.MaxValue
                || diskStart != 0
                || localHeaderOffset == uint.MaxValue)
            {
                throw new InvalidDataException("Package archive central directory uses unsupported Zip64 or split-disk metadata.");
            }

            var entryBytes = checked(fixedHeaderBytes + nameLength + extraLength + commentLength);
            if (entryBytes > bytes.Length - offset)
                throw new InvalidDataException("Package archive central-directory entry exceeds its bounded record.");
            var extraOffset = offset + fixedHeaderBytes + nameLength;
            ValidateNoZip64ExtraField(bytes.AsSpan(extraOffset, extraLength));
            offset += entryBytes;
        }
        if (offset != bytes.Length)
            throw new InvalidDataException("Package archive central directory has trailing or missing records.");
    }

    private static void ValidateNoZip64ExtraField(ReadOnlySpan<byte> extraFields)
    {
        var offset = 0;
        while (offset < extraFields.Length)
        {
            if (offset > extraFields.Length - 4)
                throw new InvalidDataException("Package archive has a truncated central-directory extra field.");
            var identifier = BinaryPrimitives.ReadUInt16LittleEndian(extraFields[offset..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extraFields[(offset + 2)..]);
            offset += 4;
            if (length > extraFields.Length - offset)
                throw new InvalidDataException("Package archive has an out-of-bounds central-directory extra field.");
            if (identifier == Zip64ExtraFieldIdentifier)
                throw new InvalidDataException("Package archive uses an unsupported Zip64 extra field.");
            offset += length;
        }
    }

    public static void CopyEntryTo(ZipArchiveEntry entry, Stream? output, ref long aggregateUncompressedBytes)
    {
        ValidateEntryMetadata(entry, ref aggregateUncompressedBytes);
        var maximum = MaximumFor(entry.Name);
        var expected = entry.Length;
        var actual = 0L;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var input = entry.Open();
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                actual = checked(actual + read);
                if (actual > expected || actual > maximum)
                    throw new InvalidDataException($"Archive entry expanded beyond its declared or allowed size: {entry.FullName}");
                output?.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (actual != expected)
            throw new InvalidDataException($"Archive entry expanded to {actual} bytes instead of its declared {expected} bytes: {entry.FullName}");
    }

    public static void ValidateDirectoryFile(string name, long length, ref long aggregateUncompressedBytes)
    {
        var maximum = MaximumFor(name);
        if (length < 0 || length > maximum)
            throw new InvalidDataException($"Package file exceeds its {maximum}-byte limit: {name} ({length} bytes).");
        AddToAggregate(length, ref aggregateUncompressedBytes);
    }

    private static void ValidateEntryMetadata(ZipArchiveEntry entry, ref long aggregateUncompressedBytes)
    {
        var maximum = MaximumFor(entry.Name);
        var uncompressed = entry.Length;
        var compressed = entry.CompressedLength;
        if (uncompressed < 0 || uncompressed > maximum)
            throw new InvalidDataException($"Archive entry exceeds its {maximum}-byte uncompressed limit: {entry.FullName} ({uncompressed} bytes).");
        if (compressed < 0 || (uncompressed > 0 && compressed == 0))
            throw new InvalidDataException($"Archive entry has an invalid compressed size: {entry.FullName} ({compressed} bytes).");
        if (compressed > 0 && uncompressed > compressed * MaximumCompressionRatio)
        {
            throw new InvalidDataException(
                $"Archive entry exceeds the {MaximumCompressionRatio}:1 compression-ratio limit: {entry.FullName} ({uncompressed}/{compressed} bytes).");
        }
        AddToAggregate(uncompressed, ref aggregateUncompressedBytes);
    }

    private static long MaximumFor(string name)
    {
        if (!MaximumUncompressedBytesByName.TryGetValue(name, out var maximum))
            throw new InvalidDataException($"Archive entry has no size policy because it is not allowlisted: {name}");
        return maximum;
    }

    private static void AddToAggregate(long length, ref long aggregateUncompressedBytes)
    {
        aggregateUncompressedBytes = checked(aggregateUncompressedBytes + length);
        if (aggregateUncompressedBytes > MaximumAggregateUncompressedBytes)
        {
            throw new InvalidDataException(
                $"Package content exceeds the {MaximumAggregateUncompressedBytes}-byte aggregate uncompressed limit.");
        }
    }
}
