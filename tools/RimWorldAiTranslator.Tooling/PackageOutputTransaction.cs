using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWorldAiTranslator.Tooling;

internal sealed class PackageOutputTransaction
{
    private const int JournalSchemaVersion = 1;
    private const int MaximumJournalBytes = 32 * 1024;
    private const string JournalFileName = "_package-output-transaction.json";
    private const string JournalTemporaryFileName = "_package-output-transaction.json.new";
    private const string LockFileName = "_package-output-transaction.lock";
    private const string PreparedState = "prepared";
    private const string BackedUpState = "backed-up";
    private const string InstalledState = "installed";
    private const string VerifiedState = "verified";

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly RepositoryLayout repository;
    private readonly string journalPath;
    private readonly string temporaryJournalPath;
    private OutputJournal journal;
    private IReadOnlyList<OutputPair> pairs;

    private PackageOutputTransaction(RepositoryLayout repository, OutputJournal journal)
    {
        this.repository = repository;
        this.journal = ValidateJournal(repository, journal);
        journalPath = JournalPath(repository);
        temporaryJournalPath = TemporaryJournalPath(repository);
        pairs = CreatePairs(repository, this.journal);
    }

    public static FileStream AcquireRepositoryLock(RepositoryLayout repository)
    {
        repository.EnsureDistDirectory();
        var path = repository.RequireRepositoryPath(Path.Combine(repository.Dist, LockFileName));
        if (Directory.Exists(path))
            throw new IOException($"Package output lock is unexpectedly a directory: {path}");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            repository.AssertNoReparseComponents(path);
            return stream;
        }
        catch (IOException ex)
        {
            stream?.Dispose();
            throw new IOException("Another package operation may own the durable output lock.", ex);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public static void Recover(RepositoryLayout repository) => RecoverCore(repository, null);

    internal static void RecoverForSelfTest(
        RepositoryLayout repository,
        Action<int> cleanupFault) =>
        RecoverCore(repository, cleanupFault ?? throw new ArgumentNullException(nameof(cleanupFault)));

    private static void RecoverCore(RepositoryLayout repository, Action<int>? cleanupFault)
    {
        var journalPath = JournalPath(repository);
        var temporaryPath = TemporaryJournalPath(repository);
        if (Directory.Exists(journalPath) || Directory.Exists(temporaryPath))
            throw new InvalidDataException("Package output journal paths must not be directories.");
        repository.AssertNoReparseComponents(journalPath);
        repository.AssertNoReparseComponents(temporaryPath);

        OutputJournal? temporaryJournal = null;
        if (File.Exists(temporaryPath))
        {
            temporaryJournal = ReadJournal(temporaryPath);
            _ = new PackageOutputTransaction(repository, temporaryJournal);
        }

        if (!File.Exists(journalPath))
        {
            if (temporaryJournal is null) return;
            File.Move(temporaryPath, journalPath);
            RecoverCore(repository, cleanupFault);
            return;
        }

        var committedJournal = ReadJournal(journalPath);
        var transaction = new PackageOutputTransaction(repository, committedJournal);
        if (temporaryJournal is not null
            && (!SameTransactionExceptState(committedJournal, temporaryJournal)
                || StateRank(temporaryJournal.State) < StateRank(committedJournal.State)))
        {
            throw new InvalidDataException("Package output journals describe conflicting or regressive transactions.");
        }
        if (transaction.journal.State.Equals(VerifiedState, StringComparison.Ordinal))
        {
            FileStream[] pinnedOutputs;
            try
            {
                transaction.VerifyCommittedOutputs();
                pinnedOutputs = transaction.PinExpectedOutputs();
            }
            catch (Exception verificationFailure)
            {
                try
                {
                    transaction.RollBack();
                    WriteRecoveryNotice("rolled back an interrupted verified package-output transaction after output verification failed");
                    return;
                }
                catch (PackageOutputRollbackBlockedException rollbackBlocked)
                {
                    throw new PackageOutputRollbackBlockedException(
                        "BLOCKED: interrupted verified package outputs failed verification, and all-pair rollback preflight could not prove a complete rollback. Rollback did not modify any final, backup, or journal path.",
                        [verificationFailure, rollbackBlocked]);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "Interrupted verified package outputs failed verification and could not be rolled back.",
                        verificationFailure,
                        rollbackFailure);
                }
            }

            try
            {
                transaction.CompleteRecovery(cleanupFault);
            }
            catch (Exception cleanupFailure)
            {
                throw new IOException(
                    "Verified package outputs remain committed; recovery cleanup must be retried.",
                    cleanupFailure);
            }
            finally
            {
                foreach (var output in pinnedOutputs) output.Dispose();
            }
            WriteRecoveryNotice("completed a previously verified package-output transaction");
            return;
        }

        transaction.RollBack();
        WriteRecoveryNotice("rolled back an interrupted unverified package-output transaction");
    }

    public static PackageOutputTransaction Install(
        RepositoryLayout repository,
        SemanticVersion version,
        string preparedArchive,
        string finalArchive,
        string archiveHash,
        string preparedManifest,
        string finalManifest,
        string manifestHash)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        repository.AssertNoReparseComponents(preparedArchive);
        repository.AssertNoReparseComponents(preparedManifest);
        repository.AssertNoReparseComponents(finalArchive);
        repository.AssertNoReparseComponents(finalManifest);
        if (Directory.Exists(finalArchive) || Directory.Exists(finalManifest))
            throw new InvalidDataException("A package output path is unexpectedly a directory.");
        var archiveName = Path.GetFileName(finalArchive)
                          ?? throw new InvalidDataException("The final archive path has no file name.");
        var manifestName = Path.GetFileName(finalManifest)
                           ?? throw new InvalidDataException("The final manifest path has no file name.");
        var archiveHadOriginal = File.Exists(finalArchive);
        var manifestHadOriginal = File.Exists(finalManifest);
        var journal = new OutputJournal(
            JournalSchemaVersion,
            transactionId,
            PreparedState,
            version.Original,
            archiveName,
            archiveName + ".backup-" + transactionId,
            archiveHadOriginal,
            archiveHadOriginal ? PackageCommand.ComputeSha256(finalArchive) : null,
            archiveHash,
            manifestName,
            manifestName + ".backup-" + transactionId,
            manifestHadOriginal,
            manifestHadOriginal ? PackageCommand.ComputeSha256(finalManifest) : null,
            manifestHash);
        var transaction = new PackageOutputTransaction(repository, journal);
        transaction.pairs =
        [
            transaction.pairs[0] with { Prepared = preparedArchive },
            transaction.pairs[1] with { Prepared = preparedManifest }
        ];
        transaction.ValidateInstallPaths();
        transaction.WriteState(PreparedState);

        try
        {
            foreach (var pair in transaction.pairs)
            {
                if (!pair.HadOriginal) continue;
                File.Move(pair.Final, pair.Backup);
                if (!PackageCommand.ComputeSha256(pair.Backup).Equals(pair.OriginalHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"An original package output changed while it was being backed up: {pair.Final}");
            }
            transaction.WriteState(BackedUpState);

            foreach (var pair in transaction.pairs)
                File.Move(pair.Prepared!, pair.Final);
            transaction.WriteState(InstalledState);
            return transaction;
        }
        catch (Exception installFailure)
        {
            try
            {
                transaction.RollBack();
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("Package output installation and rollback both failed.", installFailure, rollbackFailure);
            }
            throw;
        }
    }

    public void MarkVerified()
    {
        if (!journal.State.Equals(InstalledState, StringComparison.Ordinal))
            throw new InvalidOperationException("Package outputs cannot be marked verified before installation completes.");
        WriteState(VerifiedState);
    }

    public void RollBack()
    {
        PreflightRollBack();
        var failures = new List<Exception>();
        foreach (var pair in pairs.Reverse())
        {
            try
            {
                RollBackPair(pair);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("One or more package outputs could not be rolled back.", failures);
        DeleteJournalFiles(requireSuccess: true);
    }

    private void PreflightRollBack()
    {
        var failures = new List<Exception>();
        foreach (var pair in pairs)
        {
            try
            {
                PreflightRollBackPair(pair);
            }
            catch (Exception failure)
            {
                failures.Add(new InvalidDataException(
                    $"Rollback preflight failed for package output '{Path.GetFileName(pair.Final)}'.",
                    failure));
            }
        }

        if (failures.Count > 0)
        {
            throw new PackageOutputRollbackBlockedException(
                "BLOCKED: complete package-output rollback cannot be proven for every output pair. No final, backup, or journal path was modified.",
                failures);
        }
    }

    private void PreflightRollBackPair(OutputPair pair)
    {
        EnsureRegularOrMissing(pair.Final);
        EnsureRegularOrMissing(pair.Backup);
        if (File.Exists(pair.Backup))
        {
            if (!pair.HadOriginal || pair.OriginalHash is null)
                throw new InvalidDataException($"An unexpected backup exists for an output that had no recorded original: {pair.Backup}");
            if (!PackageCommand.ComputeSha256(pair.Backup).Equals(pair.OriginalHash, StringComparison.Ordinal))
                throw new InvalidDataException($"A package-output recovery backup does not match the recorded original hash: {pair.Backup}");

            if (!File.Exists(pair.Final)) return;
            var finalHash = PackageCommand.ComputeSha256(pair.Final);
            if (!finalHash.Equals(pair.OriginalHash, StringComparison.Ordinal)
                && !finalHash.Equals(pair.ExpectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Refusing to replace an unrecognized file at a transaction-owned output path: {pair.Final}");
            }
            return;
        }

        if (pair.HadOriginal)
        {
            if (!File.Exists(pair.Final))
                throw new InvalidDataException($"Both the original output and its recovery backup are missing: {pair.Final}");
            if (pair.OriginalHash is null
                || !PackageCommand.ComputeSha256(pair.Final).Equals(pair.OriginalHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"A required original-output backup is missing and the final path is not the recorded original: {pair.Backup}");
            }
            return;
        }

        if (!File.Exists(pair.Final)) return;
        if (!PackageCommand.ComputeSha256(pair.Final).Equals(pair.ExpectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Refusing to delete an unrecognized file at a transaction-owned output path: {pair.Final}");
    }

    public void Complete()
    {
        if (!journal.State.Equals(VerifiedState, StringComparison.Ordinal))
            throw new InvalidOperationException("Package output transaction is not durably verified.");

        var pinnedOutputs = PinExpectedOutputs();
        try
        {
            ValidateVerifiedBackups();
            var backupsRemoved = true;
            foreach (var pair in pairs)
            {
                try
                {
                    _ = DeleteVerifiedBackup(pair);
                }
                catch (Exception cleanupFailure)
                {
                    backupsRemoved = false;
                    WriteCleanupWarning("verified output backup", cleanupFailure);
                }
            }
            if (backupsRemoved) DeleteJournalFiles(requireSuccess: false);
        }
        finally
        {
            foreach (var output in pinnedOutputs) output.Dispose();
        }
    }

    private void CompleteRecovery(Action<int>? cleanupFault)
    {
        ValidateVerifiedBackups();
        var backupsRemoved = 0;
        foreach (var pair in pairs)
        {
            if (!DeleteVerifiedBackup(pair)) continue;
            backupsRemoved++;
            cleanupFault?.Invoke(backupsRemoved);
        }
        DeleteJournalFiles(requireSuccess: true);
    }

    private void VerifyCommittedOutputs()
    {
        foreach (var pair in pairs)
        {
            if (!File.Exists(pair.Final)
                || !PackageCommand.ComputeSha256(pair.Final).Equals(pair.ExpectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Recovered package output does not match its verified hash: {pair.Final}");
            }
        }
        ZeroAudit.VerifyPackage(pairs[0].Final);
        VerifyArchivedVersion(pairs[0].Final, journal.Version);
    }

    private void RollBackPair(OutputPair pair)
    {
        EnsureRegularOrMissing(pair.Final);
        EnsureRegularOrMissing(pair.Backup);
        if (File.Exists(pair.Backup))
        {
            if (!pair.HadOriginal || pair.OriginalHash is null)
                throw new InvalidDataException($"An unexpected backup exists for an output that had no recorded original: {pair.Backup}");
            var backupHash = PackageCommand.ComputeSha256(pair.Backup);
            if (!backupHash.Equals(pair.OriginalHash, StringComparison.Ordinal))
                throw new InvalidDataException($"A package-output recovery backup does not match the recorded original hash: {pair.Backup}");

            if (File.Exists(pair.Final))
            {
                var finalHash = PackageCommand.ComputeSha256(pair.Final);
                if (finalHash.Equals(pair.OriginalHash, StringComparison.Ordinal))
                {
                    File.Delete(pair.Backup);
                    return;
                }
                if (!finalHash.Equals(pair.ExpectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Refusing to delete an unrecognized file at a transaction-owned output path: {pair.Final}");
                File.Delete(pair.Final);
            }
            File.Move(pair.Backup, pair.Final);
            if (!PackageCommand.ComputeSha256(pair.Final).Equals(pair.OriginalHash, StringComparison.Ordinal))
                throw new InvalidDataException($"A restored package output does not match the recorded original hash: {pair.Final}");
            return;
        }

        if (pair.HadOriginal)
        {
            if (!File.Exists(pair.Final))
                throw new InvalidDataException($"Both the original output and its recovery backup are missing: {pair.Final}");
            if (!PackageCommand.ComputeSha256(pair.Final).Equals(pair.OriginalHash, StringComparison.Ordinal))
                throw new InvalidDataException($"A required original-output backup is missing and the final path is not the recorded original: {pair.Backup}");
            return;
        }

        if (!File.Exists(pair.Final)) return;
        if (!PackageCommand.ComputeSha256(pair.Final).Equals(pair.ExpectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Refusing to delete an unrecognized file at a transaction-owned output path: {pair.Final}");
        File.Delete(pair.Final);
    }

    private bool DeleteVerifiedBackup(OutputPair pair)
    {
        EnsureRegularOrMissing(pair.Backup);
        if (!File.Exists(pair.Backup)) return false;
        if (!pair.HadOriginal || pair.OriginalHash is null)
            throw new InvalidDataException($"Refusing to delete an unexpected package-output backup: {pair.Backup}");
        if (!PackageCommand.ComputeSha256(pair.Backup).Equals(pair.OriginalHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Refusing to delete a package-output backup whose hash is not recorded: {pair.Backup}");
        File.Delete(pair.Backup);
        return true;
    }

    private void ValidateVerifiedBackups()
    {
        foreach (var pair in pairs)
        {
            EnsureRegularOrMissing(pair.Backup);
            if (!File.Exists(pair.Backup)) continue;
            if (!pair.HadOriginal || pair.OriginalHash is null)
                throw new InvalidDataException($"Refusing to finalize an unexpected package-output backup: {pair.Backup}");
            if (!PackageCommand.ComputeSha256(pair.Backup).Equals(pair.OriginalHash, StringComparison.Ordinal))
                throw new InvalidDataException($"A package-output backup changed before verified finalization: {pair.Backup}");
        }
    }

    private FileStream[] PinExpectedOutputs()
    {
        var streams = new List<FileStream>();
        try
        {
            foreach (var pair in pairs)
            {
                EnsureRegularOrMissing(pair.Final);
                var stream = new FileStream(pair.Final, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!hash.Equals(pair.ExpectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException("A verified package output changed before backup cleanup.");
            }
            return streams.ToArray();
        }
        catch
        {
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
    }

    private void ValidateInstallPaths()
    {
        foreach (var pair in pairs)
        {
            if (pair.Prepared is null || !File.Exists(pair.Prepared))
                throw new FileNotFoundException("Prepared package output is missing.", pair.Prepared);
            repository.AssertNoReparseComponents(pair.Prepared);
            EnsureRegularOrMissing(pair.Final);
            EnsureRegularOrMissing(pair.Backup);
            if (File.Exists(pair.Backup))
                throw new IOException($"Run-unique package backup path already exists: {pair.Backup}");
        }
    }

    private void WriteState(string state)
    {
        if (state is not (PreparedState or BackedUpState or InstalledState or VerifiedState))
            throw new InvalidDataException($"Unknown package output journal state: {state}");
        var next = journal with { State = state };
        WriteJournal(temporaryJournalPath, journalPath, next);
        journal = next;
    }

    private void DeleteJournalFiles(bool requireSuccess)
    {
        try
        {
            if (File.Exists(temporaryJournalPath))
            {
                var temporaryJournal = ReadJournal(temporaryJournalPath);
                _ = new PackageOutputTransaction(repository, temporaryJournal);
                if (!SameTransactionExceptState(journal, temporaryJournal))
                    throw new InvalidDataException("Refusing to delete a temporary journal for a different package-output transaction.");
                File.Delete(temporaryJournalPath);
            }
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
        catch (Exception cleanupFailure)
        {
            if (requireSuccess) throw;
            WriteCleanupWarning("verified output journal", cleanupFailure);
        }
    }

    private void EnsureRegularOrMissing(string path)
    {
        repository.AssertNoReparseComponents(path);
        if (Directory.Exists(path))
            throw new InvalidDataException($"Package output transaction path is unexpectedly a directory: {path}");
    }

    private static OutputJournal ReadJournal(string path)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumJournalBytes)
            throw new InvalidDataException($"Package output journal has an invalid size: {info.Length} bytes.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<OutputJournal>(stream, JournalJsonOptions)
               ?? throw new InvalidDataException("Package output journal is empty.");
    }

    private static void WriteJournal(string temporaryPath, string finalPath, OutputJournal journal)
    {
        if (File.Exists(temporaryPath))
            throw new IOException("A package output temporary journal already exists and requires recovery.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalJsonOptions);
        if (bytes.Length + 1 > MaximumJournalBytes)
            throw new InvalidDataException("Package output journal exceeds its size limit.");
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, finalPath, overwrite: true);
        using var committed = new FileStream(finalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        committed.Flush(flushToDisk: true);
    }

    private static OutputJournal ValidateJournal(RepositoryLayout repository, OutputJournal journal)
    {
        if (journal.SchemaVersion != JournalSchemaVersion
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || journal.TransactionId.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))
            || journal.State is not (PreparedState or BackedUpState or InstalledState or VerifiedState)
            || !IsSha256(journal.ArchiveHash)
            || !IsSha256(journal.ManifestHash)
            || journal.ArchiveHadOriginal != (journal.ArchiveOriginalHash is not null)
            || journal.ManifestHadOriginal != (journal.ManifestOriginalHash is not null)
            || (journal.ArchiveOriginalHash is not null && !IsSha256(journal.ArchiveOriginalHash))
            || (journal.ManifestOriginalHash is not null && !IsSha256(journal.ManifestOriginalHash)))
        {
            throw new InvalidDataException("Package output journal has an invalid schema, transaction, state, or hash.");
        }

        var version = SemanticVersion.Parse(journal.Version);
        var expectedArchive = PackageLayout.ArchiveName(version);
        var expectedManifest = PackageLayout.ManifestName(version);
        if (!string.Equals(journal.ArchiveFinal, expectedArchive, StringComparison.Ordinal)
            || !string.Equals(journal.ManifestFinal, expectedManifest, StringComparison.Ordinal)
            || !string.Equals(journal.ArchiveBackup, expectedArchive + ".backup-" + journal.TransactionId, StringComparison.Ordinal)
            || !string.Equals(journal.ManifestBackup, expectedManifest + ".backup-" + journal.TransactionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Package output journal paths do not match its validated version and transaction.");
        }

        _ = repository.RequireRepositoryPath(Path.Combine(repository.Dist, journal.ArchiveFinal));
        _ = repository.RequireRepositoryPath(Path.Combine(repository.Dist, journal.ArchiveBackup));
        _ = repository.RequireRepositoryPath(Path.Combine(repository.Dist, journal.ManifestFinal));
        _ = repository.RequireRepositoryPath(Path.Combine(repository.Dist, journal.ManifestBackup));
        return journal;
    }

    private static IReadOnlyList<OutputPair> CreatePairs(RepositoryLayout repository, OutputJournal journal) =>
    [
        new OutputPair(
            null,
            repository.RequireRepositoryPath(Path.Combine(repository.Dist, journal.ArchiveFinal)),
            repository.RequireRepositoryPath(Path.Combine(repository.Dist, journal.ArchiveBackup)),
            journal.ArchiveHadOriginal,
            journal.ArchiveOriginalHash,
            journal.ArchiveHash),
        new OutputPair(
            null,
            repository.RequireRepositoryPath(Path.Combine(repository.Dist, journal.ManifestFinal)),
            repository.RequireRepositoryPath(Path.Combine(repository.Dist, journal.ManifestBackup)),
            journal.ManifestHadOriginal,
            journal.ManifestOriginalHash,
            journal.ManifestHash)
    ];

    private static void VerifyArchivedVersion(string archivePath, string expectedVersion)
    {
        PackageArchivePolicy.ValidateArchiveFile(archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(entry => entry.FullName.Equals("VERSION", StringComparison.Ordinal)).ToArray();
        if (entries.Length != 1 || entries[0].Length > 4 * 1024)
            throw new InvalidDataException("Recovered archive VERSION entry is missing, duplicated, or oversized.");
        using var stream = entries[0].Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: false);
        if (!reader.ReadToEnd().Trim().Equals(expectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException("Recovered archive VERSION does not match its durable transaction journal.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    private static bool SameTransactionExceptState(OutputJournal left, OutputJournal right) =>
        left with { State = right.State } == right;

    private static int StateRank(string state) => state switch
    {
        PreparedState => 0,
        BackedUpState => 1,
        InstalledState => 2,
        VerifiedState => 3,
        _ => throw new InvalidDataException($"Unknown package output journal state: {state}")
    };

    private static string JournalPath(RepositoryLayout repository) =>
        repository.RequireRepositoryPath(Path.Combine(repository.Dist, JournalFileName));

    private static string TemporaryJournalPath(RepositoryLayout repository) =>
        repository.RequireRepositoryPath(Path.Combine(repository.Dist, JournalTemporaryFileName));

    private static void WriteCleanupWarning(string subject, Exception failure)
    {
        try
        {
            Console.Error.WriteLine($"WARNING: {subject} cleanup failed ({failure.GetType().Name}).");
        }
        catch (Exception diagnosticFailure)
        {
            Debug.WriteLine($"Package transaction warning sink unavailable ({diagnosticFailure.GetType().Name}).");
        }
    }

    private static void WriteRecoveryNotice(string message)
    {
        try
        {
            Console.Error.WriteLine("RECOVERY: " + message + ".");
        }
        catch (Exception diagnosticFailure)
        {
            Debug.WriteLine($"Package recovery notice sink unavailable ({diagnosticFailure.GetType().Name}).");
        }
    }

    private sealed record OutputJournal(
        int SchemaVersion,
        string TransactionId,
        string State,
        string Version,
        string ArchiveFinal,
        string ArchiveBackup,
        bool ArchiveHadOriginal,
        string? ArchiveOriginalHash,
        string ArchiveHash,
        string ManifestFinal,
        string ManifestBackup,
        bool ManifestHadOriginal,
        string? ManifestOriginalHash,
        string ManifestHash);

    private sealed record OutputPair(
        string? Prepared,
        string Final,
        string Backup,
        bool HadOriginal,
        string? OriginalHash,
        string ExpectedHash);
}

internal sealed class PackageOutputRollbackBlockedException : AggregateException
{
    public PackageOutputRollbackBlockedException(
        string message,
        IEnumerable<Exception> failures)
        : base(message, failures)
    {
    }
}
