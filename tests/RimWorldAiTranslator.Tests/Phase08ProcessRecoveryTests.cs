using System.Diagnostics;
using System.Globalization;
using System.Text;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static readonly string[] Phase08RollbackCrashStages = ["plan", "ready", "applied", "done"];
    private static readonly int[] Phase08RollbackCrashOrdinals = [1, 2];
    private static readonly string[] Phase08ForwardCrashStages = ["target", "backup"];
    private static readonly string[] Phase08PublicationCrashStages =
        ["base", "snapshot-ready", "prepared", "reservation", "intent", "resolved", "cleanup"];
    private static readonly string[] Phase08StrictJsonCorruptions = ["duplicate", "unknown"];
    private static readonly string[] Phase08RmkUnreadyModes = ["create", "copy"];

    private static void Phase08ForcedExitRecovery()
    {
        Phase08AtomicTemporaryFileRecovery();
        Phase08InterruptedSnapshotCaptureRecovery();
        Phase08InterruptedMultiFileTransactionRecovery();
        Phase08ProductionServiceRestartRecovery();
        Phase08InterruptedResolvedCleanupRecovery();
        Phase08InterruptedRollbackStagesResume();
        Phase08InterruptedRollbackCopyResumes();
        Phase08ForwardDisplacementRecovery();
        Phase08UnreadyPreparedIsPreserved();
        Phase08RmkUnreadyPreparedIsPreserved();
        Phase08ArtifactPublicationAmbiguityRecovery();
        Phase08UncommittedIntentCannotResolveAsSuccess();
        Phase08InterruptedTransactionPreservesConcurrentIdentity();
        Phase08TargetLocalForgedManifestIsNeverExecuted();
        Phase08AuthorityManifestPathEscapeFailsClosed();
        Phase08AuthorityManifestTruncationFailsClosed();
        Phase08AuthorityIntentTruncationFailsClosed();
        Phase08AuthorityStrictJsonFailsClosed();
        Phase08RecoveryAuthorityMustNotOverlapTarget();
        Phase08LinearIntentJournalStructure();
    }

    private static void Phase08ProductionServiceRestartRecovery()
    {
        Phase08RmkServiceRestartRecovery();
        Phase08RmkBuilderServiceRestartRecovery();
        Phase08RmkBuilderArtifactPublicationRestartRecovery();
        Phase08TranslationServiceRestartRecovery();
        Phase08InvalidExternalMutationLeavesFailClosed();
    }

    private static void Phase08RmkServiceRestartRecovery()
    {
        WithTempRoot(root =>
        {
            var workspace = CreateRmkWorkspace(root, "Phase08CrashTarget", out _);
            var authorityRoot = Path.Combine(root, "rmk-service-authority");
            var marker = Path.Combine(root, "rmk-service-committed.marker");
            Directory.CreateDirectory(authorityRoot);
            var project = Phase08TargetProject();
            var yamlPath = Path.Combine(
                Phase08TargetEntryRoot(workspace, project),
                "1.6",
                "LoadFolders.Build.yaml");

            using var child = StartSelfProcess(
                "--phase08-rmk-service-crash-child",
                workspace,
                authorityRoot,
                marker);
            try
            {
                WaitForFile(marker, child, TimeSpan.FromSeconds(15));
                child.Kill(entireProcessTree: true);
                Assert(child.WaitForExit(15_000),
                    "The recovery-enabled RMK service child did not terminate after Kill.");
                Assert(child.ExitCode != 0,
                    "The force-killed RMK service child reported success.");
            }
            finally
            {
                KillIfRunning(child);
            }

            Assert(File.Exists(yamlPath)
                   && RecoveryTransactionDirectories(authorityRoot).Length == 1,
                "The RMK service crash fixture did not leave a committed leaf with trusted recovery evidence.");

            var recovered = new RmkWorkspaceService(
                    new FileTransactionRecoveryAuthority(authorityRoot))
                .CreateTarget(workspace, project, "1.6");

            Assert(recovered.YamlPath.Equals(yamlPath, StringComparison.OrdinalIgnoreCase)
                   && File.Exists(yamlPath)
                   && RecoveryTransactionDirectories(authorityRoot).Length == 0,
                "Restarting the recovery-enabled RMK service did not converge to one complete target without authority residue.");
        });
    }

    private static void Phase08TranslationServiceRestartRecovery()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var fixtureRoot = Path.GetDirectoryName(modRoot)
                ?? throw new InvalidDataException("The translation crash fixture has no parent directory.");
            var reviewRoot = Path.Combine(fixtureRoot, "phase08-crash-reviews");
            var authorityRoot = Path.Combine(fixtureRoot, "translation-service-authority");
            var marker = Path.Combine(fixtureRoot, "translation-service-committed.marker");
            Directory.CreateDirectory(authorityRoot);

            using var child = StartSelfProcess(
                "--phase08-translation-service-crash-child",
                modRoot,
                reviewRoot,
                authorityRoot,
                marker);
            try
            {
                WaitForFile(marker, child, TimeSpan.FromSeconds(15));
                child.Kill(entireProcessTree: true);
                Assert(child.WaitForExit(15_000),
                    "The recovery-enabled translation service child did not terminate after Kill.");
                Assert(child.ExitCode != 0,
                    "The force-killed translation service child reported success.");
            }
            finally
            {
                KillIfRunning(child);
            }

            var interruptedSource = Directory.EnumerateFiles(
                    reviewRoot,
                    "*-source.json",
                    SearchOption.AllDirectories)
                .Single();
            Assert(RecoveryTransactionDirectories(authorityRoot).Length == 1,
                "The translation service crash fixture did not leave trusted recovery evidence.");

            var authority = new FileTransactionRecoveryAuthority(authorityRoot);
            var result = new TranslationEngine(
                    RepositoryRoot(),
                    CreateExtractor(),
                    recoveryAuthority: authority)
                .RunAsync(new TranslationEngineOptions
                {
                    ModRoot = modRoot,
                    ReviewRoot = reviewRoot,
                    ReviewOnly = true,
                    SourceOnly = true,
                    SourceLanguageFolder = "English"
                })
                .GetAwaiter()
                .GetResult();

            Assert(!File.Exists(interruptedSource),
                "Restart recovery retained the translation leaf committed by the interrupted transaction.");
            Assert(result.ComparisonFile is not null
                   && File.Exists(result.ComparisonFile)
                   && RecoveryTransactionDirectories(authorityRoot).Length == 0,
                "Restarting the recovery-enabled translation service did not converge to one complete review without authority residue.");
        });
    }

    private static void Phase08RmkBuilderServiceRestartRecovery()
    {
        WithTempRoot(root =>
        {
            var workspace = CreateRmkWorkspace(root, "Phase08CrashBuilder", out _);
            var authorityRoot = Path.Combine(root, "rmk-builder-service-authority");
            var marker = Path.Combine(root, "rmk-builder-first-output.marker");
            var loadFolders = Path.Combine(workspace, "LoadFolders.xml");
            var modList = Path.Combine(workspace, "ModList.tsv");
            const string originalLoadFolders = "<loadFolders><restart-original /></loadFolders>";
            const string originalModList = "restart\toriginal\tData/restart\tfixture.restart";
            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            PreparePhase08RmkBuilderStageInputs(workspace);
            InstallRmkBuilderFixture(workspace);
            File.WriteAllText(
                Path.Combine(workspace, "builder-fixture-mode.txt"),
                "pause-after-first",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(workspace, "builder-pause-marker-path.txt"),
                marker,
                new UTF8Encoding(false));
            Directory.CreateDirectory(authorityRoot);

            using var child = StartSelfProcess(
                "--phase08-rmk-builder-service-crash-child",
                workspace,
                authorityRoot,
                marker);
            try
            {
                WaitForFile(marker, child, TimeSpan.FromSeconds(15));
                child.Kill(entireProcessTree: true);
                Assert(child.WaitForExit(15_000),
                    "The recovery-enabled RMK Builder service child did not terminate after Kill.");
                Assert(child.ExitCode != 0,
                    "The force-killed RMK Builder service child reported success.");
            }
            finally
            {
                KillIfRunning(child);
            }

            var stagingRoot = Phase08RmkBuilderStagingRoot(authorityRoot);
            Assert(File.ReadAllText(loadFolders) == originalLoadFolders
                   && File.ReadAllText(modList) == originalModList
                   && RecoveryTransactionDirectories(authorityRoot).Length == 0
                   && Directory.Exists(stagingRoot)
                   && Directory.EnumerateDirectories(
                           stagingRoot,
                           "run-*",
                           SearchOption.TopDirectoryOnly)
                       .Any(),
                "A crash while the isolated Builder was producing its first staged output changed the live pair or lost the abandoned-stage fixture.");

            File.WriteAllText(
                Path.Combine(workspace, "builder-fixture-mode.txt"),
                "success",
                new UTF8Encoding(false));
            var service = new RmkWorkspaceService(
                new FileTransactionRecoveryAuthority(authorityRoot));
            _ = RunPhase08RmkBuilderWithStageFixture(service, workspace);

            Assert(File.ReadAllText(loadFolders).Contains("generated", StringComparison.Ordinal)
                   && File.ReadAllText(modList).Contains("fixture.generated", StringComparison.Ordinal)
                   && RecoveryTransactionDirectories(authorityRoot).Length == 0
                   && !Directory.EnumerateDirectories(
                           stagingRoot,
                           "run-*",
                           SearchOption.TopDirectoryOnly)
                       .Any()
                   && !Directory.EnumerateFiles(
                           stagingRoot,
                           "*.owner",
                           SearchOption.TopDirectoryOnly)
                       .Any(),
                "Restarting after an isolated Builder crash did not clean the exact abandoned stage and publish one coherent generated pair.");
        });
    }

    private static void Phase08RmkBuilderArtifactPublicationRestartRecovery()
    {
        foreach (var stage in new[] { "stage", "prepared" })
        {
            WithTempRoot(root =>
            {
                var workspace = CreateRmkWorkspace(root, $"Phase08BuilderArtifact-{stage}", out _);
                var authorityRoot = Path.Combine(root, $"rmk-builder-{stage}-authority");
                var marker = Path.Combine(root, $"rmk-builder-{stage}.marker");
                var loadFolders = Path.Combine(workspace, "LoadFolders.xml");
                var modList = Path.Combine(workspace, "ModList.tsv");
                const string originalLoadFolders = "<loadFolders><artifact-original /></loadFolders>";
                const string originalModList = "artifact\toriginal\tData/artifact\tfixture.artifact";
                File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
                File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
                PreparePhase08RmkBuilderStageInputs(workspace);
                InstallRmkBuilderFixture(workspace);
                File.WriteAllText(
                    Path.Combine(workspace, "builder-fixture-mode.txt"),
                    "success",
                    new UTF8Encoding(false));
                Directory.CreateDirectory(authorityRoot);

                using var child = StartSelfProcess(
                    "--phase08-rmk-builder-artifact-crash-child",
                    workspace,
                    authorityRoot,
                    marker,
                    stage);
                try
                {
                    WaitForFile(marker, child, TimeSpan.FromSeconds(15));
                    child.Kill(entireProcessTree: true);
                    Assert(child.WaitForExit(15_000),
                        $"The RMK Builder {stage} publication child did not terminate after Kill.");
                    Assert(child.ExitCode != 0,
                        $"The force-killed RMK Builder {stage} publication child reported success.");
                }
                finally
                {
                    KillIfRunning(child);
                }

                var expectedTransactions = stage.Equals("prepared", StringComparison.Ordinal) ? 1 : 0;
                Assert(File.ReadAllText(loadFolders) == originalLoadFolders
                       && File.ReadAllText(modList) == originalModList
                       && RecoveryTransactionDirectories(authorityRoot).Length == expectedTransactions,
                    stage.Equals("prepared", StringComparison.Ordinal)
                        ? "The first prepared Builder publication changed a live index or did not retain exactly one durable transaction."
                        : "Stopping after staged validation but before publication changed the live pair or created a target transaction.");

                var authority = new FileTransactionRecoveryAuthority(authorityRoot);
                authority.RecoverPending(workspace);
                Assert(File.ReadAllText(loadFolders) == originalLoadFolders
                       && File.ReadAllText(modList) == originalModList
                       && RecoveryTransactionDirectories(authorityRoot).Length == 0,
                    $"Restart recovery after the Builder {stage} publication stop did not converge to the coherent original pair.");

                var service = new RmkWorkspaceService(authority);
                _ = RunPhase08RmkBuilderWithStageFixture(service, workspace);
                Assert(File.ReadAllText(loadFolders).Contains("generated", StringComparison.Ordinal)
                       && File.ReadAllText(modList).Contains("fixture.generated", StringComparison.Ordinal)
                       && RecoveryTransactionDirectories(authorityRoot).Length == 0
                       && !Directory.EnumerateDirectories(
                               Phase08RmkBuilderStagingRoot(authorityRoot),
                               "run-*",
                               SearchOption.TopDirectoryOnly)
                           .Any()
                       && !Directory.EnumerateFiles(
                               Phase08RmkBuilderStagingRoot(authorityRoot),
                               "*.owner",
                               SearchOption.TopDirectoryOnly)
                           .Any(),
                    $"A normal Builder run after the {stage} publication recovery did not publish a coherent pair without residue.");
            });
        }
    }

    private static void Phase08InvalidExternalMutationLeavesFailClosed()
    {
        foreach (var mode in new[] { "oversize", "hardlink", "reparse" })
        {
            WithTempRoot(root =>
            {
                var targetRoot = Path.Combine(root, $"external-{mode}-target");
                var authorityRoot = Path.Combine(root, $"external-{mode}-authority");
                Directory.CreateDirectory(targetRoot);
                Directory.CreateDirectory(authorityRoot);
                var first = Path.Combine(targetRoot, "LoadFolders.xml");
                var second = Path.Combine(targetRoot, "ModList.tsv");
                var sentinel = Path.Combine(root, $"external-{mode}-sentinel.txt");
                File.WriteAllText(first, "initial-one", new UTF8Encoding(false));
                File.WriteAllText(second, "initial-two", new UTF8Encoding(false));
                File.WriteAllText(sentinel, "outside-sentinel", new UTF8Encoding(false));
                var authority = new FileTransactionRecoveryAuthority(authorityRoot);

                Exception? operationFailure = null;
                try
                {
                    using var session = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
                    FileTransaction.Execute(
                        [first, second],
                        () =>
                        {
                            session.AuthorizeExternalMutations(
                                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                                {
                                    [first] = 16,
                                    [second] = 16
                                });
                            if (mode.Equals("oversize", StringComparison.Ordinal))
                            {
                                File.WriteAllBytes(first, new byte[17]);
                            }
                            else if (mode.Equals("hardlink", StringComparison.Ordinal))
                            {
                                File.Delete(first);
                                CreateHardLinkOrThrow(first, sentinel);
                            }
                            else
                            {
                                File.Delete(first);
                                File.CreateSymbolicLink(first, sentinel);
                            }
                            throw new IOException("Synthetic invalid external mutation interruption.");
                        },
                        $"Phase 08 invalid external {mode}",
                        () => { },
                        session,
                        CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException
                                                  or InvalidDataException)
                {
                    operationFailure = exception;
                }

                Assert(operationFailure is not null
                       && RecoveryTransactionDirectories(authorityRoot).Length == 1,
                    $"An invalid external {mode} leaf was rolled back or lost its recovery evidence.");
                Assert(File.ReadAllText(sentinel, Encoding.UTF8) == "outside-sentinel",
                    $"Invalid external {mode} recovery modified the outside sentinel.");
                if (mode.Equals("oversize", StringComparison.Ordinal))
                {
                    Assert(new FileInfo(first).Length == 17,
                        "Oversized external output was destructively replaced during fail-closed recovery.");
                }
                else if (mode.Equals("hardlink", StringComparison.Ordinal))
                {
                    Assert(File.Exists(first) && File.ReadAllText(first, Encoding.UTF8) == "outside-sentinel",
                        "A multiply-linked external output was destructively replaced during fail-closed recovery.");
                }
                else
                {
                    Assert(File.Exists(first)
                           && (File.GetAttributes(first) & FileAttributes.ReparsePoint) != 0,
                        "A reparse external output was destructively replaced during fail-closed recovery.");
                }

                AssertThrows<Exception>(() => authority.RecoverPending(targetRoot));
                Assert(RecoveryTransactionDirectories(authorityRoot).Length == 1,
                    $"A restart recovery attempt discarded invalid external {mode} evidence.");
            });
        }
    }

    private static void Phase08AtomicTemporaryFileRecovery()
    {
        WithTempRoot(root =>
        {
            var target = Path.Combine(root, "synthetic-state.txt");
            var marker = Path.Combine(root, "child-temp-written.marker");
            AtomicFile.WriteUtf8(target, "version-one");
            AtomicFile.WriteUtf8(target, "version-two");
            Assert(File.ReadAllText(target, Encoding.UTF8) == "version-two"
                   && File.ReadAllText(target + ".bak", Encoding.UTF8) == "version-one",
                "The forced-exit fixture did not establish its last-good file and backup.");

            using var child = StartSelfProcess("--phase08-atomic-crash-child", target, marker);
            try
            {
                WaitForFile(marker, child, TimeSpan.FromSeconds(15));
                child.Kill(entireProcessTree: true);
                Assert(child.WaitForExit(15_000), "The forced-exit child did not terminate after Kill.");
                Assert(child.ExitCode != 0, "The force-killed child reported a successful exit code.");
            }
            finally
            {
                KillIfRunning(child);
            }

            Assert(File.ReadAllText(target, Encoding.UTF8) == "version-two"
                   && File.ReadAllText(target + ".bak", Encoding.UTF8) == "version-one",
                "A forced exit before flush changed the last-good file or its backup.");
            var orphan = Directory.EnumerateFiles(
                    root,
                    $".{Path.GetFileName(target)}.*.tmp",
                    SearchOption.TopDirectoryOnly)
                .Single();
            Assert(File.Exists(orphan),
                "The crash fixture did not leave the expected uncommitted temporary file.");

            AtomicFile.WriteUtf8(target, "version-three");
            Assert(File.ReadAllText(target, Encoding.UTF8) == "version-three"
                   && File.ReadAllText(target + ".bak", Encoding.UTF8) == "version-two",
                "A normal restart write did not recover safely after the interrupted temporary write.");
            Assert(File.Exists(orphan),
                "A filename-only dead-PID heuristic deleted an unproven user-tree sibling.");
        });
    }

    private static void Phase08InterruptedSnapshotCaptureRecovery()
    {
        WithTempRoot(root =>
        {
            var targetRoot = Path.Combine(root, "capture-target");
            var authorityRoot = Path.Combine(root, "capture-authority");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(authorityRoot);
            var first = Path.Combine(targetRoot, "first.txt");
            var second = Path.Combine(targetRoot, "second.txt");
            var marker = Path.Combine(root, "snapshot-ready.marker");
            File.WriteAllText(first, "original-first", new UTF8Encoding(false));
            File.WriteAllText(second, "original-second", new UTF8Encoding(false));

            using var child = StartSelfProcess(
                "--phase08-snapshot-crash-child",
                targetRoot,
                authorityRoot,
                marker);
            try
            {
                WaitForFile(marker, child, TimeSpan.FromSeconds(15));
                child.Kill(entireProcessTree: true);
                Assert(child.WaitForExit(15_000),
                    "The snapshot-capture child did not terminate after Kill.");
                Assert(child.ExitCode != 0,
                    "The force-killed snapshot-capture child reported success.");
            }
            finally
            {
                KillIfRunning(child);
            }

            Assert(File.ReadAllText(first, Encoding.UTF8) == "original-first"
                   && File.ReadAllText(second, Encoding.UTF8) == "original-second",
                "An interrupted snapshot capture changed a transaction target.");
            Assert(RecoveryTransactionDirectories(authorityRoot).Length == 1,
                "The interrupted snapshot capture was not recognized by the authority.");
            Assert(Directory.EnumerateFiles(authorityRoot, "*.tmp", SearchOption.AllDirectories).Any(),
                "The snapshot-capture fixture did not leave its PID-owned atomic temporary artifact.");

            new FileTransactionRecoveryAuthority(authorityRoot).RecoverPending(targetRoot);

            Assert(File.ReadAllText(first, Encoding.UTF8) == "original-first"
                   && File.ReadAllText(second, Encoding.UTF8) == "original-second",
                "Incomplete-capture recovery changed a target instead of cleaning authority evidence only.");
            Assert(RecoveryTransactionDirectories(authorityRoot).Length == 0
                   && !Directory.EnumerateFiles(authorityRoot, "*.tmp", SearchOption.AllDirectories).Any(),
                "Incomplete-capture recovery left a transaction record or exited-owner temporary file.");
        });
    }

    private static void Phase08InterruptedMultiFileTransactionRecovery()
    {
        WithTempRoot(root =>
        {
            var fixture = CreateInterruptedTransaction(root, "recover");
            Assert(File.ReadAllText(fixture.First, Encoding.UTF8) == "transaction-first"
                   && File.ReadAllText(fixture.Second, Encoding.UTF8) == "original-second",
                "The forced-exit fixture did not leave the expected partial multi-file commit.");
            Assert(File.ReadAllText(fixture.First + ".bak", Encoding.UTF8) == "original-first",
                "The forced-exit fixture did not retain the first target's deterministic backup.");
            Assert(RecoveryTransactionDirectories(fixture.AuthorityRoot).Length == 1,
                "The interrupted transaction did not leave trusted durable recovery evidence.");
            Assert(!Directory.EnumerateFiles(
                    fixture.TargetRoot,
                    ".rimworld-ai-translator-transaction-*",
                    SearchOption.AllDirectories).Any(),
                "Durable recovery wrote a manifest into the user-controlled target tree.");

            new FileTransactionRecoveryAuthority(fixture.AuthorityRoot)
                .RecoverPending(fixture.TargetRoot);

            Assert(File.ReadAllText(fixture.First, Encoding.UTF8) == "original-first"
                   && File.ReadAllText(fixture.Second, Encoding.UTF8) == "original-second",
                "Restart recovery did not restore the full last-good multi-file state.");
            Assert(!File.Exists(fixture.First + ".bak")
                   && !File.Exists(fixture.Second + ".bak"),
                "Restart recovery did not restore the original absent-backup state.");
            Assert(RecoveryTransactionDirectories(fixture.AuthorityRoot).Length == 0,
                "Successful restart recovery left a trusted transaction record.");
        });
    }

    private static void Phase08InterruptedResolvedCleanupRecovery()
    {
        WithTempRoot(root =>
        {
            var targetRoot = Path.Combine(root, "cleanup-target");
            var authorityRoot = Path.Combine(root, "cleanup-authority");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(authorityRoot);
            var target = Path.Combine(targetRoot, "state.txt");
            var marker = Path.Combine(root, "cleanup-interrupted.marker");
            File.WriteAllText(target, "last-good", new UTF8Encoding(false));

            using var child = StartSelfProcess(
                "--phase08-cleanup-crash-child",
                targetRoot,
                authorityRoot,
                marker);
            try
            {
                WaitForFile(marker, child, TimeSpan.FromSeconds(15));
                child.Kill(entireProcessTree: true);
                Assert(child.WaitForExit(15_000),
                    "The resolved-cleanup child did not terminate after Kill.");
                Assert(child.ExitCode != 0,
                    "The force-killed resolved-cleanup child reported success.");
            }
            finally
            {
                KillIfRunning(child);
            }

            Assert(File.ReadAllText(target, Encoding.UTF8) == "committed"
                   && File.ReadAllText(target + ".bak", Encoding.UTF8) == "last-good",
                "The resolved-cleanup fixture did not preserve the committed target state.");
            Assert(FindSingleAuthorityArtifact(authorityRoot, "cleanup.json") is not null,
                "Cleanup interruption did not leave its durable cleanup seal.");

            new FileTransactionRecoveryAuthority(authorityRoot).RecoverPending(targetRoot);

            Assert(File.ReadAllText(target, Encoding.UTF8) == "committed"
                   && File.ReadAllText(target + ".bak", Encoding.UTF8) == "last-good",
                "Restartable cleanup rolled back or changed an already resolved transaction.");
            Assert(RecoveryTransactionDirectories(authorityRoot).Length == 0,
                "Restartable cleanup left authority transaction residue.");
        });
    }

    private static void Phase08UncommittedIntentCannotResolveAsSuccess()
    {
        WithTempRoot(root =>
        {
            var targetRoot = Path.Combine(root, "uncommitted-target");
            var authorityRoot = Path.Combine(root, "uncommitted-authority");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(authorityRoot);
            var target = Path.Combine(targetRoot, "state.txt");
            File.WriteAllText(target, "initial", new UTF8Encoding(false));

            var authority = new FileTransactionRecoveryAuthority(authorityRoot);
            try
            {
                using var session = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
                var error = CaptureException<IOException>(() =>
                    FileTransaction.Execute(
                        [target],
                        () =>
                        {
                            var plan = FileTransactionRecoverySession.ReservePreparedWrite(
                                target,
                                keepBackup: true)
                                ?? throw new InvalidOperationException("Durable reservation was not created.");
                            File.WriteAllText(plan.PreparedPath, "never-committed", new UTF8Encoding(false));
                            FileTransactionRecoverySession.MarkPreparedWriteReady(plan);
                        },
                        "Phase 08 uncommitted intent invariant",
                        () => { },
                        session,
                        CancellationToken.None));
                Assert(error.InnerException is ConcurrentLeafChangeException,
                    "An uncommitted durable intent was not rejected by the final-state invariant.");
            }
            finally { }

            Assert(File.ReadAllText(target, Encoding.UTF8) == "initial"
                   && !File.Exists(target + ".bak"),
                "Rejected uncommitted intent did not retain the immutable initial target state.");
            Assert(RecoveryTransactionDirectories(authorityRoot).Length == 1,
                "Final-state mismatch did not preserve its authority evidence for fail-closed recovery.");

            authority.RecoverPending(targetRoot);

            Assert(File.ReadAllText(target, Encoding.UTF8) == "initial"
                   && RecoveryTransactionDirectories(authorityRoot).Length == 0,
                "Restart recovery did not safely resolve the preserved uncommitted-intent evidence.");
        });
    }

    private static void Phase08InterruptedTransactionPreservesConcurrentIdentity()
    {
        WithTempRoot(root =>
        {
            var fixture = CreateInterruptedTransaction(root, "concurrent");
            var replacement = Path.Combine(fixture.TargetRoot, "same-content-user-replacement.tmp");
            File.WriteAllText(replacement, "transaction-first", new UTF8Encoding(false));
            File.Move(replacement, fixture.First, overwrite: true);

            var error = CaptureException<ConcurrentLeafChangeException>(() =>
                new FileTransactionRecoveryAuthority(fixture.AuthorityRoot)
                    .RecoverPending(fixture.TargetRoot));

            Assert(error.Message.Contains("preserved", StringComparison.OrdinalIgnoreCase),
                "Concurrent restart recovery did not explain that recovery evidence was preserved.");
            Assert(File.ReadAllText(fixture.First, Encoding.UTF8) == "transaction-first",
                "Restart recovery overwrote an identity-distinct concurrent file with matching bytes.");
            Assert(RecoveryTransactionDirectories(fixture.AuthorityRoot).Length == 1,
                "Concurrent restart recovery discarded its trusted recovery evidence.");
        });
    }

    private static void Phase08TargetLocalForgedManifestIsNeverExecuted()
    {
        WithTempRoot(root =>
        {
            var targetRoot = Path.Combine(root, "forged-target");
            var authorityRoot = Path.Combine(root, "forged-authority");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(authorityRoot);
            var sentinel = Path.Combine(targetRoot, "sentinel.txt");
            File.WriteAllText(sentinel, "do-not-delete", new UTF8Encoding(false));
            var forged = Path.Combine(
                targetRoot,
                ".rimworld-ai-translator-transaction-11111111111111111111111111111111.json");
            File.WriteAllText(
                forged,
                "{\"Version\":1,\"State\":\"prepared\",\"Entries\":[{\"RelativePath\":\"sentinel.txt\",\"InitialState\":{\"Kind\":0},\"AllowedCurrentStates\":[{\"Kind\":1,\"Length\":13}]}]}",
                new UTF8Encoding(false));

            new FileTransactionRecoveryAuthority(authorityRoot).RecoverPending(targetRoot);

            Assert(File.ReadAllText(sentinel, Encoding.UTF8) == "do-not-delete",
                "A forged target-local Initial=Missing manifest deleted the sentinel.");
            Assert(File.Exists(forged),
                "Recovery inspected or cleaned an unsigned manifest in the target tree.");
        });
    }

    private static void Phase08AuthorityManifestPathEscapeFailsClosed()
    {
        WithTempRoot(root =>
        {
            var fixture = CreateInterruptedTransaction(root, "base-escape");
            var outside = Path.Combine(root, "outside-sentinel.txt");
            File.WriteAllText(outside, "outside-safe", new UTF8Encoding(false));
            var basePath = FindSingleAuthorityArtifact(fixture.AuthorityRoot, "base.json");
            var json = File.ReadAllText(basePath, Encoding.UTF8);
            var tampered = json.Replace(
                "\"RelativePath\":\"first.txt\"",
                "\"RelativePath\":\"..\\\\outside-sentinel.txt\"",
                StringComparison.Ordinal);
            Assert(!tampered.Equals(json, StringComparison.Ordinal),
                "The authority base fixture did not contain the expected relative path.");
            File.WriteAllText(basePath, tampered, new UTF8Encoding(false));

            AssertThrows<InvalidDataException>(() =>
                new FileTransactionRecoveryAuthority(fixture.AuthorityRoot)
                    .RecoverPending(fixture.TargetRoot));

            Assert(File.ReadAllText(outside, Encoding.UTF8) == "outside-safe",
                "A path-escaped authority base modified data outside its target root.");
            AssertPartialTransactionUnchanged(fixture,
                "A path-escaped authority base performed a partial rollback.");
        });
    }

    private static void Phase08AuthorityManifestTruncationFailsClosed()
    {
        WithTempRoot(root =>
        {
            var fixture = CreateInterruptedTransaction(root, "base-truncated");
            var basePath = FindSingleAuthorityArtifact(fixture.AuthorityRoot, "base.json");
            TruncateHalf(basePath);

            AssertThrows<InvalidDataException>(() =>
                new FileTransactionRecoveryAuthority(fixture.AuthorityRoot)
                    .RecoverPending(fixture.TargetRoot));

            AssertPartialTransactionUnchanged(fixture,
                "A truncated authority base performed a partial rollback.");
            Assert(File.Exists(basePath),
                "A malformed authority base was discarded instead of failing closed.");
        });
    }

    private static void Phase08AuthorityIntentTruncationFailsClosed()
    {
        WithTempRoot(root =>
        {
            var fixture = CreateInterruptedTransaction(root, "intent-truncated");
            var intentPath = FindSingleAuthorityArtifact(fixture.AuthorityRoot, "intent-*.json");
            TruncateHalf(intentPath);

            AssertThrows<InvalidDataException>(() =>
                new FileTransactionRecoveryAuthority(fixture.AuthorityRoot)
                    .RecoverPending(fixture.TargetRoot));

            AssertPartialTransactionUnchanged(fixture,
                "A truncated authority intent performed a partial rollback.");
            Assert(File.Exists(intentPath),
                "A malformed authority intent was discarded instead of failing closed.");
        });
    }

    private static void Phase08AuthorityStrictJsonFailsClosed()
    {
        WithTempRoot(root =>
        {
            foreach (var corruption in Phase08StrictJsonCorruptions)
            {
                var fixture = CreateInterruptedTransaction(root, "base-" + corruption);
                var basePath = FindSingleAuthorityArtifact(fixture.AuthorityRoot, "base.json");
                var json = File.ReadAllText(basePath, Encoding.UTF8);
                Assert(json.StartsWith('{'), "The strict-JSON fixture was not an object document.");
                var injected = corruption == "duplicate"
                    ? json.Insert(1, "\"Version\":3,")
                    : json.Insert(1, "\"UnexpectedRecoveryField\":1,");
                File.WriteAllText(basePath, injected, new UTF8Encoding(false));

                AssertThrows<InvalidDataException>(() =>
                    new FileTransactionRecoveryAuthority(fixture.AuthorityRoot)
                        .RecoverPending(fixture.TargetRoot));
                AssertPartialTransactionUnchanged(
                    fixture,
                    $"A recovery base with a {corruption} property performed a partial rollback.");
                Assert(File.Exists(basePath),
                    $"A recovery base with a {corruption} property was discarded instead of failing closed.");
            }
        });
    }

    private static void Phase08RecoveryAuthorityMustNotOverlapTarget()
    {
        WithTempRoot(root =>
        {
            var targetRoot = Path.Combine(root, "overlap-target");
            var authorityRoot = Path.Combine(targetRoot, "authority");
            Directory.CreateDirectory(authorityRoot);
            var authority = new FileTransactionRecoveryAuthority(authorityRoot);

            AssertThrows<InvalidDataException>(() => authority.RecoverPending(targetRoot));
        });
    }

    private static void Phase08LinearIntentJournalStructure()
    {
        WithTempRoot(root =>
        {
            const int targetCount = 1_000;
            var targetRoot = Path.Combine(root, "linear-target");
            var authorityRoot = Path.Combine(root, "linear-authority");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(authorityRoot);
            var paths = Enumerable.Range(0, targetCount)
                .Select(index => Path.Combine(targetRoot, $"leaf-{index:D4}.txt"))
                .ToArray();
            var baseWrites = 0;
            var intentWrites = 0;
            FileTransactionRecoverySession.BaseManifestPublishedTestHook = _ => baseWrites++;
            FileTransactionRecoverySession.IntentPublishedTestHook = _ => intentWrites++;
            var watch = Stopwatch.StartNew();
            try
            {
                var authority = new FileTransactionRecoveryAuthority(authorityRoot);
                using var session = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
                FileTransaction.Execute(
                    paths,
                    () =>
                    {
                        foreach (var path in paths) AtomicFile.WriteUtf8(path, "x");
                    },
                    "Phase 08 linear recovery journal",
                    () => { },
                    session,
                    CancellationToken.None);
            }
            finally
            {
                watch.Stop();
                FileTransactionRecoverySession.BaseManifestPublishedTestHook = null;
                FileTransactionRecoverySession.IntentPublishedTestHook = null;
            }

            Assert(baseWrites == 1,
                $"The 1,000-target transaction published its immutable base {baseWrites} times instead of once.");
            Assert(intentWrites == targetCount,
                $"The 1,000-target transaction published {intentWrites} intents instead of {targetCount}.");
            Assert(watch.Elapsed < TimeSpan.FromSeconds(60),
                $"The 1,000-target O(n) journal evidence exceeded its 60-second bound ({watch.Elapsed}).");
            Assert(RecoveryTransactionDirectories(authorityRoot).Length == 0,
                "The completed 1,000-target transaction left a recovery record.");
        });
    }

    private static void Phase08InterruptedRollbackStagesResume()
    {
        WithTempRoot(root =>
        {
            foreach (var stage in Phase08RollbackCrashStages)
            {
                foreach (var ordinal in Phase08RollbackCrashOrdinals)
                {
                    var name = $"rollback-{stage}-{ordinal.ToString(CultureInfo.InvariantCulture)}";
                    var fixture = CreateInterruptedTransaction(root, name);
                    var marker = Path.Combine(root, name + ".marker");
                    using var child = StartSelfProcess(
                        "--phase08-recovery-stage-child",
                        fixture.TargetRoot,
                        fixture.AuthorityRoot,
                        marker,
                        stage,
                        ordinal.ToString(CultureInfo.InvariantCulture));
                    KillChildAtMarker(child, marker, "rollback " + stage);

                    var authority = new FileTransactionRecoveryAuthority(fixture.AuthorityRoot);
                    authority.RecoverPending(fixture.TargetRoot);
                    authority.RecoverPending(fixture.TargetRoot);
                    Assert(File.ReadAllText(fixture.First, Encoding.UTF8) == "original-first"
                           && File.ReadAllText(fixture.Second, Encoding.UTF8) == "original-second"
                           && !File.Exists(fixture.First + ".bak")
                           && !File.Exists(fixture.Second + ".bak"),
                        $"Rollback {stage}/{ordinal} did not resume to the exact initial boundary.");
                    Assert(RecoveryTransactionDirectories(fixture.AuthorityRoot).Length == 0,
                        $"Rollback {stage}/{ordinal} left authority residue after two restarts.");
                    Assert(!Directory.EnumerateFiles(
                            fixture.TargetRoot,
                            "*rtx-*",
                            SearchOption.TopDirectoryOnly).Any(),
                        $"Rollback {stage}/{ordinal} left a known target-tree intermediate.");
                }
            }
        });
    }

    private static void Phase08ForwardDisplacementRecovery()
    {
        WithTempRoot(root =>
        {
            foreach (var stage in Phase08ForwardCrashStages)
            {
                var targetRoot = Path.Combine(root, stage + "-target");
                var authorityRoot = Path.Combine(root, stage + "-authority");
                Directory.CreateDirectory(targetRoot);
                Directory.CreateDirectory(authorityRoot);
                var target = Path.Combine(targetRoot, "state.txt");
                File.WriteAllText(target, "original-target", new UTF8Encoding(false));
                if (stage == "backup")
                    File.WriteAllText(target + ".bak", "original-backup", new UTF8Encoding(false));
                var marker = Path.Combine(root, stage + "-displaced.marker");
                using var child = StartSelfProcess(
                    "--phase08-forward-stage-child",
                    targetRoot,
                    authorityRoot,
                    marker,
                    stage);
                KillChildAtMarker(child, marker, "forward " + stage + " displacement");

                var authority = new FileTransactionRecoveryAuthority(authorityRoot);
                authority.RecoverPending(targetRoot);
                authority.RecoverPending(targetRoot);
                Assert(File.ReadAllText(target, Encoding.UTF8) == "original-target",
                    $"Forward {stage} displacement did not restore the target.");
                Assert(stage == "backup"
                        ? File.ReadAllText(target + ".bak", Encoding.UTF8) == "original-backup"
                        : !File.Exists(target + ".bak"),
                    $"Forward {stage} displacement did not restore deterministic-backup existence/content.");
                Assert(RecoveryTransactionDirectories(authorityRoot).Length == 0
                       && !Directory.EnumerateFiles(targetRoot, "*rtx-*", SearchOption.TopDirectoryOnly).Any(),
                    $"Forward {stage} displacement left recovery residue.");
            }
        });
    }

    private static void Phase08InterruptedRollbackCopyResumes()
    {
        WithTempRoot(root =>
        {
            var fixture = CreateInterruptedTransaction(root, "rollback-copy");
            var marker = Path.Combine(root, "rollback-copy.marker");
            using var child = StartSelfProcess(
                "--phase08-recovery-stage-child",
                fixture.TargetRoot,
                fixture.AuthorityRoot,
                marker,
                "copy",
                "1");
            KillChildAtMarker(child, marker, "rollback restore copy");

            var authority = new FileTransactionRecoveryAuthority(fixture.AuthorityRoot);
            authority.RecoverPending(fixture.TargetRoot);
            authority.RecoverPending(fixture.TargetRoot);
            Assert(File.ReadAllText(fixture.First, Encoding.UTF8) == "original-first"
                   && File.ReadAllText(fixture.Second, Encoding.UTF8) == "original-second"
                   && !File.Exists(fixture.First + ".bak")
                   && !File.Exists(fixture.Second + ".bak"),
                "Rollback did not resume after termination inside restore-copy I/O.");
            Assert(RecoveryTransactionDirectories(fixture.AuthorityRoot).Length == 0
                   && !Directory.EnumerateFiles(
                       fixture.TargetRoot,
                       "*rtx-*",
                       SearchOption.TopDirectoryOnly).Any(),
                "Rollback restore-copy resumption left recovery residue.");
        });
    }

    private static void Phase08UnreadyPreparedIsPreserved()
    {
        WithTempRoot(root =>
        {
            var targetRoot = Path.Combine(root, "unready-target");
            var authorityRoot = Path.Combine(root, "unready-authority");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(authorityRoot);
            var target = Path.Combine(targetRoot, "state.txt");
            File.WriteAllText(target, "initial", new UTF8Encoding(false));
            var marker = Path.Combine(root, "unready.marker");
            using var child = StartSelfProcess(
                "--phase08-forward-stage-child",
                targetRoot,
                authorityRoot,
                marker,
                "unready");
            KillChildAtMarker(child, marker, "unready prepared write");

            var partial = Directory.EnumerateFiles(
                    targetRoot,
                    $".{Path.GetFileName(target)}.p*.tmp",
                    SearchOption.TopDirectoryOnly)
                .Single();
            var error = CaptureException<ConcurrentLeafChangeException>(() =>
                new FileTransactionRecoveryAuthority(authorityRoot).RecoverPending(targetRoot));
            Assert(error.PreservedPaths.Contains(partial, StringComparer.OrdinalIgnoreCase)
                   && File.Exists(partial)
                   && File.ReadAllText(target, Encoding.UTF8) == "initial"
                   && RecoveryTransactionDirectories(authorityRoot).Length == 1,
                "An unready partial leaf was deleted, applied, or detached from its authority evidence.");
        });
    }

    private static void Phase08ArtifactPublicationAmbiguityRecovery()
    {
        WithTempRoot(root =>
        {
            foreach (var stage in Phase08PublicationCrashStages)
            {
                var targetRoot = Path.Combine(root, stage + "-target");
                var authorityRoot = Path.Combine(root, stage + "-authority");
                Directory.CreateDirectory(targetRoot);
                Directory.CreateDirectory(authorityRoot);
                var target = Path.Combine(targetRoot, "state.txt");
                File.WriteAllText(target, "initial", new UTF8Encoding(false));
                var marker = Path.Combine(root, stage + "-publication.marker");
                using var child = StartSelfProcess(
                    "--phase08-publication-stage-child",
                    targetRoot,
                    authorityRoot,
                    marker,
                    stage);
                KillChildAtMarker(child, marker, stage + " publication");

                var authority = new FileTransactionRecoveryAuthority(authorityRoot);
                authority.RecoverPending(targetRoot);
                authority.RecoverPending(targetRoot);
                var expected = stage is "resolved" or "cleanup" ? "committed" : "initial";
                Assert(File.ReadAllText(target, Encoding.UTF8) == expected,
                    $"Ambiguous {stage} publication selected the wrong durable outcome.");
                Assert(RecoveryTransactionDirectories(authorityRoot).Length == 0,
                    $"Ambiguous {stage} publication left authority residue after restart.");
            }
        });
    }

    private static void Phase08RmkUnreadyPreparedIsPreserved()
    {
        WithTempRoot(root =>
        {
            var source = Path.Combine(root, "synthetic-source.xlsx");
            RimWorldTranslatorRmkXlsxWriter.WritePrepared(
                null,
                source,
                [CreateSyntheticRmkHistoryRow()],
                "English");
            foreach (var mode in Phase08RmkUnreadyModes)
            {
                var targetRoot = Path.Combine(root, $"rmk-unready-{mode}-target");
                var authorityRoot = Path.Combine(root, $"rmk-unready-{mode}-authority");
                Directory.CreateDirectory(targetRoot);
                Directory.CreateDirectory(authorityRoot);
                var target = Path.Combine(targetRoot, "history.xlsx");
                var initial = new UTF8Encoding(false).GetBytes("synthetic-last-good-workbook-boundary");
                File.WriteAllBytes(target, initial);
                var marker = Path.Combine(root, $"rmk-unready-{mode}.marker");
                using var child = StartSelfProcess(
                    "--phase08-rmk-unready-child",
                    targetRoot,
                    authorityRoot,
                    marker,
                    mode == "copy" ? source : "-");
                KillChildAtMarker(child, marker, $"RMK {mode} unready prepared archive");

                var partial = Directory.EnumerateFiles(
                        targetRoot,
                        $".{Path.GetFileName(target)}.p*.tmp",
                        SearchOption.TopDirectoryOnly)
                    .Single();
                var error = CaptureException<ConcurrentLeafChangeException>(() =>
                    new FileTransactionRecoveryAuthority(authorityRoot).RecoverPending(targetRoot));
                Assert(error.PreservedPaths.Contains(partial, StringComparer.OrdinalIgnoreCase)
                       && File.Exists(partial)
                       && File.ReadAllBytes(target).SequenceEqual(initial)
                       && RecoveryTransactionDirectories(authorityRoot).Length == 1,
                    $"An interrupted native RMK {mode} archive was deleted, committed, or detached from its authority evidence before ready publication.");
                Assert(Directory.EnumerateFiles(targetRoot, "*", SearchOption.TopDirectoryOnly)
                           .All(path => path.Equals(target, StringComparison.OrdinalIgnoreCase)
                                        || path.Equals(partial, StringComparison.OrdinalIgnoreCase)),
                    $"Native RMK {mode} prepared output used an untracked target-tree intermediate path.");
            }
        });
    }

    private static void KillChildAtMarker(Process child, string marker, string context)
    {
        try
        {
            WaitForFile(marker, child, TimeSpan.FromSeconds(15));
            child.Kill(entireProcessTree: true);
            Assert(child.WaitForExit(15_000), $"The {context} child did not terminate after Kill.");
            Assert(child.ExitCode != 0, $"The force-killed {context} child reported success.");
        }
        finally
        {
            KillIfRunning(child);
        }
    }

    private static int RunPhase08AtomicCrashChild(string target, string marker)
    {
        AtomicFile.BeforeTemporaryFlushTestHook = _ =>
        {
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        };
        AtomicFile.WriteUtf8(target, "uncommitted-crash-payload");
        return 41;
    }

    private static int RunPhase08SnapshotCrashChild(
        string targetRoot,
        string authorityRoot,
        string marker)
    {
        FileTransactionRecoverySession.SnapshotReadyPublishedTestHook = _ =>
        {
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        };
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        using var session = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
        FileTransaction.Execute(
            [Path.Combine(targetRoot, "first.txt"), Path.Combine(targetRoot, "second.txt")],
            () => throw new InvalidOperationException("The snapshot-capture fixture reached its action unexpectedly."),
            "Phase 08 forced-exit snapshot capture",
            () => { },
            session,
            CancellationToken.None);
        return 43;
    }

    private static int RunPhase08RmkServiceCrashChild(
        string workspaceRoot,
        string authorityRoot,
        string marker)
    {
        var project = Phase08TargetProject();
        var yamlPath = Path.Combine(
            Phase08TargetEntryRoot(workspaceRoot, project),
            "1.6",
            "LoadFolders.Build.yaml");
        PathSafety.AfterAtomicEvidencePinnedTestHook = path =>
        {
            if (!path.Equals(yamlPath, StringComparison.OrdinalIgnoreCase)) return;
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        };
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        _ = new RmkWorkspaceService(authority)
            .CreateTarget(workspaceRoot, project, "1.6");
        return 71;
    }

    private static int RunPhase08TranslationServiceCrashChild(
        string modRoot,
        string reviewRoot,
        string authorityRoot,
        string marker)
    {
        var normalizedReviewRoot = Path.GetFullPath(reviewRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        PathSafety.AfterAtomicEvidencePinnedTestHook = path =>
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(normalizedReviewRoot, StringComparison.OrdinalIgnoreCase)
                || !fullPath.EndsWith("-source.json", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        };
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        _ = new TranslationEngine(
                RepositoryRoot(),
                CreateExtractor(),
                recoveryAuthority: authority)
            .RunAsync(new TranslationEngineOptions
            {
                ModRoot = modRoot,
                ReviewRoot = reviewRoot,
                ReviewOnly = true,
                SourceOnly = true,
                SourceLanguageFolder = "English"
            })
            .GetAwaiter()
            .GetResult();
        return 72;
    }

    private static int RunPhase08RmkBuilderServiceCrashChild(
        string workspaceRoot,
        string authorityRoot,
        string marker)
    {
        var configuredMarker = File.ReadAllText(
            Path.Combine(workspaceRoot, "builder-pause-marker-path.txt"),
            Encoding.UTF8).Trim();
        if (!configuredMarker.Equals(Path.GetFullPath(marker), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RMK Builder crash marker binding changed.");
        RmkWorkspaceService.PopulateBuilderStageTestHook = PopulatePhase08RmkBuilderStageFixture;
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        var service = new RmkWorkspaceService(authority);
        _ = service.BuildAsync(service.CreateBuildPlan(workspaceRoot))
            .GetAwaiter()
            .GetResult();
        return 73;
    }

    private static int RunPhase08RmkBuilderArtifactCrashChild(
        string workspaceRoot,
        string authorityRoot,
        string marker,
        string stage)
    {
        void Stop(string _)
        {
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        }

        RmkWorkspaceService.PopulateBuilderStageTestHook = PopulatePhase08RmkBuilderStageFixture;
        if (stage.Equals("stage", StringComparison.Ordinal))
        {
            RmkWorkspaceService.BeforeBuilderOutputsPublishedTestHook = (_, _) => Stop(string.Empty);
        }
        else if (stage.Equals("prepared", StringComparison.Ordinal))
        {
            var prepared = 0;
            FileTransactionRecoverySession.PreparedReadyPublishedTestHook = path =>
            {
                if (Interlocked.Increment(ref prepared) != 1) return;
                Stop(path);
            };
        }
        else
        {
            throw new InvalidDataException("The RMK Builder publication crash stage is invalid.");
        }

        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        var service = new RmkWorkspaceService(authority);
        _ = service.BuildAsync(service.CreateBuildPlan(workspaceRoot))
            .GetAwaiter()
            .GetResult();
        return 74;
    }

    private static RmkBuildResult RunPhase08RmkBuilderWithStageFixture(
        RmkWorkspaceService service,
        string workspaceRoot)
    {
        var previous = RmkWorkspaceService.PopulateBuilderStageTestHook;
        RmkWorkspaceService.PopulateBuilderStageTestHook = PopulatePhase08RmkBuilderStageFixture;
        try
        {
            return service.BuildAsync(service.CreateBuildPlan(workspaceRoot))
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            RmkWorkspaceService.PopulateBuilderStageTestHook = previous;
        }
    }

    private static void PopulatePhase08RmkBuilderStageFixture(
        string workspaceRoot,
        string stageRoot)
    {
        foreach (var name in new[]
                 {
                     "builder-fixture-mode.txt",
                     "builder-pause-marker-path.txt"
                 })
        {
            var source = Path.Combine(workspaceRoot, name);
            if (!File.Exists(source)) continue;
            File.Copy(source, Path.Combine(stageRoot, name), overwrite: false);
        }
    }

    private static void PreparePhase08RmkBuilderStageInputs(string workspaceRoot)
    {
        var aboutRoot = Path.Combine(workspaceRoot, "About");
        Directory.CreateDirectory(aboutRoot);
        File.WriteAllText(
            Path.Combine(aboutRoot, "About.xml"),
            "<ModMetaData><supportedVersions><li>1.6</li></supportedVersions></ModMetaData>",
            new UTF8Encoding(false));
    }

    private static string Phase08RmkBuilderStagingRoot(string authorityRoot) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(authorityRoot))
            ?? throw new InvalidDataException("The RMK Builder authority has no staging parent."),
            "rmk-builder-staging");

    private static int RunPhase08TransactionCrashChild(
        string targetRoot,
        string authorityRoot,
        string marker)
    {
        var first = Path.Combine(targetRoot, "first.txt");
        var second = Path.Combine(targetRoot, "second.txt");
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        using var session = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
        FileTransaction.Execute(
            [first, second],
            () =>
            {
                AtomicFile.WriteUtf8(first, "transaction-first");
                File.WriteAllText(
                    marker,
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    new UTF8Encoding(false));
                Thread.Sleep(Timeout.Infinite);
                AtomicFile.WriteUtf8(second, "transaction-second");
            },
            "Phase 08 forced-exit multi-file transaction",
            () => { },
            session,
            CancellationToken.None);
        return 42;
    }

    private static int RunPhase08CleanupCrashChild(
        string targetRoot,
        string authorityRoot,
        string marker)
    {
        FileTransactionRecoverySession.AfterCleanupArtifactDeletedTestHook = _ =>
        {
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        };
        var target = Path.Combine(targetRoot, "state.txt");
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        using var session = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
        FileTransaction.Execute(
            [target],
            () => AtomicFile.WriteUtf8(target, "committed"),
            "Phase 08 forced-exit resolved cleanup",
            () => { },
            session,
            CancellationToken.None);
        return 44;
    }

    private static int RunPhase08RecoveryStageChild(
        string targetRoot,
        string authorityRoot,
        string marker,
        string stage,
        int ordinal)
    {
        var count = 0;
        Action<string> hook = _ =>
        {
            count++;
            if (count != ordinal) return;
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        };
        switch (stage)
        {
            case "copy":
                AtomicFile.AfterCopyChunkWrittenTestHook = hook;
                break;
            case "plan":
                FileTransactionRecoverySession.RollbackPlanPublishedTestHook = hook;
                break;
            case "ready":
                FileTransactionRecoverySession.RollbackReadyPublishedTestHook = hook;
                break;
            case "applied":
                FileTransactionRecoverySession.RollbackAppliedPublishedTestHook = hook;
                break;
            case "done":
                FileTransactionRecoverySession.RollbackDonePublishedTestHook = hook;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage));
        }
        new FileTransactionRecoveryAuthority(authorityRoot).RecoverPending(targetRoot);
        return 45;
    }

    private static int RunPhase08ForwardStageChild(
        string targetRoot,
        string authorityRoot,
        string marker,
        string stage)
    {
        void Stop(string _)
        {
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        }

        if (stage == "target") PathSafety.AfterAtomicTargetDisplacedTestHook = Stop;
        else if (stage == "backup") PathSafety.AfterAtomicBackupDisplacedTestHook = Stop;
        else if (stage == "unready") AtomicFile.BeforeTemporaryFlushTestHook = Stop;
        else throw new ArgumentOutOfRangeException(nameof(stage));

        var target = Path.Combine(targetRoot, "state.txt");
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        using var boundary = PathSafety.AcquireTrustedWriteBoundary(targetRoot, [target]);
        using var session = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
        FileTransaction.Execute(
            [target],
            () => AtomicFile.WriteBytesValidated(
                target,
                new UTF8Encoding(false).GetBytes("committed"),
                _ => { },
                boundary),
            "Phase 08 forward stage crash",
            boundary.ReleaseWriteLeafLocksForRollback,
            session,
            CancellationToken.None);
        return 46;
    }

    private static int RunPhase08PublicationStageChild(
        string targetRoot,
        string authorityRoot,
        string marker,
        string stage)
    {
        FileTransactionRecoverySession.ArtifactMovedBeforeVerificationTestHook = path =>
        {
            var fileName = Path.GetFileName(path);
            var matches = stage switch
            {
                "base" => fileName.Equals("base.json", StringComparison.Ordinal),
                "snapshot-ready" => fileName.StartsWith("snapshot-ready-", StringComparison.Ordinal),
                "prepared" => fileName.Equals("prepared.json", StringComparison.Ordinal),
                "reservation" => fileName.StartsWith("reserve-", StringComparison.Ordinal),
                "intent" => fileName.StartsWith("intent-", StringComparison.Ordinal),
                "resolved" => fileName.Equals("resolved.json", StringComparison.Ordinal),
                "cleanup" => fileName.Equals("cleanup.json", StringComparison.Ordinal),
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            };
            if (!matches) return;
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        };
        var target = Path.Combine(targetRoot, "state.txt");
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        using var session = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
        FileTransaction.Execute(
            [target],
            () => AtomicFile.WriteUtf8(target, "committed"),
            "Phase 08 publication ambiguity",
            () => { },
            session,
            CancellationToken.None);
        return 47;
    }

    private static int RunPhase08RmkUnreadyChild(
        string targetRoot,
        string authorityRoot,
        string marker,
        string sourcePath)
    {
        void Stop(string _)
        {
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        }

        var hook = typeof(RimWorldTranslatorRmkXlsxWriter).GetProperty(
            "PreparedArchiveCreatedTestHook",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native prepared-archive crash hook is unavailable.");
        hook.SetValue(null, (Action<string>)Stop);

        var target = Path.Combine(targetRoot, "history.xlsx");
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        using var session = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
        FileTransaction.Execute(
            [target],
            () =>
            {
                var plan = FileTransactionRecoverySession.ReservePreparedWrite(target, keepBackup: true)
                    ?? throw new InvalidOperationException("The RMK durable reservation was not created.");
                RimWorldTranslatorRmkXlsxWriter.WritePrepared(
                    sourcePath == "-" ? null : sourcePath,
                    plan.PreparedPath,
                    [CreateSyntheticRmkHistoryRow()],
                    "English");
                FileTransactionRecoverySession.MarkPreparedWriteReady(plan);
            },
            "Phase 08 native RMK unready crash",
            () => { },
            session,
            CancellationToken.None);
        return 48;
    }

    private static RimWorldTranslatorRmkHistoryRow CreateSyntheticRmkHistoryRow() => new()
    {
        Identifier = "Synthetic.Rmk.Entry",
        ClassName = "SyntheticDef",
        Key = "Synthetic.Rmk.Entry",
        Source = "Synthetic source",
        Translation = "합성 번역"
    };

    private static InterruptedTransactionFixture CreateInterruptedTransaction(
        string root,
        string name)
    {
        var targetRoot = Path.Combine(root, name + "-target");
        var authorityRoot = Path.Combine(root, name + "-authority");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(authorityRoot);
        var first = Path.Combine(targetRoot, "first.txt");
        var second = Path.Combine(targetRoot, "second.txt");
        var marker = Path.Combine(root, name + "-first-commit.marker");
        File.WriteAllText(first, "original-first", new UTF8Encoding(false));
        File.WriteAllText(second, "original-second", new UTF8Encoding(false));
        using var child = StartSelfProcess(
            "--phase08-transaction-crash-child",
            targetRoot,
            authorityRoot,
            marker);
        try
        {
            WaitForFile(marker, child, TimeSpan.FromSeconds(15));
            child.Kill(entireProcessTree: true);
            Assert(child.WaitForExit(15_000),
                "The multi-file forced-exit child did not terminate after Kill.");
            Assert(child.ExitCode != 0,
                "The force-killed multi-file child reported a successful exit code.");
        }
        finally
        {
            KillIfRunning(child);
        }
        return new InterruptedTransactionFixture(targetRoot, authorityRoot, first, second);
    }

    private static void AssertPartialTransactionUnchanged(
        InterruptedTransactionFixture fixture,
        string message)
    {
        Assert(File.ReadAllText(fixture.First, Encoding.UTF8) == "transaction-first"
               && File.ReadAllText(fixture.Second, Encoding.UTF8) == "original-second",
            message);
        Assert(RecoveryTransactionDirectories(fixture.AuthorityRoot).Length == 1,
            "Fail-closed recovery discarded trusted evidence.");
    }

    private static string FindSingleAuthorityArtifact(string authorityRoot, string pattern) =>
        Directory.EnumerateFiles(authorityRoot, pattern, SearchOption.AllDirectories).Single();

    private static string[] RecoveryTransactionDirectories(string authorityRoot) =>
        Directory.EnumerateDirectories(
                authorityRoot,
                "transaction-*",
                SearchOption.AllDirectories)
            .ToArray();

    private static void TruncateHalf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert(bytes.Length > 32, "The recovery artifact fixture was unexpectedly too small to truncate.");
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
    }

    private static Process StartSelfProcess(params string[] arguments)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The regression process path is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)
            ?? throw new InvalidOperationException("The forced-exit fixture process did not start.");
    }

    private static void WaitForFile(string path, Process child, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (File.Exists(path)) return;
            if (child.HasExited)
                throw new InvalidOperationException($"The forced-exit fixture exited early ({child.ExitCode}).");
            Thread.Sleep(25);
        }
        throw new TimeoutException("The forced-exit fixture did not reach its requested crash boundary.");
    }

    private static void KillIfRunning(Process child)
    {
        if (child.HasExited) return;
        child.Kill(entireProcessTree: true);
        _ = child.WaitForExit(15_000);
    }

    private sealed record InterruptedTransactionFixture(
        string TargetRoot,
        string AuthorityRoot,
        string First,
        string Second);
}
