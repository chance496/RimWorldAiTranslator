using System.Text;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void Phase08AtomicStorageFaults()
    {
        WithTempRoot(root =>
        {
            ExerciseAtomicParentRedirectPreflight(root);

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
            Assert(!EnumerateAtomicTemporaryFiles(root, target).Any(),
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
            Assert(!EnumerateAtomicTemporaryFiles(root, target).Any(),
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
            Assert(!EnumerateAtomicTemporaryFiles(root, target).Any(),
                "A read-only-target failure left a temporary file behind.");
            Assert(Encoding.UTF8.GetString(expectedPrimary) == "last-good-two",
                "The storage fault fixture did not preserve the expected last-good content.");
        });
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
        _ = CaptureException<InvalidDataException>(() =>
            AtomicFile.WriteUtf8(
                Path.Combine(redirectedAlias, "must-not-create-this-directory", "nested", "must-not-be-created.json"),
                "private-missing-descendant-payload",
                keepBackup: false));

        Assert(!Directory.EnumerateFileSystemEntries(
                    redirectedDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly).Any(),
            "Atomic parent validation created output below a redirected directory.");
    }

    private static void Phase08ProjectLoadCancellation()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(Path.Combine(root, "appdata"));
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

    private static IEnumerable<string> EnumerateAtomicTemporaryFiles(string root, string target) =>
        Directory.EnumerateFiles(
            root,
            $".{Path.GetFileName(target)}.*.tmp",
            SearchOption.TopDirectoryOnly);
}
