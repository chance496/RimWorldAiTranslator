using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RimWorldAiTranslator.Tooling;

internal sealed record IconFrameInfo(int Size, int BitsPerPixel, int PayloadLength, int Offset);

internal static class IconAssetGenerator
{
    internal const string SvgRelativePath = "src/RimWorldAiTranslator.App/Assets/RimWorldAiTranslator.svg";
    internal const string IcoRelativePath = "src/RimWorldAiTranslator.App/Assets/RimWorldAiTranslator.ico";

    private const int ViewBoxSize = 64;
    private const int SamplesPerAxis = 4;
    private const int BitmapInfoHeaderBytes = 40;
    private const int MaximumSvgBytes = 64 * 1024;
    private const int MaximumIcoBytes = 2 * 1024 * 1024;
    private const int MaximumShapes = 32;
    private const int MaximumPolygonPoints = 32;
    private static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ReadOnlyCollection<int> IconSizes = Array.AsReadOnly(
        new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 });
    private static readonly string[] RectangleAttributeNames = ["fill", "height", "width", "x", "y"];
    private static readonly string[] PolygonAttributeNames = ["fill", "points"];

    internal static ReadOnlyCollection<int> RequiredSizesForTesting => IconSizes;

    internal static int Run(RepositoryLayout repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var sourcePath = repository.RequireFile(SvgRelativePath);
        var outputPath = repository.RequireRepositoryPath(Path.Combine(repository.Root, IcoRelativePath));
        var sourceDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("The icon source has no parent directory.");
        if (!string.Equals(Path.GetDirectoryName(outputPath), sourceDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The generated icon path escaped the fixed application asset directory.");
        if (Directory.Exists(outputPath))
            throw new IOException("The generated icon path points to a directory.");

        var sourceBytes = ReadBoundedFile(sourcePath, MaximumSvgBytes, "icon SVG");
        var sourceText = DecodeSvg(sourceBytes);
        var generated = CreateIcoForTesting(sourceText);
        _ = InspectIcoForTesting(generated);

        byte[]? previous = null;
        if (File.Exists(outputPath))
        {
            repository.AssertNoReparseComponents(outputPath);
            previous = ReadBoundedFile(outputPath, MaximumIcoBytes, "generated icon");
            _ = InspectIcoForTesting(previous);
            if (previous.AsSpan().SequenceEqual(generated))
            {
                Console.WriteLine($"Application icon is up to date: {IcoRelativePath}");
                return 0;
            }
        }

        var currentSource = ReadBoundedFile(sourcePath, MaximumSvgBytes, "icon SVG");
        if (!currentSource.AsSpan().SequenceEqual(sourceBytes))
            throw new IOException("The icon SVG changed while its ICO was being generated.");

        PublishAtomically(repository, outputPath, generated, previous);
        Console.WriteLine($"Generated application icon: {IcoRelativePath}");
        Console.WriteLine($"Frames: {string.Join(", ", IconSizes)}");
        return 0;
    }

    internal static byte[] CreateIcoForTesting(string svg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(svg);
        var shapes = ParseSvg(svg);
        var payloads = new List<byte[]>(IconSizes.Count);
        foreach (var size in IconSizes)
            payloads.Add(CreateDibPayload(size, Rasterize(shapes, size)));

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write(checked((ushort)payloads.Count));
            var payloadOffset = checked(6 + payloads.Count * 16);
            for (var index = 0; index < payloads.Count; index++)
            {
                var size = IconSizes[index];
                var payload = payloads[index];
                writer.Write(size == 256 ? (byte)0 : checked((byte)size));
                writer.Write(size == 256 ? (byte)0 : checked((byte)size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(checked((uint)payload.Length));
                writer.Write(checked((uint)payloadOffset));
                payloadOffset = checked(payloadOffset + payload.Length);
            }

            foreach (var payload in payloads)
                writer.Write(payload);
            writer.Flush();
        }

        var result = output.ToArray();
        _ = InspectIcoForTesting(result);
        return result;
    }

    internal static ReadOnlyCollection<IconFrameInfo> InspectIcoForTesting(byte[] ico)
    {
        ArgumentNullException.ThrowIfNull(ico);
        if (ico.Length < 6 || ico.Length > MaximumIcoBytes)
            throw new InvalidDataException("ICO length is outside the permitted range.");

        var bytes = ico.AsSpan();
        if (ReadUInt16(bytes, 0) != 0 || ReadUInt16(bytes, 2) != 1)
            throw new InvalidDataException("ICO header is invalid.");
        var count = ReadUInt16(bytes, 4);
        if (count != IconSizes.Count)
            throw new InvalidDataException("ICO frame count differs from the required multi-size set.");

        var frames = new List<IconFrameInfo>(count);
        var nextPayloadOffset = checked(6 + count * 16);
        for (var index = 0; index < count; index++)
        {
            var directoryOffset = checked(6 + index * 16);
            RequireRange(bytes, directoryOffset, 16, "ICO directory entry");
            var size = IconSizes[index];
            var encodedDimension = size == 256 ? 0 : size;
            if (bytes[directoryOffset] != encodedDimension
                || bytes[directoryOffset + 1] != encodedDimension
                || bytes[directoryOffset + 2] != 0
                || bytes[directoryOffset + 3] != 0
                || ReadUInt16(bytes, directoryOffset + 4) != 1
                || ReadUInt16(bytes, directoryOffset + 6) != 32)
            {
                throw new InvalidDataException("ICO directory metadata is invalid.");
            }

            var payloadLength = ReadBoundedUInt32(bytes, directoryOffset + 8, "ICO payload length");
            var payloadOffset = ReadBoundedUInt32(bytes, directoryOffset + 12, "ICO payload offset");
            if (payloadOffset != nextPayloadOffset)
                throw new InvalidDataException("ICO payloads are not contiguous and canonical.");
            ValidateDibPayload(bytes, payloadOffset, payloadLength, size);
            frames.Add(new IconFrameInfo(size, 32, payloadLength, payloadOffset));
            nextPayloadOffset = checked(nextPayloadOffset + payloadLength);
        }

        if (nextPayloadOffset != bytes.Length)
            throw new InvalidDataException("ICO has trailing or unreferenced data.");
        return frames.AsReadOnly();
    }

    private static string DecodeSvg(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new InvalidDataException("Icon SVG must be UTF-8 without a byte-order mark.");
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Icon SVG is not strict UTF-8.", exception);
        }
    }

    private static List<IShape> ParseSvg(string svg)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumSvgBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false
        };
        XDocument document;
        try
        {
            using var textReader = new StringReader(svg);
            using var xmlReader = XmlReader.Create(textReader, settings);
            document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Icon SVG is not safe, well-formed XML.", exception);
        }

        var root = document.Root;
        if (root is null || root.Name != SvgNamespace + "svg")
            throw new InvalidDataException("Icon SVG root must use the SVG namespace.");
        foreach (var node in document.Nodes())
        {
            if (ReferenceEquals(node, root)
                || node is XComment
                || node is XText text && string.IsNullOrWhiteSpace(text.Value))
            {
                continue;
            }
            throw new InvalidDataException("Icon SVG contains unsupported document-level content.");
        }

        var sawViewBox = false;
        foreach (var attribute in root.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                if (attribute.Name.LocalName != "xmlns" || attribute.Value != SvgNamespace.NamespaceName)
                    throw new InvalidDataException("Icon SVG may declare only the default SVG namespace.");
                continue;
            }

            if (attribute.Name.Namespace == XNamespace.None
                && attribute.Name.LocalName == "viewBox"
                && attribute.Value == "0 0 64 64"
                && !sawViewBox)
            {
                sawViewBox = true;
                continue;
            }
            throw new InvalidDataException($"Unsupported SVG root attribute: {attribute.Name.LocalName}");
        }
        if (!sawViewBox)
            throw new InvalidDataException("Icon SVG requires the exact viewBox '0 0 64 64'.");

        var shapes = new List<IShape>();
        foreach (var node in root.Nodes())
        {
            if (node is XComment || node is XText text && string.IsNullOrWhiteSpace(text.Value))
                continue;
            if (node is not XElement element || element.Name.Namespace != SvgNamespace)
                throw new InvalidDataException("Icon SVG contains unsupported content.");
            if (element.Nodes().Any())
                throw new InvalidDataException("Icon SVG shapes must be empty elements.");

            shapes.Add(element.Name.LocalName switch
            {
                "rect" => ParseRectangle(element),
                "polygon" => ParsePolygon(element),
                _ => throw new InvalidDataException($"Unsupported SVG element: {element.Name.LocalName}")
            });
            if (shapes.Count > MaximumShapes)
                throw new InvalidDataException("Icon SVG contains too many shapes.");
        }

        if (shapes.Count == 0)
            throw new InvalidDataException("Icon SVG contains no shapes.");
        return shapes;
    }

    private static RectangleShape ParseRectangle(XElement element)
    {
        ValidateAttributes(element, RectangleAttributeNames);
        var x = ReadCoordinate(element, "x", allowZero: true);
        var y = ReadCoordinate(element, "y", allowZero: true);
        var width = ReadCoordinate(element, "width", allowZero: false);
        var height = ReadCoordinate(element, "height", allowZero: false);
        if (checked(x + width) > ViewBoxSize || checked(y + height) > ViewBoxSize)
            throw new InvalidDataException("SVG rectangle escapes the fixed viewBox.");
        return new RectangleShape(x, y, width, height, ReadColor(element));
    }

    private static PolygonShape ParsePolygon(XElement element)
    {
        ValidateAttributes(element, PolygonAttributeNames);
        var value = RequireAttribute(element, "points");
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 3 or > MaximumPolygonPoints)
            throw new InvalidDataException("SVG polygon point count is outside the permitted range.");

        var points = new List<IconPoint>(tokens.Length);
        foreach (var token in tokens)
        {
            var pair = token.Split(',');
            if (pair.Length != 2)
                throw new InvalidDataException("SVG polygon points must use integer x,y pairs.");
            points.Add(new IconPoint(ParseCoordinate(pair[0], true), ParseCoordinate(pair[1], true)));
        }

        long twiceArea = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            twiceArea += checked((long)current.X * next.Y - (long)next.X * current.Y);
        }
        if (twiceArea == 0)
            throw new InvalidDataException("SVG polygon must have non-zero area.");
        return new PolygonShape(points.AsReadOnly(), ReadColor(element));
    }

    private static void ValidateAttributes(XElement element, string[] expectedNames)
    {
        var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration
                || attribute.Name.Namespace != XNamespace.None
                || !expected.Remove(attribute.Name.LocalName))
            {
                throw new InvalidDataException($"Unsupported {element.Name.LocalName} attribute: {attribute.Name.LocalName}");
            }
        }
        if (expected.Count != 0)
            throw new InvalidDataException($"SVG {element.Name.LocalName} is missing required attributes.");
    }

    private static int ReadCoordinate(XElement element, string name, bool allowZero) =>
        ParseCoordinate(RequireAttribute(element, name), allowZero);

    private static int ParseCoordinate(string value, bool allowZero)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var coordinate)
            || coordinate < (allowZero ? 0 : 1)
            || coordinate > ViewBoxSize)
        {
            throw new InvalidDataException("SVG coordinates must be bounded non-negative decimal integers.");
        }
        return coordinate;
    }

    private static RgbColor ReadColor(XElement element)
    {
        var value = RequireAttribute(element, "fill");
        if (value.Length != 7 || value[0] != '#'
            || !byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
            || !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
            || !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            throw new InvalidDataException("SVG fill colors must use exact #RRGGBB notation.");
        }
        return new RgbColor(red, green, blue);
    }

    private static string RequireAttribute(XElement element, string name) =>
        element.Attribute(name)?.Value
        ?? throw new InvalidDataException($"SVG {element.Name.LocalName} is missing {name}.");

    private static byte[] Rasterize(List<IShape> shapes, int size)
    {
        var pixels = new byte[checked(size * size * 4)];
        var denominator = checked(2L * size * SamplesPerAxis);
        var sampleCount = SamplesPerAxis * SamplesPerAxis;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var covered = 0;
                var red = 0;
                var green = 0;
                var blue = 0;
                for (var sampleY = 0; sampleY < SamplesPerAxis; sampleY++)
                {
                    var pointY = checked((2L * (y * SamplesPerAxis + sampleY) + 1) * ViewBoxSize);
                    for (var sampleX = 0; sampleX < SamplesPerAxis; sampleX++)
                    {
                        var pointX = checked((2L * (x * SamplesPerAxis + sampleX) + 1) * ViewBoxSize);
                        RgbColor? color = null;
                        for (var shapeIndex = shapes.Count - 1; shapeIndex >= 0; shapeIndex--)
                        {
                            if (!shapes[shapeIndex].Contains(pointX, pointY, denominator)) continue;
                            color = shapes[shapeIndex].Color;
                            break;
                        }
                        if (color is null) continue;
                        covered++;
                        red += color.Value.Red;
                        green += color.Value.Green;
                        blue += color.Value.Blue;
                    }
                }

                var pixel = checked((y * size + x) * 4);
                if (covered == 0) continue;
                pixels[pixel] = checked((byte)((red + covered / 2) / covered));
                pixels[pixel + 1] = checked((byte)((green + covered / 2) / covered));
                pixels[pixel + 2] = checked((byte)((blue + covered / 2) / covered));
                pixels[pixel + 3] = checked((byte)((covered * 255 + sampleCount / 2) / sampleCount));
            }
        }
        return pixels;
    }

    private static byte[] CreateDibPayload(int size, byte[] rgba)
    {
        var pixelBytes = checked(size * size * 4);
        if (rgba.Length != pixelBytes)
            throw new InvalidDataException("Rasterized icon length is invalid.");
        var maskRowBytes = checked(((size + 31) / 32) * 4);
        using var output = new MemoryStream(checked(BitmapInfoHeaderBytes + pixelBytes + maskRowBytes * size));
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(BitmapInfoHeaderBytes);
            writer.Write(size);
            writer.Write(checked(size * 2));
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(pixelBytes);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            for (var storedRow = 0; storedRow < size; storedRow++)
            {
                var sourceY = size - 1 - storedRow;
                for (var x = 0; x < size; x++)
                {
                    var source = checked((sourceY * size + x) * 4);
                    writer.Write(rgba[source + 2]);
                    writer.Write(rgba[source + 1]);
                    writer.Write(rgba[source]);
                    writer.Write(rgba[source + 3]);
                }
            }

            for (var storedRow = 0; storedRow < size; storedRow++)
            {
                var mask = new byte[maskRowBytes];
                var sourceY = size - 1 - storedRow;
                for (var x = 0; x < size; x++)
                {
                    var alpha = rgba[checked((sourceY * size + x) * 4 + 3)];
                    if (alpha == 0)
                        mask[x / 8] |= checked((byte)(1 << (7 - x % 8)));
                }
                writer.Write(mask);
            }
            writer.Flush();
        }
        return output.ToArray();
    }

    private static void ValidateDibPayload(ReadOnlySpan<byte> ico, int offset, int length, int size)
    {
        var pixelBytes = checked(size * size * 4);
        var maskRowBytes = checked(((size + 31) / 32) * 4);
        var expectedLength = checked(BitmapInfoHeaderBytes + pixelBytes + maskRowBytes * size);
        if (length != expectedLength)
            throw new InvalidDataException("ICO DIB payload length is invalid.");
        RequireRange(ico, offset, length, "ICO DIB payload");
        if (ReadInt32(ico, offset) != BitmapInfoHeaderBytes
            || ReadInt32(ico, offset + 4) != size
            || ReadInt32(ico, offset + 8) != checked(size * 2)
            || ReadUInt16(ico, offset + 12) != 1
            || ReadUInt16(ico, offset + 14) != 32
            || ReadInt32(ico, offset + 16) != 0
            || ReadInt32(ico, offset + 20) != pixelBytes
            || ReadInt32(ico, offset + 24) != 0
            || ReadInt32(ico, offset + 28) != 0
            || ReadInt32(ico, offset + 32) != 0
            || ReadInt32(ico, offset + 36) != 0)
        {
            throw new InvalidDataException("ICO DIB header is invalid.");
        }

        var pixelOffset = checked(offset + BitmapInfoHeaderBytes);
        var maskOffset = checked(pixelOffset + pixelBytes);
        for (var row = 0; row < size; row++)
        {
            for (var x = 0; x < maskRowBytes * 8; x++)
            {
                var bit = (ico[checked(maskOffset + row * maskRowBytes + x / 8)] & (1 << (7 - x % 8))) != 0;
                if (x >= size)
                {
                    if (bit) throw new InvalidDataException("ICO AND mask padding is not zero.");
                    continue;
                }
                var alpha = ico[checked(pixelOffset + (row * size + x) * 4 + 3)];
                if (bit != (alpha == 0))
                    throw new InvalidDataException("ICO AND mask disagrees with the BGRA alpha channel.");
            }
        }
    }

    private static void PublishAtomically(
        RepositoryLayout repository,
        string outputPath,
        byte[] generated,
        byte[]? previous)
    {
        var directory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("The generated icon has no parent directory.");
        repository.AssertNoReparseComponents(directory);
        var temporaryPath = repository.RequireRepositoryPath(Path.Combine(
            directory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp"));
        if (File.Exists(temporaryPath) || Directory.Exists(temporaryPath))
            throw new IOException("The run-unique icon staging path already exists.");

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
                stream.Write(generated);
                stream.Flush(flushToDisk: true);
            }
            repository.AssertNoReparseComponents(temporaryPath);
            var staged = ReadBoundedFile(temporaryPath, MaximumIcoBytes, "staged icon");
            if (!staged.AsSpan().SequenceEqual(generated))
                throw new IOException("The staged icon does not match generated bytes.");
            _ = InspectIcoForTesting(staged);

            if (previous is null)
            {
                if (File.Exists(outputPath) || Directory.Exists(outputPath))
                    throw new IOException("The generated icon destination appeared concurrently.");
                File.Move(temporaryPath, outputPath);
            }
            else
            {
                if (!File.Exists(outputPath) || Directory.Exists(outputPath))
                    throw new IOException("The generated icon destination changed concurrently.");
                repository.AssertNoReparseComponents(outputPath);
                var current = ReadBoundedFile(outputPath, MaximumIcoBytes, "generated icon");
                if (!current.AsSpan().SequenceEqual(previous))
                    throw new IOException("The generated icon destination changed concurrently.");
                File.Replace(temporaryPath, outputPath, null, ignoreMetadataErrors: false);
            }

            repository.AssertNoReparseComponents(outputPath);
            var published = ReadBoundedFile(outputPath, MaximumIcoBytes, "published icon");
            if (!published.AsSpan().SequenceEqual(generated))
                throw new IOException("The published icon does not match generated bytes.");
            _ = InspectIcoForTesting(published);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes, string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException($"The {label} length is outside the permitted range.");
        var length = checked((int)info.Length);
        var bytes = new byte[length];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new IOException($"The {label} changed while it was being read.");
        return bytes;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
    {
        RequireRange(bytes, offset, sizeof(ushort), "16-bit integer");
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        RequireRange(bytes, offset, sizeof(int), "32-bit integer");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    }

    private static int ReadBoundedUInt32(ReadOnlySpan<byte> bytes, int offset, string label)
    {
        RequireRange(bytes, offset, sizeof(uint), label);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        if (value > int.MaxValue)
            throw new InvalidDataException($"{label} exceeds the supported range.");
        return (int)value;
    }

    private static void RequireRange(ReadOnlySpan<byte> bytes, int offset, int length, string label)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
            throw new InvalidDataException($"{label} escapes the ICO byte range.");
    }

    private interface IShape
    {
        RgbColor Color { get; }
        bool Contains(long x, long y, long denominator);
    }

    private sealed class RectangleShape(int x, int y, int width, int height, RgbColor color) : IShape
    {
        public RgbColor Color { get; } = color;

        public bool Contains(long sampleX, long sampleY, long denominator) =>
            sampleX >= x * denominator
            && sampleX < (x + width) * denominator
            && sampleY >= y * denominator
            && sampleY < (y + height) * denominator;
    }

    private sealed class PolygonShape(ReadOnlyCollection<IconPoint> points, RgbColor color) : IShape
    {
        public RgbColor Color { get; } = color;

        public bool Contains(long sampleX, long sampleY, long denominator)
        {
            var inside = false;
            var previous = points.Count - 1;
            for (var current = 0; current < points.Count; current++)
            {
                var currentX = points[current].X * denominator;
                var currentY = points[current].Y * denominator;
                var previousX = points[previous].X * denominator;
                var previousY = points[previous].Y * denominator;
                if ((currentY > sampleY) != (previousY > sampleY))
                {
                    var deltaY = previousY - currentY;
                    var left = checked((sampleX - currentX) * deltaY);
                    var right = checked((previousX - currentX) * (sampleY - currentY));
                    if (deltaY > 0 ? left < right : left > right)
                        inside = !inside;
                }
                previous = current;
            }
            return inside;
        }
    }

    private readonly record struct IconPoint(int X, int Y);
    private readonly record struct RgbColor(byte Red, byte Green, byte Blue);
}
