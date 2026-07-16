using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Tests;

internal static class Phase05StorageTests
{
    private const string TempDirectoryName = "RimWorldAiTranslator-phase05-storage-tests";
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    public static void RunAll()
    {
        InvalidUtf8PrimaryUsesValidBackup();
        BothInvalidStoresBlockWritesAndPreserveBytes();
        NullCollectionsAndElementsUseValidBackups();
        DuplicatePropertiesUseValidBackupsAndBlockUnsafeWrites();
        VersionOnlySettingsAndUnknownFieldsRoundTrip();
        MixedReviewStatusesAndUnknownFieldsRoundTrip();
        InvalidWriteIsRejectedBeforeCommit();
        ProjectLoadDoesNotChangeRepositoryBytes();
    }

    public static void InvalidUtf8PrimaryUsesValidBackup()
    {
        WithTempRoot(root =>
        {
            var path = Path.Combine(root, "projects.json");
            File.WriteAllBytes(path, [0xC3, 0x28]);
            WriteUtf8(path + ".bak", "{\"version\":2,\"projects\":[{\"id\":\"backup\",\"runs\":[]}]}");
            var expectedBackupHash = Sha256(path + ".bak");
            JsonRecoveryNotice? notice = null;
            var store = new AtomicJsonStore();
            store.RecoveredFromBackup += (_, value) => notice = value;

            var recovered = store.Read<ProjectStoreDocument>(path)
                ?? throw new InvalidOperationException("The recovered project store was null.");

            Assert(recovered.Projects.Single().Id == "backup", "Invalid UTF-8 primary content did not fall back to the valid backup.");
            Assert(!store.IsBlocked(path), "A successfully recovered store remained write-blocked.");
            Assert(notice is not null
                   && notice.PreservedCorruptPath is not null
                   && File.Exists(notice.PreservedCorruptPath),
                "Invalid UTF-8 recovery did not preserve and report the corrupt primary.");
            Assert(Sha256(path) == expectedBackupHash && Sha256(path + ".bak") == expectedBackupHash,
                "Backup recovery changed the validated backup bytes.");
        });
    }

    public static void BothInvalidStoresBlockWritesAndPreserveBytes()
    {
        WithTempRoot(root =>
        {
            var path = Path.Combine(root, "projects.json");
            File.WriteAllBytes(path, [0xF0, 0x28, 0x8C, 0x28]);
            WriteUtf8(path + ".bak", "{\"version\":2,\"projects\":null}");
            var primaryHash = Sha256(path);
            var backupHash = Sha256(path + ".bak");
            var store = new AtomicJsonStore();

            AssertThrows<InvalidDataException>(() => store.Read<ProjectStoreDocument>(path));
            Assert(store.IsBlocked(path), "A store with an invalid primary and backup was not write-blocked.");
            AssertThrows<InvalidOperationException>(() =>
                store.Write(path, new ProjectStoreDocument { Projects = [] }));
            Assert(Sha256(path) == primaryHash && Sha256(path + ".bak") == backupHash,
                "A blocked write changed the invalid primary or backup bytes.");
        });
    }

    public static void NullCollectionsAndElementsUseValidBackups()
    {
        WithTempRoot(root =>
        {
            var store = new AtomicJsonStore();

            var nullProviders = Path.Combine(root, "settings-null-providers.json");
            WritePrimaryAndBackup(
                nullProviders,
                "{\"version\":4,\"themeMode\":\"Invalid\",\"apiProviders\":null}",
                "{\"version\":4,\"themeMode\":\"Backup\",\"apiProviders\":{}}");
            Assert(store.Read<AppSettingsDocument>(nullProviders)?.ThemeMode == "Backup",
                "A null settings provider collection did not fall back to its backup.");

            var nullProviderEntry = Path.Combine(root, "settings-null-provider-entry.json");
            WritePrimaryAndBackup(
                nullProviderEntry,
                "{\"version\":4,\"themeMode\":\"Invalid\",\"apiProviders\":{\"fixture\":null}}",
                "{\"version\":4,\"themeMode\":\"Backup\",\"apiProviders\":{}}");
            Assert(store.Read<AppSettingsDocument>(nullProviderEntry)?.ThemeMode == "Backup",
                "A null settings provider entry did not fall back to its backup.");

            var nullSettingsScalar = Path.Combine(root, "settings-null-scalar.json");
            WritePrimaryAndBackup(
                nullSettingsScalar,
                "{\"version\":4,\"themeMode\":null,\"apiProviders\":{}}",
                "{\"version\":4,\"themeMode\":\"Backup\",\"apiProviders\":{}}");
            Assert(store.Read<AppSettingsDocument>(nullSettingsScalar)?.ThemeMode == "Backup",
                "A null settings scalar did not fall back to its backup.");

            var nullProjects = Path.Combine(root, "projects-null-collection.json");
            WritePrimaryAndBackup(
                nullProjects,
                "{\"version\":2,\"projects\":null}",
                "{\"version\":2,\"projects\":[]}");
            Assert(store.Read<ProjectStoreDocument>(nullProjects)?.Projects.Count == 0,
                "A null project collection did not fall back to its backup.");

            var nullProjectEntry = Path.Combine(root, "projects-null-entry.json");
            WritePrimaryAndBackup(
                nullProjectEntry,
                "{\"version\":2,\"projects\":[null]}",
                "{\"version\":2,\"projects\":[]}");
            Assert(store.Read<ProjectStoreDocument>(nullProjectEntry)?.Projects.Count == 0,
                "A null project entry did not fall back to its backup.");

            var nullProjectScalar = Path.Combine(root, "projects-null-scalar.json");
            WritePrimaryAndBackup(
                nullProjectScalar,
                "{\"version\":2,\"projects\":[{\"id\":\"invalid\",\"modRoot\":null,\"runs\":[]}]}",
                "{\"version\":2,\"projects\":[]}");
            Assert(store.Read<ProjectStoreDocument>(nullProjectScalar)?.Projects.Count == 0,
                "A null project scalar did not fall back to its backup.");

            var nullActiveReviewEntry = Path.Combine(root, "review-null-active-entry.json");
            WritePrimaryAndBackup(
                nullActiveReviewEntry,
                "{\"version\":1,\"items\":[null],\"quarantinedItems\":[]}",
                "{\"version\":1,\"items\":[],\"quarantinedItems\":[]}");
            Assert(store.Read<ReviewDecisionDocument>(nullActiveReviewEntry)?.Items.Count == 0,
                "A null active review entry did not fall back to its backup.");

            var nullQuarantinedReviewEntry = Path.Combine(root, "review-null-quarantined-entry.json");
            WritePrimaryAndBackup(
                nullQuarantinedReviewEntry,
                "{\"version\":1,\"items\":[],\"quarantinedItems\":[null]}",
                "{\"version\":1,\"items\":[],\"quarantinedItems\":[]}");
            Assert(store.Read<ReviewDecisionDocument>(nullQuarantinedReviewEntry)?.QuarantinedItems.Count == 0,
                "A null quarantined review entry did not fall back to its backup.");

            var nullReviewScalar = Path.Combine(root, "review-null-scalar.json");
            WritePrimaryAndBackup(
                nullReviewScalar,
                "{\"version\":1,\"items\":[{\"key\":\"fixture.key\",\"text\":null}],\"quarantinedItems\":[]}",
                "{\"version\":1,\"items\":[],\"quarantinedItems\":[]}");
            Assert(store.Read<ReviewDecisionDocument>(nullReviewScalar)?.Items.Count == 0,
                "A null review scalar did not fall back to its backup.");

            var emptyComparison = Path.Combine(root, "review-empty-comparison.json");
            WritePrimaryAndBackup(
                emptyComparison,
                "{\"version\":6,\"comparison\":\"\",\"comparisonSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"items\":[],\"quarantinedItems\":[]}",
                "{\"version\":1,\"items\":[],\"quarantinedItems\":[]}");
            Assert(store.Read<ReviewDecisionDocument>(emptyComparison)?.Version == 1,
                "An unbound v6 review document did not fall back to its backup.");

            var duplicateIdentity = Path.Combine(root, "review-duplicate-identity.json");
            const string duplicateDecision = "{\"key\":\"fixture.key\",\"target\":\"Keyed/Fixture.xml\",\"status\":\"approved\",\"text\":\"번역\"}";
            WritePrimaryAndBackup(
                duplicateIdentity,
                "{\"version\":6,\"comparison\":\"fixture-comparison.json\",\"comparisonSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"items\":["
                + duplicateDecision + "," + duplicateDecision + "],\"quarantinedItems\":[]}",
                "{\"version\":1,\"items\":[],\"quarantinedItems\":[]}");
            Assert(store.Read<ReviewDecisionDocument>(duplicateIdentity)?.Version == 1,
                "A duplicate v6 review identity did not fall back to its backup.");
        });
    }

    public static void InvalidWriteIsRejectedBeforeCommit()
    {
        WithTempRoot(root =>
        {
            var path = Path.Combine(root, "projects.json");
            WritePrimaryAndBackup(
                path,
                "{\"version\":2,\"projects\":[{\"id\":\"primary\",\"runs\":[]}]}",
                "{\"version\":2,\"projects\":[{\"id\":\"backup\",\"runs\":[]}]}");
            var primaryHash = Sha256(path);
            var backupHash = Sha256(path + ".bak");
            var store = new AtomicJsonStore();

            AssertThrows<InvalidDataException>(() =>
                store.Write(path, new ProjectStoreDocument { Projects = null! }));

            Assert(Sha256(path) == primaryHash && Sha256(path + ".bak") == backupHash,
                "Pre-commit validation failure changed the primary or backup store.");
            Assert(!Directory.EnumerateFiles(root, $".{Path.GetFileName(path)}.*.tmp", SearchOption.TopDirectoryOnly).Any(),
                "Pre-commit validation failure left a transaction temp file behind.");
        });
    }

    public static void DuplicatePropertiesUseValidBackupsAndBlockUnsafeWrites()
    {
        WithTempRoot(root =>
        {
            var recoverablePath = Path.Combine(root, "nested-duplicate.json");
            const string duplicatePrimary = "{\"outer\":[{\"Name\":\"first\",\"name\":\"shadowed\"}]}";
            WritePrimaryAndBackup(
                recoverablePath,
                duplicatePrimary,
                "{\"outer\":[{\"name\":\"backup\"}]}");
            JsonRecoveryNotice? notice = null;
            var store = new AtomicJsonStore();
            store.RecoveredFromBackup += (_, value) => notice = value;

            var recovered = store.Read<Dictionary<string, JsonElement>>(recoverablePath)
                ?? throw new InvalidOperationException("The recovered generic JSON document was null.");

            Assert(recovered["outer"][0].GetProperty("name").GetString() == "backup",
                "A nested case-insensitive duplicate property did not fall back to its backup.");
            Assert(notice?.PreservedCorruptPath is not null
                   && File.Exists(notice.PreservedCorruptPath)
                   && File.ReadAllText(notice.PreservedCorruptPath, Utf8NoBom) == duplicatePrimary,
                "Duplicate-property recovery did not preserve the rejected primary bytes.");

            var blockedPath = Path.Combine(root, "all-duplicates.json");
            WritePrimaryAndBackup(
                blockedPath,
                "{\"value\":1,\"VALUE\":2}",
                "{\"items\":[{\"id\":\"one\",\"id\":\"two\"}]}");
            var primaryHash = Sha256(blockedPath);
            var backupHash = Sha256(blockedPath + ".bak");

            AssertThrows<InvalidDataException>(() =>
                store.Read<Dictionary<string, JsonElement>>(blockedPath));
            Assert(store.IsBlocked(blockedPath),
                "A store with duplicate properties in both copies was not write-blocked.");
            AssertThrows<InvalidOperationException>(() =>
                store.Write(blockedPath, new Dictionary<string, string>()));
            Assert(Sha256(blockedPath) == primaryHash && Sha256(blockedPath + ".bak") == backupHash,
                "A blocked duplicate-property store changed its primary or backup bytes.");
        });
    }

    public static void VersionOnlySettingsAndUnknownFieldsRoundTrip()
    {
        WithTempRoot(root =>
        {
            var path = Path.Combine(root, "settings-version-only.json");
            WriteUtf8(path, "{\"version\":4,\"futureSettings\":{\"enabled\":true,\"label\":\"보존\"}}");
            var store = new AtomicJsonStore();

            var settings = store.Read<AppSettingsDocument>(path)
                ?? throw new InvalidOperationException("The version-only settings document was null.");
            Assert(settings.Version == AppSettingsDocument.CurrentVersion,
                "A version-only settings document did not retain its supported version.");
            Assert(settings.ExtensionData is not null
                   && settings.ExtensionData.TryGetValue("futureSettings", out var future)
                   && future.GetProperty("label").GetString() == "보존",
                "An unknown settings field was not loaded losslessly.");

            store.Write(path, settings);
            var roundTripped = store.Read<AppSettingsDocument>(path)
                ?? throw new InvalidOperationException("The round-tripped settings document was null.");
            Assert(roundTripped.ExtensionData is not null
                   && roundTripped.ExtensionData.TryGetValue("futureSettings", out future)
                   && future.GetProperty("enabled").GetBoolean()
                   && future.GetProperty("label").GetString() == "보존",
                "An unknown settings field was lost during round-trip.");
        });
    }

    public static void MixedReviewStatusesAndUnknownFieldsRoundTrip()
    {
        WithTempRoot(root =>
        {
            var path = Path.Combine(root, "review-mixed-statuses.json");
            WriteUtf8(
                path,
                "{\"version\":1,\"items\":["
                + "{\"id\":\"p\",\"key\":\"fixture.pending\",\"status\":\"pending\",\"text\":\"\",\"note\":\"대기\",\"futureItem\":1},"
                + "{\"id\":\"t\",\"key\":\"fixture.translated\",\"status\":\"translated\",\"text\":\"번역\",\"note\":\"메모\"},"
                + "{\"id\":\"a\",\"key\":\"fixture.approved\",\"status\":\"approved\",\"text\":\"승인\",\"previousSourceText\":\"이전 원문\"},"
                + "{\"id\":\"e\",\"key\":\"fixture.error\",\"status\":\"error\",\"text\":\"\",\"note\":\"재시도\"},"
                + "{\"id\":\"u\",\"key\":\"fixture.unknown\",\"status\":\"future-status\",\"text\":\"보존\"}],"
                + "\"quarantinedItems\":[],\"futureDocument\":{\"revision\":7}}");
            var store = new AtomicJsonStore();

            var document = store.Read<ReviewDecisionDocument>(path)
                ?? throw new InvalidOperationException("The mixed-status review document was null.");
            Assert(document.Items.Select(item => item.Status).SequenceEqual(
                    ["pending", "translated", "approved", "error", "future-status"]),
                "Mixed and unknown review statuses were normalized or discarded while reading.");

            store.Write(path, document);
            var roundTripped = store.Read<ReviewDecisionDocument>(path)
                ?? throw new InvalidOperationException("The round-tripped mixed-status review document was null.");
            Assert(roundTripped.Items.Select(item => item.Status).SequenceEqual(
                    ["pending", "translated", "approved", "error", "future-status"]),
                "Mixed and unknown review statuses changed during round-trip.");
            Assert(roundTripped.Items[0].ExtensionData?.ContainsKey("futureItem") == true
                   && roundTripped.ExtensionData?.TryGetValue("futureDocument", out var future) == true
                   && future.GetProperty("revision").GetInt32() == 7,
                "Unknown review fields were lost during round-trip.");
            Assert(roundTripped.Items[2].PreviousSourceText == "이전 원문"
                   && roundTripped.Items[1].Note == "메모",
                "Review history or note data was lost during round-trip.");
        });
    }

    public static void ProjectLoadDoesNotChangeRepositoryBytes()
    {
        WithTempRoot(root =>
        {
            var appRoot = Path.Combine(root, "appdata");
            var paths = new AppDataPaths(appRoot);
            paths.EnsureExists();

            var syntheticSteamRoot = Path.Combine(root, "synthetic-steam");
            Directory.CreateDirectory(syntheticSteamRoot);
            WriteUtf8(
                Path.Combine(syntheticSteamRoot, RimWorldModDiscoveryService.IsolationMarkerFileName),
                RimWorldModDiscoveryService.IsolationMarkerContent);
            var modRoot = Path.Combine(syntheticSteamRoot, "fixture-mod");
            var resolvedRoot = Path.Combine(modRoot, "v1.6");
            Directory.CreateDirectory(Path.Combine(resolvedRoot, "Defs"));
            WriteUtf8(
                Path.Combine(modRoot, "LoadFolders.xml"),
                "<loadFolders><v1.6><li>v1.6</li></v1.6></loadFolders>");

            var store = new AtomicJsonStore();
            var repository = new ProjectRepository(store, paths);
            repository.Save(new ProjectStoreDocument
            {
                Projects =
                [
                    new TranslationProject
                    {
                        Id = "legacy",
                        ModRoot = modRoot,
                        SourceKind = string.Empty,
                        SourceLanguageFolder = string.Empty,
                        Runs = []
                    }
                ]
            });
            var beforeHash = Sha256(paths.Projects);
            var service = new ProjectService(
                repository,
                RimWorldModDiscoveryService.CreateIsolated(syntheticSteamRoot),
                new ProjectCleanupService());

            var loaded = service.Load();
            var project = loaded.Projects.Single();

            Assert(project.SourceKind == "Folder" && project.SourceLanguageFolder == "Auto",
                "Project load did not apply legacy defaults in memory.");
            Assert(project.ModRoot.Equals(Path.GetFullPath(resolvedRoot), StringComparison.OrdinalIgnoreCase),
                "Project load did not resolve the active content root in memory.");
            Assert(Sha256(paths.Projects) == beforeHash,
                "Read-only project load rewrote the project repository.");
        });
    }

    private static void WritePrimaryAndBackup(string path, string primary, string backup)
    {
        WriteUtf8(path, primary);
        WriteUtf8(path + ".bak", backup);
    }

    private static void WriteUtf8(string path, string content) => File.WriteAllText(path, content, Utf8NoBom);

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void WithTempRoot(Action<string> action)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), TempDirectoryName));
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(parent, fullRoot);
        if (relative.Contains(Path.DirectorySeparatorChar)
            || relative.Contains(Path.AltDirectorySeparatorChar)
            || !Guid.TryParseExact(relative, "N", out _))
        {
            throw new InvalidOperationException("Refusing to use an unverified Phase 05 storage test directory.");
        }
        try
        {
            action(root);
        }
        finally
        {
            if (Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, true);
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static T AssertThrows<T>(Action action) where T : Exception
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
