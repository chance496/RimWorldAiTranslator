using System.Text;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void Phase08Reliability()
    {
        Phase08ProductionRecoveryAuthorityWiring();
        Phase08RmkTargetCreationReliability();
        Phase08ApplyCancellationRollback();
        Phase08RmkExportCancellationRollback();
    }

    private static void Phase08ProductionRecoveryAuthorityWiring()
    {
        WithTempRoot(root =>
        {
            var workspace = CreateRmkWorkspace(root, "Phase08DurableTarget", out _);
            var authorityRoot = Path.Combine(root, "rmk-recovery-authority");
            Directory.CreateDirectory(authorityRoot);
            var authority = new FileTransactionRecoveryAuthority(authorityRoot);
            var target = new RmkWorkspaceService(authority)
                .CreateTarget(workspace, Phase08TargetProject(), "1.6");
            Assert(File.Exists(target.YamlPath),
                "The recovery-enabled RMK target fixture did not create its metadata.");
            Assert(RecoveryTransactionDirectories(authorityRoot).Length == 0,
                "A completed recovery-enabled RMK target transaction left an authority record.");
        });

        WithFixture("SampleMod", modRoot =>
        {
            var fixtureRoot = Path.GetDirectoryName(modRoot)
                ?? throw new InvalidDataException("The translation fixture has no parent directory.");
            var reviewRoot = Path.Combine(fixtureRoot, "phase08-durable-reviews");
            var authorityRoot = Path.Combine(fixtureRoot, "translation-recovery-authority");
            Directory.CreateDirectory(authorityRoot);
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
            Assert(result.ReviewRoot is not null
                   && result.ComparisonFile is not null
                   && File.Exists(result.ComparisonFile),
                "The recovery-enabled source-only review did not produce its validated checkpoint.");
            Assert(RecoveryTransactionDirectories(authorityRoot).Length == 0,
                "A completed recovery-enabled translation transaction left an authority record.");
        });
    }

    private static void Phase08RmkTargetCreationReliability()
    {
        WithTempRoot(root =>
        {
            var workspace = CreateRmkWorkspace(root, "Phase08Seed", out _);
            var service = new RmkWorkspaceService
            {
                BeforeCreateTargetReadbackTestHook = _ =>
                    throw new InvalidDataException("synthetic RMK target readback failure")
            };
            var project = Phase08TargetProject();
            var entryRoot = Phase08TargetEntryRoot(workspace, project);
            var yamlPath = Path.Combine(entryRoot, "1.6", "LoadFolders.Build.yaml");

            var error = CaptureException<IOException>(() =>
                service.CreateTarget(workspace, project, "1.6"));

            Assert(error.InnerException is InvalidDataException,
                "The RMK target fixture did not fail at the pre-readback boundary.");
            Assert(!Directory.Exists(entryRoot) && !File.Exists(yamlPath) && !File.Exists(yamlPath + ".bak"),
                "A failed new RMK target left run-owned YAML or empty directories behind.");
            AssertPhase08NoTransactionResidue(workspace, "new RMK target rollback");
        });

        WithTempRoot(root =>
        {
            var workspace = CreateRmkWorkspace(root, "Phase08ExistingSeed", out _);
            var project = Phase08TargetProject();
            var entryRoot = Phase08TargetEntryRoot(workspace, project);
            var targetRoot = Path.Combine(entryRoot, "1.6");
            var languageRoot = Path.Combine(targetRoot, "Languages", "Korean (한국어)");
            var yamlPath = Path.Combine(targetRoot, "LoadFolders.Build.yaml");
            var backupPath = yamlPath + ".bak";
            var sentinelPath = Path.Combine(targetRoot, "preexisting-sentinel.txt");
            Directory.CreateDirectory(targetRoot);
            File.WriteAllText(yamlPath, Phase08TargetYaml(project), new UTF8Encoding(false));
            File.WriteAllBytes(backupPath, [0x50, 0x48, 0x41, 0x53, 0x45, 0x30, 0x38]);
            File.WriteAllText(sentinelPath, "preexisting", new UTF8Encoding(false));
            var yamlBefore = File.ReadAllBytes(yamlPath);
            var backupBefore = File.ReadAllBytes(backupPath);
            var service = new RmkWorkspaceService
            {
                BeforeCreateTargetReadbackTestHook = _ =>
                    throw new InvalidDataException("synthetic existing RMK target failure")
            };

            AssertThrows<InvalidDataException>(() =>
                service.CreateTarget(workspace, project, "1.6"));

            Assert(Directory.Exists(targetRoot)
                   && !Directory.Exists(languageRoot)
                   && File.ReadAllBytes(yamlPath).SequenceEqual(yamlBefore)
                   && File.ReadAllBytes(backupPath).SequenceEqual(backupBefore)
                   && File.ReadAllText(sentinelPath) == "preexisting",
                "RMK target cleanup changed pre-existing YAML, backup, parent content, or retained new empty directories.");
            AssertPhase08NoTransactionResidue(workspace, "pre-existing RMK target failure");
        });

        WithTempRoot(root =>
        {
            var workspace = CreateRmkWorkspace(root, "Phase08ConcurrentSeed", out _);
            var project = Phase08TargetProject();
            var entryRoot = Phase08TargetEntryRoot(workspace, project);
            var targetRoot = Path.Combine(entryRoot, "1.6");
            var languageRoot = Path.Combine(targetRoot, "Languages", "Korean (한국어)");
            var yamlPath = Path.Combine(targetRoot, "LoadFolders.Build.yaml");
            var concurrentPath = Path.Combine(languageRoot, "concurrent-user-file.txt");
            var service = new RmkWorkspaceService
            {
                BeforeCreateTargetReadbackTestHook = _ =>
                {
                    File.WriteAllText(concurrentPath, "concurrent", new UTF8Encoding(false));
                    throw new InvalidDataException("synthetic concurrent RMK target failure");
                }
            };

            var error = CaptureException<IOException>(() =>
                service.CreateTarget(workspace, project, "1.6"));

            Assert(error.Message.Contains("cleanup was incomplete", StringComparison.OrdinalIgnoreCase)
                   && error.Message.Contains("not empty", StringComparison.OrdinalIgnoreCase),
                "Concurrent RMK target content did not produce an explicit cleanup-incomplete outcome.");
            Assert(File.Exists(concurrentPath)
                   && File.ReadAllText(concurrentPath) == "concurrent"
                   && !File.Exists(yamlPath)
                   && !File.Exists(yamlPath + ".bak"),
                "RMK target rollback removed concurrent content or retained its run-owned YAML.");
            AssertPhase08NoTransactionResidue(workspace, "concurrent RMK target preservation");
        });

        WithTempRoot(root =>
        {
            var workspace = CreateRmkWorkspace(root, "Phase08DirectoryRaceSeed", out _);
            var project = Phase08TargetProject();
            var entryRoot = Phase08TargetEntryRoot(workspace, project);
            var service = new RmkWorkspaceService
            {
                BeforeCreateTargetDirectoryCommitTestHook = path =>
                {
                    if (path.Equals(entryRoot, StringComparison.OrdinalIgnoreCase))
                        Directory.CreateDirectory(path);
                }
            };

            var error = CaptureException<IOException>(() =>
                service.CreateTarget(workspace, project, "1.6"));

            Assert(error.Message.Contains("cleanup was incomplete", StringComparison.OrdinalIgnoreCase)
                   && error.InnerException is ConcurrentLeafChangeException
                   && Directory.Exists(entryRoot)
                   && !Directory.EnumerateFileSystemEntries(entryRoot).Any(),
                "An empty directory created concurrently was not preserved with an explicit incomplete-cleanup result.");
            AssertPhase08NoTransactionResidue(workspace, "concurrent RMK directory preservation");
        });

        WithTempRoot(root =>
        {
            var workspace = CreateRmkWorkspace(root, "Phase08CancelledSeed", out _);
            var project = Phase08TargetProject();
            var entryRoot = Phase08TargetEntryRoot(workspace, project);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            AssertThrows<OperationCanceledException>(() =>
                new RmkWorkspaceService().CreateTarget(workspace, project, "1.6", cancellation.Token));

            Assert(!Directory.Exists(entryRoot),
                "A pre-cancelled RMK target request created a target directory.");
            AssertPhase08NoTransactionResidue(workspace, "pre-cancelled RMK target");
        });
    }

    private static void Phase08ApplyCancellationRollback()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-phase08-apply-cancel");
            var defRow = run.Rows.Single(row => row.Key == "Codex_TestWorkbench.label");
            var keyedRow = run.Rows.Single(row => row.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(defRow, "취소 시험 작업대"), (keyedRow, "취소 시험 번역")]);

            var reviewLanguageRoot = Path.Combine(run.ReviewRoot!, "Languages", "Korean");
            var outputLanguageRoot = Path.Combine(modRoot, "Languages", "Korean");
            var defPath = Path.Combine(outputLanguageRoot, Path.GetRelativePath(reviewLanguageRoot, defRow.Target));
            var keyedPath = Path.Combine(outputLanguageRoot, Path.GetRelativePath(reviewLanguageRoot, keyedRow.Target));
            Assert(StringComparer.OrdinalIgnoreCase.Compare(defPath, keyedPath) < 0,
                "The Apply cancellation fixture no longer writes its existing file first.");
            Directory.CreateDirectory(Path.GetDirectoryName(defPath)!);
            File.WriteAllText(
                defPath,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><LanguageData><Phase08.Before>before</Phase08.Before></LanguageData>",
                new UTF8Encoding(false));
            File.WriteAllText(
                defPath + ".bak",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><LanguageData><Phase08.Backup>backup</Phase08.Backup></LanguageData>",
                new UTF8Encoding(false));
            var targetBefore = File.ReadAllBytes(defPath);
            var backupBefore = File.ReadAllBytes(defPath + ".bak");
            var writeBoundaryReached = false;
            var service = new ReviewApplyService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor())
            {
                AfterWriteBoundaryLockedTestHook = () => writeBoundaryReached = true
            };
            using var cancellation = new CancellationTokenSource();

            AtomicFile.AfterTemporaryValidationBeforeCommitTestHook = _ => cancellation.Cancel();
            try
            {
                AssertThrows<OperationCanceledException>(() => service.Apply(new ReviewApplyOptions
                {
                    ModRoot = modRoot,
                    ReviewRoot = run.ReviewRoot!,
                    SourceLanguageFolder = "English",
                    LanguageFolderName = "Korean",
                    Overwrite = true
                }, cancellation.Token));
            }
            finally
            {
                AtomicFile.AfterTemporaryValidationBeforeCommitTestHook = null;
            }

            Assert(writeBoundaryReached
                   && File.ReadAllBytes(defPath).SequenceEqual(targetBefore)
                   && File.ReadAllBytes(defPath + ".bak").SequenceEqual(backupBefore)
                   && !File.Exists(keyedPath)
                   && !File.Exists(keyedPath + ".bak"),
                "Cancelled Apply did not restore target/backup bytes or remove its not-yet-existing output.");
            AssertPhase08NoTransactionResidue(outputLanguageRoot, "Apply cancellation rollback");
        });
    }

    private static void Phase08RmkExportCancellationRollback()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-phase08-rmk-cancel");
            var defRow = run.Rows.Single(row => row.Key == "Codex_TestWorkbench.label");
            var keyedRow = run.Rows.Single(row => row.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(defRow, "초기 RMK 작업대")]);

            var root = Directory.GetParent(modRoot)!.FullName;
            var workspace = CreateRmkWorkspace(root, "Phase08CancelEntry", out var entryRoot);
            var defPath = Path.Combine(entryRoot, "Languages", "Korean", "DefInjected", "ThingDef", "CodexAI.xml");
            var keyedPath = Path.Combine(entryRoot, "Languages", "Korean", "Keyed", "SampleKeys.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(defPath)!);
            File.WriteAllText(
                defPath,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><LanguageData><Phase08.RmkBefore>before</Phase08.RmkBefore></LanguageData>",
                new UTF8Encoding(false));
            var workbookPath = Path.Combine(entryRoot, "phase08-history.xlsx");
            var service = CreateRmkExportService();
            var options = new RmkExportOptions
            {
                RmkWorkspaceRoot = workspace,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                RmkLanguageFolderName = "Korean",
                WorkbookPath = workbookPath,
                Overwrite = true
            };
            var initial = service.Export(options);
            Assert(initial.EligibleEntries == 1 && File.Exists(workbookPath) && File.Exists(defPath + ".bak"),
                "The RMK cancellation fixture could not establish its initial committed state.");
            File.WriteAllBytes(workbookPath + ".bak", [0x52, 0x4d, 0x4b, 0x2d, 0x50, 0x48, 0x41, 0x53, 0x45, 0x30, 0x38]);
            var workbookBefore = File.ReadAllBytes(workbookPath);
            var workbookBackupBefore = File.ReadAllBytes(workbookPath + ".bak");
            var defBefore = File.ReadAllBytes(defPath);
            var defBackupBefore = File.ReadAllBytes(defPath + ".bak");
            SaveReviewDecisions(run, [(defRow, "변경 RMK 작업대"), (keyedRow, "변경 RMK 번역")]);

            var writeBoundaryReached = false;
            service.AfterWriteBoundaryLockedTestHook = () => writeBoundaryReached = true;
            using var cancellation = new CancellationTokenSource();
            AtomicFile.AfterTemporaryValidationBeforeCommitTestHook = _ => cancellation.Cancel();
            try
            {
                AssertThrows<OperationCanceledException>(() => service.Export(options, cancellation.Token));
            }
            finally
            {
                AtomicFile.AfterTemporaryValidationBeforeCommitTestHook = null;
            }

            Assert(writeBoundaryReached
                   && File.ReadAllBytes(workbookPath).SequenceEqual(workbookBefore)
                   && File.ReadAllBytes(workbookPath + ".bak").SequenceEqual(workbookBackupBefore)
                   && File.ReadAllBytes(defPath).SequenceEqual(defBefore)
                   && File.ReadAllBytes(defPath + ".bak").SequenceEqual(defBackupBefore)
                   && !File.Exists(keyedPath)
                   && !File.Exists(keyedPath + ".bak"),
                "Cancelled RMK export did not restore workbook/XML/backup bytes or preserve absent outputs.");
            AssertPhase08NoTransactionResidue(entryRoot, "RMK export cancellation rollback");
        });
    }

    private static TranslationProject Phase08TargetProject() => new()
    {
        Name = "Phase08NewTarget",
        WorkshopId = "8080",
        PackageId = "phase08.synthetic.target"
    };

    private static string Phase08TargetEntryRoot(string workspace, TranslationProject project) =>
        Path.Combine(workspace, "Data", $"{project.Name} - {project.WorkshopId}");

    private static string Phase08TargetYaml(TranslationProject project) =>
        $"BuildRule:\n  Binding:\n    PackageID: [\"{project.PackageId}\"]\n  Version:\n    Default: \"1.6\"\nMetadata:\n  WorkshopID: \"{project.WorkshopId}\"\n  ModName: \"{project.Name}\"\n";

    private static void AssertPhase08NoTransactionResidue(string root, string scenario)
    {
        if (!Directory.Exists(root)) return;
        var residue = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains(".transaction.bak", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                       || name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase);
            })
            .Concat(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains(".rmk-create.tmp", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert(residue.Length == 0,
            $"{scenario} left transaction/temp residue: {string.Join(", ", residue.Select(Path.GetFileName))}");
    }
}
