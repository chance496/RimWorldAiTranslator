using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace RimWorldAiTranslator.Tooling;

internal static class PackageCommand
{
    private const string ApplicationWindowTitle = "RimWorld AI Translator";
    private const string DuplicateInstanceMessage =
        "동일한 데이터 폴더를 사용하는 RimWorld AI Translator가 이미 실행 중입니다.\n\n" +
        "먼저 실행 중인 창을 닫은 뒤 다시 시도하세요.";
    private const string SmokeEvidencePrefix = "PACKAGE_SMOKE_EVIDENCE_JSON=";
    private static readonly byte[] ExpectedDiscoveryAcknowledgement =
        Encoding.UTF8.GetBytes(PackageLayout.DiscoveryAckContent + "\n");
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions SmokeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task<int> RunAsync(RepositoryLayout repository, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The win-x64 package smoke test requires Windows.");

        using var outputLock = PackageOutputTransaction.AcquireRepositoryLock(repository);
        PackageOutputTransaction.Recover(repository);
        using var sourceInputs = PinnedFileTree.CaptureRepositoryInputs(repository);
        var versionSnapshot = VersionSnapshot.Capture(repository.VersionFile);
        var version = versionSnapshot.Version;
        VerifyOfflineNuGetConfig(repository.NuGetConfig);
        VerifyBuildVersionProperties(repository, version);
        ZeroAudit.VerifySourceTree(repository);
        sourceInputs.Verify();

        var processes = ExternalProcessPolicy.FromCurrentDotnetHost();
        PackageProcessWorkspace? processWorkspace = null;
        string? workRoot = null;
        try
        {
            processWorkspace = PackageProcessWorkspace.Create();
            workRoot = repository.CreateOwnedWorkDirectory();
            var deterministicPathMapProperty = CreateDeterministicPathMapProperty(workRoot);
            var sourceSnapshotRoot = CreateSourceSnapshot(repository, workRoot, sourceInputs);
            using var buildSourceInputs = PinnedFileTree.CaptureExact(
                sourceSnapshotRoot,
                "run-owned clean source snapshot");
            PinnedFileTree.AssertSameSnapshot(
                sourceInputs.Snapshot,
                buildSourceInputs.Snapshot,
                "repository inputs and clean source snapshot");
            var buildRepository = RepositoryLayout.Find(sourceSnapshotRoot);
            VerifyOfflineNuGetConfig(buildRepository.NuGetConfig);
            VerifyBuildVersionProperties(buildRepository, version);
            ZeroAudit.VerifySourceTree(buildRepository);
            buildSourceInputs.Verify();

            var isolatedPackages = repository.RequireRepositoryPath(Path.Combine(workRoot, "nuget-packages"));
            var runArtifacts = repository.RequireRepositoryPath(Path.Combine(workRoot, "dotnet-artifacts"));
            var isolatedCliHome = repository.RequireRepositoryPath(Path.Combine(workRoot, "dotnet-cli-home"));
            var processTemp = processWorkspace.CreateDirectory("t");
            var processProfile = processWorkspace.CreateDirectory("h");
            var processAppData = processWorkspace.CreateDirectoryPath("h", "AppData", "Roaming");
            var processLocalAppData = processWorkspace.CreateDirectoryPath("h", "AppData", "Local");
            Directory.CreateDirectory(isolatedPackages);
            Directory.CreateDirectory(runArtifacts);
            Directory.CreateDirectory(isolatedCliHome);
            repository.AssertNoReparseTree(workRoot);
            var buildEnvironment = new Dictionary<string, string?>
            {
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_CLI_HOME"] = isolatedCliHome,
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
                ["TEMP"] = processTemp,
                ["TMP"] = processTemp,
                ["HOME"] = processProfile,
                ["USERPROFILE"] = processProfile,
                ["APPDATA"] = processAppData,
                ["LOCALAPPDATA"] = processLocalAppData
            };

            using var runtimeFeed = await RuntimePackFeed.CreateAsync(
                buildRepository,
                repository,
                workRoot,
                processes,
                buildEnvironment,
                cancellationToken);
            runtimeFeed.Verify();
            if (runtimeFeed.Root.Contains(';', StringComparison.Ordinal))
                throw new InvalidDataException("The run-owned runtime feed path cannot be represented as one restore source.");
            var runtimeSourceProperty = $"-p:RestoreSources={runtimeFeed.Root}";

            Console.WriteLine("Restoring the solution from the verified run-owned runtime feed...");
            await processes.RunDotnetAsync(
                [
                    "restore", buildRepository.Solution,
                    "--disable-build-servers",
                    "--configfile", buildRepository.NuGetConfig,
                    "--packages", isolatedPackages,
                    "--artifacts-path", runArtifacts,
                    "--force-evaluate",
                    "--no-cache",
                    "--nologo",
                    runtimeSourceProperty,
                    "-p:RestoreAdditionalProjectSources=",
                    "-p:RestoreFallbackFolders=",
                    "-p:RestoreAdditionalProjectFallbackFolders=",
                    "-p:RestoreIgnoreFailedSources=false",
                    "-p:NuGetAudit=false"
                ],
                buildRepository.Root,
                buildEnvironment,
                cancellationToken);
            runtimeFeed.Verify();

            Console.WriteLine("Restoring the win-x64 application graph from the verified run-owned runtime feed...");
            await processes.RunDotnetAsync(
                [
                    "restore", buildRepository.AppProject,
                    "--disable-build-servers",
                    "--runtime", PackageLayout.RuntimeIdentifier,
                    "--configfile", buildRepository.NuGetConfig,
                    "--packages", isolatedPackages,
                    "--artifacts-path", runArtifacts,
                    "--force-evaluate",
                    "--no-cache",
                    "--nologo",
                    runtimeSourceProperty,
                    "-p:RestoreAdditionalProjectSources=",
                    "-p:RestoreFallbackFolders=",
                    "-p:RestoreAdditionalProjectFallbackFolders=",
                    "-p:RestoreIgnoreFailedSources=false",
                    "-p:NuGetAudit=false"
                ],
                buildRepository.Root,
                buildEnvironment,
                cancellationToken);
            Console.WriteLine("Verifying the restored runtime feed...");
            runtimeFeed.Verify();
            Console.WriteLine("Pinning the isolated package cache and restore graph...");
            using var packageCacheInputs = PinnedFileTree.CaptureExact(
                isolatedPackages,
                "run-isolated NuGet package cache");
            using var restoreGraphInputs = PinnedFileTree.CaptureExistingFilesAllowAdditions(
                runArtifacts,
                "run-owned restore graph");
            Console.WriteLine("Verifying the restored project assets and package cache...");
            VerifyRestoredAssets(
                buildRepository,
                repository,
                isolatedPackages,
                runArtifacts,
                runtimeFeed);
            Console.WriteLine("Verifying all pinned build inputs before compilation...");
            VerifyPinnedBuildInputs(buildSourceInputs, restoreGraphInputs, packageCacheInputs);

            Console.WriteLine("Building the Release solution with warnings treated as errors...");
            await processes.RunDotnetAsync(
                ["build", buildRepository.Solution, "--configuration", PackageLayout.Configuration, "--no-restore", "--disable-build-servers", "--nologo", "--artifacts-path", runArtifacts, deterministicPathMapProperty, "-p:TreatWarningsAsErrors=true", "-p:UseSharedCompilation=false"],
                buildRepository.Root,
                buildEnvironment,
                cancellationToken);
            VerifyPinnedBuildInputs(buildSourceInputs, restoreGraphInputs, packageCacheInputs);

            using (var testArtifacts = PinnedFileTree.CaptureExact(
                       runArtifacts,
                       "built artifacts used by mandatory tests"))
            {
                var testsAssembly = RequireBuiltManagedEntryPoint(
                    runArtifacts,
                    "RimWorldAiTranslator.Tests");
                var glossaryAssembly = RequireBuiltManagedEntryPoint(
                    runArtifacts,
                    "RimWorldAiTranslator.GlossaryTool");
                repository.AssertNoReparseComponents(testsAssembly);
                repository.AssertNoReparseComponents(glossaryAssembly);

                Console.WriteLine("Running the mandatory C# console regression suite...");
                await processes.RunDotnetAsync(
                    [testsAssembly],
                    buildRepository.Root,
                    buildEnvironment,
                    cancellationToken);
                testArtifacts.Verify();
                VerifyPinnedBuildInputs(buildSourceInputs, restoreGraphInputs, packageCacheInputs);

                Console.WriteLine("Running the mandatory C# glossary-tool self-test...");
                await processes.RunDotnetAsync(
                    [glossaryAssembly, "self-test"],
                    buildRepository.Root,
                    buildEnvironment,
                    cancellationToken);
                testArtifacts.Verify();
                VerifyPinnedBuildInputs(buildSourceInputs, restoreGraphInputs, packageCacheInputs);
            }

            var publishArtifacts = repository.RequireRepositoryPath(Path.Combine(workRoot, "publish-artifacts"));
            CreateFileSnapshot(
                repository,
                runArtifacts,
                publishArtifacts,
                restoreGraphInputs.Snapshot,
                "clean publish restore graph");
            using var publishRestoreGraphInputs = PinnedFileTree.CaptureExistingFilesAllowAdditions(
                publishArtifacts,
                "clean publish restore graph");
            PinnedFileTree.AssertSameSnapshot(
                restoreGraphInputs.Snapshot,
                publishRestoreGraphInputs.Snapshot,
                "build and publish restore graphs");
            var publishRoot = repository.RequireRepositoryPath(Path.Combine(workRoot, "publish"));
            var packageRoot = repository.RequireRepositoryPath(Path.Combine(workRoot, "package"));
            Directory.CreateDirectory(publishRoot);
            Directory.CreateDirectory(packageRoot);

            Console.WriteLine("Publishing the self-contained single-file win-x64 application...");
            VerifyPinnedBuildInputs(buildSourceInputs, publishRestoreGraphInputs, packageCacheInputs);
            await processes.RunDotnetAsync(
                [
                    "publish", buildRepository.AppProject,
                    "--configuration", PackageLayout.Configuration,
                    "--runtime", PackageLayout.RuntimeIdentifier,
                    "--self-contained", "true",
                    "--output", publishRoot,
                    "--artifacts-path", publishArtifacts,
                    "--no-restore",
                    "--disable-build-servers",
                    "--nologo",
                    "-p:PublishSingleFile=true",
                    "-p:IncludeNativeLibrariesForSelfExtract=true",
                    "-p:EnableCompressionInSingleFile=false",
                    "-p:PublishTrimmed=false",
                    "-p:DebugType=None",
                    "-p:DebugSymbols=false",
                    deterministicPathMapProperty,
                    "-p:TreatWarningsAsErrors=true",
                    "-p:UseSharedCompilation=false"
                ],
                buildRepository.Root,
                buildEnvironment,
                cancellationToken);
            VerifyPinnedBuildInputs(buildSourceInputs, publishRestoreGraphInputs, packageCacheInputs);

            repository.AssertNoReparseTree(publishRoot);
            using var publishInputs = PinnedFileTree.CaptureExact(publishRoot, "publish output");
            AssertExactTopLevelFiles(publishRoot, PackageLayout.RuntimeFiles, "publish output");
            publishInputs.Verify();
            CopyAllowlistedFiles(
                publishRoot,
                packageRoot,
                PackageLayout.RuntimeFiles,
                repository,
                repository);
            CopyMappedFiles(
                buildRepository.Root,
                packageRoot,
                PackageLayout.DocumentationSourceFiles,
                buildRepository,
                repository);
            repository.AssertNoReparseTree(packageRoot);
            using var packageStagingInputs = PinnedFileTree.CaptureExact(packageRoot, "package staging");
            AssertExactTopLevelFiles(packageRoot, PackageLayout.AllFiles, "package staging");
            packageStagingInputs.Verify();
            AssertStagingMatchesPinnedInputs(buildSourceInputs, publishInputs, packageStagingInputs);
            VerifyPackagedVersionFile(Path.Combine(packageRoot, "VERSION"), versionSnapshot);
            ZeroAudit.VerifyPackage(packageRoot);

            var packagedApplication = Path.Combine(packageRoot, PackageLayout.ApplicationFileName);
            VerifyPublishedVersion(packagedApplication, version);

            var preparedArchive = repository.RequireRepositoryPath(Path.Combine(workRoot, PackageLayout.ArchiveName(version)));
            CreateZip(packageRoot, preparedArchive);
            var preparedManifest = repository.RequireRepositoryPath(Path.Combine(workRoot, PackageLayout.ManifestName(version)));
            string archiveHash;
            using (var preparedArchivePin = new FileStream(
                       preparedArchive,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                var verifiedArchiveFiles = ReadVerifiedArchiveEntries(preparedArchive);
                AssertArchiveMatchesSnapshot(packageStagingInputs.Snapshot, verifiedArchiveFiles);
                VerifyPackagedVersionArchive(preparedArchive, versionSnapshot);
                ZeroAudit.VerifyPackage(preparedArchive);

                Console.WriteLine("Running the packaged application with isolated data and discovery roots...");
                await RunPackageSmokeAsync(preparedArchive, cancellationToken);

                preparedArchivePin.Position = 0;
                archiveHash = Convert.ToHexString(SHA256.HashData(preparedArchivePin)).ToLowerInvariant();
                WriteManifest(preparedManifest, preparedArchive, archiveHash, version, verifiedArchiveFiles);
            }
            var manifestHash = ComputeSha256(preparedManifest);

            VerifyPinnedBuildInputs(buildSourceInputs, publishRestoreGraphInputs, packageCacheInputs);
            processWorkspace.Dispose();
            processWorkspace = null;
            repository.EnsureDistDirectory();
            var finalArchive = repository.RequireRepositoryPath(Path.Combine(repository.Dist, PackageLayout.ArchiveName(version)));
            var finalManifest = repository.RequireRepositoryPath(Path.Combine(repository.Dist, PackageLayout.ManifestName(version)));
            var outputTransaction = PackageOutputTransaction.Install(
                repository,
                version,
                preparedArchive,
                finalArchive,
                archiveHash,
                preparedManifest,
                finalManifest,
                manifestHash);
            try
            {
                if (!ComputeSha256(finalArchive).Equals(archiveHash, StringComparison.Ordinal))
                    throw new InvalidDataException("The committed archive hash differs from its verified hash.");
                if (!ComputeSha256(finalManifest).Equals(manifestHash, StringComparison.Ordinal))
                    throw new InvalidDataException("The committed manifest hash differs from its verified hash.");
                VerifyInstalledManifest(finalArchive, finalManifest, version);
                VerifyPackagedVersionArchive(finalArchive, versionSnapshot);
                ZeroAudit.VerifyPackage(finalArchive);
                VerifyPinnedBuildInputs(buildSourceInputs, publishRestoreGraphInputs, packageCacheInputs);
                outputTransaction.MarkVerified();
            }
            catch (Exception verificationFailure)
            {
                try
                {
                    outputTransaction.RollBack();
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("Final output verification and rollback both failed.", verificationFailure, rollbackFailure);
                }
                throw;
            }
            outputTransaction.Complete();
            var archiveInfo = new FileInfo(finalArchive);
            Console.WriteLine($"Package:  {archiveInfo.FullName}");
            Console.WriteLine($"Manifest: {finalManifest}");
            Console.WriteLine($"Bytes:    {archiveInfo.Length}");
            Console.WriteLine($"SHA-256:  {archiveHash}");
            return 0;
        }
        finally
        {
            try
            {
                if (workRoot is not null)
                    repository.DeleteOwnedWorkDirectory(workRoot);
            }
            catch (Exception cleanupFailure)
            {
                WriteCleanupWarning("owned package workspace", cleanupFailure);
            }
            try
            {
                processWorkspace?.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                WriteCleanupWarning("owned package-process workspace", cleanupFailure);
            }
        }
    }

    internal static string RequireBuiltManagedEntryPoint(
        string artifactsRoot,
        string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(artifactsRoot)
            || !Path.IsPathFullyQualified(artifactsRoot)
            || string.IsNullOrWhiteSpace(assemblyName)
            || assemblyName is "." or ".."
            || assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(assemblyName), assemblyName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The mandatory managed entry-point identity is unsafe.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactsRoot));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The run-owned build artifacts root is missing.");
        AssertRegularBuildDirectory(root);

        var relativePath = Path.Combine(
            "bin",
            assemblyName,
            PackageLayout.Configuration.ToLowerInvariant(),
            assemblyName + ".dll");
        var expected = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!string.Equals(Path.GetRelativePath(root, expected), relativePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The mandatory managed entry point escaped its exact artifacts location.");

        var current = root;
        foreach (var component in new[]
                 {
                     "bin",
                     assemblyName,
                     PackageLayout.Configuration.ToLowerInvariant()
                 })
        {
            current = Path.Combine(current, component);
            AssertRegularBuildDirectory(current);
        }

        if (!File.Exists(expected) || Directory.Exists(expected))
            throw new FileNotFoundException("The mandatory managed entry point is missing from its exact artifacts location.", expected);
        var attributes = File.GetAttributes(expected);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("The mandatory managed entry point is not a regular file.");
        using var stream = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > 256L * 1024 * 1024)
            throw new InvalidDataException("The mandatory managed entry point has an invalid size.");
        return expected;
    }

    private static void AssertRegularBuildDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("A mandatory build output directory is missing.");
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("A mandatory build output directory is redirected or is not a regular directory.");
        }
    }

    private static string CreateSourceSnapshot(
        RepositoryLayout repository,
        string workRoot,
        PinnedFileTree sourceInputs)
    {
        var snapshotRoot = repository.RequireRepositoryPath(Path.Combine(workRoot, "source"));
        if (File.Exists(snapshotRoot) || Directory.Exists(snapshotRoot))
            throw new IOException("The run-owned clean source snapshot path already exists.");
        Directory.CreateDirectory(snapshotRoot);
        foreach (var file in sourceInputs.Snapshot)
        {
            var source = repository.RequireRepositoryPath(Path.Combine(repository.Root, file.RelativePath));
            var destination = repository.RequireRepositoryPath(Path.Combine(snapshotRoot, file.RelativePath));
            var relative = Path.GetRelativePath(snapshotRoot, destination);
            if (Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A source snapshot input escaped its run-owned root.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
        repository.AssertNoReparseTree(snapshotRoot);
        return snapshotRoot;
    }

    private static void CreateFileSnapshot(
        RepositoryLayout repository,
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<PinnedFileDigest> files,
        string label)
    {
        sourceRoot = repository.RequireRepositoryPath(sourceRoot);
        destinationRoot = repository.RequireRepositoryPath(destinationRoot);
        if (File.Exists(destinationRoot) || Directory.Exists(destinationRoot))
            throw new IOException($"The run-owned {label} path already exists.");
        Directory.CreateDirectory(destinationRoot);
        foreach (var file in files)
        {
            var source = repository.RequireRepositoryPath(Path.Combine(sourceRoot, file.RelativePath));
            var destination = repository.RequireRepositoryPath(Path.Combine(destinationRoot, file.RelativePath));
            var relative = Path.GetRelativePath(destinationRoot, destination);
            if (Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"A {label} input escaped its run-owned root.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
        repository.AssertNoReparseTree(destinationRoot);
    }

    private static void VerifyBuildVersionProperties(RepositoryLayout repository, SemanticVersion version)
    {
        var propsPath = repository.RequireFile("Directory.Build.props");
        var document = XDocument.Load(propsPath, LoadOptions.None);
        var declaredVersion = document.Descendants("Version").Select(element => element.Value.Trim()).FirstOrDefault();
        var declaredFileVersion = document.Descendants("FileVersion").Select(element => element.Value.Trim()).FirstOrDefault();
        if (!version.Original.Equals(declaredVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Directory.Build.props Version does not match VERSION: {declaredVersion ?? "<missing>"}");
        if (!version.NumericFileVersion.Equals(declaredFileVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Directory.Build.props FileVersion does not match VERSION: {declaredFileVersion ?? "<missing>"}");
    }

    internal static void VerifyOfflineNuGetConfig(string path)
    {
        const long maximumBytes = 64 * 1024;
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException($"NuGet.config has an invalid size: {info.Length} bytes.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maximumBytes
        };
        using var reader = XmlReader.Create(path, settings);
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root is null
            || root.Name != XName.Get("configuration")
            || root.HasAttributes
            || document.Nodes().Any(node => node is not XElement && !IsWhitespaceNode(node))
            || root.Nodes().Any(node => node is not XElement && !IsWhitespaceNode(node)))
        {
            throw new InvalidDataException("NuGet.config must have one namespace-free, attribute-free configuration root.");
        }

        var sourceGroups = root.Elements().ToArray();
        if (sourceGroups.Length != 1
            || sourceGroups[0].Name != XName.Get("packageSources")
            || sourceGroups[0].HasAttributes
            || sourceGroups[0].Nodes().Any(node => node is not XElement && !IsWhitespaceNode(node)))
        {
            throw new InvalidDataException("NuGet.config must contain exactly one namespace-free packageSources element.");
        }

        var sourceEntries = sourceGroups[0].Elements().ToArray();
        if (sourceEntries.Length != 1
            || sourceEntries[0].Name != XName.Get("clear")
            || sourceEntries[0].HasElements
            || sourceEntries[0].HasAttributes
            || sourceEntries[0].Nodes().Any())
        {
            throw new InvalidDataException("NuGet.config packageSources must contain only an empty clear element.");
        }
    }

    private static bool IsWhitespaceNode(XNode node) =>
        node is XText text && string.IsNullOrWhiteSpace(text.Value);

    private static void VerifyRestoredAssets(
        RepositoryLayout repository,
        RepositoryLayout workRepository,
        string isolatedPackages,
        string runArtifacts,
        PreparedRuntimeFeed runtimeFeed)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(repository.Solution))
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;
            var fields = line.Split('"');
            if (fields.Length < 6 || !fields[5].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;
            var project = repository.RequireRepositoryPath(Path.Combine(repository.Root, fields[5]));
            if (!File.Exists(project))
                throw new FileNotFoundException("A solution project is missing during restore verification.", project);
            projects.Add(project);
        }
        if (projects.Count == 0)
            throw new InvalidDataException("The solution did not expose any project paths for restore verification.");

        runArtifacts = workRepository.RequireRepositoryPath(runArtifacts);
        var artifactsObjectRoot = workRepository.RequireRepositoryPath(Path.Combine(runArtifacts, "obj"));
        if (!Directory.Exists(artifactsObjectRoot))
            throw new DirectoryNotFoundException("Restore did not create the run-owned artifacts object root.");
        var assetsFiles = Directory.EnumerateFiles(
                artifactsObjectRoot,
                "project.assets.json",
                SearchOption.AllDirectories)
            .Select(workRepository.RequireRepositoryPath)
            .ToArray();
        if (assetsFiles.Length != projects.Count)
            throw new InvalidDataException("Restore did not create exactly one run-owned assets file per solution project.");

        var expectedRuntimePackages = ParseExpectedRuntimePackages(runtimeFeed.ExpectedContentHashes.Keys);
        var observedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appRuntimeGraphObserved = false;
        foreach (var assetsPath in assetsFiles)
        {
            var info = new FileInfo(assetsPath);
            if (info.Length <= 0 || info.Length > 16 * 1024 * 1024)
                throw new InvalidDataException($"Restored assets file has an invalid size: {assetsPath} ({info.Length} bytes).");

            using var stream = new FileStream(assetsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            var packageFolders = root.GetProperty("packageFolders").EnumerateObject().ToArray();
            if (packageFolders.Length != 1 || !PathsEqual(packageFolders[0].Name, isolatedPackages))
                throw new InvalidDataException($"Restore assets did not use only the run-isolated package cache: {assetsPath}");

            var projectMetadata = root.GetProperty("project");
            var restore = projectMetadata.GetProperty("restore");
            var restoredProject = restore.GetProperty("projectPath").GetString();
            var project = projects.SingleOrDefault(candidate => PathsEqual(restoredProject, candidate));
            if (project is null
                || !observedProjects.Add(project)
                || !PathsEqual(restore.GetProperty("packagesPath").GetString(), isolatedPackages))
            {
                throw new InvalidDataException($"Restore assets contain unexpected project or package-cache paths: {assetsPath}");
            }
            VerifyRestoreSidecars(workRepository, assetsPath, project);

            var hasRuntimeDownloadGraph = VerifyRuntimeDownloadDependencies(
                projectMetadata,
                expectedRuntimePackages,
                assetsPath);
            if (PathsEqual(project, repository.AppProject))
            {
                if (!hasRuntimeDownloadGraph)
                {
                    throw new InvalidDataException(
                        $"The application restore assets do not declare their pinned runtime download graph: {assetsPath}");
                }
                appRuntimeGraphObserved = true;
            }

            var configFiles = restore.GetProperty("configFilePaths").EnumerateArray().ToArray();
            if (configFiles.Length != 1 || !PathsEqual(configFiles[0].GetString(), repository.NuGetConfig))
                throw new InvalidDataException($"Restore assets were not produced solely from the repository NuGet.config: {assetsPath}");
            if (!restore.TryGetProperty("sources", out var sources)
                || sources.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Restore assets do not declare their sole runtime feed: {assetsPath}");
            }
            var restoredSources = sources.EnumerateObject().ToArray();
            if (restoredSources.Length != 1 || !PathsEqual(restoredSources[0].Name, runtimeFeed.Root))
            {
                throw new InvalidDataException($"Restore assets retain a source other than the verified runtime feed: {assetsPath}");
            }

            foreach (var library in root.GetProperty("libraries").EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !type.GetString()!.Equals("package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!runtimeFeed.ExpectedContentHashes.TryGetValue(library.Name, out var expectedHash)
                    || !library.Value.TryGetProperty("sha512", out var sha512)
                    || sha512.ValueKind != JsonValueKind.String
                    || !string.Equals(sha512.GetString(), expectedHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Restore assets resolved a non-allowlisted or hash-mismatched package: {assetsPath}");
                }
            }
        }

        if (!observedProjects.SetEquals(projects))
            throw new InvalidDataException("Restore assets did not map exactly once to every solution project.");
        if (!appRuntimeGraphObserved)
            throw new InvalidDataException("Restore assets did not map the application project to its pinned runtime download graph.");
        VerifyIsolatedPackageCache(workRepository, isolatedPackages, expectedRuntimePackages);
    }

    internal static void VerifyRuntimeDownloadDependenciesForSelfTest(
        string assetsJson,
        IReadOnlyDictionary<string, string> expectedPackages)
    {
        using var document = JsonDocument.Parse(
            assetsJson,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        if (!document.RootElement.TryGetProperty("project", out var project)
            || !VerifyRuntimeDownloadDependencies(project, expectedPackages, "synthetic restore assets"))
        {
            throw new InvalidDataException("Synthetic restore assets do not declare their pinned runtime download graph.");
        }
    }

    private static bool VerifyRuntimeDownloadDependencies(
        JsonElement project,
        IReadOnlyDictionary<string, string> expectedPackages,
        string assetsPath)
    {
        if (expectedPackages.Count == 0
            || project.ValueKind != JsonValueKind.Object
            || !project.TryGetProperty("frameworks", out var frameworks)
            || frameworks.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Restore assets have an invalid project framework graph: {assetsPath}");
        }

        var foundRuntimeGraph = false;
        foreach (var framework in frameworks.EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Restore assets contain an invalid framework entry: {assetsPath}");
            if (!framework.Value.TryGetProperty("downloadDependencies", out var dependencies)) continue;
            foundRuntimeGraph = true;
            if (dependencies.ValueKind != JsonValueKind.Array
                || dependencies.GetArrayLength() != expectedPackages.Count)
            {
                throw new InvalidDataException($"Restore assets contain an incomplete runtime download graph: {assetsPath}");
            }

            var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in dependencies.EnumerateArray())
            {
                if (dependency.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Restore assets contain a malformed runtime download dependency: {assetsPath}");
                var properties = dependency.EnumerateObject().ToArray();
                var propertyNames = properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                if (properties.Length != 2 || !propertyNames.SetEquals(["name", "version"]))
                {
                    throw new InvalidDataException($"Restore assets contain a malformed runtime download dependency: {assetsPath}");
                }

                var nameElement = dependency.GetProperty("name");
                var versionElement = dependency.GetProperty("version");
                if (nameElement.ValueKind != JsonValueKind.String
                    || versionElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"Restore assets contain a malformed runtime download dependency: {assetsPath}");
                }
                var name = nameElement.GetString();
                var versionRange = versionElement.GetString();
                if (string.IsNullOrWhiteSpace(name)
                    || !expectedPackages.TryGetValue(name, out var expectedVersion)
                    || !string.Equals(versionRange, $"[{expectedVersion}, {expectedVersion}]", StringComparison.Ordinal)
                    || !observed.Add(name))
                {
                    throw new InvalidDataException($"Restore assets requested a non-pinned runtime package or version: {assetsPath}");
                }
            }

            if (!observed.SetEquals(expectedPackages.Keys))
                throw new InvalidDataException($"Restore assets do not request the exact pinned runtime package set: {assetsPath}");
        }
        return foundRuntimeGraph;
    }

    private static Dictionary<string, string> ParseExpectedRuntimePackages(
        IEnumerable<string> expectedLibraries)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in expectedLibraries)
        {
            var separator = library.LastIndexOf('/');
            if (separator <= 0
                || separator != library.IndexOf('/')
                || separator == library.Length - 1
                || !expected.TryAdd(library[..separator], library[(separator + 1)..]))
            {
                throw new InvalidDataException("The pinned runtime package identity set is malformed or ambiguous.");
            }
        }
        if (expected.Count == 0)
            throw new InvalidDataException("The pinned runtime package identity set is empty.");
        return expected;
    }

    private static void VerifyRestoreSidecars(
        RepositoryLayout workRepository,
        string assetsPath,
        string project)
    {
        var directory = Path.GetDirectoryName(assetsPath)
                        ?? throw new InvalidDataException("A restore assets path has no parent directory.");
        var projectFileName = Path.GetFileName(project);
        foreach (var name in new[]
                 {
                     "project.nuget.cache",
                     projectFileName + ".nuget.dgspec.json",
                     projectFileName + ".nuget.g.props",
                     projectFileName + ".nuget.g.targets"
                 })
        {
            var sidecar = workRepository.RequireRepositoryPath(Path.Combine(directory, name));
            if (!File.Exists(sidecar) || new FileInfo(sidecar).Length <= 0)
                throw new InvalidDataException("Restore did not create every required run-owned NuGet sidecar.");
            workRepository.AssertNoReparseComponents(sidecar);
        }
    }

    private static void VerifyIsolatedPackageCache(
        RepositoryLayout repository,
        string isolatedPackages,
        Dictionary<string, string> expected)
    {
        repository.AssertNoReparseTree(isolatedPackages);
        var packageDirectories = Directory.EnumerateDirectories(isolatedPackages, "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        if (Directory.EnumerateFiles(isolatedPackages, "*", SearchOption.TopDirectoryOnly).Any()
            || packageDirectories.Length != expected.Count)
        {
            throw new InvalidDataException("The run-isolated package cache contains unexpected top-level entries.");
        }

        foreach (var packageDirectory in packageDirectories)
        {
            var id = Path.GetFileName(packageDirectory);
            if (id is null || !expected.TryGetValue(id, out var version))
                throw new InvalidDataException("The run-isolated package cache contains a non-allowlisted package ID.");
            var versions = Directory.EnumerateDirectories(packageDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
            if (Directory.EnumerateFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly).Any()
                || versions.Length != 1
                || !string.Equals(Path.GetFileName(versions[0]), version, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The run-isolated package cache contains a non-allowlisted package version.");
            }
        }
    }

    private static void VerifyPinnedBuildInputs(
        PinnedFileTree sourceInputs,
        PinnedFileTree restoreGraphInputs,
        PinnedFileTree packageCacheInputs)
    {
        Console.WriteLine("  Verifying pinned clean-source inputs...");
        sourceInputs.Verify();
        Console.WriteLine("  Verifying pinned restore-graph inputs...");
        restoreGraphInputs.Verify();
        Console.WriteLine("  Verifying pinned package-cache inputs...");
        packageCacheInputs.Verify();
    }

    private static void AssertStagingMatchesPinnedInputs(
        PinnedFileTree sourceInputs,
        PinnedFileTree publishInputs,
        PinnedFileTree stagingInputs)
    {
        var stagedRuntime = stagingInputs.Snapshot
            .Where(file => PackageLayout.RuntimeFiles.Contains(file.RelativePath))
            .ToArray();
        PinnedFileTree.AssertSameSnapshot(
            publishInputs.Snapshot,
            stagedRuntime,
            "published runtime and package staging");

        var sourceDocumentation = PackageLayout.DocumentationSourceFiles
            .Select(mapping =>
            {
                var source = sourceInputs.Snapshot.SingleOrDefault(file =>
                    file.RelativePath.Equals(mapping.Value, StringComparison.OrdinalIgnoreCase));
                if (source is null)
                    throw new InvalidDataException($"Pinned package source is missing: {mapping.Value}");
                return source with { RelativePath = mapping.Key };
            })
            .ToArray();
        var stagedDocumentation = stagingInputs.Snapshot
            .Where(file => PackageLayout.DocumentationFiles.Contains(file.RelativePath))
            .ToArray();
        PinnedFileTree.AssertSameSnapshot(
            sourceDocumentation,
            stagedDocumentation,
            "repository documentation and package staging");
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        var leftFull = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rightFull = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return leftFull.Equals(rightFull, StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifyPackagedVersionFile(string path, VersionSnapshot snapshot)
    {
        var packaged = File.ReadAllBytes(path);
        if (!packaged.AsSpan().SequenceEqual(snapshot.Bytes))
            throw new InvalidDataException("Packaged VERSION does not exactly match the starting VERSION snapshot.");
    }

    private static void VerifyPackagedVersionArchive(string path, VersionSnapshot snapshot)
    {
        PackageArchivePolicy.ValidateArchiveFile(path);
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries
            .Where(entry => entry.FullName.Equals("VERSION", StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1 || !entries[0].Name.Equals("VERSION", StringComparison.Ordinal))
            throw new InvalidDataException("Package archive must contain exactly one top-level VERSION entry.");
        if (entries[0].Length != snapshot.Bytes.LongLength)
            throw new InvalidDataException("Archived VERSION size does not match the starting VERSION snapshot.");

        using var stream = entries[0].Open();
        using var buffer = new MemoryStream(snapshot.Bytes.Length);
        stream.CopyTo(buffer);
        if (!buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)).SequenceEqual(snapshot.Bytes))
            throw new InvalidDataException("Archived VERSION does not exactly match the starting VERSION snapshot.");
    }

    private static void CopyAllowlistedFiles(
        string sourceRoot,
        string destinationRoot,
        IEnumerable<string> names,
        RepositoryLayout sourceRepository,
        RepositoryLayout destinationRepository)
    {
        foreach (var name in names.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var source = sourceRepository.RequireRepositoryPath(Path.Combine(sourceRoot, name));
            if (!File.Exists(source)) throw new FileNotFoundException($"Allowlisted package file is missing: {name}", source);
            sourceRepository.AssertNoReparseComponents(source);
            var destination = destinationRepository.RequireRepositoryPath(Path.Combine(destinationRoot, name));
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"Package staging destination already exists: {destination}");
            File.Copy(source, destination, overwrite: false);
            destinationRepository.AssertNoReparseComponents(destination);
        }
    }

    private static void CopyMappedFiles(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyDictionary<string, string> files,
        RepositoryLayout sourceRepository,
        RepositoryLayout destinationRepository)
    {
        foreach (var file in files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var source = sourceRepository.RequireRepositoryPath(Path.Combine(sourceRoot, file.Value));
            if (!File.Exists(source))
                throw new FileNotFoundException($"Mapped package source is missing: {file.Value}", source);
            sourceRepository.AssertNoReparseComponents(source);
            var destination = destinationRepository.RequireRepositoryPath(Path.Combine(destinationRoot, file.Key));
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"Package staging destination already exists: {destination}");
            File.Copy(source, destination, overwrite: false);
            destinationRepository.AssertNoReparseComponents(destination);
        }
    }

    private static void AssertExactTopLevelFiles(string root, IReadOnlySet<string> expected, string label)
    {
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();
        var missing = expected.Except(files, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var unexpected = files.Except(expected, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (directories.Length > 0 || missing.Length > 0 || unexpected.Length > 0)
        {
            throw new InvalidDataException(
                $"{label} did not match its exact allowlist. " +
                $"missing=[{string.Join(", ", missing)}], unexpected=[{string.Join(", ", unexpected)}], " +
                $"directories=[{string.Join(", ", directories.Select(Path.GetFileName))}]");
        }
    }

    private static void VerifyPublishedVersion(string executable, SemanticVersion version)
    {
        var info = FileVersionInfo.GetVersionInfo(executable);
        if (!string.Equals(info.ProductVersion?.Trim(), version.Original, StringComparison.Ordinal)
            || !string.Equals(info.FileVersion?.Trim(), version.NumericFileVersion, StringComparison.Ordinal)
            || info.FileMajorPart != version.Major
            || info.FileMinorPart != version.Minor
            || info.FileBuildPart != version.Patch
            || info.FilePrivatePart != 0
            || info.ProductMajorPart != version.Major
            || info.ProductMinorPart != version.Minor
            || info.ProductBuildPart != version.Patch
            || info.ProductPrivatePart != 0)
        {
            throw new InvalidDataException(
                "Published executable version mismatch: " +
                $"product={info.ProductVersion} ({info.ProductMajorPart}.{info.ProductMinorPart}.{info.ProductBuildPart}.{info.ProductPrivatePart}), " +
                $"file={info.FileVersion} ({info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}), " +
                $"expected={version.Original}/{version.NumericFileVersion}");
        }
    }

    private static void CreateZip(string packageRoot, string archivePath)
    {
        if (File.Exists(archivePath) || Directory.Exists(archivePath))
            throw new IOException($"Prepared archive path already exists: {archivePath}");

        using var archiveStream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var source in Directory.EnumerateFiles(packageRoot, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path)!, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(Path.GetFileName(source)!, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        archiveStream.Flush(flushToDisk: true);
    }

    private static async Task RunPackageSmokeAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        var smoke = SmokeWorkspace.Create();
        Process? holder = null;
        Process? contender = null;
        Process? reacquirer = null;
        WindowsKillOnCloseJob? holderContainment = null;
        WindowsKillOnCloseJob? contenderContainment = null;
        WindowsKillOnCloseJob? reacquirerContainment = null;
        PackageSmokeEvidence? evidence = null;
        Exception? operationFailure = null;
        var smokeStage = "workspace-created";
        try
        {
            smokeStage = "extract-and-pin";
            var extractRoot = smoke.CreateDirectory("package");
            ExtractVerifiedZip(archivePath, extractRoot);
            using var extractedPackageInputs = PinnedFileTree.CaptureExact(
                extractRoot,
                "package-smoke extracted files");
            AssertArchiveMatchesSnapshot(
                extractedPackageInputs.Snapshot,
                ReadVerifiedArchiveEntries(archivePath));
            var dataRoot = smoke.CreateDirectory("appdata");
            var discoveryRoot = smoke.CreateDirectory("discovery");
            var processTemp = smoke.CreateDirectory("temp");
            var processTmp = smoke.CreateDirectory("tmp");
            var processProfile = smoke.CreateDirectory("profile");
            var processAppData = smoke.CreateDirectory(Path.Combine("profile", "AppData", "Roaming"));
            var processLocalAppData = smoke.CreateDirectory(Path.Combine("profile", "AppData", "Local"));
            var bundleExtractRoot = smoke.CreateDirectory("bundle-extract");
            var bundleEntriesBeforeColdStart = Directory
                .EnumerateFileSystemEntries(bundleExtractRoot, "*", SearchOption.AllDirectories)
                .Count();
            if (bundleEntriesBeforeColdStart != 0)
                throw new InvalidDataException("The cold package-smoke bundle extraction root was not empty.");
            var marker = smoke.RequireChild(Path.Combine("discovery", PackageLayout.DiscoveryMarkerFileName));
            File.WriteAllText(marker, PackageLayout.DiscoveryMarkerContent + Environment.NewLine, new UTF8Encoding(false));
            smoke.AssertNoReparseTree();

            smokeStage = "cold-start";
            var acknowledgement = smoke.RequireChild(Path.Combine("appdata", "isolated-discovery.ack"));
            var executable = smoke.RequireChild(Path.Combine("package", PackageLayout.ApplicationFileName));
            var environment = new Dictionary<string, string?>
            {
                [PackageLayout.DataRootEnvironmentVariable] = dataRoot,
                [PackageLayout.DiscoveryRootEnvironmentVariable] = discoveryRoot,
                [PackageLayout.DiscoveryAckEnvironmentVariable] = acknowledgement,
                ["TEMP"] = processTemp,
                ["TMP"] = processTmp,
                ["HOME"] = processProfile,
                ["USERPROFILE"] = processProfile,
                ["APPDATA"] = processAppData,
                ["LOCALAPPDATA"] = processLocalAppData,
                ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundleExtractRoot
            };

            var holderLaunch = Stopwatch.StartNew();
            holderContainment = WindowsKillOnCloseJob.Create();
            holder = ExternalProcessPolicy.StartGeneratedApplication(
                executable,
                extractRoot,
                environment,
                holderContainment);
            var holderReady = await WaitForUsableApplicationAsync(
                holder,
                acknowledgement,
                smoke,
                holderLaunch,
                cancellationToken);
            var descendants = WindowsProcessTree.GetDescendants(holder);
            if (descendants.Count > 0)
                throw new InvalidOperationException($"Packaged application started descendant processes: {FormatProcesses(descendants)}");
            var holderLiveAccounting = RequireLiveAccounting(holderContainment, "holder");
            smoke.AssertNoReparseTree();
            var bundleEntriesAfterColdStart = Directory
                .EnumerateFileSystemEntries(bundleExtractRoot, "*", SearchOption.AllDirectories)
                .Count();
            if (bundleEntriesAfterColdStart == 0)
                throw new InvalidDataException("The packaged cold start did not populate its isolated bundle cache.");

            smokeStage = "duplicate-instance";
            var contenderLaunch = Stopwatch.StartNew();
            contenderContainment = WindowsKillOnCloseJob.Create();
            contender = ExternalProcessPolicy.StartGeneratedApplication(
                executable,
                extractRoot,
                environment,
                contenderContainment);
            var duplicateDialog = await WaitForDuplicateDialogAsync(
                contender,
                contenderLaunch,
                cancellationToken);
            var contenderLiveAccounting = RequireLiveAccounting(contenderContainment, "contender");
            WindowsUiProbe.ClickDuplicateDialogConfirmation(
                contender.Id,
                duplicateDialog.Dialog,
                ApplicationWindowTitle,
                DuplicateInstanceMessage);
            if (!await WaitForExitAsync(contender, TimeSpan.FromSeconds(5), cancellationToken))
                throw new TimeoutException("The duplicate packaged application did not exit after confirmation.");
            if (contender.ExitCode != 2)
                throw new InvalidOperationException(
                    $"The duplicate packaged application exited with code {contender.ExitCode} instead of 2.");
            var contenderAccounting = await WaitForCompletedAccountingAsync(
                contenderContainment,
                "contender",
                cancellationToken);

            if (holder.HasExited)
                throw new InvalidOperationException("The holder exited while the duplicate-instance dialog was handled.");
            WindowsUiProbe.AssertMainWindowResponsive(
                holder.Id,
                holderReady.MainWindowHandle,
                ApplicationWindowTitle);

            smokeStage = "cold-close";
            var holderCloseMilliseconds = await CloseApplicationNormallyAsync(
                holder,
                holderReady.MainWindowHandle,
                cancellationToken);
            var holderAccounting = await WaitForCompletedAccountingAsync(
                holderContainment,
                "holder",
                cancellationToken);

            smokeStage = "warm-restart";
            DeleteSyntheticAcknowledgement(acknowledgement, smoke);
            var reacquirerLaunch = Stopwatch.StartNew();
            reacquirerContainment = WindowsKillOnCloseJob.Create();
            reacquirer = ExternalProcessPolicy.StartGeneratedApplication(
                executable,
                extractRoot,
                environment,
                reacquirerContainment);
            var reacquirerReady = await WaitForUsableApplicationAsync(
                reacquirer,
                acknowledgement,
                smoke,
                reacquirerLaunch,
                cancellationToken);
            descendants = WindowsProcessTree.GetDescendants(reacquirer);
            if (descendants.Count > 0)
                throw new InvalidOperationException($"Restarted packaged application started descendant processes: {FormatProcesses(descendants)}");
            var reacquirerLiveAccounting = RequireLiveAccounting(reacquirerContainment, "reacquirer");
            var reacquirerCloseMilliseconds = await CloseApplicationNormallyAsync(
                reacquirer,
                reacquirerReady.MainWindowHandle,
                cancellationToken);
            var reacquirerAccounting = await WaitForCompletedAccountingAsync(
                reacquirerContainment,
                "reacquirer",
                cancellationToken);

            smokeStage = "structured-evidence";
            evidence = new PackageSmokeEvidence
            {
                SchemaVersion = "package-smoke-evidence-v3",
                Status = "PASS",
                SyntheticFixture = true,
                UserDataUsed = false,
                ExternalNetworkUsed = false,
                InteractiveDialogRequired = true,
                BundleExtractEntryCountBeforeColdStart = bundleEntriesBeforeColdStart,
                BundleExtractEntryCountAfterColdStart = bundleEntriesAfterColdStart,
                WarmRestartReusedBundleExtractRoot = true,
                ColdFirst = new ApplicationRunEvidence
                {
                    Role = "holder",
                    CacheState = "cold-first-empty-bundle-extract-root",
                    ProcessId = holder.Id,
                    FirstVisibleWindowMilliseconds = holderReady.FirstVisibleWindowMilliseconds,
                    FirstUsableMilliseconds = holderReady.FirstUsableMilliseconds,
                    WorkingSet64BytesAtFirstUsable = holderReady.WorkingSet64Bytes,
                    PrivateMemorySize64BytesAtFirstUsable = holderReady.PrivateMemorySize64Bytes,
                    HandleCountAtFirstUsable = holderReady.HandleCount,
                    CloseToExitMilliseconds = holderCloseMilliseconds,
                    ExitCode = holder.ExitCode,
                    AcknowledgementVerified = true,
                    MainWindowResponsive = true,
                    DescendantProcessCountWhileRunning = 0,
                    LiveJobAccounting = ToEvidence(holderLiveAccounting),
                    CompletedJobAccounting = ToEvidence(holderAccounting)
                },
                Duplicate = new DuplicateRunEvidence
                {
                    ProcessId = contender.Id,
                    DialogObservedMilliseconds = duplicateDialog.ObservedMilliseconds,
                    TitleMatched = duplicateDialog.Dialog.TitleMatched,
                    BodyMatched = duplicateDialog.Dialog.BodyMatched,
                    ConfirmationButtonControlId = duplicateDialog.Dialog.ConfirmationButtonControlId,
                    ReportedDefaultControlId = duplicateDialog.Dialog.ReportedDefaultControlId,
                    ConfirmationButtonVisibleAndEnabled = duplicateDialog.Dialog.ConfirmationButtonVisibleAndEnabled,
                    DefaultButtonIsConfirmation = duplicateDialog.Dialog.DefaultButtonIsConfirmation,
                    ConfirmationButtonClickedByPidScopedMessage = true,
                    ExitCode = contender.ExitCode,
                    HolderResponsiveAfterDuplicate = true,
                    LiveJobAccounting = ToEvidence(contenderLiveAccounting),
                    CompletedJobAccounting = ToEvidence(contenderAccounting)
                },
                WarmRestart = new ApplicationRunEvidence
                {
                    Role = "reacquirer",
                    CacheState = "warm-reused-bundle-extract-root",
                    ProcessId = reacquirer.Id,
                    FirstVisibleWindowMilliseconds = reacquirerReady.FirstVisibleWindowMilliseconds,
                    FirstUsableMilliseconds = reacquirerReady.FirstUsableMilliseconds,
                    WorkingSet64BytesAtFirstUsable = reacquirerReady.WorkingSet64Bytes,
                    PrivateMemorySize64BytesAtFirstUsable = reacquirerReady.PrivateMemorySize64Bytes,
                    HandleCountAtFirstUsable = reacquirerReady.HandleCount,
                    CloseToExitMilliseconds = reacquirerCloseMilliseconds,
                    ExitCode = reacquirer.ExitCode,
                    AcknowledgementVerified = true,
                    MainWindowResponsive = true,
                    DescendantProcessCountWhileRunning = 0,
                    LiveJobAccounting = ToEvidence(reacquirerLiveAccounting),
                    CompletedJobAccounting = ToEvidence(reacquirerAccounting)
                },
                SamePhysicalDataRootReacquired = true
            };

            smoke.AssertNoReparseTree();
        }
        catch (Exception exception)
        {
            WriteBoundedSmokeFailure("operation:" + smokeStage, exception);
            operationFailure = exception;
        }

        var cleanupFailures = new List<Exception>();
        await CleanupSmokeProcessAsync(reacquirer, reacquirerContainment, cleanupFailures);
        await CleanupSmokeProcessAsync(contender, contenderContainment, cleanupFailures);
        await CleanupSmokeProcessAsync(holder, holderContainment, cleanupFailures);
        var smokeCleanupWatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                smoke.Dispose();
                break;
            }
            catch (IOException exception) when (
                IsSharingViolation(exception)
                && smokeCleanupWatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50, CancellationToken.None);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
                break;
            }
        }
        foreach (var cleanupFailure in cleanupFailures)
            WriteBoundedSmokeFailure("cleanup", cleanupFailure);

        if (operationFailure is not null)
        {
            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Package smoke and cleanup both failed.",
                    cleanupFailures.Prepend(operationFailure));
            }
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Package smoke cleanup failed.", cleanupFailures);
        if (evidence is null)
            throw new InvalidOperationException("Package smoke completed without structured evidence.");

        evidence.Cleanup = new SmokeCleanupEvidence
        {
            AllProcessesExited = true,
            TempRootRemoved = !Directory.Exists(smoke.Root)
        };
        if (!evidence.Cleanup.TempRootRemoved)
            throw new IOException("The package-smoke TEMP root remained after verified cleanup.");
        Console.WriteLine(SmokeEvidencePrefix + JsonSerializer.Serialize(evidence, SmokeJsonOptions));
    }

    private static void WriteBoundedSmokeFailure(string stage, Exception failure)
    {
        IEnumerable<Exception> flattened;
        if (failure is AggregateException aggregate)
        {
            flattened = aggregate.Flatten().InnerExceptions;
        }
        else
        {
            var chain = new List<Exception>();
            for (var current = failure; current is not null && chain.Count < 8; current = current.InnerException)
                chain.Add(current);
            flattened = chain;
        }
        foreach (var exception in flattened.Take(8))
        {
            var message = exception.Message
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            if (message.Length > 512) message = message[..512];
            Console.Error.WriteLine(
                $"PACKAGE_SMOKE_FAILURE stage={stage}; type={exception.GetType().Name}; message={message}");
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xffff) is 32 or 33;

    private static async Task<ReadyApplicationObservation> WaitForUsableApplicationAsync(
        Process process,
        string acknowledgement,
        SmokeWorkspace smoke,
        Stopwatch launchWatch,
        CancellationToken cancellationToken)
    {
        var wait = Stopwatch.StartNew();
        double? firstVisibleWindowMilliseconds = null;
        while (wait.Elapsed < TimeSpan.FromSeconds(25))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Packaged application exited during startup with code {process.ExitCode}.");
            if (firstVisibleWindowMilliseconds is null && WindowsUiProbe.HasVisibleWindow(process.Id))
                firstVisibleWindowMilliseconds = RoundMilliseconds(launchWatch.Elapsed.TotalMilliseconds);
            var mainWindowReady = WindowsUiProbe.TryFindMainWindow(
                process.Id,
                ApplicationWindowTitle,
                out var mainWindow);
            var isolationAcknowledged = ReadAcknowledgement(acknowledgement, smoke);
            if (mainWindowReady && isolationAcknowledged)
            {
                WindowsUiProbe.AssertMainWindowResponsive(process.Id, mainWindow, ApplicationWindowTitle);
                var firstUsableMilliseconds = RoundMilliseconds(launchWatch.Elapsed.TotalMilliseconds);
                process.Refresh();
                return new ReadyApplicationObservation(
                    mainWindow,
                    firstVisibleWindowMilliseconds
                    ?? firstUsableMilliseconds,
                    firstUsableMilliseconds,
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    process.HandleCount);
            }
            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException(
            "Packaged application did not expose an enabled, responsive MainForm and exact isolation acknowledgement within 25 seconds.");
    }

    private static async Task<DuplicateDialogObservation> WaitForDuplicateDialogAsync(
        Process process,
        Stopwatch launchWatch,
        CancellationToken cancellationToken)
    {
        var wait = Stopwatch.StartNew();
        Exception? lastInspectionFailure = null;
        while (wait.Elapsed < TimeSpan.FromSeconds(10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"The duplicate packaged application exited before presenting its notice ({process.ExitCode}).");
            try
            {
                var dialog = WindowsUiProbe.InspectDuplicateDialog(
                    process.Id,
                    ApplicationWindowTitle,
                    DuplicateInstanceMessage);
                if (dialog is not null)
                {
                    return new DuplicateDialogObservation(
                        dialog,
                        RoundMilliseconds(launchWatch.Elapsed.TotalMilliseconds));
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or System.ComponentModel.Win32Exception)
            {
                lastInspectionFailure = exception;
            }
            await Task.Delay(50, cancellationToken);
        }

        if (lastInspectionFailure is not null)
        {
            throw new InvalidDataException(
                "The PID-scoped duplicate-instance dialog never settled to its exact contract.",
                lastInspectionFailure);
        }
        throw new TimeoutException(
            "The PID-scoped duplicate-instance dialog was unavailable in the interactive session.");
    }

    private static async Task<double> CloseApplicationNormallyAsync(
        Process process,
        IntPtr mainWindow,
        CancellationToken cancellationToken)
    {
        var closeRequestedAt = WindowsUiProbe.RequestNormalClose(
            process.Id,
            mainWindow,
            ApplicationWindowTitle);
        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(10), cancellationToken))
            throw new TimeoutException("Packaged application did not exit within 10 seconds after a normal close request.");
        var closeToExitMilliseconds = RoundMilliseconds(
            Stopwatch.GetElapsedTime(closeRequestedAt).TotalMilliseconds);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Packaged application smoke test exited with code {process.ExitCode}.");
        return closeToExitMilliseconds;
    }

    private static void DeleteSyntheticAcknowledgement(string path, SmokeWorkspace smoke)
    {
        if (!ReadAcknowledgement(path, smoke))
            throw new InvalidDataException("The verified synthetic acknowledgement disappeared before restart.");
        smoke.AssertRegularFileNoReparse(path);
        File.Delete(path);
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("The synthetic acknowledgement remained after deletion.");
        smoke.AssertNoReparseTree();
    }

    private static WindowsKillOnCloseJob.JobAccounting RequireLiveAccounting(
        WindowsKillOnCloseJob containment,
        string role)
    {
        var accounting = containment.GetAccounting();
        if (accounting.TotalProcesses != 1 || accounting.ActiveProcesses != 1)
        {
            throw new InvalidOperationException(
                $"Package-smoke {role} Job Object was not exactly one live process: " +
                $"total={accounting.TotalProcesses}, active={accounting.ActiveProcesses}, " +
                $"terminated={accounting.TotalTerminatedProcesses}.");
        }
        return accounting;
    }

    private static async Task<WindowsKillOnCloseJob.JobAccounting> WaitForCompletedAccountingAsync(
        WindowsKillOnCloseJob containment,
        string role,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        WindowsKillOnCloseJob.JobAccounting? accounting = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            accounting = containment.GetAccounting();
            if (accounting.TotalProcesses == 1 && accounting.ActiveProcesses == 0)
                return accounting;
            await Task.Delay(25, cancellationToken);
        }

        accounting = containment.GetAccounting();
        if (accounting.TotalProcesses == 1 && accounting.ActiveProcesses == 0)
            return accounting;
        throw new InvalidOperationException(
            $"Package-smoke {role} Job Object did not settle to exactly one completed process: " +
            $"total={accounting.TotalProcesses}, active={accounting.ActiveProcesses}, " +
            $"terminated={accounting.TotalTerminatedProcesses}.");
    }

    private static JobAccountingEvidence ToEvidence(WindowsKillOnCloseJob.JobAccounting accounting) => new()
    {
        TotalProcesses = accounting.TotalProcesses,
        ActiveProcesses = accounting.ActiveProcesses,
        TotalTerminatedProcesses = accounting.TotalTerminatedProcesses
    };

    private static async Task CleanupSmokeProcessAsync(
        Process? process,
        WindowsKillOnCloseJob? containment,
        List<Exception> failures)
    {
        try { containment?.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        if (process is null) return;
        try
        {
            if (!process.HasExited && !process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        try { process.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
    }

    private static double RoundMilliseconds(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);

    internal static bool ReadAcknowledgement(string path, SmokeWorkspace smoke)
    {
        if (!File.Exists(path)) return false;
        try
        {
            smoke.AssertRegularFileNoReparse(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: ExpectedDiscoveryAcknowledgement.Length,
                FileOptions.SequentialScan);
            if (stream.Length != ExpectedDiscoveryAcknowledgement.Length)
                throw new InvalidDataException("Isolated-discovery acknowledgement has an unexpected size.");
            var actual = new byte[ExpectedDiscoveryAcknowledgement.Length];
            stream.ReadExactly(actual);
            smoke.AssertRegularFileNoReparse(path);
            return actual.AsSpan().SequenceEqual(ExpectedDiscoveryAcknowledgement);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string FormatProcesses(IEnumerable<WindowsProcessTree.ProcessRecord> records) =>
        string.Join(", ", records.Select(record => $"{record.Name}({record.ProcessId})"));

    private static void ExtractVerifiedZip(string archivePath, string destination)
    {
        PackageArchivePolicy.ValidateArchiveFile(archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count != PackageLayout.AllFiles.Count)
            throw new InvalidDataException($"Archive contains {archive.Entries.Count} entries instead of the required {PackageLayout.AllFiles.Count}.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aggregateUncompressedBytes = 0L;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)
                || !entry.FullName.Equals(entry.Name, StringComparison.Ordinal)
                || !PackageLayout.AllFiles.Contains(entry.Name)
                || !names.Add(entry.Name))
            {
                throw new InvalidDataException($"Archive contains an unsafe, nested, duplicate, or unexpected entry: {entry.FullName}");
            }

            var output = Path.GetFullPath(Path.Combine(destination, entry.Name));
            var relative = Path.GetRelativePath(destination, output);
            if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                throw new InvalidDataException($"Archive entry escapes its extraction root: {entry.FullName}");
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            PackageArchivePolicy.CopyEntryTo(entry, file, ref aggregateUncompressedBytes);
            file.Flush(flushToDisk: true);
        }

        var missing = PackageLayout.AllFiles.Except(names, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"Archive is missing allowlisted files: {string.Join(", ", missing)}");
    }

    internal static IReadOnlyList<ManifestFile> ReadVerifiedArchiveEntries(string archivePath)
    {
        using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        PackageArchivePolicy.ValidateArchiveFile(archivePath);
        archiveStream.Position = 0;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
        if (archive.Entries.Count != PackageLayout.AllFiles.Count)
            throw new InvalidDataException("Package archive entry count differs from its exact allowlist.");

        var files = new List<ManifestFile>(archive.Entries.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aggregateUncompressedBytes = 0L;
        foreach (var entry in archive.Entries)
        {
            var canonicalName = PackageLayout.AllFiles.SingleOrDefault(
                name => name.Equals(entry.FullName, StringComparison.OrdinalIgnoreCase));
            if (canonicalName is null
                || !entry.FullName.Equals(entry.Name, StringComparison.Ordinal)
                || !entry.Name.Equals(canonicalName, StringComparison.Ordinal)
                || !names.Add(entry.Name))
            {
                throw new InvalidDataException(
                    $"Package archive contains an unsafe, duplicate, non-canonical, or unexpected entry: {entry.FullName}");
            }

            using var hashing = new HashingWriteStream();
            PackageArchivePolicy.CopyEntryTo(entry, hashing, ref aggregateUncompressedBytes);
            files.Add(new ManifestFile(entry.Name, hashing.BytesWritten, hashing.GetHash()));
        }

        if (!names.SetEquals(PackageLayout.AllFiles))
            throw new InvalidDataException("Package archive does not contain its exact allowlisted file set.");
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    internal static void AssertArchiveMatchesSnapshot(
        IReadOnlyList<PinnedFileDigest> stagingSnapshot,
        IReadOnlyList<ManifestFile> archiveFiles)
    {
        var archiveSnapshot = archiveFiles
            .Select(file => new PinnedFileDigest(file.Path, file.Bytes, file.Sha256))
            .ToArray();
        PinnedFileTree.AssertSameSnapshot(stagingSnapshot, archiveSnapshot, "package archive and staging");
    }

    private static void WriteManifest(
        string path,
        string archivePath,
        string archiveHash,
        SemanticVersion version,
        IReadOnlyList<ManifestFile> verifiedArchiveFiles)
    {
        var manifest = new PackageManifest(
            1,
            version.Original,
            PackageLayout.Configuration,
            PackageLayout.RuntimeIdentifier,
            DateTimeOffset.UtcNow,
            new ManifestFile(Path.GetFileName(archivePath)!, new FileInfo(archivePath).Length, archiveHash),
            verifiedArchiveFiles);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, manifest, ManifestJsonOptions);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    internal static void WriteManifestForSelfTest(
        string manifestPath,
        string archivePath,
        SemanticVersion version)
    {
        var files = ReadVerifiedArchiveEntries(archivePath);
        WriteManifest(
            manifestPath,
            archivePath,
            ComputeSha256(archivePath),
            version,
            files);
    }

    internal static void VerifyInstalledManifest(
        string archivePath,
        string manifestPath,
        SemanticVersion expectedVersion)
    {
        const long maximumManifestBytes = 1024 * 1024;
        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length <= 0 || manifestInfo.Length > maximumManifestBytes)
            throw new InvalidDataException("Package manifest size is outside its bounded policy.");
        using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archivePin = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestStream, ManifestJsonOptions)
                       ?? throw new InvalidDataException("Package manifest is empty.");
        if (manifest.SchemaVersion != 1
            || !string.Equals(manifest.Version, expectedVersion.Original, StringComparison.Ordinal)
            || !string.Equals(manifest.Configuration, PackageLayout.Configuration, StringComparison.Ordinal)
            || !string.Equals(manifest.RuntimeIdentifier, PackageLayout.RuntimeIdentifier, StringComparison.Ordinal)
            || manifest.CreatedUtc == default
            || manifest.CreatedUtc.Offset != TimeSpan.Zero
            || manifest.Archive is null
            || manifest.Files is null
            || manifest.Files.Any(file => file is null))
        {
            throw new InvalidDataException("Package manifest identity or schema does not match the package policy.");
        }

        var archiveInfo = new FileInfo(archivePath);
        var archiveHash = Convert.ToHexString(SHA256.HashData(archivePin)).ToLowerInvariant();
        if (!string.Equals(manifest.Archive.Path, Path.GetFileName(archivePath), StringComparison.Ordinal)
            || manifest.Archive.Bytes != archiveInfo.Length
            || !string.Equals(manifest.Archive.Sha256, archiveHash, StringComparison.Ordinal)
            || !IsCanonicalSha256(manifest.Archive.Sha256))
        {
            throw new InvalidDataException("Package manifest archive evidence does not match the installed archive.");
        }

        var archivedFiles = ReadVerifiedArchiveEntries(archivePath);
        AssertManifestFilesEqual(manifest.Files, archivedFiles);
    }

    private static void AssertManifestFilesEqual(
        IReadOnlyList<ManifestFile> manifestFiles,
        IReadOnlyList<ManifestFile> archiveFiles)
    {
        if (manifestFiles.Count != PackageLayout.AllFiles.Count
            || archiveFiles.Count != PackageLayout.AllFiles.Count)
        {
            throw new InvalidDataException("Package manifest file count differs from the exact package allowlist.");
        }
        var manifestByPath = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifestFiles)
        {
            if (file is null
                || string.IsNullOrEmpty(file.Path)
                || !manifestByPath.TryAdd(file.Path, file))
            {
                throw new InvalidDataException("Package manifest contains a null, empty, or duplicate file path.");
            }
        }
        var archiveByPath = archiveFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        if (manifestByPath.Count != manifestFiles.Count
            || !manifestByPath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(PackageLayout.AllFiles)
            || !manifestByPath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(archiveByPath.Keys))
        {
            throw new InvalidDataException("Package manifest contains duplicate, missing, or unexpected file paths.");
        }
        foreach (var pair in manifestByPath)
        {
            var canonicalName = PackageLayout.AllFiles.Single(
                name => name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            var expected = archiveByPath[pair.Key];
            var actual = pair.Value;
            if (!string.Equals(actual.Path, canonicalName, StringComparison.Ordinal)
                || actual.Bytes != expected.Bytes
                || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal)
                || !IsCanonicalSha256(actual.Sha256))
            {
                throw new InvalidDataException("Package manifest file evidence does not exactly match the installed archive.");
            }
        }
    }

    private static bool IsCanonicalSha256(string? value) =>
        value is not null
        && value.Length == SHA256.HashSizeInBytes * 2
        && value.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    internal static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string CreateDeterministicPathMapProperty(string workRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        var fullRoot = Path.GetFullPath(workRoot);
        if (!Path.IsPathFullyQualified(fullRoot))
            throw new InvalidDataException("The deterministic build root must be fully qualified.");
        if (fullRoot.IndexOfAny([',', ';', '=']) >= 0)
        {
            throw new InvalidDataException(
                "The deterministic build root contains a character that cannot be represented by the single PathMap policy.");
        }

        var physicalPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        return $"-p:PathMap={physicalPrefix}=/_/";
    }

    private static void WriteCleanupWarning(string subject, Exception failure)
    {
        try
        {
            Console.Error.WriteLine($"WARNING: {subject} cleanup failed ({failure.GetType().Name}).");
        }
        catch (Exception diagnosticFailure)
        {
            Debug.WriteLine($"Package cleanup warning sink unavailable ({diagnosticFailure.GetType().Name}).");
        }
    }

    private sealed record ReadyApplicationObservation(
        IntPtr MainWindowHandle,
        double FirstVisibleWindowMilliseconds,
        double FirstUsableMilliseconds,
        long WorkingSet64Bytes,
        long PrivateMemorySize64Bytes,
        int HandleCount);

    private sealed record DuplicateDialogObservation(
        WindowsUiProbe.DuplicateDialog Dialog,
        double ObservedMilliseconds);

    private sealed class PackageSmokeEvidence
    {
        public string SchemaVersion { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public bool SyntheticFixture { get; init; }
        public bool UserDataUsed { get; init; }
        public bool ExternalNetworkUsed { get; init; }
        public bool InteractiveDialogRequired { get; init; }
        public int BundleExtractEntryCountBeforeColdStart { get; init; }
        public int BundleExtractEntryCountAfterColdStart { get; init; }
        public bool WarmRestartReusedBundleExtractRoot { get; init; }
        public ApplicationRunEvidence ColdFirst { get; init; } = new();
        public DuplicateRunEvidence Duplicate { get; init; } = new();
        public ApplicationRunEvidence WarmRestart { get; init; } = new();
        public bool SamePhysicalDataRootReacquired { get; init; }
        public SmokeCleanupEvidence Cleanup { get; set; } = new();
    }

    private sealed class ApplicationRunEvidence
    {
        public string Role { get; init; } = string.Empty;
        public string CacheState { get; init; } = string.Empty;
        public int ProcessId { get; init; }
        public double FirstVisibleWindowMilliseconds { get; init; }
        public double FirstUsableMilliseconds { get; init; }
        public long WorkingSet64BytesAtFirstUsable { get; init; }
        public long PrivateMemorySize64BytesAtFirstUsable { get; init; }
        public int HandleCountAtFirstUsable { get; init; }
        public double CloseToExitMilliseconds { get; init; }
        public int ExitCode { get; init; }
        public bool AcknowledgementVerified { get; init; }
        public bool MainWindowResponsive { get; init; }
        public int DescendantProcessCountWhileRunning { get; init; }
        public JobAccountingEvidence LiveJobAccounting { get; init; } = new();
        public JobAccountingEvidence CompletedJobAccounting { get; init; } = new();
    }

    private sealed class DuplicateRunEvidence
    {
        public int ProcessId { get; init; }
        public double DialogObservedMilliseconds { get; init; }
        public bool TitleMatched { get; init; }
        public bool BodyMatched { get; init; }
        public int ConfirmationButtonControlId { get; init; }
        public int ReportedDefaultControlId { get; init; }
        public bool ConfirmationButtonVisibleAndEnabled { get; init; }
        public bool DefaultButtonIsConfirmation { get; init; }
        public bool ConfirmationButtonClickedByPidScopedMessage { get; init; }
        public int ExitCode { get; init; }
        public bool HolderResponsiveAfterDuplicate { get; init; }
        public JobAccountingEvidence LiveJobAccounting { get; init; } = new();
        public JobAccountingEvidence CompletedJobAccounting { get; init; } = new();
    }

    private sealed class JobAccountingEvidence
    {
        public uint TotalProcesses { get; init; }
        public uint ActiveProcesses { get; init; }
        public uint TotalTerminatedProcesses { get; init; }
    }

    private sealed class SmokeCleanupEvidence
    {
        public bool AllProcessesExited { get; init; }
        public bool TempRootRemoved { get; init; }
    }

    internal sealed record ManifestFile(string Path, long Bytes, string Sha256);
    private sealed record PackageManifest(
        int SchemaVersion,
        string Version,
        string Configuration,
        string RuntimeIdentifier,
        DateTimeOffset CreatedUtc,
        ManifestFile Archive,
        IReadOnlyList<ManifestFile> Files);

    private sealed class HashingWriteStream : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool finalized;

        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !finalized;
        public override long Length => BytesWritten;
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public string GetHash()
        {
            if (finalized) throw new InvalidOperationException("The archive-entry hash was already finalized.");
            finalized = true;
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (finalized) throw new InvalidOperationException("The archive-entry hash is finalized.");
            hash.AppendData(buffer);
            BytesWritten = checked(BytesWritten + buffer.Length);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) hash.Dispose();
            base.Dispose(disposing);
        }
    }
    private sealed record VersionSnapshot(SemanticVersion Version, byte[] Bytes)
    {
        public static VersionSnapshot Capture(string path)
        {
            const int maximumBytes = 1024;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > maximumBytes)
                throw new InvalidDataException($"VERSION has an invalid size: {info.Length} bytes.");
            var bytes = File.ReadAllBytes(path);
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes)
                .Trim();
            return new VersionSnapshot(SemanticVersion.Parse(text), bytes);
        }
    }

}

internal sealed class SmokeWorkspace : IDisposable
{
    private const string Prefix = "RimWorldAiTranslator-package-smoke-";
    private const int MaximumRootLength = 128;
    private bool disposed;

    private SmokeWorkspace(string root, string tempRoot)
    {
        Root = root;
        TempRoot = tempRoot;
    }

    public string Root { get; }
    private string TempRoot { get; }

    public static SmokeWorkspace Create()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        AssertTemporaryRoot(temp);
        var root = Path.GetFullPath(Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N")));
        var relative = Path.GetRelativePath(temp, root);
        if (Path.IsPathRooted(relative)
            || relative.Contains(Path.DirectorySeparatorChar)
            || relative.Contains(Path.AltDirectorySeparatorChar)
            || !relative.StartsWith(Prefix, StringComparison.Ordinal)
            || relative.Length != Prefix.Length + 32
            || !Guid.TryParseExact(relative[Prefix.Length..], "N", out _))
        {
            throw new InvalidOperationException($"Refusing an unverified smoke root: {root}");
        }
        if (root.Length > MaximumRootLength)
            throw new PathTooLongException("The package-smoke temporary root is too long for legacy Windows child tools.");
        if (File.Exists(root) || Directory.Exists(root))
            throw new IOException($"The package-smoke workspace already exists: {root}");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidOperationException("The new package-smoke workspace is redirected or not empty.");
        }
        return new SmokeWorkspace(root, temp);
    }

    public string CreateDirectory(string relativePath)
    {
        var full = RequireChild(relativePath);
        if (File.Exists(full) || Directory.Exists(full))
            throw new IOException($"Smoke directory already exists: {full}");
        Directory.CreateDirectory(full);
        AssertNoReparseTree();
        return full;
    }

    public string RequireChild(string relativePath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Path.IsPathFullyQualified(relativePath))
            throw new InvalidOperationException($"Smoke child path must be relative: {relativePath}");
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        var relative = Path.GetRelativePath(Root, full);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Smoke child path escaped its run root: {full}");
        }
        return full;
    }

    public void AssertNoReparseTree()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Directory.Exists(Root) || File.Exists(Root))
            throw new DirectoryNotFoundException("The package-smoke workspace root is missing or is not a directory.");
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Smoke root became a reparse point: {Root}");
        var pending = new Stack<string>();
        pending.Push(Root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var child in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Smoke workspace contains a reparse point: {child}");
                if (Directory.Exists(child)) pending.Push(child);
            }
        }
    }

    public void AssertRegularFileNoReparse(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Root, full);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Smoke acknowledgement escaped its owned root.");
        }

        var cursor = Root;
        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, part);
            if (!File.Exists(cursor) && !Directory.Exists(cursor))
                throw new FileNotFoundException("Smoke acknowledgement path disappeared.", cursor);
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Smoke acknowledgement path contains a reparse point.");
        }
        if (!File.Exists(full) || Directory.Exists(full))
            throw new InvalidDataException("Smoke acknowledgement is not a regular file.");
    }

    public void Dispose()
    {
        if (disposed) return;
        AssertTemporaryRoot(TempRoot);
        var relative = Path.GetRelativePath(TempRoot, Root);
        if (Path.IsPathRooted(relative)
            || relative.Contains(Path.DirectorySeparatorChar)
            || relative.Contains(Path.AltDirectorySeparatorChar)
            || !relative.StartsWith(Prefix, StringComparison.Ordinal)
            || relative.Length != Prefix.Length + 32
            || !Guid.TryParseExact(relative[Prefix.Length..], "N", out _))
        {
            throw new InvalidOperationException($"Refusing to delete an unverified smoke root: {Root}");
        }
        if (File.Exists(Root))
            throw new InvalidOperationException("The package-smoke workspace root became a file and was preserved.");
        if (!Directory.Exists(Root))
        {
            disposed = true;
            return;
        }
        AssertNoReparseTree();
        Directory.Delete(Root, recursive: true);
        if (File.Exists(Root) || Directory.Exists(Root))
            throw new IOException("The package-smoke workspace remained after deletion.");
        disposed = true;
    }

    private static void AssertTemporaryRoot(string tempRoot)
    {
        if (!Directory.Exists(tempRoot)
            || File.Exists(tempRoot)
            || (File.GetAttributes(tempRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"The package-smoke temporary root is missing or redirected: {tempRoot}");
        }
    }
}
