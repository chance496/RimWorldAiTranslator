using System.Net;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void Phase08MonotonicRateLimiter()
    {
        var timeProvider = new RegressingWallClockTimeProvider();
        var delays = new List<TimeSpan>();
        var handler = new SyntheticHttpHandler(
        [
            SyntheticSuccess,
            SyntheticSuccess
        ]);
        using var client = new TranslationApiClient(
            ["synthetic-key-not-real"],
            handler,
            (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(delay);
                timeProvider.RegressWallClockAndAdvanceTimestamp(delay);
                return Task.CompletedTask;
            },
            timeProvider);
        var options = new TranslationEngineOptions
        {
            ModRoot = "synthetic-mod",
            Provider = ApiProviderCatalog.Get("Custom"),
            ProviderSettings = new ApiProviderSettings
            {
                Name = "Synthetic",
                Url = "https://fixture.invalid",
                Model = "synthetic-model",
                Temperature = 0.1
            },
            RequestsPerMinutePerKey = 60,
            InputTokensPerMinutePerKey = 0,
            DailyTokenBudgetPerKey = 0,
            MaxRetries = 1,
            Timeout = TimeSpan.FromSeconds(2),
            ResponseFormatMode = "JsonObject",
            CompletionTokenParameter = "none",
            ReasoningEffort = string.Empty
        };
        SourceEntry[] batch =
        [
            new() { Id = "id-1", Key = "Synthetic.One", Kind = "Keyed", Text = "first {0}" }
        ];

        var first = client.TranslateOpenAiAsync(
                options,
                batch,
                "synthetic system prompt",
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult();
        var second = client.TranslateOpenAiAsync(
                options,
                batch,
                "synthetic system prompt",
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert(first["id-1"] == "translated" && second["id-1"] == "translated",
            "The monotonic limiter did not preserve successful responses across a wall-clock regression.");
        Assert(handler.RequestUris.Count == 2,
            "The monotonic limiter did not issue exactly the two synthetic requests.");
        Assert(delays.Count == 0,
            "A legacy fixed RPM value throttled successful responses that contained no rate-limit headers.");
        Assert(timeProvider.GetUtcNow() == RegressingWallClockTimeProvider.InitialUtcNow,
            "The headerless request path unexpectedly invoked a rate-limit delay.");

        static HttpResponseMessage SyntheticSuccess() =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ResponseWithContent(TranslationContent(("id-1", "translated"))),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
    }

    private sealed class RegressingWallClockTimeProvider : TimeProvider
    {
        public static readonly DateTimeOffset InitialUtcNow =
            new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

        private long timestamp;
        private DateTimeOffset utcNow = InitialUtcNow;

        public override long TimestampFrequency => 1_000;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => timestamp;

        public void RegressWallClockAndAdvanceTimestamp(TimeSpan elapsed)
        {
            utcNow -= TimeSpan.FromDays(7);
            timestamp += (long)Math.Ceiling(elapsed.TotalSeconds * TimestampFrequency);
        }
    }
}
