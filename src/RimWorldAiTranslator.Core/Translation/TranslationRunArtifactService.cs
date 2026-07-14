using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Utilities;

namespace RimWorldAiTranslator.Core.Translation;

public sealed class TranslationRunArtifactService
{
    private readonly string tempRoot;
    private readonly string reviewsRoot;
    private readonly object ownedArtifactsSync = new();
    private readonly Dictionary<string, SnapshotLeafFingerprint> ownedArtifacts =
        new(StringComparer.OrdinalIgnoreCase);

    public TranslationRunArtifactService(string tempRoot, string reviewsRoot)
    {
        this.tempRoot = PathSafety.Normalize(tempRoot);
        this.reviewsRoot = PathSafety.Normalize(reviewsRoot);
    }

    public string CreatePreservedTranslations(ReviewWorkspace review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return WritePreservedTranslations(CapturePreservedTranslations(review));
    }

    public Task<string> CreatePreservedTranslationsAsync(
        ReviewWorkspace review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        var snapshot = CapturePreservedTranslations(review);
        return Task.Run(() => WritePreservedTranslations(snapshot), cancellationToken);
    }

    private PreservedTranslationSnapshot CapturePreservedTranslations(ReviewWorkspace review)
    {
        lock (review.SyncRoot)
        {
            var items = review.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Decision.Text))
                .Select(item => new PreservedTranslation(
                    item.Row.Key,
                    item.Row.Kind,
                    item.Row.DefClass,
                    item.Row.Kind == "Keyed" ? "Keyed" : item.Row.DefClass,
                    item.RelativeTarget,
                    item.Decision.Text,
                    item.Decision.TranslationOrigin,
                    item.Decision.TranslationUpdatedAt,
                    item.Decision.SourceChanged,
                    StableIdentity.Sha256(NormalizeSource(item.Row.Source)),
                    item.Row.Source,
                    !string.IsNullOrEmpty(item.Decision.PreviousSourceText)
                        ? item.Decision.PreviousSourceText
                        : item.Row.ExistingPreviousSourceText))
                .ToArray();
            return new PreservedTranslationSnapshot(
                review.ReviewRoot,
                items);
        }
    }

    private string WritePreservedTranslations(PreservedTranslationSnapshot snapshot)
    {
        var reviewRoot = PathSafety.Normalize(snapshot.ReviewRoot);
        if (!PathSafety.IsStrictlyInside(reviewRoot, reviewsRoot))
            throw new InvalidDataException("The review workspace is outside the application review root.");
        PathSafety.EnsureNoReparsePoints(reviewRoot, reviewsRoot);
        using var tempRootCreation =
            PathSafety.AcquireTrustedDirectoryCreationBoundary(tempRoot);
        PathSafety.EnsureNoReparsePoints(tempRoot, tempRoot);
        var path = Path.Combine(tempRoot, $"preserve-{Guid.NewGuid():N}.json");
        PathSafety.EnsureNoReparsePoints(path, tempRoot);
        var document = new PreservedTranslationDocument(
            2,
            Path.Combine(reviewRoot, "Languages", "Korean"),
            snapshot.Items);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        SnapshotLeafFingerprint? committedFingerprint = null;
        using (var writeBoundary = PathSafety.AcquireTrustedWriteBoundary(tempRoot, [path]))
        {
            AtomicFile.WriteBytesValidated(
                path,
                Encoding.UTF8.GetBytes(json),
                temporaryPath =>
                {
                    ValidatedArtifactFile.ValidateJson(
                        temporaryPath,
                        json,
                        root => ValidatePreservedTranslationDocument(root, document));
                    committedFingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(temporaryPath);
                    if (committedFingerprint.Kind != SnapshotLeafKind.File
                        || committedFingerprint.Sha256 is not { Length: 32 })
                    {
                        throw new IOException(
                            "The prepared preserved-translation artifact could not be verified.");
                    }
                },
                writeBoundary,
                keepBackup: false);
            var fingerprint = committedFingerprint
                ?? throw new IOException("The committed preserved-translation artifact has no ownership evidence.");
            lock (ownedArtifactsSync)
            {
                ownedArtifacts.Add(path, fingerprint);
            }
        }
        return path;
    }

    private static void ValidatePreservedTranslationDocument(
        JsonElement root,
        PreservedTranslationDocument expected)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var parsedVersion)
            || parsedVersion != expected.Version
            || !root.TryGetProperty("languageRoot", out var languageRoot)
            || languageRoot.ValueKind != JsonValueKind.String
            || !string.Equals(languageRoot.GetString(), expected.LanguageRoot, StringComparison.Ordinal)
            || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() != expected.Items.Count)
        {
            throw new InvalidDataException("The flushed preserved-translation artifact failed semantic validation.");
        }

        var requiredStringProperties = new[]
        {
            "key", "kind", "defClass", "namespace", "target", "text", "origin", "translationUpdatedAt",
            "sourceHash", "sourceText", "previousSourceText"
        };
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || requiredStringProperties.Any(name =>
                    !item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                || !item.TryGetProperty("sourceChanged", out var sourceChanged)
                || sourceChanged.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("The flushed preserved-translation artifact contains an invalid item.");
            }

            var sourceHash = item.GetProperty("sourceHash").GetString() ?? string.Empty;
            var sourceText = item.GetProperty("sourceText").GetString() ?? string.Empty;
            if (!IsSha256(sourceHash)
                || !sourceHash.Equals(
                    StableIdentity.Sha256(NormalizeSource(sourceText)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The flushed preserved-translation artifact contains invalid source evidence.");
            }
        }
    }

    public void DeletePreservedTranslations(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = PathSafety.Normalize(path);
        if (!PathSafety.IsStrictlyInside(fullPath, tempRoot)
            || !Path.GetFileName(fullPath).StartsWith("preserve-", StringComparison.Ordinal)
            || !Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Refusing to delete an unowned preserved-translation file.");
        }
        PathSafety.EnsureNoReparsePoints(fullPath, tempRoot);
        SnapshotLeafFingerprint fingerprint;
        lock (ownedArtifactsSync)
        {
            if (!ownedArtifacts.TryGetValue(fullPath, out var ownedFingerprint))
                throw new InvalidDataException(
                    "Refusing to delete a preserved-translation file not created by this service instance.");
            fingerprint = ownedFingerprint;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            lock (ownedArtifactsSync)
            {
                ownedArtifacts.Remove(fullPath);
            }
            return;
        }
        var current = Directory.Exists(fullPath)
            ? new SnapshotLeafFingerprint(SnapshotLeafKind.Directory)
            : FileSnapshotJournal.CaptureRecoveryFingerprint(fullPath);
        if (!FileSnapshotJournal.HasSameContent(current, fingerprint))
        {
            throw new ConcurrentLeafChangeException(
                "The preserved-translation artifact changed after creation and was not deleted.",
                fullPath);
        }
        File.Delete(fullPath);

        lock (ownedArtifactsSync)
        {
            ownedArtifacts.Remove(fullPath);
        }
    }

    public Task DeletePreservedTranslationsAsync(
        string? path,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => DeletePreservedTranslations(path), cancellationToken);

    public bool HasCompleteReview(TranslationRunResult result)
    {
        if (result.Rows.Count == 0
            || string.IsNullOrWhiteSpace(result.ReviewRoot)
            || string.IsNullOrWhiteSpace(result.ComparisonFile))
        {
            return false;
        }

        try
        {
            var reviewRoot = PathSafety.Normalize(result.ReviewRoot);
            var comparisonFile = PathSafety.Normalize(result.ComparisonFile);
            if (!PathSafety.IsStrictlyInside(reviewRoot, reviewsRoot)
                || !PathSafety.IsStrictlyInside(comparisonFile, reviewRoot))
            {
                return false;
            }
            PathSafety.EnsureNoReparsePoints(reviewRoot, reviewsRoot);
            PathSafety.EnsureNoReparsePoints(comparisonFile, reviewRoot);
            return Directory.Exists(reviewRoot)
                   && Directory.Exists(Path.Combine(reviewRoot, "Languages", "Korean"))
                   && File.Exists(comparisonFile);
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException
                                             or NotSupportedException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string NormalizeSource(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record PreservedTranslationDocument(
        int Version,
        string LanguageRoot,
        IReadOnlyList<PreservedTranslation> Items);

    private sealed record PreservedTranslationSnapshot(
        string ReviewRoot,
        IReadOnlyList<PreservedTranslation> Items);

    private sealed record PreservedTranslation(
        string Key,
        string Kind,
        string DefClass,
        string Namespace,
        string Target,
        string Text,
        string Origin,
        string TranslationUpdatedAt,
        bool SourceChanged,
        string SourceHash,
        string SourceText,
        string PreviousSourceText);
}
