using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RimWorldAiTranslator.UiHarness;

internal static class SnapshotComparison
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Create(
        string baselinePath,
        string candidatePath,
        string outputRoot,
        string name)
    {
        var safeName = string.Concat((name ?? string.Empty).Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "comparison";
        var fullOutputRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(fullOutputRoot);

        using var baseline = new Bitmap(Path.GetFullPath(baselinePath));
        using var candidate = new Bitmap(Path.GetFullPath(candidatePath));
        if (baseline.Size != candidate.Size)
        {
            throw new InvalidDataException(
                $"Matched snapshots must have identical pixels: baseline={baseline.Width}x{baseline.Height}, " +
                $"candidate={candidate.Width}x{candidate.Height}.");
        }

        var dynamicRegions = DynamicRegions(safeName, baseline.Size);
        SaveSideBySide(baseline, candidate, Path.Combine(fullOutputRoot, safeName + ".side-by-side.png"));
        SaveOverlay(baseline, candidate, Path.Combine(fullOutputRoot, safeName + ".overlay.png"));
        var metrics = SaveDifference(
            baseline,
            candidate,
            dynamicRegions,
            Path.Combine(fullOutputRoot, safeName + ".diff.png"));
        SaveMask(baseline.Size, dynamicRegions, Path.Combine(fullOutputRoot, safeName + ".mask.png"));
        File.WriteAllText(
            Path.Combine(fullOutputRoot, safeName + ".comparison.json"),
            JsonSerializer.Serialize(new
            {
                version = 1,
                baseline = Path.GetFileName(baselinePath),
                candidate = Path.GetFileName(candidatePath),
                width = baseline.Width,
                height = baseline.Height,
                dynamicRegions = dynamicRegions.Select(region => new
                {
                    region.X,
                    region.Y,
                    region.Width,
                    region.Height
                }),
                metrics.TotalPixels,
                metrics.ComparedPixels,
                metrics.DifferentPixels,
                metrics.DifferentPixelRatio,
                metrics.MeanAbsoluteChannelDifference,
                note = "Pixel metrics are descriptive only; structural and human review controls the verdict."
            }, JsonOptions),
            new System.Text.UTF8Encoding(false));
    }

    private static void SaveSideBySide(Bitmap baseline, Bitmap candidate, string path)
    {
        const int captionHeight = 30;
        using var output = new Bitmap(baseline.Width * 2, baseline.Height + captionHeight);
        using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.FromArgb(28, 32, 35));
        graphics.DrawImageUnscaled(baseline, 0, captionHeight);
        graphics.DrawImageUnscaled(candidate, baseline.Width, captionHeight);
        using var font = new Font("Malgun Gothic", 10f, FontStyle.Bold);
        TextRenderer.DrawText(
            graphics,
            "Golden Master",
            font,
            new Rectangle(8, 0, baseline.Width - 16, captionHeight),
            Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(
            graphics,
            "C# candidate",
            font,
            new Rectangle(baseline.Width + 8, 0, candidate.Width - 16, captionHeight),
            Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        output.Save(path, ImageFormat.Png);
    }

    private static void SaveOverlay(Bitmap baseline, Bitmap candidate, string path)
    {
        using var output = new Bitmap(baseline.Width, baseline.Height);
        using var graphics = Graphics.FromImage(output);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(baseline, 0, 0);
        graphics.CompositingMode = CompositingMode.SourceOver;
        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix { Matrix33 = 0.5f };
        attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        graphics.DrawImage(
            candidate,
            new Rectangle(0, 0, candidate.Width, candidate.Height),
            0,
            0,
            candidate.Width,
            candidate.Height,
            GraphicsUnit.Pixel,
            attributes);
        output.Save(path, ImageFormat.Png);
    }

    private static DifferenceMetrics SaveDifference(
        Bitmap baseline,
        Bitmap candidate,
        IReadOnlyList<Rectangle> dynamicRegions,
        string path)
    {
        using var left = ToArgbBitmap(baseline);
        using var right = ToArgbBitmap(candidate);
        using var output = new Bitmap(left.Width, left.Height, PixelFormat.Format32bppArgb);
        var rectangle = new Rectangle(0, 0, left.Width, left.Height);
        var leftData = left.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rightData = right.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var outputData = output.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var byteCount = Math.Abs(leftData.Stride) * left.Height;
        var leftBytes = new byte[byteCount];
        var rightBytes = new byte[byteCount];
        var outputBytes = new byte[byteCount];
        Marshal.Copy(leftData.Scan0, leftBytes, 0, byteCount);
        Marshal.Copy(rightData.Scan0, rightBytes, 0, byteCount);
        var masked = BuildMask(left.Size, dynamicRegions);
        long compared = 0;
        long different = 0;
        long absoluteDifference = 0;
        try
        {
            for (var y = 0; y < left.Height; y++)
            {
                var rowOffset = y * Math.Abs(leftData.Stride);
                for (var x = 0; x < left.Width; x++)
                {
                    var offset = rowOffset + x * 4;
                    if (masked[y * left.Width + x])
                    {
                        outputBytes[offset] = 24;
                        outputBytes[offset + 1] = 24;
                        outputBytes[offset + 2] = 24;
                        outputBytes[offset + 3] = 255;
                        continue;
                    }
                    var blue = Math.Abs(leftBytes[offset] - rightBytes[offset]);
                    var green = Math.Abs(leftBytes[offset + 1] - rightBytes[offset + 1]);
                    var red = Math.Abs(leftBytes[offset + 2] - rightBytes[offset + 2]);
                    compared++;
                    absoluteDifference += red + green + blue;
                    if (red != 0 || green != 0 || blue != 0) different++;
                    var intensity = Math.Clamp(Math.Max(red, Math.Max(green, blue)) * 3, 0, 255);
                    outputBytes[offset] = 0;
                    outputBytes[offset + 1] = (byte)Math.Min(255, intensity / 3);
                    outputBytes[offset + 2] = (byte)intensity;
                    outputBytes[offset + 3] = 255;
                }
            }
            Marshal.Copy(outputBytes, 0, outputData.Scan0, byteCount);
        }
        finally
        {
            left.UnlockBits(leftData);
            right.UnlockBits(rightData);
            output.UnlockBits(outputData);
        }
        output.Save(path, ImageFormat.Png);
        return new DifferenceMetrics(
            (long)left.Width * left.Height,
            compared,
            different,
            compared == 0 ? 0 : Math.Round(different / (double)compared, 6),
            compared == 0 ? 0 : Math.Round(absoluteDifference / (double)(compared * 3), 3));
    }

    private static Bitmap ToArgbBitmap(Image source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImageUnscaled(source, 0, 0);
        return result;
    }

    private static bool[] BuildMask(Size size, IReadOnlyList<Rectangle> regions)
    {
        var result = new bool[size.Width * size.Height];
        foreach (var region in regions)
        {
            var bounded = Rectangle.Intersect(region, new Rectangle(Point.Empty, size));
            for (var y = bounded.Top; y < bounded.Bottom; y++)
                for (var x = bounded.Left; x < bounded.Right; x++)
                    result[y * size.Width + x] = true;
        }
        return result;
    }

    private static void SaveMask(Size size, IReadOnlyList<Rectangle> dynamicRegions, string path)
    {
        using var mask = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(mask);
        graphics.Clear(Color.Black);
        using var brush = new SolidBrush(Color.White);
        foreach (var region in dynamicRegions) graphics.FillRectangle(brush, region);
        mask.Save(path, ImageFormat.Png);
    }

    private static Rectangle[] DynamicRegions(string name, Size size)
    {
        var regions = new List<Rectangle>();
        if (name.Contains("operation", StringComparison.OrdinalIgnoreCase))
            regions.Add(Rectangle.Intersect(new Rectangle(12, 82, 56, 56), new Rectangle(Point.Empty, size)));
        return regions.ToArray();
    }

    private sealed record DifferenceMetrics(
        long TotalPixels,
        long ComparedPixels,
        long DifferentPixels,
        double DifferentPixelRatio,
        double MeanAbsoluteChannelDifference);
}
