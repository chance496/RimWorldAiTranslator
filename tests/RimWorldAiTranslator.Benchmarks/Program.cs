using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using RimWorldAiTranslator.Core.Extraction;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Benchmarks;

internal static class Program
{
    private const int DefaultRows = 5_000;
    private const int DefaultIterations = 5;

    private static int Main(string[] args)
    {
        var rows = ReadPositiveArgument(args, "--rows", DefaultRows);
        var iterations = ReadPositiveArgument(args, "--iterations", DefaultIterations);
        var tempRoot = Path.Combine(Path.GetTempPath(), "RimWorldAiTranslator-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var modRoot = CreateFixture(tempRoot, rows);
            var extractor = new SourceExtractor();
            _ = extractor.Extract(modRoot, "English");

            var extractionSamples = Measure(iterations, () =>
            {
                var result = extractor.Extract(modRoot, "English");
                if (result.Entries.Count != rows) throw new InvalidOperationException($"Expected {rows} extracted rows, got {result.Entries.Count}.");
            });

            var extraction = extractor.Extract(modRoot, "English");
            var reviewRoot = CreateReviewFixture(tempRoot, extraction.Entries);
            var reviewService = new ReviewWorkspaceService(new AtomicJsonStore(), extractor);
            _ = reviewService.Load(reviewRoot);

            var loadSamples = Measure(iterations, () =>
            {
                var workspace = reviewService.Load(reviewRoot);
                if (workspace.Items.Count != rows) throw new InvalidOperationException($"Expected {rows} review rows, got {workspace.Items.Count}.");
            });

            var loaded = reviewService.Load(reviewRoot);
            var querySamples = Measure(iterations * 20, () =>
            {
                var result = reviewService.Query(loaded, new ReviewQuery("Term04999", ReviewSearchField.Key));
                if (result.Count != 1) throw new InvalidOperationException($"Expected one search result, got {result.Count}.");
            });

            var statusSamples = Measure(iterations * 20, () =>
            {
                var result = reviewService.Query(loaded, new ReviewQuery(Status: ReviewStatusFilter.Pending));
                if (result.Count != rows) throw new InvalidOperationException($"Expected {rows} pending rows, got {result.Count}.");
            });

            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelWatch = Stopwatch.StartNew();
            try
            {
                extractor.Extract(modRoot, "English", cancellationToken: cancellation.Token);
                throw new InvalidOperationException("A pre-cancelled extraction completed unexpectedly.");
            }
            catch (OperationCanceledException)
            {
                cancelWatch.Stop();
            }

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var report = new
            {
                timestamp = DateTimeOffset.Now.ToString("O"),
                machine = Environment.MachineName,
                framework = Environment.Version.ToString(),
                rows,
                iterations,
                extractionMs = Summary(extractionSamples),
                reviewLoadMs = Summary(loadSamples),
                keySearchMs = Summary(querySamples),
                statusFilterMs = Summary(statusSamples),
                preCancelledExtractionMs = Math.Round(cancelWatch.Elapsed.TotalMilliseconds, 3),
                workingSetMb = Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
                privateMemoryMb = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 1)
            };

            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            var fullRoot = Path.GetFullPath(tempRoot);
            var tempBase = Path.GetFullPath(Path.GetTempPath());
            if (fullRoot.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullRoot).StartsWith("RimWorldAiTranslator-benchmark-", StringComparison.Ordinal))
            {
                try { Directory.Delete(fullRoot, recursive: true); } catch { }
            }
        }
    }

    private static string CreateFixture(string root, int rows)
    {
        var modRoot = Path.Combine(root, "mod");
        var keyedRoot = Path.Combine(modRoot, "Languages", "English", "Keyed");
        Directory.CreateDirectory(keyedRoot);
        var file = Path.Combine(keyedRoot, "Benchmark.xml");
        var settings = new XmlWriterSettings { Indent = false, Encoding = new System.Text.UTF8Encoding(false) };
        using var writer = XmlWriter.Create(file, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("LanguageData");
        for (var index = 0; index < rows; index++)
        {
            writer.WriteElementString($"Benchmark.Term{index:D5}", $"Benchmark source text {index:D5}");
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
        return modRoot;
    }

    private static string CreateReviewFixture(string root, IReadOnlyList<SourceEntry> entries)
    {
        var reviewRoot = Path.Combine(root, "review");
        var target = Path.Combine(reviewRoot, "Languages", "Korean", "Keyed", "Benchmark.xml");
        var auditRoot = Path.Combine(reviewRoot, "_TranslationAudit");
        Directory.CreateDirectory(auditRoot);
        var rows = entries.Select(entry => new ReviewComparisonRow
        {
            Id = entry.Id,
            Key = entry.Key,
            Kind = entry.Kind,
            DefClass = entry.TypeName,
            Node = entry.Key,
            Field = entry.Field,
            Target = target,
            Source = entry.Text,
            CandidateBlank = true,
            SafeToApply = false
        }).ToArray();
        File.WriteAllText(
            Path.Combine(auditRoot, "benchmark-comparison.json"),
            JsonSerializer.Serialize(rows),
            new System.Text.UTF8Encoding(false));
        return reviewRoot;
    }

    private static double[] Measure(int count, Action action)
    {
        var samples = new double[count];
        for (var index = 0; index < count; index++)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            samples[index] = watch.Elapsed.TotalMilliseconds;
        }
        return samples;
    }

    private static object Summary(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return new
        {
            median = Math.Round(Percentile(ordered, 0.5), 3),
            p95 = Math.Round(Percentile(ordered, 0.95), 3),
            max = Math.Round(ordered[^1], 3)
        };
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 1) return ordered[0];
        var position = (ordered.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return ordered[lower];
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * (position - lower));
    }

    private static int ReadPositiveArgument(IReadOnlyList<string> args, string name, int fallback)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(args[index + 1], out var value) && value > 0) return value;
            throw new ArgumentException($"{name} must be a positive integer.");
        }
        return fallback;
    }
}
