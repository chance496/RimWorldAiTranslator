using System.Text;
using AtomicTemporaryFileOps = RimWorldAiTranslator.Core.Storage.AtomicTemporaryFiles;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Logging;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static readonly string[] Phase08PreparedSubstitutionModes = ["rewrite", "swap"];

    private static void Phase08AtomicStorageFaults()
    {
        WithTempRoot(root =>
        {
            ExerciseAtomicParentRedirectPreflight(root);
            ExerciseAtomicPreparedValidationPins(root);
            ExerciseTrustedDirectoryCreationPins(root);

            var target = Path.Combine(root, "atomic-fault.txt");
            AtomicFile.WriteUtf8(target, "last-good-one");
            AtomicFile.WriteUtf8(target, "last-good-two");
            var expectedPrimary = File.ReadAllBytes(target);
            var expectedBackup = File.ReadAllBytes(target + ".bak");

            try
            {
                AtomicFile.BeforeTemporaryFlushTestHook = _ =>
                    throw new IOException("synthetic disk-full boundary");
                _ = CaptureException<IOException>(() => AtomicFile.WriteUtf8(target, "must-not-commit"));
            }
            finally
            {
                AtomicFile.BeforeTemporaryFlushTestHook = null;
            }
            Assert(File.ReadAllBytes(target).SequenceEqual(expectedPrimary)
                   && File.ReadAllBytes(target + ".bak").SequenceEqual(expectedBackup),
                "A synthetic disk-full failure changed the primary file or its backup.");
            Assert(!AtomicTemporaryFiles(root, target).Any(),
                "A synthetic disk-full failure left a temporary file behind.");

            using (var locked = new FileStream(
                       target,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       4_096,
                       FileOptions.SequentialScan))
            {
                _ = CaptureException<IOException>(() => AtomicFile.WriteUtf8(target, "locked-commit"));
            }
            Assert(File.ReadAllBytes(target).SequenceEqual(expectedPrimary)
                   && File.ReadAllBytes(target + ".bak").SequenceEqual(expectedBackup),
                "A locked-target failure changed the primary file or its backup.");
            Assert(!AtomicTemporaryFiles(root, target).Any(),
                "A locked-target failure left a temporary file behind.");

            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
            try
            {
                _ = CaptureException<UnauthorizedAccessException>(
                    () => AtomicFile.WriteUtf8(target, "read-only-commit"));
            }
            finally
            {
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            }
            Assert(File.ReadAllBytes(target).SequenceEqual(expectedPrimary)
                   && File.ReadAllBytes(target + ".bak").SequenceEqual(expectedBackup),
                "A read-only-target failure changed the primary file or its backup.");
            Assert(!AtomicTemporaryFiles(root, target).Any(),
                "A read-only-target failure left a temporary file behind.");
            Assert(Encoding.UTF8.GetString(expectedPrimary) == "last-good-two",
                "The storage fault fixture did not preserve the expected last-good content.");
        });
        ExerciseRmkPreparedValidationPins();
    }

    private static void ExerciseAtomicParentRedirectPreflight(string root)
    {
        var redirectedDirectory = Path.Combine(root, "atomic-redirected-physical");
        var redirectedAlias = Path.Combine(root, "atomic-redirected-alias");
        Directory.CreateDirectory(redirectedDirectory);
        Directory.CreateSymbolicLink(redirectedAlias, redirectedDirectory);

        _ = CaptureException<InvalidDataException>(() =>
            AtomicFile.WriteUtf8(
                Path.Combine(redirectedAlias, "must-not-be-created.json"),
                "private-preflight-payload",
                keepBackup: false));

        var redirectedMissingDirectory = Path.Combine(
            redirectedAlias,
            "must-not-create-this-directory",
            "nested");
        _ = CaptureException<InvalidDataException>(() =>
            AtomicFile.WriteUtf8(
                Path.Combine(redirectedMissingDirectory, "must-not-be-created.json"),
                "private-missing-descendant-payload",
                keepBackup: false));

        Assert(!Directory.EnumerateFileSystemEntries(
                    redirectedDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly).Any(),
            "Atomic parent validation created a redirected descendant, output, or temporary leaf before acquiring its trusted boundary.");
    }

    private static void ExerciseAtomicPreparedValidationPins(string root)
    {
        foreach (var mode in Phase08PreparedSubstitutionModes)
        {
            var target = Path.Combine(root, $"pre-validation-pin-{mode}.txt");
            var original = $"pre-validation-original-{mode}";
            var intended = $"pre-validation-intended-{mode}";
            var forged = $"pre-validation-forged-{mode}";
            var validatorCalled = false;
            File.WriteAllText(target, original, new UTF8Encoding(false));
            ConcurrentLeafChangeException failure;
            try
            {
                AtomicFile.AfterTemporaryFlushBeforeValidationPinTestHook = temporaryPath =>
                {
                    if (mode == "swap") File.Delete(temporaryPath);
                    File.WriteAllText(temporaryPath, forged, new UTF8Encoding(false));
                };
                failure = CaptureException<ConcurrentLeafChangeException>(() =>
                    AtomicFile.WriteUtf8Validated(
                        target,
                        intended,
                        temporaryPath =>
                        {
                            validatorCalled = true;
                            Assert(File.ReadAllText(temporaryPath, Encoding.UTF8) == intended,
                                "The validator observed bytes other than the intended atomic output.");
                        }));
            }
            finally
            {
                AtomicFile.AfterTemporaryFlushBeforeValidationPinTestHook = null;
            }

            var preserved = AtomicTemporaryFiles(root, target).Single();
            Assert(!validatorCalled
                   && File.ReadAllText(target, Encoding.UTF8) == original
                   && !File.Exists(target + ".bak")
                   && File.ReadAllText(preserved, Encoding.UTF8) == forged
                   && failure.PreservedPaths.Contains(preserved, StringComparer.OrdinalIgnoreCase),
                $"The {mode} post-flush substitution was committed, deleted, or not reported as preserved.");
            File.Delete(preserved);
        }

        foreach (var mode in Phase08PreparedSubstitutionModes)
        {
            var target = Path.Combine(root, $"post-validation-pin-{mode}.txt");
            var original = $"post-validation-original-{mode}";
            var intended = $"post-validation-intended-{mode}";
            var forged = $"post-validation-forged-{mode}";
            var validatorCalled = false;
            File.WriteAllText(target, original, new UTF8Encoding(false));
            ConcurrentLeafChangeException failure;
            try
            {
                AtomicFile.AfterTemporaryValidationBeforeCommitTestHook = temporaryPath =>
                {
                    if (mode == "swap") File.Delete(temporaryPath);
                    File.WriteAllText(temporaryPath, forged, new UTF8Encoding(false));
                };
                failure = CaptureException<ConcurrentLeafChangeException>(() =>
                    AtomicFile.WriteUtf8Validated(
                        target,
                        intended,
                        temporaryPath =>
                        {
                            validatorCalled = true;
                            Assert(File.ReadAllText(temporaryPath, Encoding.UTF8) == intended,
                                "The validator observed bytes other than the intended atomic output.");
                        }));
            }
            finally
            {
                AtomicFile.AfterTemporaryValidationBeforeCommitTestHook = null;
            }

            var preserved = AtomicTemporaryFiles(root, target).Single();
            Assert(validatorCalled
                   && File.ReadAllText(target, Encoding.UTF8) == original
                   && !File.Exists(target + ".bak")
                   && File.ReadAllText(preserved, Encoding.UTF8) == forged
                   && failure.PreservedPaths.Contains(preserved, StringComparer.OrdinalIgnoreCase),
                $"The {mode} post-validation substitution was committed, deleted, or not reported as preserved.");
            File.Delete(preserved);
        }
    }

    private static void ExerciseTrustedDirectoryCreationPins(string root)
    {
        var outsideMissing = Path.Combine(root, "directory-boundary-outside-missing");
        var redirectedParent = Path.Combine(root, "directory-boundary-missing");
        var redirectedLog = Path.Combine(redirectedParent, "deep", "logs", "test.log");
        Directory.CreateDirectory(outsideMissing);
        try
        {
            PathSafety.BeforeTrustedDirectoryCreationTestHook = _ =>
                Directory.CreateSymbolicLink(redirectedParent, outsideMissing);
            _ = CaptureException<InvalidDataException>(() =>
                SafeLogFile.AppendUtf8Line(redirectedLog, "must-not-escape", flushToDisk: true));
        }
        finally
        {
            PathSafety.BeforeTrustedDirectoryCreationTestHook = null;
        }
        Assert(!Directory.EnumerateFileSystemEntries(outsideMissing).Any(),
            "Trusted missing-directory creation wrote a child or guard through an injected redirect.");

        var swappableRoot = Path.Combine(root, "directory-boundary-existing-root");
        var displacedRoot = Path.Combine(root, "directory-boundary-existing-root-displaced");
        var outsideRoot = Path.Combine(root, "directory-boundary-outside-root");
        Directory.CreateDirectory(swappableRoot);
        Directory.CreateDirectory(outsideRoot);
        try
        {
            PathSafety.BeforeTrustedBoundaryRootLockTestHook = lockedRoot =>
            {
                Assert(lockedRoot.Equals(swappableRoot, StringComparison.OrdinalIgnoreCase),
                    "The root-swap fixture targeted an unexpected trusted boundary.");
                Directory.Move(swappableRoot, displacedRoot);
                Directory.CreateSymbolicLink(swappableRoot, outsideRoot);
            };
            _ = CaptureException<InvalidDataException>(() =>
                AtomicFile.WriteUtf8(
                    Path.Combine(swappableRoot, "deep", "must-not-escape.json"),
                    "must-not-escape",
                    keepBackup: false));
        }
        finally
        {
            PathSafety.BeforeTrustedBoundaryRootLockTestHook = null;
            if (Directory.Exists(swappableRoot)) Directory.Delete(swappableRoot);
            if (Directory.Exists(displacedRoot)) Directory.Move(displacedRoot, swappableRoot);
        }
        Assert(!Directory.EnumerateFileSystemEntries(outsideRoot).Any(),
            "Trusted boundary root substitution created an external guard, child, or output.");
    }

    private static void ExerciseRmkPreparedValidationPins()
    {
        foreach (var mode in Phase08PreparedSubstitutionModes)
        {
            foreach (var durable in new[] { false, true })
            {
                var scenario = durable ? $"{mode}-durable" : $"{mode}-direct";
                WithFixture("SampleMod", modRoot =>
                {
                    var run = CreateSourceOnlyReview(
                        modRoot,
                        $"reviews-rmk-prepared-pin-{scenario}");
                    var defRow = run.Rows.Single(row => row.Key == "Codex_TestWorkbench.label");
                    var keyedRow = run.Rows.Single(row => row.Key == "CodexTranslator.SampleButton");
                    SaveReviewDecisions(run, [(defRow, $"준비 원본 {mode}")]);

                    var root = Directory.GetParent(modRoot)!.FullName;
                    var workspaceRoot = CreateRmkWorkspace(
                        root,
                        $"PreparedPin{scenario}",
                        out var rmkRoot);
                    var workbookPath = Path.Combine(rmkRoot, "history.xlsx");
                    var authorityRoot = Path.Combine(root, $"recovery-authority-{scenario}");
                    Directory.CreateDirectory(authorityRoot);
                    var service = new RmkExportService(
                        new AtomicJsonStore(),
                        new LanguageFileService(),
                        CreateExtractor(),
                        recoveryAuthority: durable
                            ? new FileTransactionRecoveryAuthority(authorityRoot)
                            : null);
                    RmkExportOptions Options() => new()
                    {
                        RmkWorkspaceRoot = workspaceRoot,
                        RmkEntryRoot = rmkRoot,
                        ReviewRoot = run.ReviewRoot!,
                        ModRoot = modRoot,
                        RmkLanguageFolderName = "Korean",
                        WorkbookPath = workbookPath,
                        Overwrite = true
                    };

                    var initial = service.Export(Options());
                    Assert(initial.EligibleEntries == 1 && File.Exists(workbookPath),
                        $"The {scenario} RMK prepared-pin fixture did not create its initial workbook " +
                        $"(eligible={initial.EligibleEntries}, exists={File.Exists(workbookPath)}, " +
                        $"status={initial.SkippedStatus}, changed={initial.SkippedSourceChanged}, " +
                        $"unsafe={initial.SkippedUnsafe}, unmapped={initial.SkippedUnmapped}, " +
                        $"ambiguous={initial.SkippedAmbiguous}).");
                    var original = File.ReadAllBytes(workbookPath);
                    Assert(!File.Exists(workbookPath + ".bak"),
                        "The initial RMK prepared-pin workbook unexpectedly has a backup.");

                    SaveReviewDecisions(
                        run,
                        [
                            (defRow, $"준비 원본 {mode}"),
                            (keyedRow, $"준비 추가 {mode}")
                        ]);
                    var forged = Encoding.UTF8.GetBytes($"forged-rmk-prepared-{mode}");
                    string? forgedPath = null;
                    IOException failure;
                    try
                    {
                        service.AfterWorkbookPreparedValidationTestHook = preparedPath =>
                        {
                            forgedPath = preparedPath;
                            if (mode == "swap") File.Delete(preparedPath);
                            File.WriteAllBytes(preparedPath, forged);
                        };
                        failure = CaptureException<IOException>(() => service.Export(Options()));
                    }
                    finally
                    {
                        service.AfterWorkbookPreparedValidationTestHook = null;
                    }

                    Assert(File.ReadAllBytes(workbookPath).SequenceEqual(original)
                           && !File.Exists(workbookPath + ".bak"),
                        $"The {scenario} post-validation RMK substitution changed the canonical workbook or backup state.");
                    Assert(forgedPath is not null
                           && File.Exists(forgedPath)
                           && File.ReadAllBytes(forgedPath).SequenceEqual(forged)
                           && failure is ConcurrentLeafChangeException concurrent
                           && concurrent.PreservedPaths.Contains(forgedPath, StringComparer.OrdinalIgnoreCase),
                        $"The {scenario} post-validation RMK winner was committed, deleted, or not explicitly preserved.");
                    Assert(!durable || !Directory.EnumerateFiles(
                                authorityRoot,
                                "intent-*.json",
                                SearchOption.AllDirectories).Any(),
                        $"The {scenario} forged RMK prepared workbook was published as a durable commit intent.");
                });
            }
        }
    }

    private static void Phase08ProjectLoadCancellation()
    {
        WithTempRoot(root =>
        {
            var appRoot = Path.Combine(root, "appdata");
            var paths = new AppDataPaths(appRoot);
            paths.EnsureExists();
            var discoveryRoot = Path.Combine(root, "synthetic-discovery");
            Directory.CreateDirectory(discoveryRoot);
            File.WriteAllText(
                Path.Combine(discoveryRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
                RimWorldModDiscoveryService.IsolationMarkerContent,
                new UTF8Encoding(false));
            var firstMod = Path.Combine(discoveryRoot, "first-mod");
            var secondMod = Path.Combine(discoveryRoot, "second-mod");
            Directory.CreateDirectory(firstMod);
            Directory.CreateDirectory(secondMod);

            var repository = new ProjectRepository(new AtomicJsonStore(), paths);
            repository.Save(new ProjectStoreDocument
            {
                Projects =
                [
                    new TranslationProject { Id = "first", Name = "First", ModRoot = firstMod },
                    new TranslationProject { Id = "second", Name = "Second", ModRoot = secondMod }
                ]
            });
            var before = File.ReadAllBytes(paths.Projects);
            using (var readCancellation = new CancellationTokenSource())
            {
                try
                {
                    BoundedFileReader.AfterChunkReadTestHook = _ => readCancellation.Cancel();
                    _ = CaptureException<OperationCanceledException>(
                        () => repository.Load(readCancellation.Token));
                }
                finally
                {
                    BoundedFileReader.AfterChunkReadTestHook = null;
                }
            }
            Assert(File.ReadAllBytes(paths.Projects).SequenceEqual(before),
                "Cancellation during the JSON read changed the project repository.");

            var activeTemporary = Path.Combine(
                Path.GetDirectoryName(paths.Projects)!,
                $".{Path.GetFileName(paths.Projects)}.p{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(activeTemporary, "synthetic active writer", new UTF8Encoding(false));
            using var cancellation = new CancellationTokenSource();
            var probeCount = 0;
            var service = new ProjectService(
                repository,
                RimWorldModDiscoveryService.CreateIsolated(discoveryRoot),
                new ProjectCleanupService())
            {
                BeforeModRootProbeTestHook = _ =>
                {
                    probeCount++;
                    cancellation.Cancel();
                }
            };

            _ = CaptureException<OperationCanceledException>(() => service.Load(cancellation.Token));

            Assert(cancellation.IsCancellationRequested && probeCount == 1,
                "Project loading did not stop at the first deterministic cancellation boundary.");
            Assert(File.ReadAllBytes(paths.Projects).SequenceEqual(before),
                "Cancelled project loading changed the persisted project repository.");
            Assert(File.Exists(activeTemporary),
                "Project loading removed a temporary file owned by the active process.");
            File.Delete(activeTemporary);
        });
    }

    private static void Phase08NonDurableFinalEvidenceSubstitution()
    {
        WithTempRoot(root =>
        {
            foreach (var mode in Phase08PreparedSubstitutionModes)
            {
                var target = Path.Combine(root, $"forward-final-evidence-{mode}.txt");
                var original = $"original-{mode}";
                var forged = $"forged-{mode}";
                File.WriteAllText(target, original, new UTF8Encoding(false));
                try
                {
                    PathSafety.AfterAtomicFinalEvidenceReleasedTestHook = prepared =>
                    {
                        if (mode == "swap") File.Delete(prepared);
                        File.WriteAllText(prepared, forged, new UTF8Encoding(false));
                    };
                    var error = CaptureException<IOException>(() =>
                        AtomicFile.WriteUtf8(target, $"intended-{mode}"));
                    Assert(File.ReadAllText(target, Encoding.UTF8) == original,
                        $"The {mode} prepared substitution remained at the canonical target.");
                    var rejected = Directory.EnumerateFiles(
                            root,
                            $".{Path.GetFileName(target)}.*.rejected.tmp",
                            SearchOption.TopDirectoryOnly)
                        .ToArray();
                    Assert(rejected.Length <= 1,
                        $"The {mode} substitution produced ambiguous rejected-output leaves.");
                    if (rejected.Length == 1)
                    {
                        Assert(File.ReadAllText(rejected[0], Encoding.UTF8) == forged
                               && error is ConcurrentLeafChangeException concurrent
                               && concurrent.PreservedPaths.Contains(rejected[0], StringComparer.OrdinalIgnoreCase),
                            $"The {mode} substitution was not quarantined and explicitly reported.");
                        File.Delete(rejected[0]);
                    }
                    Assert(!Directory.EnumerateFiles(
                            root,
                            $".{Path.GetFileName(target)}.*.displaced.tmp",
                            SearchOption.TopDirectoryOnly).Any(),
                        $"The {mode} substitution left the original target outside its canonical path.");
                }
                finally
                {
                    PathSafety.AfterAtomicFinalEvidenceReleasedTestHook = null;
                }
            }

            foreach (var mode in Phase08PreparedSubstitutionModes)
            {
                var target = Path.Combine(root, $"absent-pinned-source-{mode}.txt");
                var intended = $"absent-source-intended-{mode}";
                var blocked = false;
                try
                {
                    AtomicTemporaryFileOps.BeforePinnedMoveTestHook = source =>
                    {
                        if (!Path.GetFileName(source).StartsWith(
                                $".{Path.GetFileName(target)}.p",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                        try
                        {
                            if (mode == "swap") File.Delete(source);
                            File.WriteAllText(source, $"absent-source-forged-{mode}", new UTF8Encoding(false));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            blocked = true;
                            return;
                        }
                        throw new InvalidOperationException(
                            $"The {mode} absent-target source substitution was not blocked by its mutation handle.");
                    };
                    AtomicFile.WriteUtf8(target, intended, keepBackup: false);
                    Assert(blocked && File.ReadAllText(target, Encoding.UTF8) == intended,
                        $"The {mode} absent-target source substitution escaped the pinned rename handle.");
                }
                finally
                {
                    AtomicTemporaryFileOps.BeforePinnedMoveTestHook = null;
                }
            }

            foreach (var mode in Phase08PreparedSubstitutionModes)
            {
                var target = Path.Combine(root, $"backup-pinned-source-{mode}.txt");
                var original = $"backup-source-original-{mode}";
                var priorBackup = $"prior-backup-{mode}";
                var intended = $"backup-source-intended-{mode}";
                var blocked = false;
                File.WriteAllText(target, original, new UTF8Encoding(false));
                File.WriteAllText(target + ".bak", priorBackup, new UTF8Encoding(false));
                try
                {
                    AtomicTemporaryFileOps.BeforePinnedMoveTestHook = source =>
                    {
                        if (!source.EndsWith(".displaced.tmp", StringComparison.OrdinalIgnoreCase)) return;
                        try
                        {
                            if (mode == "swap") File.Delete(source);
                            File.WriteAllText(source, $"forged-backup-source-{mode}", new UTF8Encoding(false));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            blocked = true;
                            return;
                        }
                        throw new InvalidOperationException(
                            $"The {mode} backup-source substitution was not blocked by its mutation handle.");
                    };
                    AtomicFile.WriteUtf8(target, intended);
                    Assert(blocked
                           && File.ReadAllText(target, Encoding.UTF8) == intended
                           && File.ReadAllText(target + ".bak", Encoding.UTF8) == original,
                        $"The {mode} backup-source substitution escaped the pinned handle or changed committed data.");
                }
                finally
                {
                    AtomicTemporaryFileOps.BeforePinnedMoveTestHook = null;
                }
            }

            foreach (var mode in Phase08PreparedSubstitutionModes)
            {
                var target = Path.Combine(root, $"rollback-pinned-target-{mode}.txt");
                var original = $"rollback-original-{mode}";
                var concurrent = $"rollback-concurrent-{mode}";
                var blocked = false;
                File.WriteAllText(target, original, new UTF8Encoding(false));
                try
                {
                    PathSafety.AfterAtomicCommitBeforeEvidencePinnedTestHook = path =>
                    {
                        if (!path.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
                        File.Delete(target);
                        File.WriteAllText(target, concurrent, new UTF8Encoding(false));
                    };
                    AtomicTemporaryFileOps.BeforePinnedMoveTestHook = source =>
                    {
                        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
                        try
                        {
                            if (mode == "swap") File.Delete(source);
                            File.WriteAllText(source, $"rollback-forged-{mode}", new UTF8Encoding(false));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            blocked = true;
                            return;
                        }
                        throw new InvalidOperationException(
                            $"The {mode} rollback-target substitution was not blocked by its mutation handle.");
                    };
                    var error = CaptureException<ConcurrentLeafChangeException>(() =>
                        AtomicFile.WriteUtf8(target, $"rollback-intended-{mode}", keepBackup: false));
                    var rejected = Directory.EnumerateFiles(
                            root,
                            $".{Path.GetFileName(target)}.*.rejected.tmp",
                            SearchOption.TopDirectoryOnly)
                        .Single();
                    Assert(blocked
                           && File.ReadAllText(target, Encoding.UTF8) == original
                           && File.ReadAllText(rejected, Encoding.UTF8) == concurrent
                           && error.PreservedPaths.Contains(rejected, StringComparer.OrdinalIgnoreCase),
                        $"The {mode} rollback substitution was not blocked or its concurrent winner was not quarantined.");
                    File.Delete(rejected);
                }
                finally
                {
                    PathSafety.AfterAtomicCommitBeforeEvidencePinnedTestHook = null;
                    AtomicTemporaryFileOps.BeforePinnedMoveTestHook = null;
                }
            }

            foreach (var mode in Phase08PreparedSubstitutionModes)
            {
                var target = Path.Combine(root, $"cleanup-pinned-source-{mode}.txt");
                var original = $"cleanup-original-{mode}";
                var intended = $"cleanup-intended-{mode}";
                var blocked = false;
                File.WriteAllText(target, original, new UTF8Encoding(false));
                try
                {
                    AtomicTemporaryFileOps.BeforePinnedDeleteTestHook = path =>
                    {
                        if (!path.EndsWith(".displaced.tmp", StringComparison.OrdinalIgnoreCase)) return;
                        try
                        {
                            if (mode == "swap") File.Delete(path);
                            File.WriteAllText(path, $"cleanup-forged-{mode}", new UTF8Encoding(false));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            blocked = true;
                            return;
                        }
                        throw new InvalidOperationException(
                            $"The {mode} cleanup substitution was not blocked by its deletion handle.");
                    };
                    AtomicFile.WriteUtf8(target, intended, keepBackup: false);
                    Assert(blocked && File.ReadAllText(target, Encoding.UTF8) == intended,
                        $"The {mode} cleanup substitution escaped the pinned deletion handle.");
                }
                finally
                {
                    AtomicTemporaryFileOps.BeforePinnedDeleteTestHook = null;
                }
            }
        });
    }

    private static IEnumerable<string> AtomicTemporaryFiles(string root, string target) =>
        Directory.EnumerateFiles(
            root,
            $".{Path.GetFileName(target)}.*.tmp",
            SearchOption.TopDirectoryOnly);
}
