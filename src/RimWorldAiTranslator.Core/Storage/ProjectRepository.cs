using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Validation;

namespace RimWorldAiTranslator.Core.Storage;

public sealed class ProjectRepository
{
    private const string WriteLockSuffix = ".write.lock";
    private readonly AtomicJsonStore store;
    private readonly AppDataPaths paths;

    public ProjectRepository(AtomicJsonStore store, AppDataPaths paths)
    {
        this.store = store;
        this.paths = paths;
    }

    public string ReviewsRoot => paths.Reviews;

    public ProjectStoreDocument Load(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var writeLease = AcquireWriteLease();
        using var writeBoundary = AcquireProjectBoundary(cancellationToken);
        var document = ReadCurrentDocument(writeBoundary, cancellationToken, out _);
        if (document is null)
        {
            return new ProjectStoreDocument { ObservedMissing = true };
        }
        ValidateLoadedDocument(document);

        store.Unblock(paths.Projects);
        document.ObservedContentSha256 = ComputeContentToken(document);
        document.ObservedMissing = false;
        return document;
    }

    public void Save(ProjectStoreDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Revision < 0)
            throw new InvalidDataException("Project store revision cannot be negative.");
        if (document.Projects is null)
            throw new InvalidDataException("Project store is missing the projects collection.");
        if (store.IsBlocked(paths.Projects))
        {
            throw new InvalidOperationException(
                "Refusing to overwrite a project store that could not be read.");
        }

        using var writeLease = AcquireWriteLease();
        using var writeBoundary = AcquireProjectBoundary(CancellationToken.None);
        var current = ReadCurrentDocument(
            writeBoundary,
            CancellationToken.None,
            out var recoveredDuringSave);
        if (recoveredDuringSave) ThrowConcurrentChange();
        if (current is null)
        {
            if (document.ObservedContentSha256 is not null)
                ThrowConcurrentChange();
        }
        else if (document.ObservedContentSha256 is null
                 || !document.ObservedContentSha256.Equals(
                     ComputeContentToken(current),
                     StringComparison.Ordinal)
                 || current.Revision != document.Revision)
        {
            ThrowConcurrentChange();
        }
        var currentRevision = current?.Revision ?? 0;
        if (currentRevision == long.MaxValue)
            throw new InvalidDataException("Project store revision is exhausted.");

        var previousVersion = document.Version;
        var previousUpdatedAt = document.UpdatedAt;
        var previousRevision = document.Revision;
        var previousObservedHash = document.ObservedContentSha256;
        var previousObservedMissing = document.ObservedMissing;
        document.Version = ProjectStoreDocument.CurrentVersion;
        document.UpdatedAt = DateTimeOffset.Now.ToString("O");
        document.Revision = currentRevision + 1;
        try
        {
            store.Write(
                paths.Projects,
                document,
                writeBoundary,
                CancellationToken.None);
            document.ObservedContentSha256 = ComputeContentToken(document);
            document.ObservedMissing = false;
        }
        catch
        {
            document.Version = previousVersion;
            document.UpdatedAt = previousUpdatedAt;
            document.Revision = previousRevision;
            document.ObservedContentSha256 = previousObservedHash;
            document.ObservedMissing = previousObservedMissing;
            throw;
        }
    }

    private ProjectStoreDocument? ReadCurrentDocument(
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken,
        out bool recovered)
    {
        var recoveredLocal = false;
        void OnRecovered(object? _, JsonRecoveryNotice notice)
        {
            if (notice.StorePath.Equals(paths.Projects, StringComparison.OrdinalIgnoreCase))
                recoveredLocal = true;
        }

        store.RecoveredFromBackup += OnRecovered;
        ProjectStoreDocument? current;
        try
        {
            current = store.Read<ProjectStoreDocument>(
                paths.Projects,
                writeBoundary,
                allowMissing: true,
                cancellationToken: cancellationToken);
        }
        finally
        {
            store.RecoveredFromBackup -= OnRecovered;
            recovered = recoveredLocal;
        }
        if (current is null) return null;
        ValidateLoadedDocument(current);
        return current;
    }

    private PathSafety.TrustedWriteBoundary AcquireProjectBoundary(
        CancellationToken cancellationToken) =>
        PathSafety.AcquireTrustedWriteBoundary(
            paths.Root,
            [paths.Projects],
            cancellationToken);

    private void ValidateLoadedDocument(ProjectStoreDocument document)
    {
        if (document.Version is not (1 or 2 or ProjectStoreDocument.CurrentVersion))
        {
            store.Block(paths.Projects);
            throw new InvalidDataException($"Unsupported project store version: {document.Version}");
        }
        if (document.Projects is null)
        {
            store.Block(paths.Projects);
            throw new InvalidDataException("Project store is missing the projects collection.");
        }
        if (document.Revision < 0)
        {
            store.Block(paths.Projects);
            throw new InvalidDataException("Project store revision cannot be negative.");
        }
    }

    private static string ComputeContentToken(ProjectStoreDocument document) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document)));

    private static void ThrowConcurrentChange() =>
        throw new InvalidOperationException(
            "The project store changed after it was loaded. Reload it before saving again.");

    private ProjectWriteLease AcquireWriteLease()
    {
        using var creationBoundary =
            PathSafety.AcquireTrustedDirectoryCreationBoundary(paths.Root);
        PathSafety.EnsureNoReparsePointsToVolumeRoot(paths.Root);
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(paths.Root);
        if (!canonicalRoot.Equals(paths.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The project data root must use its canonical physical path.");
        var lockPath = Path.Combine(canonicalRoot, Path.GetFileName(paths.Projects) + WriteLockSuffix);
        PathSafety.EnsureNoReparsePoints(lockPath, canonicalRoot);
        var rootLease = PathSafety.WindowsPathHandle.OpenDirectoryWithoutDeleteSharing(canonicalRoot);
        try
        {
            var lockedRoot = PathSafety.Normalize(PathSafety.WindowsPathHandle.GetFinalPath(rootLease));
            if (!lockedRoot.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The project data root changed while it was being locked.");
            var lease = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            try
            {
                var identity = PathSafety.WindowsPathHandle.GetIdentity(lease.SafeFileHandle);
                var finalPath = PathSafety.Normalize(
                    PathSafety.WindowsPathHandle.GetFinalPath(lease.SafeFileHandle));
                if (!finalPath.Equals(lockPath, StringComparison.OrdinalIgnoreCase)
                    || identity.NumberOfLinks != 1
                    || (identity.FileAttributes
                        & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException("The project write lock must be a regular file.");
                }
                return new ProjectWriteLease(lease, rootLease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            rootLease.Dispose();
            throw new InvalidOperationException(
                "The project store is being changed by another process. Try again after it finishes.",
                exception);
        }
        catch
        {
            rootLease.Dispose();
            throw;
        }
    }

    private sealed class ProjectWriteLease(FileStream fileLease, SafeFileHandle rootLease) : IDisposable
    {
        private FileStream? fileLease = fileLease;
        private SafeFileHandle? rootLease = rootLease;

        public void Dispose()
        {
            Interlocked.Exchange(ref fileLease, null)?.Dispose();
            Interlocked.Exchange(ref rootLease, null)?.Dispose();
        }
    }
}

public sealed class SettingsRepository : IDisposable
{
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly System.Text.Json.JsonSerializerOptions InspectionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly AtomicJsonStore store;
    private readonly AppDataPaths paths;
    private readonly object saveSync = new();
    private Task saveTail = Task.CompletedTask;
    private Task? deferredDrainObserver;
    private bool disposed;

    public SettingsRepository(AtomicJsonStore store, AppDataPaths paths)
    {
        this.store = store;
        this.paths = paths;
    }

    public bool UnsafeExtensionDataExcludedOnLastLoad { get; private set; }

    public bool StoredCredentialCorrectionRequired()
    {
        foreach (var candidate in new[] { paths.Settings, paths.Settings + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var bytes = BoundedFileReader.ReadAllBytes(
                    candidate,
                    AtomicJsonStore.DefaultMaximumBytes,
                    "settings credential inspection");
                using var document = System.Text.Json.JsonDocument.Parse(bytes);
                if (StoredJsonDocumentValidator.ValidateNoDuplicateProperties(document.RootElement) is not null)
                    return true;
                if (ContainsCredentialLikeContent(document.RootElement)) return true;
                var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettingsDocument>(
                    bytes,
                    InspectionJsonOptions);
                if (settings is null || ContainsCredentialMaterial(settings)) return true;
            }
            catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or System.Text.Json.JsonException
                                                   or NotSupportedException
                                                   or InvalidDataException
                                                   or System.Security.SecurityException)
            {
                // An unreadable settings copy is not proven clean. Keep the correction
                // warning active without exposing its path or contents.
                return true;
            }
        }
        return false;
    }

    public AppSettingsDocument Load(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = store.Exists(paths.Settings)
            ? store.Read<AppSettingsDocument>(paths.Settings, cancellationToken: cancellationToken)
              ?? new AppSettingsDocument()
            : new AppSettingsDocument();
        var excluded = false;
        settings.ExtensionData = SanitizeLoadedExtensionData(settings.ExtensionData, ref excluded);
        foreach (var provider in settings.ApiProviders.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProviderValidator.IsCredentialLikeProviderText(provider.Name))
            {
                provider.Name = string.Empty;
                excluded = true;
            }
            if (ProviderValidator.IsCredentialLikeProviderText(provider.Model))
            {
                provider.Model = string.Empty;
                excluded = true;
            }
            provider.ExtensionData = SanitizeLoadedExtensionData(provider.ExtensionData, ref excluded);
        }
        UnsafeExtensionDataExcludedOnLastLoad = excluded;
        return settings;
    }

    public void Save(AppSettingsDocument settings) =>
        SaveAsync(settings).GetAwaiter().GetResult();

    public Task SaveAsync(AppSettingsDocument settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = CreateSnapshot(settings);
        lock (saveSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var operation = SaveAfterAsync(saveTail, snapshot, cancellationToken);
            saveTail = operation;
            return operation;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task acceptedSaves;
        lock (saveSync)
        {
            acceptedSaves = saveTail;
        }
        await acceptedSaves.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Task acceptedSaves;
        lock (saveSync)
        {
            if (disposed) return;
            disposed = true;
            acceptedSaves = saveTail;
        }

        var completed = Task.WhenAny(acceptedSaves, Task.Delay(DisposeDrainTimeout))
            .GetAwaiter()
            .GetResult();
        if (!ReferenceEquals(completed, acceptedSaves))
        {
            deferredDrainObserver = ObserveDeferredDrainAsync(acceptedSaves);
            System.Diagnostics.Debug.WriteLine("Settings drain is continuing after the bounded dispose wait.");
        }
        else if (acceptedSaves.Exception is { } exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Settings drain completed with an error ({exception.GetBaseException().GetType().Name}).");
        }
    }

    private static async Task ObserveDeferredDrainAsync(Task acceptedSaves)
    {
        await acceptedSaves.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (acceptedSaves.IsFaulted && acceptedSaves.Exception is { } exception)
            System.Diagnostics.Debug.WriteLine(
                $"Deferred settings drain failed ({exception.GetBaseException().GetType().Name}).");
    }

    private async Task SaveAfterAsync(
        Task predecessor,
        AppSettingsDocument snapshot,
        CancellationToken cancellationToken)
    {
        await predecessor.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                store.Write(paths.Settings, snapshot);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static AppSettingsDocument CreateSnapshot(AppSettingsDocument settings)
    {
        if (settings.ApiProviders is null)
            throw new InvalidDataException("Settings are missing the provider collection.");
        EnsureSafeExtensionData(settings.ExtensionData);
        var providers = new Dictionary<string, ApiProviderSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.ApiProviders)
        {
            if (pair.Value is null)
                throw new InvalidDataException("Settings contain an invalid provider entry.");
            EnsureSafeExtensionData(pair.Value.ExtensionData);
            if (ProviderValidator.IsCredentialLikeProviderText(pair.Value.Name)
                || ProviderValidator.IsCredentialLikeProviderText(pair.Value.Model))
            {
                throw new InvalidDataException(
                    "Provider display or model text contains credential-like material and cannot be saved.");
            }
            ProviderValidator.EnsureValidEndpoint(
                pair.Value.Url,
                allowLoopbackHttp: false,
                allowEmpty: true);
            providers[pair.Key] = new ApiProviderSettings
            {
                Name = pair.Value.Name,
                Url = pair.Value.Url,
                Model = pair.Value.Model,
                Temperature = pair.Value.Temperature,
                ExtensionData = CloneExtensionData(pair.Value.ExtensionData)
            };
        }

        return new AppSettingsDocument
        {
            Version = AppSettingsDocument.CurrentVersion,
            ThemeMode = settings.ThemeMode,
            DesignPreset = settings.DesignPreset,
            TextSize = settings.TextSize,
            HighContrast = settings.HighContrast,
            AutoSave = settings.AutoSave,
            RmkWorkspaceRoot = settings.RmkWorkspaceRoot,
            RmkUseExisting = settings.RmkUseExisting,
            CustomGlossaryPath = settings.CustomGlossaryPath,
            ApiProviderId = settings.ApiProviderId,
            ApiProviders = providers,
            ExtensionData = CloneExtensionData(settings.ExtensionData)
        };
    }

    private static Dictionary<string, System.Text.Json.JsonElement>? CloneExtensionData(
        Dictionary<string, System.Text.Json.JsonElement>? extensionData) =>
        extensionData?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);

    private static Dictionary<string, System.Text.Json.JsonElement>? SanitizeLoadedExtensionData(
        Dictionary<string, System.Text.Json.JsonElement>? extensionData,
        ref bool excluded)
    {
        if (extensionData is null) return null;
        var sanitized = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        foreach (var pair in extensionData)
        {
            if (IsCredentialProperty(pair.Key, pair.Value)
                || pair.Value.ValueKind == System.Text.Json.JsonValueKind.String
                && ProviderValidator.IsCredentialLikeValue(pair.Value.GetString()))
            {
                excluded = true;
                continue;
            }
            if (!ContainsCredentialLikeContent(pair.Value))
            {
                sanitized[pair.Key] = pair.Value.Clone();
                continue;
            }

            using var buffer = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                if (!TryWriteSanitizedExtensionValue(pair.Value, writer, ref excluded))
                {
                    excluded = true;
                    continue;
                }
            }
            using var document = System.Text.Json.JsonDocument.Parse(buffer.ToArray());
            sanitized[pair.Key] = document.RootElement.Clone();
        }
        return sanitized;
    }

    private static bool TryWriteSanitizedExtensionValue(
        System.Text.Json.JsonElement value,
        System.Text.Json.Utf8JsonWriter writer,
        ref bool excluded)
    {
        switch (value.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (IsCredentialProperty(property.Name, property.Value)
                        || property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        && ProviderValidator.IsCredentialLikeValue(property.Value.GetString()))
                    {
                        excluded = true;
                        continue;
                    }
                    writer.WritePropertyName(property.Name);
                    _ = TryWriteSanitizedExtensionValue(property.Value, writer, ref excluded);
                }
                writer.WriteEndObject();
                return true;

            case System.Text.Json.JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String
                        && ProviderValidator.IsCredentialLikeValue(item.GetString()))
                    {
                        excluded = true;
                        continue;
                    }
                    _ = TryWriteSanitizedExtensionValue(item, writer, ref excluded);
                }
                writer.WriteEndArray();
                return true;

            case System.Text.Json.JsonValueKind.String:
                if (ProviderValidator.IsCredentialLikeValue(value.GetString()))
                {
                    excluded = true;
                    return false;
                }
                writer.WriteStringValue(value.GetString());
                return true;

            default:
                value.WriteTo(writer);
                return true;
        }
    }

    private static void EnsureSafeExtensionData(
        Dictionary<string, System.Text.Json.JsonElement>? extensionData)
    {
        if (extensionData is null) return;
        foreach (var pair in extensionData)
        {
            if (IsCredentialProperty(pair.Key, pair.Value)
                || ContainsCredentialLikeContent(pair.Value))
            {
                throw new InvalidDataException(
                    "Settings extension data contains credential-like content and cannot be saved.");
            }
        }
    }

    private static bool ContainsCredentialLikeContent(System.Text.Json.JsonElement root)
    {
        var pending = new Stack<System.Text.Json.JsonElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            switch (current.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject())
                    {
                        if (IsCredentialProperty(property.Name, property.Value)) return true;
                        pending.Push(property.Value);
                    }
                    break;

                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray()) pending.Push(item);
                    break;

                case System.Text.Json.JsonValueKind.String:
                    if (ProviderValidator.IsCredentialLikeValue(current.GetString())) return true;
                    break;
            }
        }
        return false;
    }

    private static bool ContainsCredentialMaterial(AppSettingsDocument settings)
    {
        if (ContainsCredentialLikeContent(settings.ExtensionData)) return true;
        if (settings.ApiProviders is null) return true;
        foreach (var provider in settings.ApiProviders.Values)
        {
            if (provider is null) return true;
            if (ProviderValidator.IsCredentialLikeProviderText(provider.Name)
                || ProviderValidator.IsCredentialLikeProviderText(provider.Model))
            {
                return true;
            }
            if (ContainsCredentialLikeContent(provider.ExtensionData)) return true;
            var endpointError = ProviderValidator.GetEndpointErrorCode(
                provider.Url,
                allowLoopbackHttp: false,
                allowEmpty: true);
            if (endpointError is "UrlContainsCredential" or "UrlQueryNotAllowed" or "UrlFragmentNotAllowed")
                return true;
        }
        return false;
    }

    private static bool ContainsCredentialLikeContent(
        Dictionary<string, System.Text.Json.JsonElement>? extensionData)
    {
        if (extensionData is null) return false;
        foreach (var pair in extensionData)
        {
            if (IsCredentialProperty(pair.Key, pair.Value)
                || ContainsCredentialLikeContent(pair.Value))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsCredentialProperty(
        string name,
        System.Text.Json.JsonElement value)
    {
        if (ProviderValidator.IsGenericKeyName(name))
        {
            return ProviderValidator.IsUnsafeGenericKeyProperty(
                name,
                value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null);
        }

        if (ProviderValidator.IsCredentialName(name)
            || ProviderValidator.IsCredentialLikeValue(name))
        {
            return true;
        }

        // Unknown boolean/null metadata can describe a credential requirement
        // without containing credential material (for example, requiresPassword).
        // Numeric, string and structured values remain fail-closed.
        if (value.ValueKind is System.Text.Json.JsonValueKind.True
            or System.Text.Json.JsonValueKind.False
            or System.Text.Json.JsonValueKind.Null)
        {
            return false;
        }

        return ProviderValidator.IsCredentialPropertyName(name);
    }

}

public sealed class ReviewRepository
{
    private readonly AtomicJsonStore store;

    public ReviewRepository(AtomicJsonStore store)
    {
        this.store = store;
    }

    public ReviewDecisionDocument Load(
        string reviewRoot,
        string? trustedReviewsRoot = null,
        CancellationToken cancellationToken = default)
    {
        _ = TryLoad(
            reviewRoot,
            out var decisions,
            trustedReviewsRoot,
            cancellationToken);
        return decisions;
    }

    public bool TryLoad(
        string reviewRoot,
        out ReviewDecisionDocument decisions,
        string? trustedReviewsRoot = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeReviewRoot = ValidateWritableReviewRoot(reviewRoot, trustedReviewsRoot);
        var decisionPath = GetDecisionPath(safeReviewRoot);
        using var writeBoundary = PathSafety.AcquireTrustedWriteBoundary(
            safeReviewRoot,
            [decisionPath],
            cancellationToken);
        writeBoundary.VerifyUnchanged();
        var loaded = store.Read<ReviewDecisionDocument>(
            decisionPath,
            writeBoundary,
            allowMissing: true,
            cancellationToken);
        if (loaded is null)
        {
            decisions = new ReviewDecisionDocument { ReviewRoot = safeReviewRoot };
            return false;
        }

        decisions = loaded;
        return true;
    }

    public void Save(
        string reviewRoot,
        ReviewDecisionDocument decisions,
        ReviewComparisonEvidence trustedComparisonEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedComparisonEvidence);
        cancellationToken.ThrowIfCancellationRequested();
        var safeReviewRoot = ValidateWritableReviewRoot(reviewRoot);
        if (string.IsNullOrWhiteSpace(trustedComparisonEvidence.Path)
            || !ReviewComparisonDocument.IsSha256(trustedComparisonEvidence.Sha256))
        {
            throw new InvalidDataException("Trusted comparison evidence is incomplete.");
        }
        ReviewComparisonDocument.VerifyUnchanged(safeReviewRoot, trustedComparisonEvidence);
        ValidateActiveKeyedTargets(decisions.Items);
        ValidateActiveKeylessIdentities(decisions.Items);
        decisions.Version = ReviewDecisionDocument.CurrentVersion;
        decisions.Sparse = true;
        decisions.ReviewRoot = safeReviewRoot;
        decisions.Comparison = Path.GetFullPath(trustedComparisonEvidence.Path);
        decisions.ComparisonSha256 = trustedComparisonEvidence.Sha256.ToUpperInvariant();
        decisions.UpdatedAt = DateTimeOffset.Now.ToString("O");
        var decisionPath = GetDecisionPath(safeReviewRoot);
        using var writeBoundary = PathSafety.AcquireTrustedWriteBoundary(
            safeReviewRoot,
            [decisionPath],
            [trustedComparisonEvidence.Path],
            cancellationToken);
        writeBoundary.VerifyUnchanged();
        ReviewComparisonDocument.VerifyUnchanged(safeReviewRoot, trustedComparisonEvidence);
        cancellationToken.ThrowIfCancellationRequested();
        store.Write(decisionPath, decisions, writeBoundary, cancellationToken);
    }

    internal static string ValidateWritableReviewRoot(
        string reviewRoot,
        string? trustedReviewsRoot = null)
    {
        if (PathSafety.IsNetworkPath(reviewRoot))
            throw new InvalidDataException("Review workspaces must use a local path.");
        var root = PathSafety.Normalize(reviewRoot);
        if (PathSafety.IsWorkshopContentPath(root))
            throw new InvalidOperationException("Steam Workshop content is read-only and cannot be used as a review workspace.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Review folder was not found: {root}");
        PathSafety.EnsureNoReparsePointsToVolumeRoot(root);
        var canonicalRoot = PathSafety.GetCanonicalExistingDirectory(root);
        if (PathSafety.IsNetworkPath(canonicalRoot))
            throw new InvalidDataException("Review workspaces must use a local path.");
        if (PathSafety.IsWorkshopContentPath(canonicalRoot))
            throw new InvalidOperationException("Steam Workshop content is read-only and cannot be used through a filesystem alias.");

        if (!string.IsNullOrWhiteSpace(trustedReviewsRoot))
        {
            var trustedRoot = PathSafety.Normalize(trustedReviewsRoot);
            if (!Directory.Exists(trustedRoot))
                throw new DirectoryNotFoundException($"Trusted review root was not found: {trustedRoot}");
            PathSafety.EnsureNoReparsePointsToVolumeRoot(trustedRoot);
            trustedRoot = PathSafety.GetCanonicalExistingDirectory(trustedRoot);
            if (PathSafety.IsNetworkPath(trustedRoot) || PathSafety.IsWorkshopContentPath(trustedRoot))
                throw new InvalidDataException("The trusted review root is not a writable local application root.");
            if (!PathSafety.IsStrictlyInside(canonicalRoot, trustedRoot))
                throw new InvalidDataException("Review workspaces must remain inside the application review root.");
            PathSafety.EnsureNoReparsePoints(canonicalRoot, trustedRoot);
        }

        return canonicalRoot;
    }

    private static void ValidateActiveKeyedTargets(IEnumerable<ReviewDecision> decisions)
    {
        foreach (var decision in decisions.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
        {
            if (string.IsNullOrWhiteSpace(ReviewTargetIdentity.Canonicalize(decision.Target)))
            {
                throw new InvalidDataException(
                    "Active keyed decisions require a canonical target identity.");
            }
        }
    }

    private static void ValidateActiveKeylessIdentities(IEnumerable<ReviewDecision> decisions)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in decisions.Where(item => string.IsNullOrWhiteSpace(item.Key)))
        {
            if (string.IsNullOrWhiteSpace(decision.Target)
                || string.IsNullOrWhiteSpace(decision.DefClass)
                || string.IsNullOrWhiteSpace(decision.Node))
            {
                throw new InvalidDataException(
                    "Active keyless decisions require target, defClass, and node identity.");
            }
            var target = ReviewTargetIdentity.Canonicalize(decision.Target);
            var identity = $"{target.Length}:{target}{decision.DefClass.Length}:{decision.DefClass}{decision.Node.Length}:{decision.Node}";
            if (!identities.Add(identity))
            {
                throw new InvalidDataException(
                    "Active keyless decisions contain a duplicate target+defClass+node identity.");
            }
        }
    }

    public static string GetDecisionPath(string reviewRoot) => Path.Combine(Path.GetFullPath(reviewRoot), "review-decisions.json");
}
