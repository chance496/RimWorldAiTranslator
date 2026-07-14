using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using RimWorldAiTranslator.Core.Validation;

namespace RimWorldAiTranslator.Core.Logging;

public sealed partial class AppLogger : IDisposable, IAsyncDisposable
{
    private const int DefaultQueueCapacity = 4096;
    internal const int MaximumMessageCharacters = 16_384;
    private static readonly TimeSpan DefaultDisposeDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly object sync = new();
    private readonly Channel<string> queue;
    private readonly TextWriter writer;
    private readonly Task drainTask;
    private readonly TimeSpan disposeDrainTimeout;
    private bool disposed;
    private long droppedMessages;

    public AppLogger(string logDirectory)
        : this(OpenPersistentWriter(logDirectory), DefaultQueueCapacity, DefaultDisposeDrainTimeout)
    {
    }

    internal AppLogger(
        TextWriter writer,
        int queueCapacity = DefaultQueueCapacity,
        TimeSpan? disposeDrainTimeout = null)
        : this(
            new WriterTarget(writer ?? throw new ArgumentNullException(nameof(writer)), "<synthetic>"),
            queueCapacity,
            disposeDrainTimeout ?? DefaultDisposeDrainTimeout)
    {
    }

    private AppLogger(WriterTarget target, int queueCapacity, TimeSpan disposeDrainTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(disposeDrainTimeout, TimeSpan.Zero);
        writer = target.Writer;
        Path = target.Path;
        this.disposeDrainTimeout = disposeDrainTimeout;
        queue = Channel.CreateBounded<string>(new BoundedChannelOptions(queueCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        drainTask = Task.Run(DrainAsync);
    }

    public string Path { get; }
    public event EventHandler<string>? MessageWritten;

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var safe = Redact(message);
        var line = $"[{DateTime.Now:HH:mm:ss}] {level,-5} {safe}";
        bool accepted;
        var firstOmission = false;
        lock (sync)
        {
            if (disposed)
            {
                Debug.WriteLine("Log write skipped after logger disposal.");
                return;
            }

            accepted = queue.Writer.TryWrite(line);
            if (!accepted)
            {
                droppedMessages++;
                firstOmission = droppedMessages == 1;
            }
        }

        NotifySubscribers(line);
        if (firstOmission)
            Debug.WriteLine("Persistent log queue is full; a message was omitted from the file sink.");
    }

    private void NotifySubscribers(string line)
    {
        var subscribers = MessageWritten;
        if (subscribers is null) return;
        foreach (EventHandler<string> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, line);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Log subscriber notification skipped ({exception.GetType().Name}).");
            }
        }
    }

    public static string Redact(string? value)
    {
        var text = value ?? string.Empty;
        text = DoubleQuotedNamedCredentialValueRegex().Replace(text, RedactNamedCredentialValue);
        text = SingleQuotedNamedCredentialValueRegex().Replace(text, RedactNamedCredentialValue);
        text = AuthorizationValueRegex().Replace(text, RedactCredentialValue);
        text = KeyValueRegex().Replace(text, RedactCredentialValue);
        text = NamedCredentialValueRegex().Replace(text, RedactNamedCredentialValue);
        text = AuthorizationSchemeRegex().Replace(text, "[REDACTED]");
        text = KnownCredentialTokenRegex().Replace(text, "[REDACTED]");
        text = JsonWebTokenRegex().Replace(text, "[REDACTED]");
        text = QuotedWindowsPathRegex().Replace(text, match =>
            match.Groups["quote"].Value + "[PATH REDACTED]" + match.Groups["quote"].Value);
        text = WindowsPathRegex().Replace(text, "[PATH REDACTED]");
        var safe = SanitizeLogStructure(text);
        if (safe.Length <= MaximumMessageCharacters) return safe;

        var truncateAt = MaximumMessageCharacters;
        if (char.IsHighSurrogate(safe[truncateAt - 1]) && char.IsLowSurrogate(safe[truncateAt]))
            truncateAt--;
        return safe[..truncateAt] + " [TRUNCATED]";
    }

    private static string SanitizeLogStructure(string value)
    {
        StringBuilder? safe = null;
        var index = 0;
        while (index < value.Length)
        {
            if (!Rune.TryGetRuneAt(value, index, out var rune))
            {
                safe ??= new StringBuilder(value.Length).Append(value, 0, index);
                safe.Append(' ');
                index++;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is not UnicodeCategory.Control
                and not UnicodeCategory.Format
                and not UnicodeCategory.LineSeparator
                and not UnicodeCategory.ParagraphSeparator)
            {
                safe?.Append(rune.ToString());
                index += rune.Utf16SequenceLength;
                continue;
            }

            safe ??= new StringBuilder(value.Length).Append(value, 0, index);
            safe.Append(' ');
            index += rune.Utf16SequenceLength;
        }

        return safe?.ToString() ?? value;
    }

    private static string RedactCredentialValue(Match match)
    {
        var prefix = match.Groups["prefix"].Value;
        var credential = match.Groups["value"].Value;
        if (credential.Length >= 2
            && credential[0] is '"' or '\''
            && credential[^1] == credential[0])
        {
            return prefix + credential[0] + "[REDACTED]" + credential[^1];
        }

        return prefix + "[REDACTED]";
    }

    private static string RedactNamedCredentialValue(Match match)
    {
        var name = match.Groups["key"].Value;
        return ProviderValidator.IsCredentialName(name)
               || ProviderValidator.IsCredentialPropertyName(name)
            ? RedactCredentialValue(match)
            : match.Value;
    }

    public void Dispose()
    {
        var completion = StopAcceptingWrites();

        try
        {
            // Keep the synchronous fallback bounded for non-UI ownership cleanup and
            // direct callers. Production form shutdown uses DisposeAsync.
            if (!completion.Wait(disposeDrainTimeout))
                Debug.WriteLine("Persistent log drain exceeded the shutdown time limit and will finish in the background.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Persistent log drain skipped ({exception.GetType().Name}).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        var completion = StopAcceptingWrites();
        try
        {
            await completion.WaitAsync(disposeDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("Persistent log drain exceeded the shutdown time limit and will finish in the background.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Persistent log drain skipped ({exception.GetType().Name}).");
        }
    }

    private Task StopAcceptingWrites()
    {
        lock (sync)
        {
            if (!disposed)
            {
                disposed = true;
                queue.Writer.TryComplete();
            }
            return drainTask;
        }
    }

    internal Task DrainCompletionForTesting => drainTask;

    private async Task DrainAsync()
    {
        var sinkAvailable = true;
        try
        {
            await foreach (var line in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                if (sinkAvailable) sinkAvailable = TryWriteLine(line);

            var omitted = Interlocked.Exchange(ref droppedMessages, 0);
            if (sinkAvailable && omitted > 0)
                TryWriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  Persistent log queue capacity was exceeded; {omitted:N0} message(s) omitted.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Persistent log drain failed ({exception.GetType().Name}).");
        }
        finally
        {
            try
            {
                writer.Flush();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Persistent log flush skipped ({exception.GetType().Name}).");
            }

            try
            {
                writer.Dispose();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Persistent log disposal skipped ({exception.GetType().Name}).");
            }
        }
    }

    private bool TryWriteLine(string line)
    {
        try
        {
            writer.WriteLine(line);
            writer.Flush();
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Persistent log write skipped ({exception.GetType().Name}).");
            return false;
        }
    }

    private static WriterTarget OpenPersistentWriter(string logDirectory)
    {
        var path = System.IO.Path.Combine(logDirectory, $"RimWorldAiTranslator-{DateTime.Now:yyyyMMdd}.log");
        try
        {
            return new WriterTarget(SafeLogFile.OpenUtf8AppendWriter(path), path);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException
                                          or InvalidDataException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            Debug.WriteLine($"Persistent logging is unavailable ({exception.GetType().Name}).");
            return new WriterTarget(TextWriter.Null, path);
        }
    }

    private sealed record WriterTarget(TextWriter Writer, string Path);

    [GeneratedRegex("(?i)(?<prefix>(?<![a-z0-9])(?<keyQuote>[\"']?)authorization\\k<keyQuote>\\s*[:=]\\s*)(?<value>\"(?:\\\\.|[^\"\\\\\\r\\n])*\"|'(?:\\\\.|[^'\\\\\\r\\n])*'|(?:(?:basic|bearer|token|apikey)\\s+)?(?:(?!&[a-z][a-z0-9_.-]*\\s*=)[^\\s])+)", RegexOptions.CultureInvariant)] private static partial Regex AuthorizationValueRegex();
    [GeneratedRegex("(?i)(?<prefix>(?<![a-z0-9])(?<keyQuote>[\"']?)(?:(?:x[\\s_-]*)?api[\\s_-]*key|x[\\s_-]*goog[\\s_-]*api[\\s_-]*key|ocp[\\s_-]*apim[\\s_-]*subscription[\\s_-]*key|subscription[\\s_-]*key|(?:access|refresh|id|session|secret|security|api|auth|bearer|sas)[\\s_-]*token|client[\\s_-]*(?:secret|assertion|credential)|(?:secret|auth|private|access|encryption|signing)[\\s_-]*key|api[\\s_-]*secret|shared[\\s_-]*(?:secret|access[\\s_-]*signature)|aws[\\s_-]*(?:access[\\s_-]*key[\\s_-]*id|secret[\\s_-]*access[\\s_-]*key|session[\\s_-]*token)|x[\\s_-]*amz[\\s_-]*(?:signature|credential|security[\\s_-]*token)|secret[\\s_-]*access[\\s_-]*key|session[\\s_-]*id|password|credential|authorization|secret|token)(?:(?:[\\s_.-]*)(?:value|values|text|data|setting|settings|encrypted|ciphertext|reference|ref|field|property|backup|primary|secondary|alternate|fallback|previous|current|next|old|new|legacy|default|temporary|temp|copy|[0-9]+))*\\k<keyQuote>\\s*[:=]\\s*)(?<value>\"(?:\\\\.|[^\"\\\\\\r\\n])*\"|'(?:\\\\.|[^'\\\\\\r\\n])*'|(?:(?!&[a-z][a-z0-9_.-]*\\s*=)[^\\s])+)", RegexOptions.CultureInvariant)] private static partial Regex KeyValueRegex();
    [GeneratedRegex("(?i)(?<prefix>(?<![a-z0-9])\"(?<key>[^\"\\r\\n]{1,128})\"\\s*[:=]\\s*)(?<value>\"(?:\\\\.|[^\"\\\\\\r\\n])*\"|'(?:\\\\.|[^'\\\\\\r\\n])*'|(?:(?!&[a-z][a-z0-9_.-]*\\s*=)[^\\s])+)", RegexOptions.CultureInvariant)] private static partial Regex DoubleQuotedNamedCredentialValueRegex();
    [GeneratedRegex("(?i)(?<prefix>(?<![a-z0-9])'(?<key>[^'\\r\\n]{1,128})'\\s*[:=]\\s*)(?<value>\"(?:\\\\.|[^\"\\\\\\r\\n])*\"|'(?:\\\\.|[^'\\\\\\r\\n])*'|(?:(?!&[a-z][a-z0-9_.-]*\\s*=)[^\\s])+)", RegexOptions.CultureInvariant)] private static partial Regex SingleQuotedNamedCredentialValueRegex();
    [GeneratedRegex("(?i)(?<prefix>(?<![a-z0-9])(?<key>[a-z][a-z0-9_.-]{0,95})\\s*[:=]\\s*)(?<value>\"(?:\\\\.|[^\"\\\\\\r\\n])*\"|'(?:\\\\.|[^'\\\\\\r\\n])*'|(?:(?!&[a-z][a-z0-9_.-]*\\s*=)[^\\s])+)", RegexOptions.CultureInvariant)] private static partial Regex NamedCredentialValueRegex();
    [GeneratedRegex("(?i)(?<![A-Za-z0-9])(?:Bearer|Basic)\\s+\\S+", RegexOptions.CultureInvariant)] private static partial Regex AuthorizationSchemeRegex();
    [GeneratedRegex("(?i)(?<![A-Za-z0-9])(?:(?:(?:sk-|csk-|gsk_|AIza|ghp_|gho_|ghu_|ghs_|ghr_|github_pat_|hf_|glpat-|xoxb-|xoxp-|xoxa-|xoxr-|xoxs-|ya29\\.)[A-Za-z0-9._~+/=-]{8,})(?![A-Za-z0-9._~+/=-])|(?:(?:AKIA|ASIA)[0-9A-Z]{16})(?![A-Za-z0-9])|(?:(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}):fx)(?![A-Za-z0-9]))", RegexOptions.CultureInvariant)] private static partial Regex KnownCredentialTokenRegex();
    [GeneratedRegex("(?i)(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{5,}\\.[A-Za-z0-9_-]{5,}\\.[A-Za-z0-9_-]{5,}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)] private static partial Regex JsonWebTokenRegex();
    [GeneratedRegex("(?i)(?<quote>[\"'])(?:[a-z]:[\\\\/]|\\\\\\\\[^\\\\/\\r\\n]+[\\\\/][^\\\\/\\r\\n]+)[^\"'\\r\\n]*\\k<quote>", RegexOptions.CultureInvariant)] private static partial Regex QuotedWindowsPathRegex();
    [GeneratedRegex("(?i)(?<![a-z0-9])(?:[a-z]:[\\\\/]|\\\\\\\\[^\\\\/\\r\\n,;]+[\\\\/][^\\\\/\\r\\n,;]+)[^\\r\\n,;]*", RegexOptions.CultureInvariant)] private static partial Regex WindowsPathRegex();
}
