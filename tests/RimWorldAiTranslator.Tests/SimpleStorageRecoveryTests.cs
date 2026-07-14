using System.Diagnostics;
using System.Text;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void SimpleStorageRecoveryThreatModel()
    {
        WithTempRoot(root =>
        {
            ExerciseSimpleNewAndExistingSave(root);
            ExerciseSimpleFailureAndCancellationRollback(root);
            ExerciseSimpleValidationAndConflict(root);
            ExerciseSimpleInterruptedTransaction(root);
        });
    }

    private static void ExerciseSimpleNewAndExistingSave(string root)
    {
        var target = Path.Combine(root, "new-and-existing.json");
        FileTransaction.Execute(
            [target],
            () => AtomicFile.WriteUtf8Validated(
                target,
                "{\"value\":1}",
                prepared => _ = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(prepared))),
            "new JSON save");
        Assert(File.ReadAllText(target, Encoding.UTF8) == "{\"value\":1}"
               && !File.Exists(target + ".bak"),
            "A new transaction output was not committed cleanly.");

        FileTransaction.Execute(
            [target],
            () => AtomicFile.WriteUtf8Validated(
                target,
                "{\"value\":2}",
                prepared => _ = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(prepared))),
            "existing JSON save");
        Assert(File.ReadAllText(target, Encoding.UTF8) == "{\"value\":2}"
               && File.ReadAllText(target + ".bak", Encoding.UTF8) == "{\"value\":1}",
            "An existing transaction output was not backed up before replacement.");
    }

    private static void ExerciseSimpleFailureAndCancellationRollback(string root)
    {
        var first = Path.Combine(root, "rollback-first.txt");
        var second = Path.Combine(root, "rollback-second.txt");
        File.WriteAllText(first, "first-old", new UTF8Encoding(false));
        File.WriteAllText(second, "second-old", new UTF8Encoding(false));

        _ = CaptureException<IOException>(() => FileTransaction.Execute(
            [first, second],
            () =>
            {
                AtomicFile.WriteUtf8(first, "first-new");
                throw new IOException("synthetic second-target write failure");
            },
            "two-file failure"));
        Assert(File.ReadAllText(first, Encoding.UTF8) == "first-old"
               && File.ReadAllText(second, Encoding.UTF8) == "second-old",
            "A second-target failure did not restore the first target.");

        using var cancellation = new CancellationTokenSource();
        _ = CaptureException<OperationCanceledException>(() => FileTransaction.Execute(
            [first, second],
            () =>
            {
                AtomicFile.WriteUtf8(first, "first-cancelled");
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            },
            "two-file cancellation",
            cancellation.Token));
        Assert(File.ReadAllText(first, Encoding.UTF8) == "first-old"
               && File.ReadAllText(second, Encoding.UTF8) == "second-old",
            "Cancellation did not restore files already replaced by the transaction.");
    }

    private static void ExerciseSimpleValidationAndConflict(string root)
    {
        var xml = Path.Combine(root, "validated.xml");
        File.WriteAllText(xml, "<LanguageData><Key>old</Key></LanguageData>", new UTF8Encoding(false));
        _ = CaptureException<IOException>(() => FileTransaction.Execute(
            [xml],
            () => AtomicFile.WriteUtf8Validated(
                xml,
                "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///never-read'>]><LanguageData>&e;</LanguageData>",
                prepared => _ = SafeXml.Load(prepared)),
            "invalid XML save"));
        Assert(File.ReadAllText(xml, Encoding.UTF8)
                == "<LanguageData><Key>old</Key></LanguageData>",
            "An XML file changed before DTD/external-entity validation succeeded.");

        var conflict = Path.Combine(root, "ordinary-editor-conflict.txt");
        File.WriteAllText(conflict, "original", new UTF8Encoding(false));
        using var boundary = PathSafety.AcquireTrustedWriteBoundary(root, [conflict]);
        try
        {
            AtomicFile.AfterTemporaryValidationBeforeCommitTestHook = _ =>
                File.WriteAllText(conflict, "edited-elsewhere", new UTF8Encoding(false));
            _ = CaptureException<ConcurrentLeafChangeException>(() =>
                AtomicFile.WriteBytesValidated(
                    conflict,
                    Encoding.UTF8.GetBytes("app-output"),
                    _ => { },
                    boundary));
        }
        finally
        {
            AtomicFile.AfterTemporaryValidationBeforeCommitTestHook = null;
        }
        Assert(File.ReadAllText(conflict, Encoding.UTF8) == "edited-elsewhere",
            "A normal external edit was silently overwritten after the write boundary was captured.");
    }

    private static void ExerciseSimpleInterruptedTransaction(string root)
    {
        var targetRoot = Path.Combine(root, "interrupted-target");
        var authorityRoot = Path.Combine(root, "interrupted-authority");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(authorityRoot);
        var first = Path.Combine(targetRoot, "first.txt");
        var second = Path.Combine(targetRoot, "second.txt");
        var signal = Path.Combine(root, "interrupted.signal");
        File.WriteAllText(first, "first-old", new UTF8Encoding(false));
        File.WriteAllText(second, "second-old", new UTF8Encoding(false));

        using (var child = StartSimpleStorageChild(targetRoot, authorityRoot, signal))
        {
            WaitForSimpleStorageSignal(signal, child, TimeSpan.FromSeconds(15));
            child.Kill(entireProcessTree: true);
            Assert(child.WaitForExit(15_000), "The interrupted-storage child did not stop.");
        }

        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        var pending = authority.FindPending(targetRoot);
        Assert(pending.Count == 1,
            "An app-owned interrupted transaction was not detected.");
        Assert(File.ReadAllText(first, Encoding.UTF8) == "first-new"
               && File.ReadAllText(second, Encoding.UTF8) == "second-old",
            "The interrupted fixture did not stop after exactly one replacement.");

        _ = CaptureException<IncompleteFileTransactionException>(() =>
            authority.RecoverPending(targetRoot));
        Assert(File.ReadAllText(first, Encoding.UTF8) == "first-new"
               && File.ReadAllText(second, Encoding.UTF8) == "second-old",
            "Pending recovery changed files without an explicit user restore choice.");

        authority.Restore(pending.Single());
        Assert(File.ReadAllText(first, Encoding.UTF8) == "first-old"
               && File.ReadAllText(second, Encoding.UTF8) == "second-old"
               && authority.FindPending(targetRoot).Count == 0,
            "Explicit backup restoration did not restore and resolve the interrupted transaction.");
    }

    private static int RunSimpleStorageCrashChild(
        string targetRoot,
        string authorityRoot,
        string signal)
    {
        var first = Path.Combine(targetRoot, "first.txt");
        var second = Path.Combine(targetRoot, "second.txt");
        var authority = new FileTransactionRecoveryAuthority(authorityRoot);
        using var recovery = FileTransaction.AcquireRecoveryLease(authority, targetRoot);
        using var boundary = PathSafety.AcquireTrustedWriteBoundary(targetRoot, [first, second]);
        FileTransaction.Execute(
            [first, second],
            () =>
            {
                AtomicFile.WriteBytesValidated(
                    first,
                    Encoding.UTF8.GetBytes("first-new"),
                    _ => { },
                    boundary);
                File.WriteAllText(signal, "ready", new UTF8Encoding(false));
                Thread.Sleep(Timeout.Infinite);
            },
            "interrupted characterization",
            boundary.ReleaseWriteLeafLocksForRollback,
            boundary,
            recovery,
            CancellationToken.None);
        return 0;
    }

    private static Process StartSimpleStorageChild(
        string targetRoot,
        string authorityRoot,
        string signal)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The regression process path is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--simple-storage-crash-child");
        start.ArgumentList.Add(targetRoot);
        start.ArgumentList.Add(authorityRoot);
        start.ArgumentList.Add(signal);
        return Process.Start(start)
            ?? throw new InvalidOperationException("The interrupted-storage child did not start.");
    }

    private static void WaitForSimpleStorageSignal(string signal, Process child, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (File.Exists(signal)) return;
            if (child.HasExited)
                throw new InvalidOperationException($"The interrupted-storage child exited early ({child.ExitCode}).");
            Thread.Sleep(25);
        }
        throw new TimeoutException("The interrupted-storage child did not reach the replacement boundary.");
    }

    private static string[] RecoveryTransactionDirectories(string authorityRoot) =>
        Directory.EnumerateDirectories(
                authorityRoot,
                "transaction-*",
                SearchOption.TopDirectoryOnly)
            .ToArray();

    private static void RunSimpleStorageBenchmark(int targetCount)
    {
        WithTempRoot(root =>
        {
            var authorityRoot = Path.Combine(root, "authority");
            var targetRoot = Path.Combine(root, "targets");
            Directory.CreateDirectory(authorityRoot);
            Directory.CreateDirectory(targetRoot);
            var targets = Enumerable.Range(0, targetCount)
                .Select(index => Path.Combine(targetRoot, $"target-{index:D4}.json"))
                .ToArray();
            foreach (var target in targets)
                File.WriteAllText(target, "{\"value\":0}", new UTF8Encoding(false));
            var authority = new FileTransactionRecoveryAuthority(authorityRoot);
            var watch = Stopwatch.StartNew();
            using (var recovery = FileTransaction.AcquireRecoveryLease(authority, targetRoot))
            using (var boundary = PathSafety.AcquireTrustedWriteBoundary(targetRoot, targets))
            {
                FileTransaction.Execute(
                    targets,
                    () =>
                    {
                        foreach (var target in targets)
                        {
                            AtomicFile.WriteBytesValidated(
                                target,
                                Encoding.UTF8.GetBytes("{\"value\":1}"),
                                prepared => _ = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(prepared)),
                                boundary);
                        }
                    },
                    "storage benchmark",
                    boundary.ReleaseWriteLeafLocksForRollback,
                    boundary,
                    recovery,
                    CancellationToken.None);
            }
            watch.Stop();
            Console.WriteLine($"METRIC simple-storage targets={targetCount} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3}");
        });
    }
}
