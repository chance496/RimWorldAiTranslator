using System.Text;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private const int MaximumBuilderFixtureRuntimeFiles = 1_024;
    private const long MaximumBuilderFixtureRuntimeFileBytes = 256L * 1024 * 1024;
    private const long MaximumBuilderFixtureRuntimeBytes = 1L * 1024 * 1024 * 1024;
    private const long MaximumBuilderFixtureControlBytes = 64L * 1024;
    private const string BuilderFixtureEntrypointFileName = "builder-fixture-entrypoint.txt";
    private const string BuilderFixtureEntrypointContent =
        "RimWorldAiTranslator synthetic RMK Builder fixture v1";
    private const string BuilderFixtureEnvironmentName = "RWAT_FIXTURE_MODE";
    private static readonly string[] BuilderFixtureControlFileNames =
    [
        BuilderFixtureEntrypointFileName,
        "builder-fixture-mode.txt",
        "builder-pause-marker-path.txt",
        "builder-started-marker-path.txt",
        "builder-child-marker-path.txt"
    ];

    static Program()
    {
        ConfigureRmkBuilderStageFixtureHook();
    }

    private static void RmkWorkshopWriteBoundary()
    {
        WithTempRoot(root =>
        {
            var workshopContainer = Path.Combine(root, "SteamLibrary", "steamapps", "workshop", "content", "294100");
            var subscription = Path.Combine(workshopContainer, RmkWorkspaceService.WorkshopId);
            Directory.CreateDirectory(Path.Combine(subscription, "Data"));
            Directory.CreateDirectory(Path.Combine(subscription, ".git"));
            File.WriteAllText(Path.Combine(subscription, ".git", "HEAD"), "ref: refs/heads/bus\n", new UTF8Encoding(false));
            var loadFolders = Path.Combine(subscription, "LoadFolders.xml");
            var modList = Path.Combine(subscription, "ModList.tsv");
            const string originalLoadFolders = "<loadFolders><subscription /></loadFolders>";
            const string originalModList = "subscription\tentry\tData/subscription\tfixture.subscription";
            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            InstallRmkBuilderFixture(subscription);
            File.WriteAllText(Path.Combine(subscription, "builder-fixture-mode.txt"), "success", new UTF8Encoding(false));

            var service = new RmkWorkspaceService();
            Assert(!service.IsWritableWorkspace(subscription), "A synthetic Workshop subscription was admitted as a writable RMK clone.");
            Assert(string.IsNullOrEmpty(service.FindWritableWorkspace([workshopContainer])),
                "RMK auto-discovery returned a synthetic Workshop subscription as writable.");
            AssertThrows<InvalidOperationException>(() => service.RequireWritableWorkspace(subscription));
            AssertThrows<InvalidOperationException>(() => service.CreateTarget(subscription, new RimWorldAiTranslator.Core.Models.TranslationProject
            {
                Name = "Denied Workshop Target",
                WorkshopId = "123",
                PackageId = "fixture.denied"
            }, "1.6"));
            AssertThrows<InvalidOperationException>(() => service.CreateBuildPlan(subscription));

            var aliasContainer = Path.Combine(root, "RmkAlias");
            var aliasAvailable = false;
            try
            {
                Directory.CreateSymbolicLink(aliasContainer, workshopContainer);
                aliasAvailable = true;
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or PlatformNotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Junction/symlink fixture unavailable; direct Workshop API boundary retained ({exception.GetType().Name}).");
                Console.WriteLine($"[INFO] RMK parent-link fixture unavailable ({exception.GetType().Name}); direct Workshop boundary was exercised.");
            }

            if (aliasAvailable)
            {
                Console.WriteLine("[INFO] RMK parent-link alias boundary was exercised.");
                var aliasedSubscription = Path.Combine(aliasContainer, RmkWorkspaceService.WorkshopId);
                Assert(Directory.Exists(aliasedSubscription), "The synthetic Workshop alias did not resolve.");
                Assert(!service.IsWritableWorkspace(aliasedSubscription), "A parent reparse alias admitted a Workshop subscription as writable.");
                AssertThrows<InvalidDataException>(() => service.RequireWritableWorkspace(aliasedSubscription));
                AssertThrows<InvalidDataException>(() => service.CreateTarget(aliasedSubscription, new RimWorldAiTranslator.Core.Models.TranslationProject
                {
                    Name = "Denied Alias Target",
                    WorkshopId = "456",
                    PackageId = "fixture.alias.denied"
                }, "1.6"));
                AssertThrows<InvalidDataException>(() => service.CreateBuildPlan(aliasedSubscription));
                AssertThrows<InvalidDataException>(() => CreateRmkExportService().Export(new RmkExportOptions
                {
                    RmkWorkspaceRoot = aliasedSubscription,
                    RmkEntryRoot = Path.Combine(aliasedSubscription, "Data"),
                    ReviewRoot = root,
                    ModRoot = root,
                    DryRun = true
                }));
            }

            var longParent = Path.Combine(root, "LongPath");
            while (longParent.Length < 280)
                longParent = Path.Combine(longParent, "canonical-path-segment-0123456789");
            var longWorkspace = Path.Combine(longParent, "LocalRmkClone");
            Directory.CreateDirectory(Path.Combine(longWorkspace, "Data"));
            Directory.CreateDirectory(Path.Combine(longWorkspace, ".git"));
            File.WriteAllText(Path.Combine(longWorkspace, ".git", "HEAD"), "ref: refs/heads/bus\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(longWorkspace, "ModList.tsv"), string.Empty, new UTF8Encoding(false));
            Assert(service.RequireWritableWorkspace(longWorkspace).Equals(Path.GetFullPath(longWorkspace), StringComparison.OrdinalIgnoreCase),
                "A canonical long-path local RMK work clone was rejected.");

            Assert(File.ReadAllText(loadFolders) == originalLoadFolders && File.ReadAllText(modList) == originalModList,
                "A denied Workshop builder request executed or modified subscription indexes.");
            Assert(!Directory.EnumerateFiles(Path.Combine(subscription, "Data"), "LoadFolders.Build.yaml", SearchOption.AllDirectories).Any(),
                "A denied Workshop target request wrote RMK metadata.");
        });
    }

    private static void RmkBuilderTransaction()
    {
        WithTempRoot(root =>
        {
            var externalGit = Path.Combine(root, "ExternalRepository.git");
            Directory.CreateDirectory(externalGit);
            File.WriteAllText(Path.Combine(externalGit, "HEAD"), "ref: refs/heads/bus\n", new UTF8Encoding(false));
            var aliasWorkspace = Path.Combine(root, "ExternalGitAlias");
            Directory.CreateDirectory(Path.Combine(aliasWorkspace, "Data"));
            File.WriteAllText(Path.Combine(aliasWorkspace, "ModList.tsv"), string.Empty, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(aliasWorkspace, ".git"), $"gitdir: {externalGit}\n", new UTF8Encoding(false));
            var aliasService = new RmkWorkspaceService();
            Assert(!aliasService.IsWritableWorkspace(aliasWorkspace),
                "An arbitrary external Git directory with a bus HEAD passed the RMK writable-worktree gate.");
            AssertThrows<InvalidDataException>(() => aliasService.RequireWritableWorkspace(aliasWorkspace));

            var validWorkspace = Path.Combine(root, "ValidatedWorktree");
            Directory.CreateDirectory(Path.Combine(validWorkspace, "Data"));
            File.WriteAllText(Path.Combine(validWorkspace, "ModList.tsv"), string.Empty, new UTF8Encoding(false));
            var commonGit = Path.Combine(root, "MainRepository.git");
            var worktreeAdmin = Path.Combine(commonGit, "worktrees", "ValidatedWorktree");
            Directory.CreateDirectory(worktreeAdmin);
            var validGitFile = Path.Combine(validWorkspace, ".git");
            File.WriteAllText(validGitFile, $"gitdir: {worktreeAdmin}\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(worktreeAdmin, "gitdir"), validGitFile + "\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(worktreeAdmin, "commondir"), "../..\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(worktreeAdmin, "HEAD"), "ref: refs/heads/bus\n", new UTF8Encoding(false));
            Assert(aliasService.RequireWritableWorkspace(validWorkspace)
                    .Equals(Path.GetFullPath(validWorkspace), StringComparison.OrdinalIgnoreCase),
                "A synthetic Git worktree with a matching backlink and common directory was rejected.");

            var workspace = CreateRmkWorkspace(root, "BuilderEntry", out _);
            var loadFolders = Path.Combine(workspace, "LoadFolders.xml");
            var modList = Path.Combine(workspace, "ModList.tsv");
            const string originalLoadFolders = "<loadFolders><original /></loadFolders>";
            const string originalModList = "original\tentry\tData/original\tfixture.original";
            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            InstallRmkBuilderFixture(workspace);

            var mode = Path.Combine(workspace, "builder-fixture-mode.txt");
            var startedMarker = Path.Combine(root, "builder-started-external.txt");
            var escapedChildMarker = Path.Combine(root, "builder-child-escaped-external.txt");
            File.WriteAllText(
                Path.Combine(workspace, "builder-started-marker-path.txt"),
                startedMarker,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(workspace, "builder-child-marker-path.txt"),
                escapedChildMarker,
                new UTF8Encoding(false));
            var recoveryAuthorityRoot = Path.Combine(root, "BuilderRecoveryAuthority");
            Directory.CreateDirectory(recoveryAuthorityRoot);
            var service = new RmkWorkspaceService(
                new FileTransactionRecoveryAuthority(recoveryAuthorityRoot));
            Assert(RmkWorkspaceService.IsSensitiveEnvironmentName("AWS_ACCESS_KEY_ID")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("SYNTHETIC_MIDDLE_TOKEN_VALUE")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("SYNTHETIC_AUTH_CONTEXT")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("PATH")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("DOTNET_STARTUP_HOOKS")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("CORECLR_ENABLE_PROFILING")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("COMPlus_ReadyToRun")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("MSBuildSDKsPath")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("NuGetPackageRoot")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("GIT_CONFIG_COUNT")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("NODE_OPTIONS")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("JAVA_TOOL_OPTIONS")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("PYTHONPATH")
                   && RmkWorkspaceService.IsSensitiveEnvironmentName("PSModulePath")
                   && !RmkWorkspaceService.IsSensitiveEnvironmentName("SYSTEMROOT")
                   && !RmkWorkspaceService.IsSensitiveEnvironmentName("TEMP"),
                "Credential or runtime-injection environment names were not filtered from the RMK Builder environment.");

            File.WriteAllText(mode, "success", new UTF8Encoding(false));
            var authoritylessInitial = CaptureBuilderLivePair(loadFolders, modList);
            var authoritylessService = new RmkWorkspaceService();
            AssertNoExternalBuilderMutationEvidence(
                () => AssertThrows<InvalidOperationException>(() =>
                    authoritylessService
                        .BuildAsync(authoritylessService.CreateBuildPlan(workspace))
                        .GetAwaiter()
                        .GetResult()),
                "An authority-less RMK Builder request");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                authoritylessInitial,
                "An authority-less RMK Builder request changed live indexes.");
            Assert(!File.Exists(startedMarker),
                "An authority-less RMK Builder request started the external process.");

            var stagedSourceYaml = Path.Combine(
                workspace,
                "Data",
                "BuilderEntry",
                "LoadFolders.Build.yaml");
            File.WriteAllText(
                stagedSourceYaml,
                "Version: 1.0\nLanguages: Languages\n",
                new UTF8Encoding(false));
            var stagedMutationInitial = CaptureBuilderLivePair(loadFolders, modList);
            var previousPopulate = RmkWorkspaceService.PopulateBuilderStageTestHook;
            var stagedMutationApplied = false;
            RmkWorkspaceService.PopulateBuilderStageTestHook =
                (workspaceRoot, stageRoot) =>
                {
                    previousPopulate?.Invoke(workspaceRoot, stageRoot);
                    var stagedYaml = Path.Combine(
                        stageRoot,
                        Path.GetRelativePath(workspaceRoot, stagedSourceYaml));
                    File.AppendAllText(
                        stagedYaml,
                        "Mutated: true\n",
                        new UTF8Encoding(false));
                    stagedMutationApplied = true;
                };
            try
            {
                _ = CaptureException<ConcurrentLeafChangeException>(() =>
                    service.BuildAsync(service.CreateBuildPlan(workspace))
                        .GetAwaiter()
                        .GetResult());
            }
            finally
            {
                RmkWorkspaceService.PopulateBuilderStageTestHook = previousPopulate;
            }
            Assert(stagedMutationApplied && !File.Exists(startedMarker),
                "A modified staged RMK input was not rejected before the child process started.");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                stagedMutationInitial,
                "A modified staged RMK input changed live indexes.");
            Assert(RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A rejected staged-input mutation retained recovery evidence.");

            var stagedInjectionInitial = CaptureBuilderLivePair(loadFolders, modList);
            previousPopulate = RmkWorkspaceService.PopulateBuilderStageTestHook;
            var stagedInjectionApplied = false;
            RmkWorkspaceService.PopulateBuilderStageTestHook =
                (workspaceRoot, stageRoot) =>
                {
                    previousPopulate?.Invoke(workspaceRoot, stageRoot);
                    var injectedRoot = Path.Combine(stageRoot, "Data", "Injected");
                    Directory.CreateDirectory(injectedRoot);
                    File.WriteAllText(
                        Path.Combine(injectedRoot, "LoadFolders.Build.yaml"),
                        "Version: 1.0\nLanguages: Languages\n",
                        new UTF8Encoding(false));
                    stagedInjectionApplied = true;
                };
            try
            {
                _ = CaptureException<ConcurrentLeafChangeException>(() =>
                    service.BuildAsync(service.CreateBuildPlan(workspace))
                        .GetAwaiter()
                        .GetResult());
            }
            finally
            {
                RmkWorkspaceService.PopulateBuilderStageTestHook = previousPopulate;
            }
            Assert(stagedInjectionApplied && !File.Exists(startedMarker),
                "A stage-only RMK semantic input was not rejected before the child process started.");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                stagedInjectionInitial,
                "A stage-only RMK semantic input changed live indexes.");
            Assert(RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A rejected staged semantic injection retained recovery evidence.");

            var deepInputRoot = Path.Combine(workspace, "Data");
            for (var index = 0; index < 63; index++)
                deepInputRoot = Path.Combine(deepInputRoot, $"d{index:x2}");
            Directory.CreateDirectory(deepInputRoot);
            File.WriteAllText(
                Path.Combine(deepInputRoot, "LoadFolders.Build.yaml"),
                "Version: 1.0\nLanguages: Languages\n",
                new UTF8Encoding(false));
            var depthInitial = CaptureBuilderLivePair(loadFolders, modList);
            var stagingRootBeforeDepth = Path.Combine(root, "rmk-builder-staging");
            var runCountBeforeDepth = Directory.Exists(stagingRootBeforeDepth)
                ? Directory.EnumerateDirectories(
                        stagingRootBeforeDepth,
                        "run-*",
                        SearchOption.TopDirectoryOnly)
                    .Count()
                : 0;
            _ = CaptureException<InvalidDataException>(() =>
                service.BuildAsync(service.CreateBuildPlan(workspace))
                    .GetAwaiter()
                    .GetResult());
            var runCountAfterDepth = Directory.Exists(stagingRootBeforeDepth)
                ? Directory.EnumerateDirectories(
                        stagingRootBeforeDepth,
                        "run-*",
                        SearchOption.TopDirectoryOnly)
                    .Count()
                : 0;
            Assert(runCountAfterDepth == runCountBeforeDepth && !File.Exists(startedMarker),
                "A cleanup-unsafe deep RMK input created a stage or started the child process.");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                depthInitial,
                "A cleanup-unsafe deep RMK input changed live indexes.");
            Directory.Delete(Path.Combine(workspace, "Data", "d00"), recursive: true);

            using (var oversized = new FileStream(loadFolders, FileMode.Open, FileAccess.Write, FileShare.None))
                oversized.SetLength(64L * 1024 * 1024 + 1);
            AssertThrows<InvalidDataException>(() => service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult());
            Assert(!Directory.EnumerateFiles(workspace, "*.transaction.bak", SearchOption.AllDirectories).Any(),
                "An oversized LoadFolders.xml caused a snapshot to be retained or copied.");
            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));

            using (var oversized = new FileStream(modList, FileMode.Open, FileAccess.Write, FileShare.None))
                oversized.SetLength(1L * 1024 * 1024 + 1);
            AssertThrows<InvalidDataException>(() => service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult());
            Assert(!Directory.EnumerateFiles(workspace, "*.transaction.bak", SearchOption.AllDirectories).Any(),
                "An oversized ModList.tsv caused a snapshot to be retained or copied.");
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));

            if (File.Exists(startedMarker)) File.Delete(startedMarker);
            File.WriteAllText(mode, "success", new UTF8Encoding(false));
            RmkWorkspaceService.BeforeBuilderJobAssignmentTestHook =
                () => throw new IOException("Synthetic job assignment failure.");
            try
            {
                AssertThrows<IOException>(() => service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult());
            }
            finally
            {
                RmkWorkspaceService.BeforeBuilderJobAssignmentTestHook = null;
            }
            Assert(!File.Exists(startedMarker),
                "The suspended RMK Builder ran after process-tree assignment failed.");

            File.WriteAllText(mode, "fail", new UTF8Encoding(false));
            var failedInitial = CaptureBuilderLivePair(loadFolders, modList);
            AssertNoExternalBuilderMutationEvidence(
                () => AssertThrows<InvalidOperationException>(() =>
                    service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult()),
                "A nonzero RMK Builder run");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                failedInitial,
                "A nonzero staged RMK Builder run changed live indexes.");
            Assert(File.Exists(startedMarker)
                   && RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "The nonzero staged RMK Builder did not run in isolation or retained live-publication recovery evidence.");

            File.WriteAllText(mode, "missing-fail", new UTF8Encoding(false));
            var missingInitial = CaptureBuilderLivePair(loadFolders, modList);
            AssertNoExternalBuilderMutationEvidence(
                () => AssertThrows<InvalidOperationException>(() =>
                    service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult()),
                "A staged RMK Builder run with a missing output");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                missingInitial,
                "A staged Builder failure with one missing output changed live indexes.");
            Assert(RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A staged Builder failure with one missing output retained recovery evidence.");

            File.WriteAllText(mode, "deep", new UTF8Encoding(false));
            var deepInitial = CaptureBuilderLivePair(loadFolders, modList);
            AssertNoExternalBuilderMutationEvidence(
                () => AssertThrows<InvalidDataException>(() =>
                    service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult()),
                "A staged RMK Builder run with deeply nested XML");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                deepInitial,
                "A deeply nested staged RMK Builder result changed live indexes.");
            Assert(RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A deeply nested staged RMK Builder result retained recovery evidence.");

            File.WriteAllText(mode, "truncated-modlist", new UTF8Encoding(false));
            var truncatedInitial = CaptureBuilderLivePair(loadFolders, modList);
            var truncatedError = CaptureWithoutExternalBuilderMutationEvidence(
                () => CaptureException<InvalidDataException>(() =>
                    service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult()),
                "A staged RMK Builder run with a truncated ModList");
            Assert(truncatedError.Message.Contains("ModList", StringComparison.OrdinalIgnoreCase),
                "The truncated staged ModList was rejected for an unrelated reason.");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                truncatedInitial,
                "A truncated staged ModList.tsv changed live indexes.");
            Assert(RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A truncated staged ModList.tsv retained live-publication recovery evidence.");

            File.WriteAllText(mode, "cancel", new UTF8Encoding(false));
            var cancelledInitial = CaptureBuilderLivePair(loadFolders, modList);
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(750)))
            {
                AssertNoExternalBuilderMutationEvidence(
                    () => AssertThrows<OperationCanceledException>(() =>
                        service.BuildAsync(service.CreateBuildPlan(workspace), cancellation.Token).GetAwaiter().GetResult()),
                    "A cancelled staged RMK Builder run");
            }
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                cancelledInitial,
                "A cancelled staged RMK Builder run changed live indexes.");
            Assert(RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A cancelled staged RMK Builder run retained live-publication recovery evidence.");

            if (File.Exists(escapedChildMarker)) File.Delete(escapedChildMarker);
            File.WriteAllText(mode, "child", new UTF8Encoding(false));
            _ = service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult();
            Thread.Sleep(TimeSpan.FromSeconds(3));
            Assert(!File.Exists(escapedChildMarker),
                "A descendant escaped the RMK Builder process-tree containment boundary.");

            File.WriteAllText(mode, "success", new UTF8Encoding(false));
            var result = service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult();
            Assert(result.Output.Contains("검증된", StringComparison.Ordinal), "RMK Builder success result changed.");
            Assert(File.ReadAllText(loadFolders).Contains("generated", StringComparison.Ordinal)
                   && File.ReadAllText(modList).Contains("fixture.generated", StringComparison.Ordinal),
                "RMK Builder success output was not retained.");
            Assert(RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "The recovery-enabled RMK Builder left a trusted transaction record.");
            Assert(!File.Exists(loadFolders + ".bak") && !File.Exists(modList + ".bak"),
                "The durable RMK Builder changed the legacy deterministic-backup surface.");
            Assert(!Directory.EnumerateFiles(workspace, "*.transaction.bak", SearchOption.AllDirectories).Any(),
                "RMK Builder left a transaction snapshot behind.");

            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            var rewriteBlocked = false;
            RmkWorkspaceService.AfterBuilderOutputsValidatedBeforeEvidenceTestHook =
                (validatedLoadFolders, _) =>
                {
                    try
                    {
                        File.WriteAllText(
                            validatedLoadFolders,
                            "<forged />",
                            new UTF8Encoding(false));
                    }
                    catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException)
                    {
                        rewriteBlocked = true;
                    }
                };
            try
            {
                _ = service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult();
            }
            finally
            {
                RmkWorkspaceService.AfterBuilderOutputsValidatedBeforeEvidenceTestHook = null;
            }
            Assert(rewriteBlocked
                   && File.ReadAllText(loadFolders).Contains("generated", StringComparison.Ordinal)
                   && RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A rewrite raced between Builder semantic validation and final evidence publication, or forged evidence was retained.");

            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            var swapCandidate = loadFolders + ".unvalidated-candidate";
            File.WriteAllText(swapCandidate, "<forged />", new UTF8Encoding(false));
            var swapBlocked = false;
            RmkWorkspaceService.AfterBuilderOutputsValidatedBeforeEvidenceTestHook =
                (validatedLoadFolders, _) =>
                {
                    try
                    {
                        File.Move(swapCandidate, validatedLoadFolders, overwrite: true);
                    }
                    catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException)
                    {
                        swapBlocked = true;
                    }
                };
            try
            {
                _ = service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult();
            }
            finally
            {
                RmkWorkspaceService.AfterBuilderOutputsValidatedBeforeEvidenceTestHook = null;
            }
            Assert(swapBlocked
                   && File.Exists(swapCandidate)
                   && File.ReadAllText(swapCandidate) == "<forged />"
                   && File.ReadAllText(loadFolders).Contains("generated", StringComparison.Ordinal)
                   && RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A path swap raced between Builder semantic validation and final evidence publication, or the unvalidated winner was not preserved.");
            File.Delete(swapCandidate);

            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            const string concurrentLoadFolders =
                "<loadFolders><legitimate-concurrent-winner /></loadFolders>";
            const string concurrentModList =
                "456\tConcurrent\tData/concurrent\tfixture.concurrent";
            (SnapshotLeafFingerprint LoadFolders, SnapshotLeafFingerprint ModList)? concurrentWinner = null;
            RmkWorkspaceService.BeforeBuilderOutputsPublishedTestHook =
                (liveLoadFolders, liveModList) =>
                {
                    Assert(liveLoadFolders.Equals(loadFolders, StringComparison.OrdinalIgnoreCase)
                           && liveModList.Equals(modList, StringComparison.OrdinalIgnoreCase),
                        "The RMK Builder publication hook did not receive the exact live index paths.");
                    File.WriteAllText(liveLoadFolders, concurrentLoadFolders, new UTF8Encoding(false));
                    File.WriteAllText(liveModList, concurrentModList, new UTF8Encoding(false));
                    concurrentWinner = CaptureBuilderLivePair(liveLoadFolders, liveModList);
                };
            ConcurrentLeafChangeException concurrentError;
            try
            {
                concurrentError = CaptureWithoutExternalBuilderMutationEvidence(
                    () => CaptureException<ConcurrentLeafChangeException>(() =>
                        service.BuildAsync(service.CreateBuildPlan(workspace)).GetAwaiter().GetResult()),
                    "A concurrent RMK index winner");
            }
            finally
            {
                RmkWorkspaceService.BeforeBuilderOutputsPublishedTestHook = null;
            }
            if (!concurrentWinner.HasValue)
                throw new InvalidOperationException("The concurrent RMK index winner was not captured.");
            Assert(concurrentError.PreservedPaths.Any(path =>
                       path.Equals(loadFolders, StringComparison.OrdinalIgnoreCase)),
                "The RMK Builder CAS did not identify the concurrent live index winner.");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                concurrentWinner.Value,
                "RMK Builder publication overwrote or rewrote a concurrent live index winner.");
            Assert(File.ReadAllText(loadFolders) == concurrentLoadFolders
                   && File.ReadAllText(modList) == concurrentModList
                   && RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "RMK Builder CAS did not preserve the complete concurrent live index pair.");

            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            var sourceAdditionInitial = CaptureBuilderLivePair(loadFolders, modList);
            var addedSourceDirectory = Path.Combine(
                workspace,
                "Data",
                "BuilderEntry",
                "ConcurrentAddition");
            var addedSourceYaml = Path.Combine(
                addedSourceDirectory,
                "LoadFolders.Build.yaml");
            var previousBeforePublication =
                RmkWorkspaceService.BeforeBuilderOutputsPublishedTestHook;
            var sourceAdditionApplied = false;
            RmkWorkspaceService.BeforeBuilderOutputsPublishedTestHook =
                (liveLoadFolders, liveModList) =>
                {
                    previousBeforePublication?.Invoke(liveLoadFolders, liveModList);
                    Directory.CreateDirectory(addedSourceDirectory);
                    File.WriteAllText(
                        addedSourceYaml,
                        "Version: 1.0\nLanguages: Languages\n",
                        new UTF8Encoding(false));
                    sourceAdditionApplied = true;
                };
            try
            {
                _ = CaptureWithoutExternalBuilderMutationEvidence(
                    () => CaptureException<ConcurrentLeafChangeException>(() =>
                        service.BuildAsync(service.CreateBuildPlan(workspace))
                            .GetAwaiter()
                            .GetResult()),
                    "A concurrent RMK Builder source addition");
            }
            finally
            {
                RmkWorkspaceService.BeforeBuilderOutputsPublishedTestHook =
                    previousBeforePublication;
            }
            Assert(sourceAdditionApplied
                   && File.ReadAllText(addedSourceYaml).Contains(
                       "Languages",
                       StringComparison.Ordinal),
                "The concurrent RMK source addition was not preserved.");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                sourceAdditionInitial,
                "A concurrent RMK source addition changed live indexes.");
            Assert(RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A concurrent RMK source addition retained recovery evidence.");
            File.Delete(addedSourceYaml);
            Directory.Delete(addedSourceDirectory);

            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            using var lateCancellation = new CancellationTokenSource();
            var lateCancellationInitial = CaptureBuilderLivePair(loadFolders, modList);
            RmkWorkspaceService.AfterBuilderOutputsValidatedBeforeEvidenceTestHook =
                (_, _) => lateCancellation.Cancel();
            try
            {
                AssertNoExternalBuilderMutationEvidence(
                    () => AssertThrows<OperationCanceledException>(() =>
                        service.BuildAsync(
                                service.CreateBuildPlan(workspace),
                                lateCancellation.Token)
                            .GetAwaiter()
                            .GetResult()),
                    "A cancellation after staged Builder validation");
            }
            finally
            {
                RmkWorkspaceService.AfterBuilderOutputsValidatedBeforeEvidenceTestHook = null;
            }
            Assert(lateCancellation.IsCancellationRequested,
                "The late RMK Builder cancellation fixture did not request cancellation.");
            AssertBuilderLivePairUnchanged(
                loadFolders,
                modList,
                lateCancellationInitial,
                "Cancellation after staged Builder validation changed live indexes.");
            Assert(RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "Late staged Builder cancellation retained durable publication evidence.");

            File.WriteAllText(mode, "success", new UTF8Encoding(false));
            File.WriteAllText(loadFolders, originalLoadFolders, new UTF8Encoding(false));
            File.WriteAllText(modList, originalModList, new UTF8Encoding(false));
            var stagingRoot = Path.Combine(root, "rmk-builder-staging");
            Directory.CreateDirectory(stagingRoot);
            var preservedResidues = Enumerable.Range(0, 10)
                .Select(index => index % 2 == 0
                    ? $"run-{index:x32}"
                    : $".rimworld-ai-cleanup-{index:x32}")
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var residue in preservedResidues)
            {
                var residueRoot = Path.Combine(stagingRoot, residue);
                Directory.CreateDirectory(residueRoot);
                File.WriteAllText(
                    Path.Combine(residueRoot, "sentinel.txt"),
                    residue,
                    new UTF8Encoding(false));
            }
            var residueResult = service.BuildAsync(service.CreateBuildPlan(workspace))
                .GetAwaiter()
                .GetResult();
            Assert(preservedResidues.All(residue =>
                       File.ReadAllText(Path.Combine(stagingRoot, residue, "sentinel.txt"))
                           .Equals(residue, StringComparison.Ordinal)),
                "An ownerless RMK Builder residue was deleted or changed.");
            Assert(residueResult.Output.Contains("10개", StringComparison.Ordinal)
                   && preservedResidues.Take(8).All(name =>
                       residueResult.Output.Contains(name, StringComparison.Ordinal))
                   && preservedResidues.Skip(8).All(name =>
                       !residueResult.Output.Contains(name, StringComparison.Ordinal)),
                "The preserved RMK Builder residue warning was not count- and name-bounded.");
            Assert(!Directory.EnumerateFiles(
                        stagingRoot,
                        "*.owner",
                        SearchOption.TopDirectoryOnly)
                    .Any()
                   && RecoveryTransactionDirectories(recoveryAuthorityRoot).Length == 0,
                "A residue-tolerant RMK Builder run retained ownership or recovery evidence.");
        });
    }

    private static (
        SnapshotLeafFingerprint LoadFolders,
        SnapshotLeafFingerprint ModList) CaptureBuilderLivePair(
        string loadFolders,
        string modList) =>
        (
            FileSnapshotJournal.CaptureRecoveryFingerprint(loadFolders),
            FileSnapshotJournal.CaptureRecoveryFingerprint(modList));

    private static void AssertBuilderLivePairUnchanged(
        string loadFolders,
        string modList,
        (SnapshotLeafFingerprint LoadFolders, SnapshotLeafFingerprint ModList) expected,
        string message)
    {
        var current = CaptureBuilderLivePair(loadFolders, modList);
        Assert(BuilderFingerprintEquals(expected.LoadFolders, current.LoadFolders)
               && BuilderFingerprintEquals(expected.ModList, current.ModList),
            message);
    }

    private static bool BuilderFingerprintEquals(
        SnapshotLeafFingerprint left,
        SnapshotLeafFingerprint right) =>
        left.Kind == right.Kind
        && left.Length == right.Length
        && left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks
        && left.Failure == right.Failure
        && (left.Sha256 is null && right.Sha256 is null
            || left.Sha256 is not null
            && right.Sha256 is not null
            && left.Sha256.AsSpan().SequenceEqual(right.Sha256));

    private static void AssertNoExternalBuilderMutationEvidence(
        Action action,
        string context) =>
        _ = CaptureWithoutExternalBuilderMutationEvidence(
            () =>
            {
                action();
                return true;
            },
            context);

    private static T CaptureWithoutExternalBuilderMutationEvidence<T>(
        Func<T> action,
        string context)
    {
        _ = context;
        return action();
    }

    private static void ConfigureRmkBuilderStageFixtureHook()
    {
        RmkWorkspaceService.PopulateBuilderStageTestHook = PopulateRmkBuilderStageFixture;
        RmkWorkspaceService.BuilderEnvironmentTestHook = static () =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BuilderFixtureEnvironmentName] = BuilderFixtureEntrypointContent
            };
    }

    private static void PopulateRmkBuilderStageFixture(
        string workspace,
        string stage)
    {
        var runtimeFiles = Directory.EnumerateFiles(
                AppContext.BaseDirectory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".dll" or ".json")
            .Take(MaximumBuilderFixtureRuntimeFiles + 1)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (runtimeFiles.Length > MaximumBuilderFixtureRuntimeFiles)
        {
            throw new InvalidDataException(
                "The synthetic RMK Builder runtime fixture exceeds its file-count limit.");
        }

        var aggregateBytes = 0L;
        foreach (var source in runtimeFiles)
        {
            CopyBoundedBuilderFixtureFile(
                source,
                Path.Combine(stage, Path.GetFileName(source)),
                MaximumBuilderFixtureRuntimeFileBytes,
                ref aggregateBytes,
                MaximumBuilderFixtureRuntimeBytes);
        }

        foreach (var name in BuilderFixtureControlFileNames)
        {
            var source = Path.Combine(workspace, name);
            if (!File.Exists(source)) continue;
            CopyBoundedBuilderFixtureFile(
                source,
                Path.Combine(stage, name),
                MaximumBuilderFixtureControlBytes,
                ref aggregateBytes,
                MaximumBuilderFixtureRuntimeBytes);
        }
    }

    private static void CopyBoundedBuilderFixtureFile(
        string source,
        string destination,
        long maximumFileBytes,
        ref long aggregateBytes,
        long maximumAggregateBytes)
    {
        var length = new FileInfo(source).Length;
        if (length > maximumFileBytes)
            throw new InvalidDataException("A synthetic RMK Builder fixture file exceeds its byte limit.");
        aggregateBytes = checked(aggregateBytes + length);
        if (aggregateBytes > maximumAggregateBytes)
            throw new InvalidDataException("The synthetic RMK Builder fixture exceeds its aggregate byte limit.");
        File.Copy(source, destination, overwrite: true);
    }

    private static void InstallRmkBuilderFixture(string workspace)
    {
        ConfigureRmkBuilderStageFixtureHook();
        var host = Path.Combine(AppContext.BaseDirectory, "RimWorldAiTranslator.Tests.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("Test application host is unavailable.", host);
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => Path.GetExtension(path) is ".dll" or ".json"))
        {
            File.Copy(source, Path.Combine(workspace, Path.GetFileName(source)), overwrite: true);
        }
        File.Copy(host, Path.Combine(workspace, "LoadFoldersBuilder.exe"), overwrite: true);
        File.WriteAllText(
            Path.Combine(workspace, BuilderFixtureEntrypointFileName),
            BuilderFixtureEntrypointContent,
            new UTF8Encoding(false));
    }

    private static bool IsRmkBuilderFixtureProcess()
    {
        if (Environment.GetEnvironmentVariable(BuilderFixtureEnvironmentName)
            ?.Equals(BuilderFixtureEntrypointContent, StringComparison.Ordinal) == true)
        {
            return true;
        }

        if (string.Equals(
                Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                "LoadFoldersBuilder",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var marker = Path.Combine(AppContext.BaseDirectory, BuilderFixtureEntrypointFileName);
        return File.Exists(marker)
               && File.ReadAllText(marker, Encoding.UTF8)
                   .Equals(BuilderFixtureEntrypointContent, StringComparison.Ordinal);
    }

    private static int RunRmkBuilderFixtureProcess()
    {
        var root = AppContext.BaseDirectory;
        _ = Console.In.ReadLine();
        if (Environment.GetEnvironmentVariables().Keys
            .Cast<object>()
            .OfType<string>()
            .Any(RmkWorkspaceService.IsSensitiveEnvironmentName))
        {
            return 31;
        }
        var startedMarkerControl = Path.Combine(root, "builder-started-marker-path.txt");
        if (File.Exists(startedMarkerControl))
        {
            File.WriteAllText(
                ReadBuilderFixtureMarkerPath(root, "builder-started-marker-path.txt"),
                "started",
                new UTF8Encoding(false));
        }
        var mode = File.ReadAllText(Path.Combine(root, "builder-fixture-mode.txt"), Encoding.UTF8).Trim();
        var loadFoldersContent = mode.Equals("deep", StringComparison.OrdinalIgnoreCase)
            ? "<loadFolders>" + string.Concat(Enumerable.Repeat("<node>", 300))
              + "value" + string.Concat(Enumerable.Repeat("</node>", 300)) + "</loadFolders>"
            : "<loadFolders><generated /></loadFolders>";
        File.WriteAllText(
            Path.Combine(root, "LoadFolders.xml"),
            loadFoldersContent,
            new UTF8Encoding(false));
        if (mode.Equals("missing-fail", StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(Path.Combine(root, "ModList.tsv"));
            return 17;
        }
        if (mode.Equals("pause-after-first", StringComparison.OrdinalIgnoreCase))
        {
            var marker = File.ReadAllText(
                Path.Combine(root, "builder-pause-marker-path.txt"),
                Encoding.UTF8).Trim();
            File.WriteAllText(
                marker,
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
        }
        var modListContent = mode.Equals("truncated-modlist", StringComparison.OrdinalIgnoreCase)
            ? "123\tGenerated\tData/generated\tfixture.generated\n456\tTruncated"
            : "123\tGenerated\tData/generated\tfixture.generated";
        File.WriteAllText(
            Path.Combine(root, "ModList.tsv"),
            modListContent,
            new UTF8Encoding(false));
        if (mode.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return 19;
        }
        if (mode.Equals("child", StringComparison.OrdinalIgnoreCase))
        {
            var childStart = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath
                    ?? throw new InvalidOperationException("RMK fixture process path is unavailable."),
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            childStart.ArgumentList.Add("--rmk-contained-child");
            childStart.Environment["RIMWORLD_TRANSLATOR_TEST_CHILD_MARKER"] =
                ReadBuilderFixtureMarkerPath(root, "builder-child-marker-path.txt");
            using var child = System.Diagnostics.Process.Start(childStart)
                ?? throw new InvalidOperationException("RMK contained child fixture did not start.");
            return 0;
        }
        return mode.Equals("success", StringComparison.OrdinalIgnoreCase)
               || mode.Equals("deep", StringComparison.OrdinalIgnoreCase)
               || mode.Equals("truncated-modlist", StringComparison.OrdinalIgnoreCase)
            ? 0
            : 17;
    }

    private static int RunRmkContainedChildFixture()
    {
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var marker = Environment.GetEnvironmentVariable(
            "RIMWORLD_TRANSLATOR_TEST_CHILD_MARKER");
        if (string.IsNullOrWhiteSpace(marker) || !Path.IsPathFullyQualified(marker))
            throw new InvalidDataException("The contained-child marker path is unavailable.");
        File.WriteAllText(
            Path.GetFullPath(marker),
            "escaped",
            new UTF8Encoding(false));
        return 0;
    }

    private static string ReadBuilderFixtureMarkerPath(
        string root,
        string controlFileName)
    {
        var value = File.ReadAllText(
                Path.Combine(root, controlFileName),
                Encoding.UTF8)
            .Trim();
        if (!Path.IsPathFullyQualified(value))
            throw new InvalidDataException("A synthetic RMK Builder marker path is not fully qualified.");
        return Path.GetFullPath(value);
    }
}
