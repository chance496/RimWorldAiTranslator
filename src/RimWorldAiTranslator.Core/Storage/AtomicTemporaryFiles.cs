using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace RimWorldAiTranslator.Core.Storage;

internal static class AtomicTemporaryFiles
{
    private const int MaximumCandidatesPerTarget = 256;
    private const int MaximumInventoryEntriesPerTarget = 4_096;
    private static readonly HashSet<string> AllowedKinds =
        new(StringComparer.Ordinal)
        {
            "tmp",
            "restore.tmp",
            "discard.tmp"
        };

    internal static string CreatePath(string targetPath, string kind = "tmp")
    {
        if (!AllowedKinds.Contains(kind))
            throw new ArgumentException("Unsupported atomic temporary-file kind.", nameof(kind));
        var fullTarget = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTarget)
            ?? throw new InvalidOperationException("Atomic target has no parent directory.");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullTarget)}.p{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.{Guid.NewGuid():N}.{kind}");
    }

    internal static int CleanupExitedProcessFiles(string targetPath) =>
        CleanupExitedProcessFiles(targetPath, protectedExactPaths: null);

    internal static int CleanupExitedProcessFiles(
        string targetPath,
        IReadOnlyCollection<string>? protectedExactPaths)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTarget);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
        var protectedPaths = protectedExactPaths is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : protectedExactPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefix = "." + Path.GetFileName(fullTarget) + ".p";
        var candidates = new List<(string Path, SnapshotLeafFingerprint Fingerprint)>();

        try
        {
            var inspected = 0;
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "*", SearchOption.TopDirectoryOnly))
            {
                if (++inspected > MaximumInventoryEntriesPerTarget || candidates.Count >= MaximumCandidatesPerTarget)
                {
                    Debug.WriteLine("Atomic temporary-file cleanup stopped at its bounded inventory limit.");
                    break;
                }
                if (protectedPaths.Contains(Path.GetFullPath(path))
                    || !TryGetOwnerProcessId(Path.GetFileName(path), prefix, out var ownerProcessId)
                    || !OwnerProcessHasExited(ownerProcessId))
                {
                    continue;
                }

                try
                {
                    var fingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(path);
                    if (fingerprint.Kind == SnapshotLeafKind.File && fingerprint.Sha256 is { Length: 32 })
                        candidates.Add((path, fingerprint));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Atomic temporary-file ownership capture skipped ({exception.GetType().Name}).");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Atomic temporary-file inventory skipped ({exception.GetType().Name}).");
            return 0;
        }

        var removed = 0;
        foreach (var candidate in candidates)
        {
            if (protectedPaths.Contains(Path.GetFullPath(candidate.Path))
                || !TryGetOwnerProcessId(Path.GetFileName(candidate.Path), prefix, out var ownerProcessId)
                || !OwnerProcessHasExited(ownerProcessId))
            {
                continue;
            }
            if (TryDeleteRegularFile(candidate.Path, candidate.Fingerprint.Length, candidate.Fingerprint.Sha256))
                removed++;
        }
        return removed;
    }

    internal static bool TryDeleteRegularFile(
        string path,
        long? expectedLength = null,
        byte[]? expectedSha256 = null)
    {
        try
        {
            var fingerprint = FileSnapshotJournal.CaptureRecoveryFingerprint(path);
            if (fingerprint.Kind != SnapshotLeafKind.File
                || expectedLength.HasValue && fingerprint.Length != expectedLength.Value
                || expectedSha256 is not null
                && (fingerprint.Sha256 is not { Length: 32 }
                    || expectedSha256.Length != 32
                    || !CryptographicOperations.FixedTimeEquals(fingerprint.Sha256, expectedSha256)))
            {
                return false;
            }
            File.Delete(path);
            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Atomic temporary-file cleanup skipped ({exception.GetType().Name}).");
            return false;
        }
    }

    private static bool TryGetOwnerProcessId(string fileName, string prefix, out int processId)
    {
        processId = 0;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var remainder = fileName[prefix.Length..];
        var processSeparator = remainder.IndexOf('.');
        if (processSeparator <= 0
            || !int.TryParse(
                remainder.AsSpan(0, processSeparator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processId)
            || processId <= 0)
        {
            return false;
        }
        remainder = remainder[(processSeparator + 1)..];
        var identitySeparator = remainder.IndexOf('.');
        return identitySeparator == 32
               && Guid.TryParseExact(remainder[..identitySeparator], "N", out _)
               && AllowedKinds.Contains(remainder[(identitySeparator + 1)..]);
    }

    private static bool OwnerProcessHasExited(int processId)
    {
        if (processId == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception exception)
        {
            Debug.WriteLine($"Atomic temporary-file owner probe skipped ({exception.GetType().Name}).");
            return false;
        }
    }
}
