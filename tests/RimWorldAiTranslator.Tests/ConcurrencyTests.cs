using RimWorldAiTranslator.Core.Review;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Projects;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.Tests;

internal static partial class Program
{
    private static readonly string[] AppDataChildDirectoryNames =
        ["reviews", "logs", "temp", "rmk-builder-staging", "recovery-authority"];

    private static void AtomicJsonBackupSingleRead()
    {
        WithTempRoot(root =>
        {
            var path = Path.Combine(root, "single-read.json");
            var backupPath = path + ".bak";
            var replacementPath = Path.Combine(root, "replacement.json");
            File.WriteAllText(path, "{invalid", new System.Text.UTF8Encoding(false));
            File.WriteAllText(backupPath, "{\"value\":\"A\"}", new System.Text.UTF8Encoding(false));
            File.WriteAllText(replacementPath, "{\"value\":\"B\"}", new System.Text.UTF8Encoding(false));

            var store = new AtomicJsonStore();
            var hookCalls = 0;
            var recoveryNotices = 0;
            store.RecoveredFromBackup += (_, _) => recoveryNotices++;
            store.AfterBackupValidatedTestHook = candidate =>
            {
                Assert(candidate.Equals(backupPath, StringComparison.OrdinalIgnoreCase),
                    "The backup substitution hook received an unexpected path.");
                hookCalls++;
                File.Move(replacementPath, backupPath, overwrite: true);
            };

            InvalidDataException error;
            try
            {
                error = CaptureException<InvalidDataException>(() =>
                    store.Read<Dictionary<string, string>>(path));
            }
            finally
            {
                store.AfterBackupValidatedTestHook = null;
            }

            Assert(hookCalls == 1
                   && recoveryNotices == 0
                   && error.Message.Contains("substituted before recovery", StringComparison.Ordinal),
                "Backup substitution did not fail closed before reporting recovery success.");
            var concurrentBackup = new AtomicJsonStore().Read<Dictionary<string, string>>(backupPath);
            Assert(File.ReadAllText(path) == "{invalid"
                   && concurrentBackup?["value"] == "B"
                   && !Directory.EnumerateFiles(
                           root,
                           "single-read.json.corrupt-*",
                           SearchOption.TopDirectoryOnly)
                       .Any()
                   && store.IsBlocked(path),
                 "Backup substitution changed the primary/current backup, created recovery output, or left writes unblocked.");
        });

        foreach (var initialPrimaryState in new[]
                 {
                     "invalid-replace",
                     "invalid-rewrite",
                     "missing",
                     "occupied-directory"
                 })
        {
            WithTempRoot(root =>
            {
                var path = Path.Combine(root, $"primary-race-{initialPrimaryState}.json");
                var backupPath = path + ".bak";
                var replacementPath = Path.Combine(root, $"primary-race-{initialPrimaryState}-replacement.json");
                const string backupJson = "{\"value\":\"A\"}";
                const string replacementJson = "{\"value\":\"C\"}";
                if (initialPrimaryState.StartsWith("invalid-", StringComparison.Ordinal))
                    File.WriteAllText(path, "{invalid", new System.Text.UTF8Encoding(false));
                else if (initialPrimaryState == "occupied-directory")
                    Directory.CreateDirectory(path);
                File.WriteAllText(backupPath, backupJson, new System.Text.UTF8Encoding(false));
                File.WriteAllText(replacementPath, replacementJson, new System.Text.UTF8Encoding(false));

                var store = new AtomicJsonStore();
                var hookCalls = 0;
                var recoveryNotices = 0;
                store.RecoveredFromBackup += (_, _) => recoveryNotices++;
                store.AfterBackupValidatedTestHook = candidate =>
                {
                    Assert(candidate.Equals(backupPath, StringComparison.OrdinalIgnoreCase),
                        "The primary-substitution hook received an unexpected backup path.");
                    hookCalls++;
                    if (initialPrimaryState == "invalid-rewrite")
                    {
                        File.WriteAllText(path, replacementJson, new System.Text.UTF8Encoding(false));
                        File.Delete(replacementPath);
                    }
                    else
                    {
                        if (initialPrimaryState == "occupied-directory") Directory.Delete(path);
                        File.Move(
                            replacementPath,
                            path,
                            overwrite: initialPrimaryState == "invalid-replace");
                    }
                };

                InvalidDataException error;
                try
                {
                    error = CaptureException<InvalidDataException>(() =>
                        store.Read<Dictionary<string, string>>(path));
                }
                finally
                {
                    store.AfterBackupValidatedTestHook = null;
                }

                Assert(hookCalls == 1
                       && recoveryNotices == 0
                       && error.Message.Contains("primary changed before backup recovery", StringComparison.Ordinal),
                    $"The {initialPrimaryState} primary substitution did not fail closed before recovery success.");
                Assert(File.ReadAllText(path) == replacementJson
                       && File.ReadAllText(backupPath) == backupJson
                       && !File.Exists(replacementPath)
                       && !Directory.EnumerateFiles(
                               root,
                               $"{Path.GetFileName(path)}.corrupt-*",
                               SearchOption.TopDirectoryOnly)
                           .Any()
                       && store.IsBlocked(path),
                    $"The {initialPrimaryState} primary substitution changed the current primary/backup, created recovery output, or left writes unblocked.");
            });
        }

        WithTempRoot(root =>
        {
            var path = Path.Combine(root, "pinned-read.json");
            var replacementPath = Path.Combine(root, "pinned-read-replacement.json");
            File.WriteAllText(path, "{\"value\":\"A\"}", new System.Text.UTF8Encoding(false));
            File.WriteAllText(replacementPath, "{\"value\":\"B\"}", new System.Text.UTF8Encoding(false));

            var writerStore = new AtomicJsonStore();
            using (var existingWriter = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                var loaded = writerStore.Read<Dictionary<string, string>>(path);
                Assert(loaded?["value"] == "A" && !writerStore.IsBlocked(path),
                    "A stable JSON file held by another program was rejected unnecessarily.");
            }
            Assert(File.ReadAllText(path) == "{\"value\":\"A\"}",
                "Reading a stable file held by another program changed the JSON store.");
        });
    }

    private static void ProjectRevisionConcurrency()
    {
        WithTempRoot(root =>
        {
            var paths = new RimWorldAiTranslator.Core.Storage.AppDataPaths(root);
            var seedRepository = new ProjectRepository(new AtomicJsonStore(), paths);
            var seed = new RimWorldAiTranslator.Core.Models.ProjectStoreDocument
            {
                Projects =
                [
                    new RimWorldAiTranslator.Core.Models.TranslationProject
                    {
                        Id = "seed",
                        Name = "Seed"
                    }
                ]
            };
            seedRepository.Save(seed);
            Assert(seed.Revision == 1, "The initial project save did not advance its revision.");

            var firstRepository = new ProjectRepository(new AtomicJsonStore(), paths);
            var staleRepository = new ProjectRepository(new AtomicJsonStore(), paths);
            var first = firstRepository.Load();
            var stale = staleRepository.Load();
            Assert(first.Revision == 1 && stale.Revision == 1,
                "Independent repositories did not load the same starting revision.");

            first.Projects.Add(new RimWorldAiTranslator.Core.Models.TranslationProject
            {
                Id = "first-writer",
                Name = "First writer"
            });
            firstRepository.Save(first);
            Assert(first.Revision == 2, "The accepted project save did not advance its revision.");

            stale.Projects.Add(new RimWorldAiTranslator.Core.Models.TranslationProject
            {
                Id = "stale-writer",
                Name = "Stale writer"
            });
            var conflict = CaptureException<InvalidOperationException>(() => staleRepository.Save(stale));
            Assert(conflict.Message.Contains("changed after it was loaded", StringComparison.Ordinal),
                "A stale project save did not report an explicit concurrency conflict.");
            Assert(stale.Revision == 1,
                "A rejected stale project save changed its in-memory revision.");

            var persisted = new ProjectRepository(new AtomicJsonStore(), paths).Load();
            Assert(persisted.Revision == 2
                   && persisted.Projects.Any(project => project.Id == "first-writer")
                   && persisted.Projects.All(project => project.Id != "stale-writer"),
                "The stale project save replaced or merged into the accepted first writer.");
            var backup = new AtomicJsonStore().Read<RimWorldAiTranslator.Core.Models.ProjectStoreDocument>(
                paths.Projects + ".bak");
            Assert(backup?.Revision == 1
                   && backup.Projects.Any(project => project.Id == "seed")
                   && backup.Projects.All(project => project.Id != "stale-writer"),
                "The stale project save damaged the last-good backup.");

            var primaryBeforeContention = File.ReadAllBytes(paths.Projects);
            var backupBeforeContention = File.ReadAllBytes(paths.Projects + ".bak");
            using (var heldLease = new FileStream(
                       paths.Projects + ".write.lock",
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                var contention = CaptureException<InvalidOperationException>(() =>
                    firstRepository.Save(persisted));
                Assert(contention.Message.Contains("another process", StringComparison.Ordinal),
                    "A held project write lease did not report explicit contention.");
            }
            Assert(File.ReadAllBytes(paths.Projects).SequenceEqual(primaryBeforeContention)
                   && File.ReadAllBytes(paths.Projects + ".bak").SequenceEqual(backupBeforeContention),
                "Project write-lock contention changed the primary or backup.");

            var legacyPaths = new RimWorldAiTranslator.Core.Storage.AppDataPaths(
                Path.Combine(root, "legacy-appdata"));
            legacyPaths.EnsureExists();
            File.WriteAllText(
                legacyPaths.Projects,
                "{\"version\":2,\"updatedAt\":\"legacy\",\"projects\":[{\"id\":\"legacy\",\"name\":\"Legacy A\",\"runs\":[]}]}",
                new System.Text.UTF8Encoding(false));
            var legacyRepository = new ProjectRepository(new AtomicJsonStore(), legacyPaths);
            var unobserved = new RimWorldAiTranslator.Core.Models.ProjectStoreDocument
            {
                Projects = [new RimWorldAiTranslator.Core.Models.TranslationProject { Id = "unobserved" }]
            };
            _ = CaptureException<InvalidOperationException>(() => legacyRepository.Save(unobserved));
            Assert(File.ReadAllText(legacyPaths.Projects).Contains("Legacy A", StringComparison.Ordinal),
                "An unobserved default document overwrote a revision-less legacy store.");

            var observedLegacy = legacyRepository.Load();
            Assert(observedLegacy.Revision == 0
                   && observedLegacy.ObservedContentSha256 is { Length: 64 },
                "A revision-less legacy store did not receive an observed content token.");
            File.WriteAllText(
                legacyPaths.Projects,
                "{\"version\":2,\"updatedAt\":\"legacy\",\"projects\":[{\"id\":\"legacy\",\"name\":\"Legacy B\",\"runs\":[]}]}",
                new System.Text.UTF8Encoding(false));
            var legacyConflict = CaptureException<InvalidOperationException>(() =>
                legacyRepository.Save(observedLegacy));
            Assert(legacyConflict.Message.Contains("changed after it was loaded", StringComparison.Ordinal),
                "A same-revision legacy writer bypassed the observed-content CAS.");
            var migratedLegacy = legacyRepository.Load();
            legacyRepository.Save(migratedLegacy);
            Assert(migratedLegacy.Version == RimWorldAiTranslator.Core.Models.ProjectStoreDocument.CurrentVersion
                   && migratedLegacy.Revision == 1
                   && migratedLegacy.ObservedContentSha256 is { Length: 64 },
                "An explicitly loaded legacy store did not migrate to the revision/content-token protocol.");

            var recoveryPaths = new RimWorldAiTranslator.Core.Storage.AppDataPaths(
                Path.Combine(root, "recovery-appdata"));
            recoveryPaths.EnsureExists();
            var recoverySeed = new ProjectRepository(new AtomicJsonStore(), recoveryPaths);
            var recoveryDocument = new RimWorldAiTranslator.Core.Models.ProjectStoreDocument
            {
                Projects = [new RimWorldAiTranslator.Core.Models.TranslationProject { Id = "backup" }]
            };
            recoverySeed.Save(recoveryDocument);
            recoveryDocument.Projects.Add(
                new RimWorldAiTranslator.Core.Models.TranslationProject { Id = "latest" });
            recoverySeed.Save(recoveryDocument);
            var wouldBeWriter = new ProjectRepository(new AtomicJsonStore(), recoveryPaths).Load();
            File.WriteAllText(recoveryPaths.Projects, "{invalid", new System.Text.UTF8Encoding(false));
            var recoveryStore = new AtomicJsonStore();
            var writerWasBlocked = false;
            recoveryStore.AfterBackupValidatedTestHook = _ =>
            {
                var error = CaptureException<InvalidOperationException>(() =>
                    new ProjectRepository(new AtomicJsonStore(), recoveryPaths).Save(wouldBeWriter));
                writerWasBlocked = error.Message.Contains("another process", StringComparison.Ordinal);
            };
            try
            {
                var recovered = new ProjectRepository(recoveryStore, recoveryPaths).Load();
                Assert(recovered.Projects.Count == 1 && recovered.Projects[0].Id == "backup",
                    "The controlled backup recovery did not restore its last-good revision.");
            }
            finally
            {
                recoveryStore.AfterBackupValidatedTestHook = null;
            }
            Assert(writerWasBlocked,
                "Project Load backup recovery did not exclude a concurrent project writer.");

            var physicalRoot = Path.Combine(root, "physical-appdata");
            var redirectedRoot = Path.Combine(root, "redirected-appdata");
            Directory.CreateDirectory(physicalRoot);
            var redirectedRootFixtureCreated = false;
            try
            {
                Directory.CreateSymbolicLink(redirectedRoot, physicalRoot);
                redirectedRootFixtureCreated = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine(
                    $"[INFO] App-data reparse fixture unavailable ({exception.GetType().Name}).");
            }
            if (redirectedRootFixtureCreated)
            {
                _ = CaptureException<InvalidDataException>(() =>
                    _ = new RimWorldAiTranslator.Core.Storage.AppDataPaths(redirectedRoot));
            }

            foreach (var childName in AppDataChildDirectoryNames)
            {
                var childRoot = Path.Combine(root, "appdata-child-" + childName);
                var outsideChild = Path.Combine(root, "outside-child-" + childName);
                Directory.CreateDirectory(childRoot);
                Directory.CreateDirectory(outsideChild);
                var sentinel = Path.Combine(outsideChild, "sentinel.txt");
                File.WriteAllText(sentinel, "sentinel", new System.Text.UTF8Encoding(false));
                var redirectedChild = Path.Combine(childRoot, childName);
                var redirectedChildFixtureCreated = false;
                try
                {
                    Directory.CreateSymbolicLink(redirectedChild, outsideChild);
                    redirectedChildFixtureCreated = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine(
                        $"[INFO] App-data child reparse fixture unavailable for {childName} ({exception.GetType().Name}).");
                }
                if (redirectedChildFixtureCreated)
                {
                    var childError = CaptureException<InvalidDataException>(() =>
                        new RimWorldAiTranslator.Core.Storage.AppDataPaths(childRoot).EnsureExists());
                    Assert(childError.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase)
                           || childError.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
                           || childError.Message.Contains("path", StringComparison.OrdinalIgnoreCase),
                        $"The pre-existing {childName} junction was not rejected explicitly.");
                    Assert(File.ReadAllText(sentinel) == "sentinel",
                        $"App-data validation touched the {childName} junction target.");
                }
            }

            foreach (var invalidProjectStore in new[]
                     {
                         (Name: "blank-id", Json: "{\"version\":2,\"updatedAt\":\"legacy\",\"projects\":[{\"id\":\"  \",\"name\":\"Blank\",\"runs\":[]}]}"),
                         (Name: "duplicate-id", Json: "{\"version\":2,\"updatedAt\":\"legacy\",\"projects\":[{\"id\":\"same\",\"name\":\"First\",\"runs\":[]},{\"id\":\"same\",\"name\":\"Second\",\"runs\":[]}]}")
                     })
            {
                var invalidPath = Path.Combine(root, invalidProjectStore.Name + ".json");
                File.WriteAllText(
                    invalidPath,
                    invalidProjectStore.Json,
                    new System.Text.UTF8Encoding(false));
                var invalidStore = new AtomicJsonStore();
                var invalidError = CaptureException<InvalidDataException>(() =>
                    invalidStore.Read<ProjectStoreDocument>(invalidPath));
                Assert(invalidStore.IsBlocked(invalidPath)
                       && invalidError.Message.Contains(
                           invalidProjectStore.Name == "blank-id" ? "blank ID" : "duplicate project IDs",
                           StringComparison.Ordinal),
                    $"The {invalidProjectStore.Name} project schema was accepted or left writable.");
            }

            var removalPaths = new AppDataPaths(Path.Combine(root, "stale-removal-appdata"));
            removalPaths.EnsureExists();
            var removalModRoot = Path.Combine(root, "stale-removal-mod");
            Directory.CreateDirectory(removalModRoot);
            var removalReviewRoot = Path.Combine(removalPaths.Reviews, "owned-review");
            var removalProject = new TranslationProject
            {
                Id = "stale-removal-project",
                Name = "Stale removal",
                ModRoot = removalModRoot,
                LatestReviewRoot = removalReviewRoot,
                Runs = [new ProjectRun { ReviewRoot = removalReviewRoot }]
            };
            ProjectCleanupService.WriteOwnershipMarker(removalProject, removalReviewRoot);
            var removalSeedRepository = new ProjectRepository(new AtomicJsonStore(), removalPaths);
            removalSeedRepository.Save(new ProjectStoreDocument { Projects = [removalProject] });
            var staleRemovalRepository = new ProjectRepository(new AtomicJsonStore(), removalPaths);
            var staleRemovalDocument = staleRemovalRepository.Load();
            var staleRemovalProject = staleRemovalDocument.Projects.Single();
            var staleRemovalService = new ProjectService(
                staleRemovalRepository,
                new RimWorldModDiscoveryService(),
                new ProjectCleanupService());
            var staleRemovalPlan = staleRemovalService.GetRemovalPlan(
                staleRemovalProject,
                [removalPaths.Reviews]);
            var concurrentRemovalRepository = new ProjectRepository(new AtomicJsonStore(), removalPaths);
            var concurrentRemovalDocument = concurrentRemovalRepository.Load();
            concurrentRemovalDocument.Projects.Add(new TranslationProject
            {
                Id = "concurrent-project",
                Name = "Concurrent project"
            });
            concurrentRemovalRepository.Save(concurrentRemovalDocument);

            _ = CaptureException<InvalidOperationException>(() => staleRemovalService.Remove(
                staleRemovalDocument,
                staleRemovalProject,
                staleRemovalPlan));
            var afterStaleRemoval = new ProjectRepository(new AtomicJsonStore(), removalPaths).Load();
            Assert(Directory.Exists(removalReviewRoot)
                   && staleRemovalDocument.Projects.Contains(staleRemovalProject)
                   && afterStaleRemoval.Projects.Any(project => project.Id == staleRemovalProject.Id)
                   && afterStaleRemoval.Projects.Any(project => project.Id == "concurrent-project"),
                "A stale project-removal CAS deleted review data, mutated memory, or damaged the winning store.");

            var lockedRemovalRepository = new ProjectRepository(new AtomicJsonStore(), removalPaths);
            var lockedRemovalDocument = lockedRemovalRepository.Load();
            var lockedRemovalProject = lockedRemovalDocument.Projects.Single(
                project => project.Id == staleRemovalProject.Id);
            var lockedRemovalService = new ProjectService(
                lockedRemovalRepository,
                new RimWorldModDiscoveryService(),
                new ProjectCleanupService());
            var lockedRemovalPlan = lockedRemovalService.GetRemovalPlan(
                lockedRemovalProject,
                [removalPaths.Reviews]);
            using (var heldProjectStoreLease = new FileStream(
                       removalPaths.Projects + ".write.lock",
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                _ = CaptureException<InvalidOperationException>(() => lockedRemovalService.Remove(
                    lockedRemovalDocument,
                    lockedRemovalProject,
                    lockedRemovalPlan));
            }
            Assert(Directory.Exists(removalReviewRoot)
                   && lockedRemovalDocument.Projects.Contains(lockedRemovalProject),
                "A project-store save failure deleted review data or removed the in-memory project record.");

            var cleanupFailurePaths = new AppDataPaths(Path.Combine(root, "cleanup-failure-appdata"));
            cleanupFailurePaths.EnsureExists();
            var cleanupFailureModRoot = Path.Combine(root, "cleanup-failure-mod");
            Directory.CreateDirectory(cleanupFailureModRoot);
            var cleanupFailureReviewRoot = Path.Combine(cleanupFailurePaths.Reviews, "owned-review");
            var cleanupFailureProject = new TranslationProject
            {
                Id = "cleanup-failure-project",
                Name = "Cleanup failure",
                ModRoot = cleanupFailureModRoot,
                LatestReviewRoot = cleanupFailureReviewRoot,
                Runs = [new ProjectRun { ReviewRoot = cleanupFailureReviewRoot }]
            };
            ProjectCleanupService.WriteOwnershipMarker(cleanupFailureProject, cleanupFailureReviewRoot);
            var cleanupFailureRepository = new ProjectRepository(new AtomicJsonStore(), cleanupFailurePaths);
            var cleanupFailureDocument = new ProjectStoreDocument
            {
                Projects = [cleanupFailureProject]
            };
            cleanupFailureRepository.Save(cleanupFailureDocument);
            var cleanupFailureService = new ProjectService(
                cleanupFailureRepository,
                new RimWorldModDiscoveryService(),
                new ProjectCleanupService());
            var cleanupFailurePlan = cleanupFailureService.GetRemovalPlan(
                cleanupFailureProject,
                [cleanupFailurePaths.Reviews]);
            var heldCleanupFile = Path.Combine(cleanupFailureReviewRoot, "held-open.txt");
            File.WriteAllText(heldCleanupFile, "synthetic", new System.Text.UTF8Encoding(false));
            using (var heldCleanupLease = new FileStream(
                       heldCleanupFile,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                var cleanupFailureResult = cleanupFailureService.Remove(
                    cleanupFailureDocument,
                    cleanupFailureProject,
                    cleanupFailurePlan);
                var restartedAfterCleanupFailure = new ProjectRepository(
                    new AtomicJsonStore(),
                    cleanupFailurePaths).Load();
                var preservedQuarantines = Directory.EnumerateDirectories(
                        cleanupFailurePaths.Reviews,
                        ".rimworld-ai-cleanup-*",
                        SearchOption.TopDirectoryOnly)
                    .ToArray();
                var preservedCleanupPath = preservedQuarantines.FirstOrDefault()
                                           ?? (Directory.Exists(cleanupFailureReviewRoot)
                                               ? cleanupFailureReviewRoot
                                               : string.Empty);
                Assert(cleanupFailureResult.ProjectRecordRemoved
                       && cleanupFailureResult.CleanupFailures.Count > 0
                       && cleanupFailureDocument.Projects.Count == 0
                       && restartedAfterCleanupFailure.Projects.Count == 0
                       && !string.IsNullOrEmpty(preservedCleanupPath)
                       && preservedQuarantines.Length <= 1
                       && File.Exists(Path.Combine(preservedCleanupPath, "held-open.txt"))
                       && cleanupFailureResult.CleanupFailures.Any(failure =>
                           failure.Contains(preservedCleanupPath, StringComparison.OrdinalIgnoreCase)),
                    "A post-commit cleanup failure did not leave an explicit, restart-consistent removed-record/retained-folder state.");
            }
        });
    }

    private static void AsyncTransactionRollback()
    {
        WithTempRoot(root =>
        {
            var existing = Path.Combine(root, "existing.txt");
            var created = Path.Combine(root, "created.txt");
            File.WriteAllText(existing, "before");

            var error = CaptureException<IOException>(() => FileTransaction.ExecuteAsync(
                [existing, created],
                async () =>
                {
                    File.WriteAllText(existing, "changed");
                    File.WriteAllText(created, "new");
                    await Task.Yield();
                    throw new InvalidDataException("injected failure");
#pragma warning disable CS0162
                    return 0;
#pragma warning restore CS0162
                },
                "fixture transaction").GetAwaiter().GetResult());

            Assert(error.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase),
                "Async transaction failure did not report rollback.");
            Assert(File.ReadAllText(existing) == "before", "Async transaction did not restore the original file.");
            Assert(!File.Exists(created), "Async transaction did not remove a newly created file.");
            Assert(!Directory.EnumerateFiles(root, "*.transaction.bak", SearchOption.AllDirectories).Any(),
                "Async transaction left a snapshot behind.");
        });
    }

    private static void SettingsAsyncSnapshot()
    {
        WithTempRoot(root =>
        {
            var paths = new AppDataPaths(root);
            using var repository = new SettingsRepository(new AtomicJsonStore(), paths);
            using var futureRoot = System.Text.Json.JsonDocument.Parse("{\"kept\":true}");
            using var futureProvider = System.Text.Json.JsonDocument.Parse("[1,2,3]");
            var settings = new RimWorldAiTranslator.Core.Models.AppSettingsDocument
            {
                ThemeMode = "Light",
                DesignPreset = "initial",
                ApiProviderId = "Custom",
                ApiProviders = new Dictionary<string, RimWorldAiTranslator.Core.Models.ApiProviderSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Custom"] = new()
                    {
                        Name = "Fixture",
                        Url = "https://example.invalid/v1",
                        Model = "snapshot-model",
                        ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
                        {
                            ["futureProvider"] = futureProvider.RootElement.Clone()
                        }
                    }
                },
                ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
                {
                    ["futureRoot"] = futureRoot.RootElement.Clone()
                }
            };

            var immutableSave = repository.SaveAsync(settings);
            settings.ThemeMode = "Dark";
            settings.ApiProviders["Custom"].Model = "mutated-after-queue";
            settings.ApiProviders["Custom"].ExtensionData!.Clear();
            settings.ExtensionData!.Clear();
            immutableSave.GetAwaiter().GetResult();

            var first = repository.Load();
            Assert(first.ThemeMode == "Light", "Async settings save serialized a later root mutation.");
            Assert(first.ApiProviders["Custom"].Model == "snapshot-model",
                "Async settings save serialized a later provider mutation.");
            Assert(first.ExtensionData?.ContainsKey("futureRoot") == true
                   && first.ApiProviders["Custom"].ExtensionData?.ContainsKey("futureProvider") == true,
                "Async settings snapshot lost extension data.");

            var queued = new List<Task>();
            for (var index = 0; index < 24; index++)
            {
                settings.DesignPreset = $"queued-{index:D2}";
                settings.ApiProviders["Custom"].Model = $"model-{index:D2}";
                queued.Add(repository.SaveAsync(settings));
            }
            settings.DesignPreset = "mutated-after-all-queues";
            settings.ApiProviders["Custom"].Model = "mutated-after-all-queues";
            repository.FlushAsync().GetAwaiter().GetResult();
            Task.WhenAll(queued).GetAwaiter().GetResult();

            var latest = repository.Load();
            Assert(latest.DesignPreset == "queued-23"
                   && latest.ApiProviders["Custom"].Model == "model-23",
                "Serialized settings saves did not retain invocation order or stable snapshots.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledSave = repository.SaveAsync(settings, cancelled.Token);
            settings.DesignPreset = "after-cancelled-save";
            var recoverySave = repository.SaveAsync(settings);
            CaptureException<OperationCanceledException>(() => cancelledSave.GetAwaiter().GetResult());
            recoverySave.GetAwaiter().GetResult();
            Assert(repository.Load().DesignPreset == "after-cancelled-save",
                "A cancelled settings save prevented a later durable save.");

            settings.DesignPreset = "dispose-drained-save";
            var acceptedBeforeDispose = repository.SaveAsync(settings);
            repository.Dispose();
            acceptedBeforeDispose.GetAwaiter().GetResult();
            using var verifier = new SettingsRepository(new AtomicJsonStore(), paths);
            Assert(verifier.Load().DesignPreset == "dispose-drained-save",
                "Disposing the settings repository lost an accepted queued save.");
        });
    }

    private static void ReviewConcurrentSaveRevision()
    {
        WithTempRoot(root =>
        {
            var reviewRoot = Path.Combine(root, "review");
            var rows = Enumerable.Range(0, 20_000)
                .Select(index => Row(
                    reviewRoot,
                    $"E{index:D6}",
                    $"fixture.key.{index:D6}",
                    $"source {index:D6}"))
                .ToArray();
            WriteComparison(reviewRoot, rows);

            var store = new AtomicJsonStore();
            var service = new ReviewWorkspaceService(store, CreateExtractor());
            var workspace = service.Load(reviewRoot);
            foreach (var item in workspace.Items)
                service.UpdateTranslation(workspace, item, $"번역 {item.OriginalIndex:D6}", string.Empty, true);

            var firstItem = workspace.Items[0];
            var inFlight = service.SaveAsync(workspace);
            service.UpdateTranslation(workspace, firstItem, "저장 중 새 번역", "새 메모", true);
            inFlight.GetAwaiter().GetResult();

            Assert(workspace.Dirty && firstItem.Touched,
                "An older asynchronous save cleared a newer workspace revision.");
            var firstSnapshot = new ReviewRepository(store).Load(reviewRoot);
            Assert(firstSnapshot.Items.Single(item => item.Key == firstItem.Row.Key).Text == "번역 000000",
                "The in-flight save did not persist its immutable snapshot.");

            service.Save(workspace);
            Assert(!workspace.Dirty && !firstItem.Touched, "The newest revision did not become clean after a successful save.");
            var latest = new ReviewRepository(store).Load(reviewRoot);
            var latestItem = latest.Items.Single(item => item.Key == firstItem.Row.Key);
            Assert(latestItem.Text == "저장 중 새 번역" && latestItem.Note == "새 메모",
                "A newer edit was lost after the follow-up save.");
        });
    }

    private static void ReviewSaveCoordinatorBarrier()
    {
        WithTempRoot(root =>
        {
            var reviewRoot = Path.Combine(root, "review");
            WriteComparison(reviewRoot, [Row(reviewRoot, "E000001", "fixture.key", "source")]);

            var store = new AtomicJsonStore();
            var service = new ReviewWorkspaceService(store, CreateExtractor());
            var workspace = service.Load(reviewRoot);
            var item = workspace.Items[0];
            service.UpdateTranslation(workspace, item, "첫 번역", string.Empty, true);
            using var coordinator = new ReviewSaveCoordinator(service);

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                CaptureException<OperationCanceledException>(() =>
                    coordinator.SaveAsync(workspace, cancelled.Token).GetAwaiter().GetResult());
            }
            Assert(workspace.Dirty && item.Touched,
                "A failed coordinated save incorrectly marked the review as clean.");
            Assert(!store.Exists(ReviewRepository.GetDecisionPath(reviewRoot)),
                "A cancelled coordinated save wrote a decision file.");

            var first = coordinator.SaveAsync(workspace);
            service.UpdateTranslation(workspace, item, "가장 최신 번역", "최신 메모", true);
            var second = coordinator.SaveAsync(workspace);
            Task.WhenAll(first, second).GetAwaiter().GetResult();

            Assert(!workspace.Dirty && !item.Touched,
                "Serialized saves did not make the latest review revision clean.");
            var saved = new ReviewRepository(store).Load(reviewRoot).Items.Single();
            Assert(saved.Text == "가장 최신 번역" && saved.Note == "최신 메모",
                "A queued older save overwrote the latest review revision.");
        });
    }
}
