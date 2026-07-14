using System.Buffers.Binary;
using System.IO.Compression;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RimWorldAiTranslator.Core.Apply;
using RimWorldAiTranslator.Core.Rmk;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;
using RimWorldAiTranslator.Core.Xml;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static void Phase07NativeArchiveAndWriteBoundary()
    {
        Phase07DriveRootNormalization();
        Phase07ReadBoundaryCreatesNoSourceFiles();
        Phase07SnapshotBudgetsAndCancellation();
        Phase07RollbackPreservesConcurrentSaves();
        Phase07DirectoryRollbackStateTransitions();
        Phase07NativeWorkbookPackageGuards();
        Phase07NativeXmlDepthLimit();
        Phase07RmkStaleOutputEvidence();
        Phase07RmkLanguageWalkerBoundary();
        Phase07RmkResourceBudgets();
    }

    private static void Phase07DirectoryRollbackStateTransitions()
    {
        WithTempRoot(root =>
        {
            var existing = Path.Combine(root, "existing.txt");
            var blocker = Path.Combine(root, "blocked.txt");
            File.WriteAllText(existing, "before", new UTF8Encoding(false));
            Directory.CreateDirectory(blocker);
            var rolledBack = CaptureException<IOException>(() => FileTransaction.Execute(
                [existing, blocker],
                () =>
                {
                    File.WriteAllText(existing, "changed", new UTF8Encoding(false));
                    File.WriteAllText(blocker, "cannot replace a directory", new UTF8Encoding(false));
                },
                "Phase 07 existing-directory rollback"));
            Assert(rolledBack.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase)
                   && File.ReadAllText(existing) == "before"
                   && Directory.Exists(blocker),
                "An unchanged initial directory prevented rollback of other transaction files.");
        });

        WithTempRoot(root =>
        {
            var blocker = Path.Combine(root, "removed-directory");
            Directory.CreateDirectory(blocker);
            var error = CaptureException<IOException>(() => FileTransaction.Execute(
                [blocker],
                () =>
                {
                    Directory.Delete(blocker);
                    throw new InvalidDataException("injected directory removal");
                },
                "Phase 07 removed-directory rollback"));
            Assert(error.Message.Contains("rollback was incomplete", StringComparison.OrdinalIgnoreCase)
                   && !Directory.Exists(blocker),
                "Rollback silently treated a removed initial directory as an initially missing leaf.");
        });

        WithTempRoot(root =>
        {
            var blocker = Path.Combine(root, "file-replaced-directory");
            Directory.CreateDirectory(blocker);
            var error = CaptureException<IOException>(() => FileTransaction.Execute(
                [blocker],
                () =>
                {
                    Directory.Delete(blocker);
                    File.WriteAllText(blocker, "replacement", new UTF8Encoding(false));
                    throw new InvalidDataException("injected directory replacement");
                },
                "Phase 07 file-replaced-directory rollback"));
            Assert(error.Message.Contains("rollback was incomplete", StringComparison.OrdinalIgnoreCase)
                   && File.ReadAllText(blocker) == "replacement",
                "Rollback deleted a file that replaced an initial directory or reported success.");
        });

        WithTempRoot(root =>
        {
            var blocker = Path.Combine(root, "concurrent-directory");
            Directory.CreateDirectory(blocker);
            FileSnapshotJournal.BeforeRollbackRestoreTestHook = () =>
            {
                Directory.Delete(blocker);
                Directory.CreateDirectory(blocker);
            };
            try
            {
                AssertThrows<ConcurrentLeafChangeException>(() => FileTransaction.Execute(
                    [blocker],
                    () => throw new InvalidDataException("injected directory transaction failure"),
                    "Phase 07 concurrent-directory rollback"));
                Assert(Directory.Exists(blocker),
                    "Concurrent directory rollback removed the replacement directory.");
            }
            finally
            {
                FileSnapshotJournal.BeforeRollbackRestoreTestHook = null;
            }
        });

        if (OperatingSystem.IsWindows())
        {
            WithTempRoot(root =>
            {
                var realDirectory = Path.Combine(root, "real-directory");
                var aliasDirectory = Path.Combine(root, "directory-alias");
                Directory.CreateDirectory(realDirectory);
                try
                {
                    Directory.CreateSymbolicLink(aliasDirectory, realDirectory);
                    var actionInvoked = false;
                    var error = CaptureException<IOException>(() => FileTransaction.Execute(
                        [aliasDirectory],
                        () => actionInvoked = true,
                        "Phase 07 reparse-directory snapshot"));
                    Assert(error.InnerException is InvalidDataException && !actionInvoked,
                        "A reparse-directory transaction target reached its action.");
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or PlatformNotSupportedException)
                {
                    Console.WriteLine(
                        $"[INFO] Directory-link fixture unavailable ({exception.GetType().Name}); handle-level reparse rejection remained compiled.");
                }
            });
        }
    }

    private static void Phase07DriveRootNormalization()
    {
        var driveRoot = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("The Phase 07 fixture requires a rooted working directory.");
        var canonicalRoot = Path.GetFullPath(driveRoot);
        Assert(PathSafety.Normalize(driveRoot).Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase),
            "Path normalization removed the separator from a drive root.");
        Assert(!PathSafety.IsStrictlyInside(driveRoot, driveRoot),
            "A drive root was classified as strictly inside itself.");

        var child = Path.Combine(driveRoot, "phase07-root-child");
        Assert(PathSafety.IsStrictlyInside(child, driveRoot),
            "A direct child was not classified inside a drive root.");
        Assert(PathSafety.ResolveInside(driveRoot, "phase07-root-child")
                .Equals(PathSafety.Normalize(child), StringComparison.OrdinalIgnoreCase),
            "Drive-root resolution did not retain its rooted path semantics.");
    }

    private static void Phase07ReadBoundaryCreatesNoSourceFiles()
    {
        WithTempRoot(root =>
        {
            var workshopRoot = Path.Combine(root, "steamapps", "workshop", "content", "294100", "1234567890");
            var sourceDirectory = Path.Combine(workshopRoot, "Languages", "English", "Keyed");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "Phase07.xml");
            const string source = "<LanguageData><Phase07.ReadBoundary>source</Phase07.ReadBoundary></LanguageData>";
            File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
            var before = EnumerateRelativeFiles(workshopRoot);

            using (var boundary = PathSafety.AcquireTrustedReadBoundary(workshopRoot, [sourcePath]))
            {
                Assert(EnumerateRelativeFiles(workshopRoot).SequenceEqual(before, StringComparer.OrdinalIgnoreCase),
                    "Acquiring a trusted read boundary created a file in the source tree.");
                File.WriteAllText(sourcePath, "PHASE07-UNTRUSTED-WRITE", new UTF8Encoding(false));
                AssertThrows<ConcurrentLeafChangeException>(() => boundary.VerifyUnchanged());
                File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
                boundary.VerifyUnchanged();
            }

            Assert(EnumerateRelativeFiles(workshopRoot).SequenceEqual(before, StringComparer.OrdinalIgnoreCase),
                "Releasing a trusted read boundary left a file in the source tree.");
            Assert(File.ReadAllText(sourcePath) == source,
                "The trusted read boundary did not retain its protected source leaf.");

            var missingDirectory = Path.Combine(workshopRoot, "missing", "Keyed");
            AssertThrows<DirectoryNotFoundException>(() =>
            {
                using var boundary = PathSafety.AcquireTrustedReadBoundary(
                    workshopRoot,
                    [Path.Combine(missingDirectory, "Missing.xml")]);
            });
            Assert(!Directory.Exists(missingDirectory),
                "A trusted read boundary created a missing source directory.");
        });

        static string[] EnumerateRelativeFiles(string root) => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void Phase07SnapshotBudgetsAndCancellation()
    {
        WithTempRoot(root =>
        {
            var targetPath = Path.Combine(root, "bounded.txt");
            var backupPath = targetPath + ".bak";
            File.WriteAllText(targetPath, "before", new UTF8Encoding(false));
            using (var stream = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(PathSafety.MaximumTrustedLeafBytes + 1);

            AssertThrows<InvalidDataException>(() =>
            {
                using var boundary = PathSafety.AcquireTrustedWriteBoundary(root, [targetPath]);
            });
            var actionInvoked = false;
            var snapshotError = CapturePhase07Exception<IOException>(() => FileTransaction.Execute(
                [targetPath],
                () => actionInvoked = true,
                "Phase 07 oversized backup"));
            Assert(snapshotError.InnerException is InvalidDataException && !actionInvoked,
                "An oversized deterministic backup was not rejected before the transaction action.");
            Assert(TransactionSnapshots(root).Length == 0,
                "Oversized snapshot rejection left a partial recovery snapshot.");
            AssertNoWriteBoundaryGuards(root, "oversized backup rejection");

            File.Delete(backupPath);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            AssertThrows<OperationCanceledException>(() =>
            {
                using var boundary = PathSafety.AcquireTrustedReadBoundary(root, [targetPath], cancellation.Token);
            });
            actionInvoked = false;
            AssertThrows<OperationCanceledException>(() => FileTransaction.Execute(
                [targetPath],
                () => actionInvoked = true,
                "Phase 07 cancelled snapshot",
                cancellation.Token));
            Assert(!actionInvoked && TransactionSnapshots(root).Length == 0,
                "Cancelled snapshot acquisition invoked the action or left recovery artifacts.");

            actionInvoked = false;
            var excessiveTargets = Enumerable.Range(0, FileSnapshotJournal.MaximumSnapshotTargets + 1)
                .Select(index => Path.Combine(root, $"missing-{index:D5}.txt"));
            var countError = CapturePhase07Exception<IOException>(() => FileTransaction.Execute(
                excessiveTargets,
                () => actionInvoked = true,
                "Phase 07 excessive snapshot targets"));
            Assert(countError.InnerException is InvalidDataException && !actionInvoked,
                "An excessive snapshot target set reached the transaction action.");

            var translationTargets = Enumerable.Range(0, FileSnapshotJournal.MaximumSnapshotTargets + 1)
                .Select(index => Path.Combine(root, $"translation-{index:D5}.xml"));
            AssertThrows<InvalidDataException>(() =>
                LanguageFileService.ValidateTransactionTargetCount(translationTargets));
            LanguageFileService.ValidateTransactionTargetCount(
                Enumerable.Repeat(targetPath, FileSnapshotJournal.MaximumSnapshotTargets + 1));
        });
    }

    private static void Phase07RollbackPreservesConcurrentSaves()
    {
        WithTempRoot(root =>
        {
            var targetPath = Path.Combine(root, "sync.txt");
            File.WriteAllText(targetPath, "before", new UTF8Encoding(false));
            FileSnapshotJournal.BeforeRollbackRestoreTestHook = () =>
                File.WriteAllText(targetPath, "user-concurrent", new UTF8Encoding(false));
            try
            {
                var error = CapturePhase07Exception<ConcurrentLeafChangeException>(() => FileTransaction.Execute(
                    [targetPath],
                    () =>
                    {
                        File.WriteAllText(targetPath, "transaction", new UTF8Encoding(false));
                        throw new InvalidDataException("injected Phase 07 transaction failure");
                    },
                    "Phase 07 concurrent rollback"));
                Assert(error.InnerException is InvalidDataException,
                    "Concurrent rollback did not retain the original operation failure.");
                Assert(File.ReadAllText(targetPath) == "user-concurrent",
                    "Synchronous rollback overwrote a concurrent user save.");
            }
            finally
            {
                FileSnapshotJournal.BeforeRollbackRestoreTestHook = null;
            }
        });

        WithTempRoot(root =>
        {
            var targetPath = Path.Combine(root, "created.txt");
            FileSnapshotJournal.BeforeRollbackRestoreTestHook = () =>
                File.WriteAllText(targetPath, "user-created", new UTF8Encoding(false));
            try
            {
                AssertThrows<ConcurrentLeafChangeException>(() => FileTransaction.Execute(
                    [targetPath],
                    () =>
                    {
                        File.WriteAllText(targetPath, "transaction-created", new UTF8Encoding(false));
                        throw new InvalidDataException("injected Phase 07 created-file failure");
                    },
                    "Phase 07 created-file rollback"));
                Assert(File.ReadAllText(targetPath) == "user-created",
                    "Rollback deleted a concurrent save at a transaction-created path.");
            }
            finally
            {
                FileSnapshotJournal.BeforeRollbackRestoreTestHook = null;
            }
        });

        WithTempRoot(root =>
        {
            var targetPath = Path.Combine(root, "async.txt");
            File.WriteAllText(targetPath, "before", new UTF8Encoding(false));
            FileSnapshotJournal.BeforeRollbackRestoreTestHook = () =>
                File.WriteAllText(targetPath, "user-async", new UTF8Encoding(false));
            try
            {
                AssertThrows<ConcurrentLeafChangeException>(() => FileTransaction.ExecuteAsync(
                        [targetPath],
                        () =>
                        {
                            File.WriteAllText(targetPath, "transaction-async", new UTF8Encoding(false));
                            return Task.FromException<int>(
                                new InvalidDataException("injected Phase 07 async transaction failure"));
                        },
                        "Phase 07 async rollback")
                    .GetAwaiter()
                    .GetResult());
                Assert(File.ReadAllText(targetPath) == "user-async",
                    "Asynchronous rollback overwrote a concurrent user save.");
            }
            finally
            {
                FileSnapshotJournal.BeforeRollbackRestoreTestHook = null;
            }
        });

        WithTempRoot(root =>
        {
            var firstPath = Path.Combine(root, "A.xml");
            var secondPath = Path.Combine(root, "B.xml");
            File.WriteAllText(
                firstPath,
                "<LanguageData><Phase07.Value>before-a</Phase07.Value></LanguageData>",
                new UTF8Encoding(false));
            File.WriteAllText(
                secondPath,
                "<LanguageData><Phase07.Value>before-b</Phase07.Value></LanguageData>",
                new UTF8Encoding(false));
            const string userSave = "<LanguageData><Phase07.Value>user-language</Phase07.Value></LanguageData>";
            FileSnapshotJournal.BeforeRollbackRestoreTestHook = () =>
                File.WriteAllText(firstPath, userSave, new UTF8Encoding(false));
            try
            {
                var service = new LanguageFileService();
                AssertThrows<ConcurrentLeafChangeException>(() => service.WriteTransaction(
                    new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [firstPath] = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Phase07.Value"] = "transaction-language"
                        },
                        [secondPath] = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["invalid key"] = "injected failure"
                        }
                    },
                    overwrite: true));
                Assert(File.ReadAllText(firstPath) == userSave,
                    "Language transaction rollback overwrote a concurrent user save.");
                Assert(new LanguageFileService().Read(secondPath)["Phase07.Value"] == "before-b",
                    "Language transaction rollback changed an untouched file.");
            }
            finally
            {
                FileSnapshotJournal.BeforeRollbackRestoreTestHook = null;
            }
        });
    }

    private static void Phase07NativeWorkbookPackageGuards()
    {
        WithTempRoot(root =>
        {
            var validWorkbook = Path.Combine(root, "valid.xlsx");
            RimWorldTranslatorRmkXlsxWriter.Write(
                validWorkbook,
                [
                    new RimWorldTranslatorRmkHistoryRow
                    {
                        Identifier = "Keyed+Phase07.Synthetic",
                        ClassName = "Keyed",
                        Key = "Phase07.Synthetic",
                        Source = "Synthetic source",
                        Translation = "Synthetic translation"
                    }
                ],
                "English");
            Assert(RimWorldTranslatorRmkXlsxReader.Read(validWorkbook).Rows.Count == 1,
                "The Phase 07 native XLSX baseline was not readable before mutation.");

            var unsafeEntryWorkbook = CopyWorkbook(validWorkbook, root, "unsafe-entry.xlsx");
            using (var archive = ZipFile.Open(unsafeEntryWorkbook, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry("../PHASE07-OUTSIDE.xml", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write("<outside>synthetic</outside>");
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(unsafeEntryWorkbook));
            Assert(!File.Exists(Path.Combine(root, "PHASE07-OUTSIDE.xml")),
                "Reading an XLSX with an unsafe package path created an outside file.");

            var duplicateEntryWorkbook = CopyWorkbook(validWorkbook, root, "duplicate-entry.xlsx");
            using (var archive = ZipFile.Open(duplicateEntryWorkbook, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry("xl/workbook.xml", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>");
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(duplicateEntryWorkbook));

            var externalRelationshipWorkbook = CopyWorkbook(validWorkbook, root, "external-relationship.xlsx");
            using (var archive = ZipFile.Open(externalRelationshipWorkbook, ZipArchiveMode.Update))
            {
                ReplaceZipEntry(archive, "xl/_rels/workbook.xml.rels", content => content.Replace(
                    "Target=\"worksheets/sheet1.xml\"",
                    "Target=\"https://outside.invalid/phase07.xml\" TargetMode=\"External\"",
                    StringComparison.Ordinal));
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(externalRelationshipWorkbook));

            var compressionBombWorkbook = CopyWorkbook(validWorkbook, root, "compression-ratio.xlsx");
            using (var archive = ZipFile.Open(compressionBombWorkbook, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry("customXml/phase07-compression-limit.txt", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(new string('A', 2 * 1024 * 1024));
            }
            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.Read(compressionBombWorkbook));

            var updateRows = new[]
            {
                new RimWorldTranslatorRmkHistoryRow
                {
                    Identifier = "Keyed+Phase07.Synthetic",
                    ClassName = "Keyed",
                    Key = "Phase07.Synthetic",
                    Source = "Synthetic source",
                    Translation = "Updated synthetic translation"
                }
            };
            var eocdSentinelWorkbook = CreateMutatedWorkbook(
                validWorkbook,
                root,
                "zip64-eocd-sentinel.xlsx",
                static (bytes, layout) =>
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes.AsSpan(layout.EndRecordOffset + 8, sizeof(ushort)),
                        ushort.MaxValue);
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes.AsSpan(layout.EndRecordOffset + 10, sizeof(ushort)),
                        ushort.MaxValue);
                    return bytes;
                });
            AssertNativeWorkbookReadAndUpdateRejected(
                eocdSentinelWorkbook,
                updateRows,
                "an EOCD Zip64 sentinel without a locator");

            var centralSizeSentinelWorkbook = CreateMutatedWorkbook(
                validWorkbook,
                root,
                "zip64-central-size-sentinel.xlsx",
                static (bytes, layout) =>
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes.AsSpan(layout.DirectoryOffset + 20, sizeof(uint)),
                        uint.MaxValue);
                    return bytes;
                });
            AssertNativeWorkbookReadAndUpdateRejected(
                centralSizeSentinelWorkbook,
                updateRows,
                "a per-entry central-directory Zip64 size sentinel");

            var centralOffsetSentinelWorkbook = CreateMutatedWorkbook(
                validWorkbook,
                root,
                "zip64-central-offset-sentinel.xlsx",
                static (bytes, layout) =>
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes.AsSpan(layout.DirectoryOffset + 42, sizeof(uint)),
                        uint.MaxValue);
                    return bytes;
                });
            AssertNativeWorkbookReadAndUpdateRejected(
                centralOffsetSentinelWorkbook,
                updateRows,
                "a per-entry central-directory Zip64 local-header sentinel");

            var centralSplitDiskWorkbook = CreateMutatedWorkbook(
                validWorkbook,
                root,
                "central-directory-split-disk.xlsx",
                static (bytes, layout) =>
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes.AsSpan(layout.DirectoryOffset + 34, sizeof(ushort)),
                        1);
                    return bytes;
                });
            AssertNativeWorkbookReadAndUpdateRejected(
                centralSplitDiskWorkbook,
                updateRows,
                "a per-entry split-disk start index");

            var zip64ExtraWorkbook = CreateMutatedWorkbook(
                validWorkbook,
                root,
                "zip64-redundant-extra.xlsx",
                AddRedundantZip64ExtraField);
            AssertNativeWorkbookReadAndUpdateRejected(
                zip64ExtraWorkbook,
                updateRows,
                "a redundant central-directory Zip64 extra field");

            var trailingRecordWorkbook = CreateMutatedWorkbook(
                validWorkbook,
                root,
                "central-directory-trailing-record.xlsx",
                static (bytes, layout) =>
                {
                    var declared = checked((ushort)(layout.EntryCount - 1));
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes.AsSpan(layout.EndRecordOffset + 8, sizeof(ushort)),
                        declared);
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes.AsSpan(layout.EndRecordOffset + 10, sizeof(ushort)),
                        declared);
                    return bytes;
                });
            AssertNativeWorkbookReadAndUpdateRejected(
                trailingRecordWorkbook,
                updateRows,
                "a central-directory record trailing the declared count");

            var missingRecordWorkbook = CreateMutatedWorkbook(
                validWorkbook,
                root,
                "central-directory-missing-record.xlsx",
                static (bytes, layout) =>
                {
                    var declared = checked((ushort)(layout.EntryCount + 1));
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes.AsSpan(layout.EndRecordOffset + 8, sizeof(ushort)),
                        declared);
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes.AsSpan(layout.EndRecordOffset + 10, sizeof(ushort)),
                        declared);
                    return bytes;
                });
            AssertNativeWorkbookReadAndUpdateRejected(
                missingRecordWorkbook,
                updateRows,
                "a missing central-directory record");

            var locatorWorkbook = CreateMutatedWorkbook(
                validWorkbook,
                root,
                "zip64-locator.xlsx",
                static (bytes, layout) =>
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes.AsSpan(layout.EndRecordOffset - 20, sizeof(uint)),
                        0x07064b50U);
                    return bytes;
                });
            AssertNativeWorkbookReadAndUpdateRejected(
                locatorWorkbook,
                updateRows,
                "a Zip64 end-of-central-directory locator");
        });
    }

    private static void AssertNativeWorkbookReadAndUpdateRejected(
        string workbookPath,
        IReadOnlyList<RimWorldTranslatorRmkHistoryRow> updateRows,
        string mutation)
    {
        var original = File.ReadAllBytes(workbookPath);
        AssertThrows<InvalidDataException>(() => RimWorldTranslatorRmkXlsxReader.Read(workbookPath));
        Assert(File.ReadAllBytes(workbookPath).SequenceEqual(original),
            $"Reading a workbook with {mutation} changed its bytes.");

        AssertThrows<InvalidDataException>(() =>
            RimWorldTranslatorRmkXlsxWriter.Write(workbookPath, updateRows, "English"));
        Assert(File.ReadAllBytes(workbookPath).SequenceEqual(original),
            $"Updating a workbook with {mutation} changed its bytes.");
        Assert(!File.Exists(workbookPath + ".bak"),
            $"Updating a workbook with {mutation} created a backup before validation.");
        var directory = Path.GetDirectoryName(workbookPath)!;
        Assert(!Directory.EnumerateFiles(
                directory,
                Path.GetFileName(workbookPath) + ".tmp-*",
                SearchOption.TopDirectoryOnly)
            .Any(),
            $"Updating a workbook with {mutation} retained a temporary output.");
    }

    private static string CreateMutatedWorkbook(
        string source,
        string root,
        string name,
        Func<byte[], ZipCentralDirectoryLayout, byte[]> mutation)
    {
        var bytes = File.ReadAllBytes(source);
        var layout = FindZipCentralDirectory(bytes);
        var mutated = mutation(bytes, layout);
        var target = Path.Combine(root, name);
        File.WriteAllBytes(target, mutated);
        return target;
    }

    private static ZipCentralDirectoryLayout FindZipCentralDirectory(byte[] bytes)
    {
        const uint endRecordSignature = 0x06054b50U;
        const int endRecordBytes = 22;
        var minimumOffset = Math.Max(0, bytes.Length - endRecordBytes - ushort.MaxValue);
        for (var offset = bytes.Length - endRecordBytes; offset >= minimumOffset; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)))
                != endRecordSignature)
                continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(offset + 20, sizeof(ushort)));
            if (offset + endRecordBytes + commentLength != bytes.Length) continue;
            var directorySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 12, sizeof(uint))));
            var directoryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 16, sizeof(uint))));
            var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(offset + 10, sizeof(ushort)));
            Assert(directoryOffset >= 0
                   && directorySize > 0
                   && directoryOffset + directorySize == offset
                   && entryCount > 1,
                "The native Zip64 hostile-test baseline has an invalid central directory.");
            return new ZipCentralDirectoryLayout(offset, directoryOffset, directorySize, entryCount);
        }
        throw new InvalidDataException("The native Zip64 hostile-test baseline has no ZIP end record.");
    }

    private static byte[] AddRedundantZip64ExtraField(
        byte[] bytes,
        ZipCentralDirectoryLayout layout)
    {
        const int fixedHeaderBytes = 46;
        const int zip64ExtraBytes = 12;
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(layout.DirectoryOffset + 28, sizeof(ushort)));
        var oldExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(layout.DirectoryOffset + 30, sizeof(ushort)));
        var insertOffset = checked(layout.DirectoryOffset + fixedHeaderBytes + nameLength + oldExtraLength);
        var result = new byte[checked(bytes.Length + zip64ExtraBytes)];
        Buffer.BlockCopy(bytes, 0, result, 0, insertOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(insertOffset, sizeof(ushort)), 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(insertOffset + 2, sizeof(ushort)), 8);
        var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(layout.DirectoryOffset + 24, sizeof(uint)));
        BinaryPrimitives.WriteUInt64LittleEndian(
            result.AsSpan(insertOffset + 4, sizeof(ulong)),
            uncompressedSize);
        Buffer.BlockCopy(
            bytes,
            insertOffset,
            result,
            insertOffset + zip64ExtraBytes,
            bytes.Length - insertOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            result.AsSpan(layout.DirectoryOffset + 30, sizeof(ushort)),
            checked((ushort)(oldExtraLength + zip64ExtraBytes)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(layout.EndRecordOffset + zip64ExtraBytes + 12, sizeof(uint)),
            checked((uint)(layout.DirectorySize + zip64ExtraBytes)));
        return result;
    }

    private readonly record struct ZipCentralDirectoryLayout(
        int EndRecordOffset,
        int DirectoryOffset,
        int DirectorySize,
        ushort EntryCount);

    private static string CopyWorkbook(string source, string root, string name)
    {
        var target = Path.Combine(root, name);
        File.Copy(source, target);
        return target;
    }

    private static void Phase07NativeXmlDepthLimit()
    {
        WithTempRoot(root =>
        {
            var path = Path.Combine(root, "native-too-deep.xml");
            const int nestedDepth = 260;
            var xml = new StringBuilder(nestedDepth * 14);
            xml.Append("<LanguageData><Phase07.Deep>");
            for (var index = 0; index < nestedDepth; index++) xml.Append("<node>");
            xml.Append("synthetic");
            for (var index = 0; index < nestedDepth; index++) xml.Append("</node>");
            xml.Append("</Phase07.Deep></LanguageData>");
            File.WriteAllText(path, xml.ToString(), new UTF8Encoding(false));

            AssertThrows<InvalidDataException>(() =>
                RimWorldTranslatorRmkXlsxReader.ReadLanguageData(path));
        });
    }

    private static void Phase07RmkLanguageWalkerBoundary()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-phase07-rmk-walker");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "합성 번역 " + row.Source)]);
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "Phase07Walker", out var entryRoot);
            var languageRoot = Path.Combine(entryRoot, "Languages", "Korean");
            Directory.CreateDirectory(languageRoot);
            var outsideRoot = Path.Combine(fixtureRoot, "phase07-rmk-walker-outside");
            Directory.CreateDirectory(outsideRoot);
            var outsideXml = Path.Combine(outsideRoot, "outside.xml");
            const string outsideContent = "<LanguageData><CodexTranslator.SampleButton>outside-sentinel</CodexTranslator.SampleButton></LanguageData>";
            File.WriteAllText(outsideXml, outsideContent, new UTF8Encoding(false));
            var alias = Path.Combine(languageRoot, "outside-alias");
            Directory.CreateSymbolicLink(alias, outsideRoot);
            var workbookPath = Path.Combine(entryRoot, "phase07-walker.xlsx");
            var options = new RmkExportOptions
            {
                RmkWorkspaceRoot = workspaceRoot,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                ReviewLanguageFolderName = "Korean",
                RmkLanguageFolderName = "Korean",
                SourceLanguage = "English",
                WorkbookPath = workbookPath,
                Overwrite = true,
                ApplyStatus = ReviewApplyStatus.ApprovedOnly
            };
            var service = new RmkExportService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor());
            var result = service.Export(options);
            Assert(result.EligibleEntries == 1,
                "RMK safe walker did not complete its in-root baseline export.");
            Assert(File.ReadAllText(outsideXml) == outsideContent,
                "RMK safe walker traversed or changed a junction/symlink outside its language root.");

            var workbookBeforeDepthFailure = File.ReadAllBytes(workbookPath);
            var deep = Path.Combine(languageRoot, "deep");
            Directory.CreateDirectory(deep);
            for (var depth = 0; depth < 33; depth++)
            {
                deep = Path.Combine(deep, "d");
                Directory.CreateDirectory(deep);
            }
            File.WriteAllText(
                Path.Combine(deep, "deep.xml"),
                "<LanguageData><Phase07.Deep>value</Phase07.Deep></LanguageData>",
                new UTF8Encoding(false));
            AssertThrows<InvalidDataException>(() => service.Export(options));
            Assert(File.ReadAllBytes(workbookPath).SequenceEqual(workbookBeforeDepthFailure),
                "RMK depth-limit rejection changed the workbook.");
            Assert(File.ReadAllText(outsideXml) == outsideContent,
                "RMK depth-limit rejection changed the outside sentinel.");
        });
    }

    private static void Phase07RmkResourceBudgets()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-phase07-rmk-discovery-budget");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "?⑹꽦 踰덉뿭 " + row.Source)]);
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "Phase07DiscoveryBudget", out var entryRoot);
            var alphaWorkbook = Path.Combine(entryRoot, "alpha.xlsx");
            var zetaWorkbook = Path.Combine(entryRoot, "zeta.xlsx");
            RimWorldTranslatorRmkXlsxWriter.Write(
                alphaWorkbook,
                [HistoryRow("Keyed+Phase07.Alpha", "alpha")],
                "English");
            RimWorldTranslatorRmkXlsxWriter.Write(
                zetaWorkbook,
                [HistoryRow("Keyed+Phase07.Zeta", "zeta")],
                "English");
            var zetaBefore = File.ReadAllBytes(zetaWorkbook);
            var options = new RmkExportOptions
            {
                RmkWorkspaceRoot = workspaceRoot,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                ReviewLanguageFolderName = "Korean",
                RmkLanguageFolderName = "Korean",
                SourceLanguage = "English",
                WorkbookPath = string.Empty,
                Overwrite = true,
                ApplyStatus = ReviewApplyStatus.ApprovedOnly
            };
            var service = new RmkExportService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor());
            var result = service.Export(options);
            Assert(Path.GetFullPath(result.WorkbookPath).Equals(
                    Path.GetFullPath(alphaWorkbook),
                    StringComparison.OrdinalIgnoreCase),
                "Bounded RMK workbook discovery did not choose the deterministic ordinal filename.");
            Assert(File.ReadAllBytes(zetaWorkbook).SequenceEqual(zetaBefore),
                "Bounded RMK workbook discovery changed an unselected workbook.");

            var alphaBeforeLimit = File.ReadAllBytes(alphaWorkbook);
            var boundedService = new RmkExportService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor())
            {
                ResourceLimitsTestOverride = new RmkExportResourceLimits(
                    MaximumWorkbookDiscoveryEntries: 1,
                    MaximumLanguageFiles: 100,
                    MaximumLanguageXmlBytes: 1_000_000,
                    MaximumLanguageRecords: 1_000,
                    MaximumLanguageCharacters: 1_000_000)
            };
            AssertThrows<InvalidDataException>(() => boundedService.Export(options));
            Assert(File.ReadAllBytes(alphaWorkbook).SequenceEqual(alphaBeforeLimit)
                   && File.ReadAllBytes(zetaWorkbook).SequenceEqual(zetaBefore),
                "RMK workbook discovery budget rejection changed a workbook.");
        });

        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-phase07-rmk-aggregate-budget");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "?⑹꽦 踰덉뿭 " + row.Source)]);
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "Phase07AggregateBudget", out var entryRoot);
            var languageRoot = Path.Combine(entryRoot, "Languages", "Korean");
            Directory.CreateDirectory(languageRoot);
            var firstXml = Path.Combine(languageRoot, "first.xml");
            var secondXml = Path.Combine(languageRoot, "second.xml");
            const string firstContent = "<LanguageData><Phase07.One>one</Phase07.One><Phase07.Two>two</Phase07.Two></LanguageData>";
            const string secondContent = "<LanguageData><Phase07.Three>three</Phase07.Three></LanguageData>";
            File.WriteAllText(firstXml, firstContent, new UTF8Encoding(false));
            File.WriteAllText(secondXml, secondContent, new UTF8Encoding(false));
            var workbookPath = Path.Combine(entryRoot, "aggregate-budget.xlsx");
            var options = new RmkExportOptions
            {
                RmkWorkspaceRoot = workspaceRoot,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                ReviewLanguageFolderName = "Korean",
                RmkLanguageFolderName = "Korean",
                SourceLanguage = "English",
                WorkbookPath = workbookPath,
                Overwrite = true,
                ApplyStatus = ReviewApplyStatus.ApprovedOnly
            };

            void AssertAggregateLimit(RmkExportResourceLimits limits, string description)
            {
                var budgetedService = new RmkExportService(
                    new AtomicJsonStore(),
                    new LanguageFileService(),
                    CreateExtractor())
                {
                    ResourceLimitsTestOverride = limits
                };
                AssertThrows<InvalidDataException>(() => budgetedService.Export(options));
                Assert(!File.Exists(workbookPath)
                       && File.ReadAllText(firstXml) == firstContent
                       && File.ReadAllText(secondXml) == secondContent,
                    $"RMK {description} budget rejection changed an input or created an output.");
            }

            AssertAggregateLimit(
                new RmkExportResourceLimits(100, 1, 1_000_000, 1_000, 1_000_000),
                "file-count");
            AssertAggregateLimit(
                new RmkExportResourceLimits(100, 100, 1, 1_000, 1_000_000),
                "XML-byte");
            AssertAggregateLimit(
                new RmkExportResourceLimits(100, 100, 1_000_000, 1, 1_000_000),
                "record-count");
            AssertAggregateLimit(
                new RmkExportResourceLimits(100, 100, 1_000_000, 1_000, 1),
                "character-count");
        });
    }

    private static void Phase07RmkStaleOutputEvidence()
    {
        WithFixture("SampleMod", modRoot =>
        {
            var run = CreateSourceOnlyReview(modRoot, "reviews-phase07-rmk-stale-output");
            var row = run.Rows.Single(item => item.Key == "CodexTranslator.SampleButton");
            SaveReviewDecisions(run, [(row, "합성 번역 " + row.Source)]);
            var fixtureRoot = Directory.GetParent(modRoot)!.FullName;
            var workspaceRoot = CreateRmkWorkspace(fixtureRoot, "Phase07StaleOutput", out var entryRoot);
            var relativeTarget = Path.GetRelativePath(
                Path.Combine(run.ReviewRoot!, "Languages", "Korean"),
                row.Target);
            var xmlPath = Path.Combine(entryRoot, "Languages", "Korean", relativeTarget);
            Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
            const string originalXml = "<LanguageData><Phase07.UserValue>before</Phase07.UserValue></LanguageData>";
            const string changedXml = "<LanguageData><Phase07.UserValue>changed-before-lock</Phase07.UserValue></LanguageData>";
            File.WriteAllText(xmlPath, originalXml, new UTF8Encoding(false));

            var workbookPath = Path.Combine(entryRoot, "phase07-stale-history.xlsx");
            RimWorldTranslatorRmkXlsxWriter.Write(
                workbookPath,
                [HistoryRow("Keyed+Phase07.Initial", "initial")],
                "English");
            var originalWorkbook = File.ReadAllBytes(workbookPath);
            var options = new RmkExportOptions
            {
                RmkWorkspaceRoot = workspaceRoot,
                RmkEntryRoot = entryRoot,
                ReviewRoot = run.ReviewRoot!,
                ModRoot = modRoot,
                ReviewLanguageFolderName = "Korean",
                RmkLanguageFolderName = "Korean",
                SourceLanguage = "English",
                WorkbookPath = workbookPath,
                Overwrite = true,
                ApplyStatus = ReviewApplyStatus.ApprovedOnly
            };

            var xmlService = new RmkExportService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor())
            {
                BeforeWriteBoundaryLockedTestHook = () =>
                    File.WriteAllText(xmlPath, changedXml, new UTF8Encoding(false))
            };
            AssertThrows<InvalidDataException>(() => xmlService.Export(options));
            Assert(File.ReadAllText(xmlPath) == changedXml,
                "RMK stale-plan rejection reverted the user's XML saved before boundary acquisition.");
            Assert(File.ReadAllBytes(workbookPath).SequenceEqual(originalWorkbook),
                "RMK stale XML rejection changed the workbook.");

            byte[]? changedWorkbook = null;
            var workbookService = new RmkExportService(
                new AtomicJsonStore(),
                new LanguageFileService(),
                CreateExtractor())
            {
                BeforeWriteBoundaryLockedTestHook = () =>
                {
                    RimWorldTranslatorRmkXlsxWriter.Write(
                        workbookPath,
                        [HistoryRow("Keyed+Phase07.UserSaved", "user-saved-before-lock")],
                        "English");
                    changedWorkbook = File.ReadAllBytes(workbookPath);
                }
            };
            AssertThrows<InvalidDataException>(() => workbookService.Export(options));
            Assert(changedWorkbook is not null
                   && File.ReadAllBytes(workbookPath).SequenceEqual(changedWorkbook),
                "RMK stale-plan rejection reverted the user's workbook saved before boundary acquisition.");
            Assert(File.ReadAllText(xmlPath) == changedXml,
                "RMK stale workbook rejection changed the user's XML.");
            AssertNoWriteBoundaryGuards(workspaceRoot, "RMK stale-output rejection");
        });
    }

    private static RimWorldTranslatorRmkHistoryRow HistoryRow(string identifier, string translation) => new()
    {
        Identifier = identifier,
        ClassName = "Keyed",
        Key = identifier[(identifier.IndexOf('+') + 1)..],
        Source = "synthetic source",
        Translation = translation
    };

    private static void CreateHardLinkOrThrow(string linkPath, string existingPath)
    {
        if (CreateHardLink(linkPath, existingPath, IntPtr.Zero)) return;
        throw new IOException(
            "The hard-link fixture could not be created.",
            new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static void AssertNoWriteBoundaryGuards(string root, string operation)
    {
        var leftovers = Directory.EnumerateFiles(
                root,
                ".rimworld-ai-translator-write-boundary-*.lock",
                SearchOption.AllDirectories)
            .ToArray();
        Assert(leftovers.Length == 0, $"The {operation} boundary left a guard file behind.");
    }

    private static T CapturePhase07Exception<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
