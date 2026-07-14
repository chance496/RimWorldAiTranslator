using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Storage;

public sealed record JsonRecoveryNotice(string StorePath, string? PreservedCorruptPath);

public sealed class AtomicJsonStore
{
    public const long DefaultMaximumBytes = 128L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private readonly ConcurrentDictionary<string, byte> blockedWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions options;
    private readonly long maximumBytes;

    internal Action<string>? AfterBackupValidatedTestHook { get; set; }
    internal Action<string>? AfterSnapshotHandlePinnedTestHook { get; set; }

    public AtomicJsonStore(
        JsonSerializerOptions? options = null,
        long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, int.MaxValue);
        this.options = options ?? new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        this.maximumBytes = maximumBytes;
    }

    public event EventHandler<JsonRecoveryNotice>? RecoveredFromBackup;

    public bool Exists(string path)
    {
        var fullPath = Normalize(path);
        return File.Exists(fullPath)
               || Directory.Exists(fullPath)
               || File.Exists(fullPath + ".bak")
               || Directory.Exists(fullPath + ".bak");
    }

    public T? Read<T>(
        string path,
        bool allowMissing = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Normalize(path);
        var found = false;
        var errors = new List<string>();
        SnapshotLeafFingerprint? observedPrimary = null;

        foreach (var candidate in new[] { fullPath, fullPath + ".bak" })
        {
            var isPrimary = candidate.Equals(fullPath, StringComparison.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate))
            {
                if (isPrimary)
                {
                    observedPrimary = CaptureNonFilePrimaryState(candidate, cancellationToken);
                    if (observedPrimary.Kind == SnapshotLeafKind.File
                        || observedPrimary.Kind == SnapshotLeafKind.Unstable)
                    {
                        found = true;
                        errors.Add($"{candidate} : JSON store primary changed while it was being observed.");
                        continue;
                    }
                    if (observedPrimary.Kind == SnapshotLeafKind.Directory)
                    {
                        found = true;
                        errors.Add($"{candidate} : JSON store path is occupied by a directory.");
                        continue;
                    }
                }
                if (Directory.Exists(candidate))
                {
                    found = true;
                    errors.Add($"{candidate} : JSON store path is occupied by a directory.");
                }
                continue;
            }

            found = true;
            SnapshotLeafFingerprint? observedCandidate = null;
            try
            {
                var validated = ReadValidatedSnapshot<T>(
                    candidate,
                    fingerprint => observedCandidate = fingerprint,
                    cancellationToken);

                if (!isPrimary)
                {
                    AfterBackupValidatedTestHook?.Invoke(candidate);
                    var preserved = RestoreFromBackup<T>(
                        fullPath,
                        candidate,
                        validated.Bytes,
                        validated.Fingerprint,
                        observedPrimary ?? new SnapshotLeafFingerprint(
                            SnapshotLeafKind.Unstable,
                            Failure: "Primary observation was unavailable."),
                        cancellationToken);
                    RecoveredFromBackup?.Invoke(this, new JsonRecoveryNotice(fullPath, preserved));
                }

                Unblock(fullPath);
                return validated.Value;
            }
            catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or NotSupportedException
                                           or DecoderFallbackException
                                           or InvalidDataException)
            {
                if (isPrimary)
                {
                    observedPrimary = observedCandidate ?? new SnapshotLeafFingerprint(
                        SnapshotLeafKind.Unstable,
                        Failure: ex.GetType().Name);
                }
                errors.Add($"{candidate} : {ex.Message}");
            }
        }

        if (!found && allowMissing)
        {
            return default;
        }

        if (!found)
        {
            throw new FileNotFoundException($"JSON file was not found: {fullPath}", fullPath);
        }

        Block(fullPath);
        throw new InvalidDataException($"JSON file and its backup could not be read. {string.Join(" | ", errors)}");
    }

    internal T? Read<T>(
        string path,
        PathSafety.TrustedWriteBoundary writeBoundary,
        bool allowMissing = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeBoundary);
        var fullPath = Normalize(path);
        var found = false;
        var errors = new List<string>();

        foreach (var candidate in new[] { fullPath, fullPath + ".bak" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            writeBoundary.VerifyUnchanged();
            if (!File.Exists(candidate))
            {
                if (Directory.Exists(candidate))
                {
                    found = true;
                    errors.Add($"{candidate} : JSON store path is occupied by a directory.");
                }
                continue;
            }

            found = true;
            try
            {
                var validated = ReadValidatedSnapshot<T>(
                    candidate,
                    cancellationToken: cancellationToken);
                if (!candidate.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    AfterBackupValidatedTestHook?.Invoke(candidate);
                    var preserved = RestoreFromBackup<T>(
                        fullPath,
                        candidate,
                        validated.Bytes,
                        validated.Fingerprint,
                        writeBoundary,
                        cancellationToken);
                    RecoveredFromBackup?.Invoke(this, new JsonRecoveryNotice(fullPath, preserved));
                }

                Unblock(fullPath);
                return validated.Value;
            }
            catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or JsonException
                                                   or NotSupportedException
                                                   or DecoderFallbackException
                                                   or InvalidDataException)
            {
                errors.Add($"{candidate} : {exception.Message}");
            }
        }

        if (!found && allowMissing) return default;
        if (!found)
            throw new FileNotFoundException($"JSON file was not found: {fullPath}", fullPath);

        Block(fullPath);
        throw new InvalidDataException($"JSON file and its backup could not be read. {string.Join(" | ", errors)}");
    }

    public void Write<T>(string path, T value)
    {
        var fullPath = Normalize(path);
        if (blockedWrites.ContainsKey(fullPath))
        {
            throw new InvalidOperationException($"Refusing to overwrite a JSON store that could not be read: {fullPath}");
        }

        if (Path.GetDirectoryName(fullPath) is null)
            throw new InvalidOperationException($"JSON path has no parent directory: {fullPath}");
        if (File.Exists(fullPath) && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException($"Refusing to replace a read-only JSON store: {fullPath}");

        var json = JsonSerializer.Serialize(value, options);
        var bytes = Utf8NoBom.GetBytes(json);
        if (bytes.LongLength > maximumBytes)
            throw new InvalidDataException($"JSON store exceeds the {maximumBytes:N0}-byte limit.");
        using var writeBoundary = AcquirePublicWriteBoundary(fullPath);
        AtomicFile.WriteBytesValidated(
            fullPath,
            bytes,
            temporaryPath => _ = ReadValidated(
                temporaryPath,
                value is null ? typeof(T) : value.GetType()),
            writeBoundary,
            keepBackup: true);
    }

    public void Block(string path) => blockedWrites[Normalize(path)] = 0;

    public void Unblock(string path) => blockedWrites.TryRemove(Normalize(path), out _);

    public bool IsBlocked(string path) => blockedWrites.ContainsKey(Normalize(path));

    private string? RestoreFromBackup<T>(
        string path,
        string backupPath,
        byte[] validatedBackupBytes,
        SnapshotLeafFingerprint validatedBackupFingerprint,
        SnapshotLeafFingerprint observedPrimaryFingerprint,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"JSON path has no parent directory: {path}");
        using var creationBoundary =
            PathSafety.AcquireTrustedDirectoryCreationBoundary(directory, cancellationToken);
        var corruptPath = CreateCorruptPreservationPath(path);
        using var writeBoundary = PathSafety.AcquireTrustedWriteBoundary(
            directory,
            [path, corruptPath],
            [backupPath],
            cancellationToken);
        EnsureValidatedBackupUnchanged(
            backupPath,
            validatedBackupFingerprint,
            writeBoundary,
            cancellationToken);
        EnsureObservedPrimaryUnchanged(
            path,
            backupPath,
            observedPrimaryFingerprint,
            writeBoundary,
            cancellationToken);
        return RestoreValidatedBackup<T>(
            path,
            validatedBackupBytes,
            corruptPath,
            writeBoundary,
            cancellationToken);
    }

    private string? RestoreFromBackup<T>(
        string path,
        string backupPath,
        byte[] validatedBackupBytes,
        SnapshotLeafFingerprint validatedBackupFingerprint,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken)
    {
        EnsureValidatedBackupUnchanged(
            backupPath,
            validatedBackupFingerprint,
            writeBoundary,
            cancellationToken);
        return RestoreValidatedBackup<T>(
            path,
            validatedBackupBytes,
            CreateCorruptPreservationPath(path),
            writeBoundary,
            cancellationToken);
    }

    private string? RestoreValidatedBackup<T>(
        string path,
        byte[] validatedBackupBytes,
        string corruptPath,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? preservedCorruptPath = null;
        if (writeBoundary.TargetExisted(path))
        {
            writeBoundary.CopyTargetToNew(path, corruptPath, cancellationToken);
            preservedCorruptPath = corruptPath;
        }

        cancellationToken.ThrowIfCancellationRequested();
        AtomicFile.WriteBytesValidated(
            path,
            validatedBackupBytes,
            temporaryPath => _ = ReadValidated<T>(temporaryPath, cancellationToken),
            writeBoundary,
            keepBackup: false);
        return preservedCorruptPath;
    }

    internal void Write<T>(
        string path,
        T value,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeBoundary);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Normalize(path);
        if (blockedWrites.ContainsKey(fullPath))
        {
            throw new InvalidOperationException($"Refusing to overwrite a JSON store that could not be read: {fullPath}");
        }

        if (File.Exists(fullPath) && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException($"Refusing to replace a read-only JSON store: {fullPath}");

        var json = JsonSerializer.Serialize(value, options);
        var bytes = Utf8NoBom.GetBytes(json);
        if (bytes.LongLength > maximumBytes)
            throw new InvalidDataException($"JSON store exceeds the {maximumBytes:N0}-byte limit.");
        cancellationToken.ThrowIfCancellationRequested();
        AtomicFile.WriteBytesValidated(
            fullPath,
            bytes,
            temporaryPath => _ = ReadValidated(
                temporaryPath,
                value is null ? typeof(T) : value.GetType()),
            writeBoundary,
            keepBackup: true);
    }

    private T ReadValidated<T>(string path, CancellationToken cancellationToken = default) =>
        (T)ReadValidated(path, typeof(T), cancellationToken);

    private ValidatedJson<T> ReadValidatedSnapshot<T>(
        string path,
        Action<SnapshotLeafFingerprint>? snapshotObserver = null,
        CancellationToken cancellationToken = default)
    {
        using var pinnedHandle = PathSafety.WindowsPathHandle.OpenFileWithoutWriteOrDeleteSharing(path);
        var identity = PathSafety.WindowsPathHandle.GetIdentity(pinnedHandle);
        var canonical = Normalize(PathSafety.WindowsPathHandle.GetFinalPath(pinnedHandle));
        if (!canonical.Equals(Normalize(path), StringComparison.OrdinalIgnoreCase)
            || identity.FileIndex == 0
            || identity.NumberOfLinks != 1
            || (identity.FileAttributes
                & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("JSON store snapshot is not a unique regular file.");
        }
        AfterSnapshotHandlePinnedTestHook?.Invoke(path);
        using var pinnedStream = new FileStream(
            pinnedHandle,
            FileAccess.Read,
            64 * 1024,
            isAsync: false);
        var bytes = BoundedFileReader.ReadAllBytes(
            pinnedStream,
            maximumBytes,
            "JSON store",
            cancellationToken: cancellationToken);
        var fingerprint = new SnapshotLeafFingerprint(
            SnapshotLeafKind.File,
            bytes.LongLength,
            SHA256.HashData(bytes),
            VolumeSerialNumber: identity.VolumeSerialNumber,
            FileIndex: identity.FileIndex);
        snapshotObserver?.Invoke(fingerprint);
        var value = (T)ReadValidated(bytes, typeof(T), cancellationToken);
        return new ValidatedJson<T>(value, bytes, fingerprint);
    }

    private static SnapshotLeafFingerprint CaptureNonFilePrimaryState(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return FileSnapshotJournal.CaptureRecoveryFingerprint(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                                or UnauthorizedAccessException
                                                or InvalidDataException
                                                or NotSupportedException)
        {
            return new SnapshotLeafFingerprint(
                SnapshotLeafKind.Unstable,
                Failure: exception.GetType().Name);
        }
    }

    private static void EnsureObservedPrimaryUnchanged(
        string path,
        string backupPath,
        SnapshotLeafFingerprint observedPrimary,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken)
    {
        writeBoundary.VerifyUnchanged();
        var current = FileSnapshotJournal.CaptureRecoveryFingerprint(path, cancellationToken);
        if (!SameRecoveryFingerprint(current, observedPrimary))
        {
            throw new ConcurrentLeafChangeException(
                "The JSON store primary changed before backup recovery; the current primary and backup were preserved.",
                path,
                backupPath);
        }
        writeBoundary.VerifyUnchanged();
    }

    private static void EnsureValidatedBackupUnchanged(
        string backupPath,
        SnapshotLeafFingerprint validatedBackup,
        PathSafety.TrustedWriteBoundary writeBoundary,
        CancellationToken cancellationToken)
    {
        writeBoundary.VerifyUnchanged();
        var current = FileSnapshotJournal.CaptureRecoveryFingerprint(
            backupPath,
            cancellationToken);
        if (!SameRecoveryFingerprint(current, validatedBackup))
        {
            throw new ConcurrentLeafChangeException(
                "The validated JSON backup was substituted before recovery; the primary and current backup were preserved.",
                backupPath);
        }
        writeBoundary.VerifyUnchanged();
    }

    private static bool SameRecoveryFingerprint(
        SnapshotLeafFingerprint current,
        SnapshotLeafFingerprint expected)
    {
        if (current.Kind != expected.Kind) return false;
        return current.Kind switch
        {
            SnapshotLeafKind.Missing => true,
            SnapshotLeafKind.Directory =>
                current.VolumeSerialNumber.HasValue
                && current.FileIndex.HasValue
                && current.VolumeSerialNumber == expected.VolumeSerialNumber
                && current.FileIndex == expected.FileIndex,
            SnapshotLeafKind.File =>
                current.Length == expected.Length
                && current.VolumeSerialNumber.HasValue
                && current.FileIndex.HasValue
                && current.VolumeSerialNumber == expected.VolumeSerialNumber
                && current.FileIndex == expected.FileIndex
                && current.Sha256 is { Length: 32 }
                && expected.Sha256 is { Length: 32 }
                && CryptographicOperations.FixedTimeEquals(current.Sha256, expected.Sha256),
            _ => false
        };
    }

    private object ReadValidated(
        string path,
        Type valueType,
        CancellationToken cancellationToken = default)
    {
        var bytes = BoundedFileReader.ReadAllBytes(
            path,
            maximumBytes,
            "JSON store",
            cancellationToken: cancellationToken);
        return ReadValidated(bytes, valueType, cancellationToken);
    }

    private object ReadValidated(
        byte[] bytes,
        Type valueType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var raw = Utf8NoBom.GetString(bytes, offset, bytes.Length - offset);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new JsonException("JSON file is empty.");
        }

        using var document = JsonDocument.Parse(raw);
        cancellationToken.ThrowIfCancellationRequested();
        var duplicateError = StoredJsonDocumentValidator.ValidateNoDuplicateProperties(document.RootElement);
        if (duplicateError is not null)
        {
            throw new InvalidDataException(duplicateError);
        }

        var value = JsonSerializer.Deserialize(raw, valueType, options);
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null)
        {
            throw new JsonException("JSON root value is null.");
        }

        if (StoredJsonDocumentValidator.RequiresValidation(value))
        {
            var validationError = StoredJsonDocumentValidator.Validate(value, document.RootElement);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                throw new InvalidDataException(validationError);
            }
        }

        return value;
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static PathSafety.TrustedWriteBoundary AcquirePublicWriteBoundary(string path)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException($"JSON path has no parent directory: {path}");
        using var creationBoundary =
            PathSafety.AcquireTrustedDirectoryCreationBoundary(directory);
        PathSafety.EnsureNoReparsePointsToVolumeRoot(directory);
        return PathSafety.AcquireTrustedWriteBoundary(directory, [path]);
    }

    private static string CreateCorruptPreservationPath(string path) =>
        $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";

    private readonly record struct ValidatedJson<T>(
        T Value,
        byte[] Bytes,
        SnapshotLeafFingerprint Fingerprint);
}
