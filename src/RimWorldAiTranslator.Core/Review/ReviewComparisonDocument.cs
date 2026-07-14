using System.Security.Cryptography;
using System.Text.Json;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Core.Review;

public sealed record ReviewComparisonEvidence(string Path, string Sha256);

internal sealed class ReviewComparisonLimits
{
    public int MaximumRows { get; init; } = 250_000;
    public int MaximumDepth { get; init; } = 64;
    public long MaximumStringTokenBytes { get; init; } = 1L * 1024 * 1024;
    public long MaximumAggregateStringValueBytes { get; init; } = 64L * 1024 * 1024;
    public int MaximumJsonTokens { get; init; } = 20_000_000;
    public long MaximumRowBytes { get; init; } = 8L * 1024 * 1024;
    public int MaximumTokensPerRow { get; init; } = 16_384;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumStringTokenBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAggregateStringValueBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumJsonTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRowBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTokensPerRow);
    }
}

internal sealed record ReviewComparisonDocument(
    ReviewComparisonEvidence Evidence,
    IReadOnlyList<ReviewComparisonRow> Rows)
{
    internal const long MaximumBytes = 128L * 1024 * 1024;
    internal const int MaximumCandidateFiles = 1_024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };

    public static ReviewComparisonDocument LoadExact(
        string reviewRoot,
        string comparisonPath,
        CancellationToken cancellationToken = default) =>
        LoadExact(
            reviewRoot,
            comparisonPath,
            new ReviewComparisonLimits(),
            cancellationToken: cancellationToken);

    internal static ReviewComparisonDocument LoadExact(
        string reviewRoot,
        string comparisonPath,
        ReviewComparisonLimits limits,
        Action<long>? lengthObserver = null,
        Action<int>? rowObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (PathSafety.IsNetworkPath(reviewRoot) || PathSafety.IsNetworkPath(comparisonPath))
            throw new InvalidDataException("Comparison evidence must use local paths.");
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(reviewRoot);
        var fullPath = ResolveExactPathForAdmission(root, comparisonPath);
        PathSafety.EnsureNoReparsePoints(fullPath, root);
        byte[] bytes;
        try
        {
            bytes = BoundedFileReader.ReadAllBytes(
                fullPath,
                MaximumBytes,
                "Declared comparison file",
                lengthObserver,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Declared comparison file could not be read: {fullPath}", exception);
        }
        PathSafety.EnsureNoReparsePoints(fullPath, root);
        IReadOnlyList<ReviewComparisonRow> rows;
        try
        {
            var rawRowCount = ValidateResourceBounds(bytes, limits, rowObserver, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            rows = DeserializeRows(bytes, rawRowCount, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException($"Declared comparison file is invalid: {fullPath}", exception);
        }
        return new ReviewComparisonDocument(
            new ReviewComparisonEvidence(fullPath, ComputeSha256(bytes, cancellationToken)),
            rows);
    }

    private static IReadOnlyList<ReviewComparisonRow> DeserializeRows(
        ReadOnlySpan<byte> bytes,
        int expectedRows,
        CancellationToken cancellationToken)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = JsonOptions.MaxDepth
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException("Declared comparison file must contain a root JSON array.");

        var rows = new List<ReviewComparisonRow>(expectedRows);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Declared comparison rows must be JSON objects.");
            var row = JsonSerializer.Deserialize<ReviewComparisonRow>(ref reader, JsonOptions);
            if (row is null || HasNullScalar(row))
            {
                throw new InvalidDataException(
                    $"Declared comparison row {rows.Count + 1} contains an explicit null scalar field.");
            }
            rows.Add(row);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count != expectedRows)
            throw new InvalidDataException("Declared comparison row count changed during validation.");
        return rows;
    }

    private static bool HasNullScalar(ReviewComparisonRow row) =>
        row.Id is null
        || row.Key is null
        || row.Kind is null
        || row.DefClass is null
        || row.Node is null
        || row.Field is null
        || row.Target is null
        || row.Source is null
        || row.Existing is null
        || row.ExistingSourceHash is null
        || row.ExistingSourceText is null
        || row.ExistingPreviousSourceText is null
        || row.Candidate is null
        || row.ExistingOrigin is null
        || row.TranslationOrigin is null
        || row.TranslationUpdatedAt is null
        || row.RmkIdentifier is null
        || row.RmkHistoricalSource is null
        || row.RmkCurrentSource is null
        || row.RmkWorkbook is null
        || row.MissingTokens is null
        || row.UnexpectedTokens is null
        || row.TokenCountMismatches is null
        || row.InvalidKoreanParticles is null;

    internal static IReadOnlyList<string> EnumerateCandidateFiles(
        string auditRoot,
        int maximumCandidates = MaximumCandidateFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCandidates);
        var candidates = new List<string>(Math.Min(maximumCandidates, 256));
        foreach (var path in Directory.EnumerateFiles(
                     auditRoot,
                     "*-comparison.json",
                     SearchOption.TopDirectoryOnly))
        {
            if (candidates.Count >= maximumCandidates)
            {
                throw new InvalidDataException(
                    $"Translation audit contains more than {maximumCandidates:N0} comparison files.");
            }
            candidates.Add(path);
        }
        return candidates;
    }

    public static void VerifyUnchanged(string reviewRoot, ReviewComparisonEvidence evidence)
    {
        var current = LoadExact(reviewRoot, evidence.Path);
        if (!current.Evidence.Path.Equals(evidence.Path, StringComparison.OrdinalIgnoreCase)
            || !current.Evidence.Sha256.Equals(evidence.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Declared comparison file changed after it was loaded: {evidence.Path}");
        }
    }

    public static void RequireBoundDecisionStore(
        ReviewDecisionDocument decisions,
        ReviewComparisonEvidence evidence)
    {
        if (decisions.Version != ReviewDecisionDocument.CurrentVersion
            || !IsSha256(decisions.ComparisonSha256))
        {
            throw new InvalidDataException(
                "Review decisions are not bound to verified comparison evidence. Open and save the review before applying or exporting it.");
        }
        if (!decisions.ComparisonSha256.Equals(evidence.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The declared comparison file does not match the evidence bound to the review decisions.");
        }
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static int ValidateResourceBounds(
        ReadOnlySpan<byte> bytes,
        ReviewComparisonLimits limits,
        Action<int>? rowObserver,
        CancellationToken cancellationToken)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = limits.MaximumDepth
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException("Declared comparison file must contain a root JSON array.");

        var rows = 0;
        var tokens = 1;
        long aggregateStringValueBytes = 0;
        var closed = false;
        var propertyScopes = new List<HashSet<string>>();
        var propertyScopeDepth = 0;
        long rowStart = -1;
        var rowTokens = 0;
        while (reader.Read())
        {
            if ((tokens & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (++tokens > limits.MaximumJsonTokens)
            {
                throw new InvalidDataException(
                    $"Declared comparison file contains more than {limits.MaximumJsonTokens:N0} JSON tokens.");
            }
            if (rowStart >= 0)
            {
                if (++rowTokens > limits.MaximumTokensPerRow)
                    throw new InvalidDataException(
                        $"Declared comparison row contains more than {limits.MaximumTokensPerRow:N0} JSON tokens.");
                if (reader.BytesConsumed - rowStart > limits.MaximumRowBytes)
                    throw new InvalidDataException(
                        $"Declared comparison row exceeds the {limits.MaximumRowBytes:N0}-byte limit.");
            }

            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                var tokenBytes = reader.HasValueSequence
                    ? reader.ValueSequence.Length
                    : reader.ValueSpan.Length;
                if (tokenBytes > limits.MaximumStringTokenBytes)
                {
                    throw new InvalidDataException(
                        $"Declared comparison file contains a string token larger than {limits.MaximumStringTokenBytes:N0} bytes.");
                }
                // Repeated known property names are not materialized per row by JsonSerializer. Count
                // string values here while still applying the individual token cap to property names.
                if (reader.TokenType == JsonTokenType.String)
                {
                    if (tokenBytes > limits.MaximumAggregateStringValueBytes - aggregateStringValueBytes)
                    {
                        throw new InvalidDataException(
                            $"Declared comparison file exceeds the aggregate {limits.MaximumAggregateStringValueBytes:N0}-byte string-value limit.");
                    }
                    aggregateStringValueBytes += tokenBytes;
                }
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                if (propertyScopeDepth == propertyScopes.Count)
                    propertyScopes.Add(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                else
                    propertyScopes[propertyScopeDepth].Clear();
                propertyScopeDepth++;
                if (reader.CurrentDepth == 1)
                {
                    rowStart = reader.TokenStartIndex;
                    rowTokens = 1;
                }
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (propertyScopeDepth == 0
                    || !propertyScopes[propertyScopeDepth - 1].Add(reader.GetString() ?? string.Empty))
                {
                    throw new InvalidDataException(
                        "Declared comparison file contains duplicate case-insensitive property names.");
                }
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                propertyScopeDepth--;
                if (reader.CurrentDepth == 1) rowStart = -1;
            }

            if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 0)
            {
                closed = true;
                break;
            }

            if (reader.CurrentDepth != 1) continue;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                rowObserver?.Invoke(1);
                if (++rows > limits.MaximumRows)
                {
                    throw new InvalidDataException(
                        $"Declared comparison file contains more than {limits.MaximumRows:N0} rows.");
                }
                continue;
            }
            if (reader.TokenType != JsonTokenType.EndObject)
                throw new InvalidDataException("Declared comparison rows must be JSON objects.");
        }

        if (!closed)
            throw new InvalidDataException("Declared comparison file does not contain a complete root JSON array.");
        if (reader.Read())
            throw new InvalidDataException("Declared comparison file contains data after the root JSON array.");
        return rows;
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunkSize = 64 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(bytes.Slice(offset, Math.Min(chunkSize, bytes.Length - offset)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string ResolveExactPathForAdmission(string reviewRoot, string comparisonPath)
    {
        if (PathSafety.IsNetworkPath(reviewRoot) || PathSafety.IsNetworkPath(comparisonPath))
            throw new InvalidDataException("Comparison evidence must use local paths.");
        var root = Path.GetFullPath(reviewRoot);
        return ResolveExactPath(root, Path.Combine(root, "_TranslationAudit"), comparisonPath);
    }

    private static string ResolveExactPath(string reviewRoot, string auditRoot, string comparisonPath)
    {
        if (string.IsNullOrWhiteSpace(comparisonPath))
            throw new InvalidDataException("Review decisions do not declare a comparison file.");
        string fullPath;
        try
        {
            fullPath = Path.IsPathRooted(comparisonPath)
                ? Path.GetFullPath(comparisonPath)
                : Path.GetFullPath(Path.Combine(reviewRoot, comparisonPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw new InvalidDataException("Declared comparison path is invalid.", exception);
        }
        if (!PathSafety.IsStrictlyInside(fullPath, auditRoot)
            || !Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Declared comparison file must be a JSON file inside this review's audit folder: {fullPath}");
        }
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Declared comparison file was not found.", fullPath);
        return fullPath;
    }
}
