using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimWorldAiTranslator.Tooling;

internal static class SelfTest
{
    public static int Run(RepositoryLayout repository)
    {
        repository.AssertNoReparseComponents(repository.Root);
        var tests = new (string Name, Action Body)[]
        {
            ("PackageLayout.ExactAllowlist", PackageLayoutExactAllowlist),
            ("IconAsset.ExactGeneratedBytes", () => IconAssetExactGeneratedBytes(repository)),
            ("Archive.EocdPolicy", ArchiveEocdPolicy),
            ("Archive.ContentAndManifestEvidence", ArchiveContentAndManifestEvidence),
            ("ZeroAudit.BypassResistance", ZeroAuditBypassResistance),
            ("Environment.FailClosed", EnvironmentFailClosed),
            ("Environment.OwnedPackageProcessWorkspace", OwnedPackageProcessWorkspace),
            ("Build.DeterministicPathMap", DeterministicPathMap),
            ("Inputs.PinnedTrees", InputsPinnedTrees),
            ("NuGetConfig.ExactOfflineShape", NuGetConfigExactOfflineShape),
            ("RuntimeFeed.CacheAndArchivePolicy", RuntimeFeedCacheAndArchivePolicy),
            ("RuntimeFeed.PinnedSdkPolicy", RuntimeFeedPinnedSdkPolicy),
            ("RestoreAssets.RuntimeDownloadDependencies", RestoreAssetsRuntimeDownloadDependencies),
            ("BuildArtifacts.ManagedEntryPoints", BuildArtifactsManagedEntryPoints),
            ("OutputTransaction.FailureRecovery", OutputTransactionFailureRecovery),
            ("Smoke.StrictAcknowledgement", SmokeStrictAcknowledgement),
            ("ProcessTree.PidReuseIdentity", ProcessTreePidReuseIdentity),
            ("Job.ActiveProcessLimit", JobActiveProcessLimit)
        };

        var passed = 0;
        foreach (var test in tests)
        {
            test.Body();
            passed++;
            Console.WriteLine($"PASS {test.Name}");
        }
        Console.WriteLine($"Tooling self-test passed: {passed}/{tests.Length}.");
        return 0;
    }

    private static void IconAssetExactGeneratedBytes(RepositoryLayout repository)
    {
        var svgPath = repository.RequireFile(IconAssetGenerator.SvgRelativePath);
        var icoPath = repository.RequireFile(IconAssetGenerator.IcoRelativePath);
        var svg = File.ReadAllText(svgPath, new UTF8Encoding(false, true));
        var expected = IconAssetGenerator.CreateIcoForTesting(svg);
        var actual = File.ReadAllBytes(icoPath);
        Assert(actual.AsSpan().SequenceEqual(expected),
            "The committed application ICO differs from the deterministic SVG output.");

        var frames = IconAssetGenerator.InspectIcoForTesting(actual);
        Assert(frames.Select(frame => frame.Size).SequenceEqual(IconAssetGenerator.RequiredSizesForTesting),
            "The committed application ICO frame sizes differ from the required set.");
        Assert(frames.All(frame => frame.BitsPerPixel == 32),
            "The committed application ICO must use 32-bit frames only.");
    }

    private static void PackageLayoutExactAllowlist()
    {
        string[] expectedRuntimeFiles =
        [
            PackageLayout.ApplicationFileName,
            "rimworld-def-field-rules.txt"
        ];
        string[] expectedDocumentationFiles =
        [
            "PACKAGE_README.txt",
            "RELEASE_NOTES.md",
            "sample-glossary.txt",
            "VERSION",
            "LICENSE",
            "SECURITY.md",
            "PRIVACY.md",
            "THIRD_PARTY_NOTICES.md",
            "DOTNET_RUNTIME_LICENSE.txt",
            "DOTNET_RUNTIME_THIRD_PARTY_NOTICES.txt",
            "DOTNET_WINDOWSDESKTOP_LICENSE.txt",
            "DOTNET_ASPNETCORE_THIRD_PARTY_NOTICES.txt"
        ];
        var expectedAllFiles = expectedRuntimeFiles
            .Concat(expectedDocumentationFiles)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert(PackageLayout.RuntimeFiles.SetEquals(expectedRuntimeFiles),
            "Package runtime files differ from the exact public-RC allowlist.");
        Assert(PackageLayout.DocumentationFiles.SetEquals(expectedDocumentationFiles),
            "Package documentation files differ from the exact public-RC allowlist.");
        Assert(PackageLayout.AllFiles.SetEquals(expectedAllFiles),
            "Combined package files differ from the exact public-RC allowlist.");
        Assert(!PackageLayout.AllFiles.Contains("glossary.generated.ko.json"),
            "The rights-blocked generated glossary remained in the distribution allowlist.");
    }

    private static void OwnedPackageProcessWorkspace()
    {
        string root;
        var workspace = PackageProcessWorkspace.Create();
        try
        {
            root = workspace.Root;
            var temporaryRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            var relative = Path.GetRelativePath(temporaryRoot, root);
            Assert(!Path.IsPathRooted(relative)
                   && !relative.Contains(Path.DirectorySeparatorChar)
                   && !relative.Contains(Path.AltDirectorySeparatorChar)
                   && relative.StartsWith(PackageProcessWorkspace.DirectoryPrefix, StringComparison.Ordinal)
                   && relative.Length == PackageProcessWorkspace.DirectoryPrefix.Length + 32
                   && root.Length <= PackageProcessWorkspace.MaximumRootLength,
                "The package-process workspace was not a verified direct child of the temporary root.");

            var temp = workspace.CreateDirectory("t");
            var profile = workspace.CreateDirectory("h");
            var nested = workspace.CreateDirectoryPath("h", "AppData", "Roaming");
            File.WriteAllText(Path.Combine(nested, "synthetic.txt"), "synthetic", new UTF8Encoding(false));
            Assert(Directory.Exists(temp) && Directory.Exists(profile),
                "The package-process workspace did not create its isolated children.");
            ExpectThrows<IOException>(() => workspace.CreateDirectory("t"));
            ExpectThrows<InvalidOperationException>(() => workspace.CreateDirectory(".."));
            ExpectThrows<InvalidOperationException>(() => workspace.CreateDirectory(Path.Combine("a", "b")));
            ExpectThrows<InvalidOperationException>(() => workspace.CreateDirectory(root));
        }
        finally
        {
            workspace.Dispose();
        }

        Assert(!Directory.Exists(root),
            "The package-process workspace was not removed after disposal.");
        ExpectThrows<ObjectDisposedException>(() => workspace.CreateDirectory("x"));
        Assert(!Directory.Exists(root),
            "A disposed package-process workspace was recreated.");

        var tampered = PackageProcessWorkspace.Create();
        var tamperedRoot = tampered.Root;
        Directory.Delete(tamperedRoot);
        File.WriteAllText(tamperedRoot, "synthetic replacement", new UTF8Encoding(false));
        try
        {
            ExpectThrows<InvalidOperationException>(() => tampered.Dispose());
            Assert(File.Exists(tamperedRoot),
                "Package-process cleanup deleted a replacement file at its former root.");
        }
        finally
        {
            if (File.Exists(tamperedRoot)) File.Delete(tamperedRoot);
        }
        Assert(!File.Exists(tamperedRoot) && !Directory.Exists(tamperedRoot),
            "The replacement-file safety self-test left its exact temporary root behind.");
    }

    private static void DeterministicPathMap()
    {
        var root = Path.Combine(Path.GetTempPath(), "rwat deterministic build root");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
                       + Path.DirectorySeparatorChar;
        var property = PackageCommand.CreateDeterministicPathMapProperty(root);
        Assert(string.Equals(property, $"-p:PathMap={fullRoot}=/_/", StringComparison.Ordinal),
            "The package PathMap did not map the complete run-owned work root to its stable virtual root.");

        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.CreateDeterministicPathMapProperty(root + ",ambiguous"));
        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.CreateDeterministicPathMapProperty(root + ";ambiguous"));
        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.CreateDeterministicPathMapProperty(root + "=ambiguous"));
    }

    private static void ArchiveEocdPolicy()
    {
        using var workspace = TestWorkspace.Create();
        var valid = workspace.Child("valid.zip");
        CreateArchive(valid, PackageLayout.AllFiles);
        PackageArchivePolicy.ValidateArchiveFile(valid);

        var wrongCount = workspace.Child("wrong-count.zip");
        CreateArchive(wrongCount, PackageLayout.AllFiles.Take(PackageLayout.AllFiles.Count - 1));
        ExpectThrows<InvalidDataException>(() => PackageArchivePolicy.ValidateArchiveFile(wrongCount));

        var zip64Sentinel = workspace.Child("zip64-sentinel.zip");
        File.Copy(valid, zip64Sentinel);
        PatchEndOfCentralDirectory(zip64Sentinel, (bytes, offset) =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8), ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 10), ushort.MaxValue);
        });
        ExpectThrows<InvalidDataException>(() => PackageArchivePolicy.ValidateArchiveFile(zip64Sentinel));

        var oversizedCentralDirectory = workspace.Child("oversized-central.zip");
        File.Copy(valid, oversizedCentralDirectory);
        PatchEndOfCentralDirectory(
            oversizedCentralDirectory,
            (bytes, offset) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 12), 64 * 1024 + 1));
        ExpectThrows<InvalidDataException>(() => PackageArchivePolicy.ValidateArchiveFile(oversizedCentralDirectory));
    }

    private static void ArchiveContentAndManifestEvidence()
    {
        using var workspace = TestWorkspace.Create();
        var staging = workspace.Child("staging");
        Directory.CreateDirectory(staging);
        var index = 0;
        foreach (var name in PackageLayout.AllFiles.OrderBy(value => value, StringComparer.Ordinal))
            File.WriteAllText(Path.Combine(staging, name), $"synthetic-{index++}-{name}", new UTF8Encoding(false));

        using var stagingPins = PinnedFileTree.CaptureExact(staging, "synthetic package staging");
        var valid = workspace.Child("valid-content.zip");
        CreateArchive(valid, PackageLayout.AllFiles);
        var evidence = PackageCommand.ReadVerifiedArchiveEntries(valid);
        PackageCommand.AssertArchiveMatchesSnapshot(stagingPins.Snapshot, evidence);

        var mismatched = workspace.Child("mismatched-content.zip");
        CreateArchive(mismatched, PackageLayout.AllFiles, "changed");
        var mismatchedEvidence = PackageCommand.ReadVerifiedArchiveEntries(mismatched);
        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.AssertArchiveMatchesSnapshot(stagingPins.Snapshot, mismatchedEvidence));

        var manifest = workspace.Child("valid.manifest.json");
        var version = SemanticVersion.Parse("1.2.3-rc.1");
        PackageCommand.WriteManifestForSelfTest(manifest, valid, version);
        PackageCommand.VerifyInstalledManifest(valid, manifest, version);
        var archiveHash = PackageCommand.ComputeSha256(valid);
        var text = File.ReadAllText(manifest, Encoding.UTF8);
        File.WriteAllText(
            manifest,
            text.Replace(archiveHash, new string('0', 64), StringComparison.Ordinal),
            new UTF8Encoding(false));
        ExpectThrows<InvalidDataException>(() => PackageCommand.VerifyInstalledManifest(valid, manifest, version));
    }

    private static void ZeroAuditBypassResistance()
    {
        using var workspace = TestWorkspace.Create();
        var repository = workspace.CreateRepository();
        ZeroAudit.VerifySourceTree(repository);

        var excluded = workspace.Child("obj");
        Directory.CreateDirectory(excluded);
        var forbiddenExtension = ".c" + "md";
        var hiddenScript = Path.Combine(excluded, "hidden" + forbiddenExtension);
        File.WriteAllText(hiddenScript, "exit /b 0", Encoding.UTF8);
        ExpectAuditFailure(() => ZeroAudit.VerifySourceTree(repository));
        File.Delete(hiddenScript);

        var hiddenSource = Path.Combine(excluded, "hidden.cs");
        var forbiddenHost = "p" + "wsh";
        File.WriteAllText(hiddenSource, $"class Hidden {{ const string Host = \"{forbiddenHost}\"; }}", Encoding.UTF8);
        ExpectAuditFailure(() => ZeroAudit.VerifySourceTree(repository));
        File.Delete(hiddenSource);

        var sidecar = workspace.Child("sample.csproj.user");
        var commandHost = "c" + "md.exe";
        File.WriteAllText(sidecar, $"<Project><Target><Exec Command=\"{commandHost} /c exit\" /></Target></Project>", Encoding.UTF8);
        ExpectAuditFailure(() => ZeroAudit.VerifySourceTree(repository));
        File.Delete(sidecar);

        var activeArchive = workspace.Child("active.zip");
        using (var file = new FileStream(activeArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("hidden." + "bat");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("exit /b 0");
        }
        ExpectAuditFailure(() => ZeroAudit.VerifySourceTree(repository));
        File.Delete(activeArchive);

        var privateReviews = workspace.Child("reviews");
        Directory.CreateDirectory(privateReviews);
        File.WriteAllText(
            Path.Combine(privateReviews, "private.cs"),
            $"class PrivateData {{ const string Host = \"{forbiddenHost}\"; }}",
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(privateReviews, "private" + forbiddenExtension), "exit /b 0", Encoding.UTF8);
        ZeroAudit.VerifySourceTree(repository);
    }

    private static void EnvironmentFailClosed()
    {
        var inherited = new Dictionary<string, string?>
        {
            ["SystemRoot"] = "Z:\\untrusted-windows",
            ["windir"] = "Z:\\untrusted-windows",
            ["SystemDrive"] = "Z:",
            ["ProgramFiles"] = "Z:\\untrusted-program-files",
            ["PATH"] = "untrusted-path",
            ["DOTNET_ROOT"] = "untrusted-root",
            ["MSBuildSDKsPath"] = "untrusted-sdks",
            ["DirectoryBuildPropsPath"] = "untrusted-props",
            ["NUGET_PACKAGES"] = "untrusted-packages",
            ["SERVICE_TOKEN"] = "secret",
            ["USERPROFILE"] = "real-user-profile",
            ["APPDATA"] = "real-user-appdata",
            ["LOCALAPPDATA"] = "real-user-localappdata",
            ["TEMP"] = "real-user-temp",
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["CORECLR_PROFILER_PATH"] = "profiler.dll",
            ["COR_ENABLE_PROFILING"] = "1",
            ["COMPlus_ReadyToRun"] = "0",
            ["VSTEST_HOST_DEBUG"] = "1",
            ["CscToolPath"] = "compiler-override",
            ["CscToolExe"] = "evil.exe",
            ["VbcToolPath"] = "compiler-override",
            ["FscToolPath"] = "compiler-override",
            ["RoslynTargetsPath"] = "compiler-override",
            ["ArbitraryMsBuildProperty"] = "injected"
        };
        var requested = new Dictionary<string, string?>
        {
            ["DOTNET_CLI_HOME"] = "isolated-home",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
            ["USERPROFILE"] = "isolated-profile",
            ["APPDATA"] = "isolated-appdata",
            ["LOCALAPPDATA"] = "isolated-localappdata",
            ["TEMP"] = "isolated-temp",
            ["NUGET_CERT_REVOCATION_MODE"] = "offline"
        };
        var sanitized = ExternalProcessPolicy.BuildChildEnvironment(inherited, requested);
        var trustedWindows = ExternalProcessPolicy.GetTrustedWindowsDirectoryForSelfTest();
        var trustedProgramFiles = ExternalProcessPolicy.GetTrustedProgramFilesDirectoryForSelfTest();
        Assert(sanitized.TryGetValue("SystemRoot", out var systemRoot) && systemRoot == trustedWindows
               && sanitized.TryGetValue("windir", out var windir) && windir == trustedWindows
               && sanitized.TryGetValue("SystemDrive", out var systemDrive)
               && systemDrive == Path.GetPathRoot(trustedWindows)!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               && sanitized.TryGetValue("ProgramFiles", out var programFiles) && programFiles == trustedProgramFiles,
            "Trusted OS environment was not fixed to the actual Windows installation.");
        Assert(!sanitized.ContainsKey("PATH"), "Inherited executable search path was retained.");
        Assert(!sanitized.ContainsKey("DOTNET_ROOT"), "Inherited runtime override was retained.");
        Assert(!sanitized.ContainsKey("MSBuildSDKsPath"), "Inherited build SDK override was retained.");
        Assert(!sanitized.ContainsKey("DirectoryBuildPropsPath"), "Inherited build import override was retained.");
        Assert(!sanitized.ContainsKey("NUGET_PACKAGES"), "Inherited NuGet package-cache override was retained.");
        Assert(!sanitized.ContainsKey("SERVICE_TOKEN"), "Inherited sensitive value was retained.");
        Assert(!sanitized.Keys.Any(name => name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                                           || name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
                                           || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
                                           || name.StartsWith("VSTEST_", StringComparison.OrdinalIgnoreCase)),
            "Inherited runtime profiler or test-host injection was retained.");
        Assert(!sanitized.ContainsKey("CscToolPath")
               && !sanitized.ContainsKey("CscToolExe")
               && !sanitized.ContainsKey("VbcToolPath")
               && !sanitized.ContainsKey("FscToolPath")
               && !sanitized.ContainsKey("RoslynTargetsPath")
               && !sanitized.ContainsKey("ArbitraryMsBuildProperty"),
            "Inherited compiler or arbitrary MSBuild override was retained.");
        Assert(sanitized["DOTNET_CLI_HOME"] == "isolated-home", "Explicit isolated CLI home was not retained.");
        Assert(sanitized["USERPROFILE"] == "isolated-profile"
               && sanitized["APPDATA"] == "isolated-appdata"
               && sanitized["LOCALAPPDATA"] == "isolated-localappdata"
               && sanitized["TEMP"] == "isolated-temp",
            "Explicit run-owned profile or temporary environment was not retained.");
        Assert(sanitized["NUGET_CERT_REVOCATION_MODE"] == "offline", "Explicit offline revocation mode was not retained.");
        ExpectThrows<InvalidOperationException>(() => ExternalProcessPolicy.BuildChildEnvironment(
            inherited,
            new Dictionary<string, string?> { ["MSBuildSDKsPath"] = "override" }));
        ExpectThrows<InvalidOperationException>(() => ExternalProcessPolicy.BuildChildEnvironment(
            inherited,
            new Dictionary<string, string?> { ["NUGET_CERT_REVOCATION_MODE"] = "online" }));
        ExpectThrows<InvalidOperationException>(() => ExternalProcessPolicy.BuildChildEnvironment(
            inherited,
            new Dictionary<string, string?> { ["ProgramFiles"] = "Z:\\requested-program-files" }));
        foreach (var malicious in new[]
                 {
                     "CORECLR_ENABLE_PROFILING", "COR_PROFILER", "COMPlus_ReadyToRun", "VSTEST_HOST_DEBUG",
                     "CscToolPath", "CscToolExe", "VbcToolPath", "FscToolPath", "RoslynTargetsPath",
                     "ArbitraryMsBuildProperty"
                 })
        {
            ExpectThrows<InvalidOperationException>(() => ExternalProcessPolicy.BuildChildEnvironment(
                inherited,
                new Dictionary<string, string?> { [malicious] = "injected" }));
        }

        var block = WindowsSuspendedProcessLauncher.BuildEnvironmentBlockForSelfTest(
            new Dictionary<string, string?> { ["Z_VAR"] = "2", ["A_VAR"] = "1" });
        Assert(block == "A_VAR=1\0Z_VAR=2\0\0", "Native environment block was not sorted and double-NUL terminated.");
        ExpectThrows<InvalidDataException>(() => WindowsSuspendedProcessLauncher.BuildEnvironmentBlockForSelfTest(
            new[]
            {
                new KeyValuePair<string, string?>("DUP", "1"),
                new KeyValuePair<string, string?>("dup", "2")
            }));
    }

    private static void InputsPinnedTrees()
    {
        using var workspace = TestWorkspace.Create();
        var root = workspace.Child("tree");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "input.txt");
        File.WriteAllText(file, "original", new UTF8Encoding(false));

        IReadOnlyList<PinnedFileDigest> original;
        using (var pinned = PinnedFileTree.CaptureExact(root, "synthetic input tree"))
        {
            original = pinned.Snapshot.ToArray();
            ExpectThrows<IOException>(() => File.WriteAllText(file, "changed", new UTF8Encoding(false)));
            ExpectThrows<IOException>(() => File.Delete(file));
            var addedFile = Path.Combine(root, "added.txt");
            File.WriteAllText(addedFile, "added", new UTF8Encoding(false));
            ExpectThrows<InvalidDataException>(pinned.Verify);
            File.Delete(addedFile);
            pinned.Verify();
            var addedDirectory = Path.Combine(root, "added-directory");
            Directory.CreateDirectory(addedDirectory);
            ExpectThrows<InvalidDataException>(pinned.Verify);
            Directory.Delete(addedDirectory);
            pinned.Verify();
            ExpectThrows<IOException>(() => Directory.Move(root, root + "-renamed"));
            pinned.Verify();
        }

        File.WriteAllText(file, "changed", new UTF8Encoding(false));
        using var changed = PinnedFileTree.CaptureExact(root, "changed synthetic input tree");
        ExpectThrows<InvalidDataException>(() =>
            PinnedFileTree.AssertSameSnapshot(original, changed.Snapshot, "synthetic input tree"));

        var repository = workspace.CreateRepository();
        repository.EnsureDistDirectory();
        var distOutput = Path.Combine(repository.Dist, "generated.bin");
        File.WriteAllText(distOutput, "first", new UTF8Encoding(false));
        var obj = workspace.Child(Path.Combine("src", "RimWorldAiTranslator.App", "obj"));
        Directory.CreateDirectory(obj);
        var objOutput = Path.Combine(obj, "generated.bin");
        File.WriteAllText(objOutput, "first", new UTF8Encoding(false));
        var reviews = workspace.Child("reviews");
        Directory.CreateDirectory(reviews);
        var privateReviewOutput = Path.Combine(reviews, "private-review.json");
        File.WriteAllText(privateReviewOutput, "first", new UTF8Encoding(false));
        using var repositoryPins = PinnedFileTree.CaptureRepositoryInputs(repository);
        ExpectThrows<IOException>(() =>
            File.WriteAllText(repository.VersionFile, "9.9.9-rc.9\n", new UTF8Encoding(false)));
        File.WriteAllText(distOutput, "second", new UTF8Encoding(false));
        File.WriteAllText(objOutput, "second", new UTF8Encoding(false));
        File.WriteAllText(privateReviewOutput, "second", new UTF8Encoding(false));
        repositoryPins.Verify();

        var mutableRoot = workspace.Child("mutable-tree");
        Directory.CreateDirectory(mutableRoot);
        var immutableRestoreInput = Path.Combine(mutableRoot, "project.assets.json");
        File.WriteAllText(immutableRestoreInput, "restore", new UTF8Encoding(false));
        using var mutablePins = PinnedFileTree.CaptureExistingFilesAllowAdditions(
            mutableRoot,
            "synthetic mutable output namespace");
        ExpectThrows<IOException>(() =>
            File.WriteAllText(immutableRestoreInput, "changed", new UTF8Encoding(false)));
        File.WriteAllText(Path.Combine(mutableRoot, "build-output.dll"), "build", new UTF8Encoding(false));
        ExpectThrows<IOException>(() => Directory.Move(mutableRoot, mutableRoot + "-renamed"));
        mutablePins.Verify();
    }

    private static void NuGetConfigExactOfflineShape()
    {
        using var workspace = TestWorkspace.Create();
        var valid = workspace.Child("valid.config");
        File.WriteAllText(
            valid,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><clear /></packageSources></configuration>\n",
            new UTF8Encoding(false));
        PackageCommand.VerifyOfflineNuGetConfig(valid);

        var namespaced = workspace.Child("namespaced.config");
        File.WriteAllText(
            namespaced,
            "<configuration xmlns=\"urn:not-allowed\"><packageSources><clear /></packageSources></configuration>",
            new UTF8Encoding(false));
        ExpectThrows<InvalidDataException>(() => PackageCommand.VerifyOfflineNuGetConfig(namespaced));

        var extra = workspace.Child("extra.config");
        File.WriteAllText(
            extra,
            "<configuration><packageSources><clear /></packageSources><config /></configuration>",
            new UTF8Encoding(false));
        ExpectThrows<InvalidDataException>(() => PackageCommand.VerifyOfflineNuGetConfig(extra));
    }

    private static void RuntimeFeedCacheAndArchivePolicy()
    {
        using var workspace = TestWorkspace.Create();
        const string id = "Microsoft.NETCore.App.Runtime.win-x64";
        const string version = RuntimePackFeed.RuntimePackVersion;
        var valid = workspace.Child("valid.nupkg");
        CreateRuntimePackage(valid, id, version, "runtimes/win-x64/native/example.dll");
        var sidecar = valid + ".sha512";
        var metadata = workspace.Child(".nupkg.metadata");
        var contentHash = Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, SHA512.HashSizeInBytes).ToArray());
        WriteRuntimeCacheEvidence(valid, sidecar, metadata, contentHash);
        var rawPackageHash = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(valid)));
        Assert(
            RuntimePackFeed.VerifyCacheCandidateForSelfTest(
                valid, sidecar, metadata, id, version, rawPackageHash, contentHash) == contentHash,
            "Valid synthetic runtime package cache evidence was rejected.");

        File.WriteAllText(sidecar, new string('A', 88), Encoding.ASCII);
        ExpectThrows<InvalidDataException>(() =>
            RuntimePackFeed.VerifyCacheCandidateForSelfTest(
                valid, sidecar, metadata, id, version, rawPackageHash, contentHash));
        WriteRuntimeCacheEvidence(valid, sidecar, metadata, contentHash);
        ExpectThrows<InvalidDataException>(() =>
            RuntimePackFeed.VerifyCacheCandidateForSelfTest(
                valid,
                sidecar,
                metadata,
                id,
                version,
                Convert.ToBase64String(Enumerable.Repeat((byte)0x33, SHA512.HashSizeInBytes).ToArray()),
                contentHash));
        ExpectThrows<InvalidDataException>(() =>
            RuntimePackFeed.VerifyCacheCandidateForSelfTest(
                valid,
                sidecar,
                metadata,
                id,
                version,
                rawPackageHash,
                Convert.ToBase64String(Enumerable.Repeat((byte)0x44, SHA512.HashSizeInBytes).ToArray())));

        var unsafeArchive = workspace.Child("unsafe.nupkg");
        CreateRuntimePackage(unsafeArchive, id, version, "../escape.dll");
        var unsafeSidecar = unsafeArchive + ".sha512";
        WriteRuntimeCacheEvidence(unsafeArchive, unsafeSidecar, metadata, contentHash);
        var unsafeRawHash = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(unsafeArchive)));
        ExpectThrows<InvalidDataException>(() =>
            RuntimePackFeed.VerifyCacheCandidateForSelfTest(
                unsafeArchive, unsafeSidecar, metadata, id, version, unsafeRawHash, contentHash));
    }

    private static void RuntimeFeedPinnedSdkPolicy()
    {
        var expectedIdentities = new[]
        {
            (
                "Microsoft.NETCore.App.Runtime.win-x64",
                "SRdQnoumUQkUjrO/IKeC9joscXGWFtxOxhImwiHfBCaRyiQCZSOX7G+NvrpY1HKaeDEdk3plYaXAM2U2Sa59zQ==",
                "G2SWebJKnBkixQcJlVkCV0EbqdoAhqAf6evVKDcY6CmFjPOFeC+gZeO6/dlm4v1fbKERjpjJzMg0mnKA1il1Zg=="),
            (
                "Microsoft.WindowsDesktop.App.Runtime.win-x64",
                "ooWLItKGmK5MGOfwWtrW69HFzra1rZndhoAVzN+Ir3H9AYYQWiCONs86GmJ02YjqwB+LLUfUrSZypgbzujeNqg==",
                "sKbAXRze+wBxtj2zexZfpR4vGe2yKZqRn79wFpwa6Ev7be81noXIewGpWvoy7s41y1bTPFzt2YDl3pGFFsXYmQ=="),
            (
                "Microsoft.AspNetCore.App.Runtime.win-x64",
                "Uex6/8s52jeHAgeT01k6VKCAmf8YqrUb4D103LIZgyI9T4cZKyPj8ymWOkBt7bQP1EDwp1ctxJckvzaubjJvXA==",
                "a97O/kRRTEX+VUL66tyQ2Lgl+As+AEw08f9Qwu8+p7k909Zk+L1dyqZECkF5vqxiEpc9sOtK2+NrS946/uuITw==")
        };
        foreach (var expected in expectedIdentities)
        {
            var actual = RuntimePackFeed.GetPinnedIdentityForSelfTest(expected.Item1);
            Assert(actual.RawPackageHash == expected.Item2
                   && actual.MetadataContentHash == expected.Item3,
                "A production runtime-package identity constant is missing or wired to the wrong package.");
        }
        ExpectThrows<InvalidDataException>(() =>
            RuntimePackFeed.GetPinnedIdentityForSelfTest("Contoso.Unpinned.Runtime.win-x64"));

        using var workspace = TestWorkspace.Create();
        var valid = workspace.Child("global.json");
        File.WriteAllText(
            valid,
            "{\"sdk\":{\"version\":\"8.0.422\",\"rollForward\":\"latestPatch\",\"allowPrerelease\":false}}",
            new UTF8Encoding(false));
        RuntimePackFeed.VerifyPinnedSdkConfig(valid);

        var changed = workspace.Child("changed.json");
        File.WriteAllText(
            changed,
            "{\"sdk\":{\"version\":\"8.0.423\",\"rollForward\":\"latestPatch\",\"allowPrerelease\":false}}",
            new UTF8Encoding(false));
        ExpectThrows<InvalidDataException>(() => RuntimePackFeed.VerifyPinnedSdkConfig(changed));
    }

    private static void RestoreAssetsRuntimeDownloadDependencies()
    {
        const string version = RuntimePackFeed.RuntimePackVersion;
        var exactRange = $"[{version}, {version}]";
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.NETCore.App.Runtime.win-x64"] = version,
            ["Microsoft.WindowsDesktop.App.Runtime.win-x64"] = version,
            ["Microsoft.AspNetCore.App.Runtime.win-x64"] = version
        };

        var valid = CreateRuntimeDownloadAssets(
            ("Microsoft.AspNetCore.App.Runtime.win-x64", exactRange),
            ("Microsoft.NETCore.App.Runtime.win-x64", exactRange),
            ("Microsoft.WindowsDesktop.App.Runtime.win-x64", exactRange));
        PackageCommand.VerifyRuntimeDownloadDependenciesForSelfTest(valid, expected);

        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.VerifyRuntimeDownloadDependenciesForSelfTest(
                CreateRuntimeDownloadAssets(
                    ("Microsoft.NETCore.App.Runtime.win-x64", exactRange),
                    ("Microsoft.WindowsDesktop.App.Runtime.win-x64", exactRange)),
                expected));
        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.VerifyRuntimeDownloadDependenciesForSelfTest(
                CreateRuntimeDownloadAssets(
                    ("Microsoft.AspNetCore.App.Runtime.win-x64", "[8.0.27, 8.0.28]"),
                    ("Microsoft.NETCore.App.Runtime.win-x64", exactRange),
                    ("Microsoft.WindowsDesktop.App.Runtime.win-x64", exactRange)),
                expected));
        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.VerifyRuntimeDownloadDependenciesForSelfTest(
                CreateRuntimeDownloadAssets(
                    ("Contoso.Unpinned.Runtime.win-x64", exactRange),
                    ("Microsoft.NETCore.App.Runtime.win-x64", exactRange),
                    ("Microsoft.WindowsDesktop.App.Runtime.win-x64", exactRange)),
                expected));
        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.VerifyRuntimeDownloadDependenciesForSelfTest(
                CreateRuntimeDownloadAssets(
                    ("Microsoft.NETCore.App.Runtime.win-x64", exactRange),
                    ("Microsoft.NETCore.App.Runtime.win-x64", exactRange),
                    ("Microsoft.WindowsDesktop.App.Runtime.win-x64", exactRange)),
                expected));
        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.VerifyRuntimeDownloadDependenciesForSelfTest(
                "{\"project\":{\"frameworks\":{\"net8.0-windows7.0\":{}}}}",
                expected));
    }

    private static string CreateRuntimeDownloadAssets(
        params (string Name, string VersionRange)[] dependencies) =>
        JsonSerializer.Serialize(
            new
            {
                project = new
                {
                    frameworks = new Dictionary<string, object>
                    {
                        ["net8.0-windows7.0"] = new
                        {
                            downloadDependencies = dependencies.Select(dependency => new
                            {
                                name = dependency.Name,
                                version = dependency.VersionRange
                            })
                        }
                    }
                }
            });

    private static void BuildArtifactsManagedEntryPoints()
    {
        using var workspace = TestWorkspace.Create();
        var artifacts = workspace.Child("artifacts");
        Directory.CreateDirectory(artifacts);
        const string assemblyName = "Synthetic.Tests";
        ExpectThrows<DirectoryNotFoundException>(() =>
            PackageCommand.RequireBuiltManagedEntryPoint(artifacts, assemblyName));
        ExpectThrows<InvalidDataException>(() =>
            PackageCommand.RequireBuiltManagedEntryPoint(artifacts, ".."));

        var expected = Path.Combine(
            artifacts,
            "bin",
            assemblyName,
            PackageLayout.Configuration.ToLowerInvariant(),
            assemblyName + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllBytes(expected, [0x4d, 0x5a]);
        Assert(
            string.Equals(
                PackageCommand.RequireBuiltManagedEntryPoint(artifacts, assemblyName),
                expected,
                StringComparison.OrdinalIgnoreCase),
            "The exact run-owned managed entry point was not resolved.");
    }

    private static void CreateRuntimePackage(string path, string id, string version, string payloadName)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        var nuspec = archive.CreateEntry(id + ".nuspec", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(nuspec.Open(), new UTF8Encoding(false)))
        {
            writer.Write($"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>");
        }
        var signature = archive.CreateEntry(".signature.p7s", CompressionLevel.NoCompression);
        using (var output = signature.Open()) output.Write([1, 2, 3, 4]);
        var payload = archive.CreateEntry(payloadName, CompressionLevel.NoCompression);
        using (var output = payload.Open()) output.Write([5, 6, 7, 8]);
    }

    private static void WriteRuntimeCacheEvidence(
        string package,
        string sidecar,
        string metadata,
        string contentHash)
    {
        using (var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            File.WriteAllText(sidecar, Convert.ToBase64String(SHA512.HashData(stream)), Encoding.ASCII);
        }
        File.WriteAllText(
            metadata,
            $"{{\"version\":2,\"contentHash\":\"{contentHash}\",\"source\":\"https://api.nuget.org/v3/index.json\"}}",
            new UTF8Encoding(false));
    }

    private static void OutputTransactionFailureRecovery()
    {
        using var workspace = TestWorkspace.Create();
        var repository = workspace.CreateRepository();
        repository.EnsureDistDirectory();
        var version = SemanticVersion.Read(repository.VersionFile);
        var finalArchive = Path.Combine(repository.Dist, PackageLayout.ArchiveName(version));
        var finalManifest = Path.Combine(repository.Dist, PackageLayout.ManifestName(version));
        var oldArchive = Encoding.UTF8.GetBytes("old archive");
        var oldManifest = Encoding.UTF8.GetBytes("old manifest");
        var newArchive = Encoding.UTF8.GetBytes("new archive");
        var newManifest = Encoding.UTF8.GetBytes("new manifest");
        File.WriteAllBytes(finalArchive, oldArchive);
        File.WriteAllBytes(finalManifest, oldManifest);

        var preparedArchive = workspace.Child("prepared-archive.bin");
        var preparedManifest = workspace.Child("prepared-manifest.bin");
        File.WriteAllBytes(preparedArchive, newArchive);
        File.WriteAllBytes(preparedManifest, newManifest);
        var transaction = PackageOutputTransaction.Install(
            repository,
            version,
            preparedArchive,
            finalArchive,
            PackageCommand.ComputeSha256(preparedArchive),
            preparedManifest,
            finalManifest,
            PackageCommand.ComputeSha256(preparedManifest));

        var unknown = Encoding.UTF8.GetBytes("unknown replacement");
        File.WriteAllBytes(finalArchive, unknown);
        ExpectThrows<AggregateException>(transaction.RollBack);
        Assert(File.ReadAllBytes(finalArchive).AsSpan().SequenceEqual(unknown), "Rollback deleted or replaced an unknown final output.");
        Assert(Directory.EnumerateFiles(repository.Dist, "_package-output-transaction.json").Any(), "Ambiguous rollback removed its durable journal.");

        File.WriteAllBytes(finalArchive, newArchive);
        transaction.RollBack();
        Assert(File.ReadAllBytes(finalArchive).AsSpan().SequenceEqual(oldArchive), "Archive original was not restored.");
        Assert(File.ReadAllBytes(finalManifest).AsSpan().SequenceEqual(oldManifest), "Manifest original was not restored.");
        Assert(!Directory.EnumerateFiles(repository.Dist, "_package-output-transaction.json").Any(), "Successful rollback retained its journal.");

        preparedArchive = workspace.Child("prepared-archive-2.bin");
        preparedManifest = workspace.Child("prepared-manifest-2.bin");
        File.WriteAllBytes(preparedArchive, newArchive);
        File.WriteAllBytes(preparedManifest, newManifest);
        _ = PackageOutputTransaction.Install(
            repository,
            version,
            preparedArchive,
            finalArchive,
            PackageCommand.ComputeSha256(preparedArchive),
            preparedManifest,
            finalManifest,
            PackageCommand.ComputeSha256(preparedManifest));
        var committedJournal = Path.Combine(repository.Dist, "_package-output-transaction.json");
        var temporaryJournal = committedJournal + ".new";
        File.Move(committedJournal, temporaryJournal);
        PackageOutputTransaction.Recover(repository);
        Assert(File.ReadAllBytes(finalArchive).AsSpan().SequenceEqual(oldArchive), "Crash recovery did not restore the archive original.");
        Assert(File.ReadAllBytes(finalManifest).AsSpan().SequenceEqual(oldManifest), "Crash recovery did not restore the manifest original.");

        preparedArchive = workspace.Child("prepared-archive-3.bin");
        preparedManifest = workspace.Child("prepared-manifest-3.bin");
        File.WriteAllBytes(preparedArchive, newArchive);
        File.WriteAllBytes(preparedManifest, newManifest);
        var backupTransaction = PackageOutputTransaction.Install(
            repository,
            version,
            preparedArchive,
            finalArchive,
            PackageCommand.ComputeSha256(preparedArchive),
            preparedManifest,
            finalManifest,
            PackageCommand.ComputeSha256(preparedManifest));
        var archiveBackup = Directory.EnumerateFiles(
            repository.Dist,
            Path.GetFileName(finalArchive) + ".backup-*").Single();
        var damagedBackup = Encoding.UTF8.GetBytes("damaged backup");
        File.WriteAllBytes(archiveBackup, damagedBackup);
        ExpectThrows<AggregateException>(backupTransaction.RollBack);
        Assert(File.ReadAllBytes(finalArchive).AsSpan().SequenceEqual(newArchive), "Rollback deleted a final output after backup verification failed.");
        Assert(File.ReadAllBytes(archiveBackup).AsSpan().SequenceEqual(damagedBackup), "Rollback deleted an unrecognized recovery backup.");
        Assert(Directory.EnumerateFiles(repository.Dist, "_package-output-transaction.json").Any(), "Backup-hash ambiguity removed its durable journal.");

        using var finalizeWorkspace = TestWorkspace.Create();
        var finalizeRepository = finalizeWorkspace.CreateRepository();
        finalizeRepository.EnsureDistDirectory();
        var finalizeVersion = SemanticVersion.Read(finalizeRepository.VersionFile);
        var finalizeArchive = Path.Combine(finalizeRepository.Dist, PackageLayout.ArchiveName(finalizeVersion));
        var finalizeManifest = Path.Combine(finalizeRepository.Dist, PackageLayout.ManifestName(finalizeVersion));
        File.WriteAllBytes(finalizeArchive, oldArchive);
        File.WriteAllBytes(finalizeManifest, oldManifest);
        var finalizePreparedArchive = finalizeWorkspace.Child("prepared-archive.bin");
        var finalizePreparedManifest = finalizeWorkspace.Child("prepared-manifest.bin");
        File.WriteAllBytes(finalizePreparedArchive, newArchive);
        File.WriteAllBytes(finalizePreparedManifest, newManifest);
        var finalizeTransaction = PackageOutputTransaction.Install(
            finalizeRepository,
            finalizeVersion,
            finalizePreparedArchive,
            finalizeArchive,
            PackageCommand.ComputeSha256(finalizePreparedArchive),
            finalizePreparedManifest,
            finalizeManifest,
            PackageCommand.ComputeSha256(finalizePreparedManifest));
        finalizeTransaction.MarkVerified();
        File.WriteAllBytes(finalizeArchive, unknown);
        ExpectThrows<InvalidDataException>(finalizeTransaction.Complete);
        Assert(Directory.EnumerateFiles(finalizeRepository.Dist, Path.GetFileName(finalizeArchive) + ".backup-*").Any(),
            "Finalization deleted the rollback backup after the verified output changed.");
        Assert(Directory.EnumerateFiles(finalizeRepository.Dist, "_package-output-transaction.json").Any(),
            "Finalization removed its journal after the verified output changed.");
        File.WriteAllBytes(finalizeArchive, newArchive);
        finalizeTransaction.RollBack();
        Assert(File.ReadAllBytes(finalizeArchive).AsSpan().SequenceEqual(oldArchive),
            "Finalization recovery did not restore the original archive.");

        using var verifiedWorkspace = TestWorkspace.Create();
        var verifiedRepository = verifiedWorkspace.CreateRepository();
        verifiedRepository.EnsureDistDirectory();
        var verifiedVersion = SemanticVersion.Read(verifiedRepository.VersionFile);
        var verifiedArchive = Path.Combine(verifiedRepository.Dist, PackageLayout.ArchiveName(verifiedVersion));
        var verifiedManifest = Path.Combine(verifiedRepository.Dist, PackageLayout.ManifestName(verifiedVersion));
        File.WriteAllBytes(verifiedArchive, oldArchive);
        File.WriteAllBytes(verifiedManifest, oldManifest);
        var verifiedPreparedArchive = verifiedWorkspace.Child("prepared-recovery.zip");
        var verifiedPreparedManifest = verifiedWorkspace.Child("prepared-recovery.manifest.json");
        CreateRecoveryArchive(verifiedPreparedArchive, verifiedVersion.Original);
        File.WriteAllBytes(verifiedPreparedManifest, newManifest);
        var verifiedTransaction = PackageOutputTransaction.Install(
            verifiedRepository,
            verifiedVersion,
            verifiedPreparedArchive,
            verifiedArchive,
            PackageCommand.ComputeSha256(verifiedPreparedArchive),
            verifiedPreparedManifest,
            verifiedManifest,
            PackageCommand.ComputeSha256(verifiedPreparedManifest));
        verifiedTransaction.MarkVerified();
        var committedArchiveHash = PackageCommand.ComputeSha256(verifiedArchive);
        ExpectThrows<IOException>(() => PackageOutputTransaction.RecoverForSelfTest(
            verifiedRepository,
            deletedBackups =>
            {
                if (deletedBackups == 1)
                    throw new IOException("Synthetic partial verified-cleanup failure.");
            }));
        Assert(PackageCommand.ComputeSha256(verifiedArchive) == committedArchiveHash,
            "Verified cleanup failure rolled back or changed the committed archive.");
        Assert(File.ReadAllBytes(verifiedManifest).AsSpan().SequenceEqual(newManifest),
            "Verified cleanup failure rolled back or changed the committed manifest.");
        Assert(Directory.EnumerateFiles(verifiedRepository.Dist, "*.backup-*").Count() == 1,
            "Partial verified cleanup did not retain exactly its remaining backup.");
        Assert(Directory.EnumerateFiles(verifiedRepository.Dist, "_package-output-transaction.json").Any(),
            "Partial verified cleanup removed its retry journal.");
        PackageOutputTransaction.Recover(verifiedRepository);
        Assert(!Directory.EnumerateFiles(verifiedRepository.Dist, "*.backup-*").Any(),
            "Successful verified recovery retained an original-output backup.");
        Assert(!Directory.EnumerateFiles(verifiedRepository.Dist, "_package-output-transaction.json").Any(),
            "Successful verified recovery retained its durable journal.");

        using var blockedWorkspace = TestWorkspace.Create();
        var blockedRepository = blockedWorkspace.CreateRepository();
        blockedRepository.EnsureDistDirectory();
        var blockedVersion = SemanticVersion.Read(blockedRepository.VersionFile);
        var blockedArchive = Path.Combine(blockedRepository.Dist, PackageLayout.ArchiveName(blockedVersion));
        var blockedManifest = Path.Combine(blockedRepository.Dist, PackageLayout.ManifestName(blockedVersion));
        File.WriteAllBytes(blockedArchive, oldArchive);
        File.WriteAllBytes(blockedManifest, oldManifest);
        var blockedPreparedArchive = blockedWorkspace.Child("prepared-blocked-recovery.zip");
        var blockedPreparedManifest = blockedWorkspace.Child("prepared-blocked-recovery.manifest.json");
        CreateRecoveryArchive(blockedPreparedArchive, blockedVersion.Original);
        File.WriteAllBytes(blockedPreparedManifest, newManifest);
        var blockedTransaction = PackageOutputTransaction.Install(
            blockedRepository,
            blockedVersion,
            blockedPreparedArchive,
            blockedArchive,
            PackageCommand.ComputeSha256(blockedPreparedArchive),
            blockedPreparedManifest,
            blockedManifest,
            PackageCommand.ComputeSha256(blockedPreparedManifest));
        blockedTransaction.MarkVerified();
        ExpectThrows<IOException>(() => PackageOutputTransaction.RecoverForSelfTest(
            blockedRepository,
            deletedBackups =>
            {
                if (deletedBackups == 1)
                    throw new IOException("Synthetic cleanup failure before blocked rollback preflight.");
            }));
        Assert(!Directory.EnumerateFiles(
                blockedRepository.Dist,
                Path.GetFileName(blockedArchive) + ".backup-*").Any(),
            "Fault-chain setup retained the archive backup that should have been deleted first.");
        Assert(Directory.EnumerateFiles(
                blockedRepository.Dist,
                Path.GetFileName(blockedManifest) + ".backup-*").Count() == 1,
            "Fault-chain setup did not retain the manifest backup.");
        File.WriteAllBytes(blockedArchive, unknown);
        var beforeBlockedRecovery = CaptureFlatFileHashes(blockedRepository.Dist);
        var blockedFailure = ExpectThrows<PackageOutputRollbackBlockedException>(
            () => PackageOutputTransaction.Recover(blockedRepository));
        Assert(blockedFailure.Message.StartsWith("BLOCKED:", StringComparison.Ordinal),
            "Rollback preflight failure was not reported as an explicit BLOCKED condition.");
        var afterBlockedRecovery = CaptureFlatFileHashes(blockedRepository.Dist);
        Assert(beforeBlockedRecovery.Count == afterBlockedRecovery.Count
               && beforeBlockedRecovery.All(pair =>
                   afterBlockedRecovery.TryGetValue(pair.Key, out var hash)
                   && hash.Equals(pair.Value, StringComparison.Ordinal)),
            "Blocked verified recovery changed a final, backup, or journal path after partial cleanup.");

        using var failedVerifiedWorkspace = TestWorkspace.Create();
        var failedVerifiedRepository = failedVerifiedWorkspace.CreateRepository();
        failedVerifiedRepository.EnsureDistDirectory();
        var failedVerifiedVersion = SemanticVersion.Read(failedVerifiedRepository.VersionFile);
        var failedVerifiedArchive = Path.Combine(failedVerifiedRepository.Dist, PackageLayout.ArchiveName(failedVerifiedVersion));
        var failedVerifiedManifest = Path.Combine(failedVerifiedRepository.Dist, PackageLayout.ManifestName(failedVerifiedVersion));
        File.WriteAllBytes(failedVerifiedArchive, oldArchive);
        File.WriteAllBytes(failedVerifiedManifest, oldManifest);
        var failedPreparedArchive = failedVerifiedWorkspace.Child("prepared-recovery.zip");
        var failedPreparedManifest = failedVerifiedWorkspace.Child("prepared-recovery.manifest.json");
        CreateRecoveryArchive(failedPreparedArchive, failedVerifiedVersion.Original);
        File.WriteAllBytes(failedPreparedManifest, newManifest);
        var failedVerifiedTransaction = PackageOutputTransaction.Install(
            failedVerifiedRepository,
            failedVerifiedVersion,
            failedPreparedArchive,
            failedVerifiedArchive,
            PackageCommand.ComputeSha256(failedPreparedArchive),
            failedPreparedManifest,
            failedVerifiedManifest,
            PackageCommand.ComputeSha256(failedPreparedManifest));
        failedVerifiedTransaction.MarkVerified();
        File.Delete(failedVerifiedManifest);
        PackageOutputTransaction.Recover(failedVerifiedRepository);
        Assert(File.ReadAllBytes(failedVerifiedArchive).AsSpan().SequenceEqual(oldArchive),
            "Failed verified recovery did not restore the original archive.");
        Assert(File.ReadAllBytes(failedVerifiedManifest).AsSpan().SequenceEqual(oldManifest),
            "Failed verified recovery did not restore the missing original manifest.");
        Assert(!Directory.EnumerateFiles(failedVerifiedRepository.Dist, "*.backup-*").Any(),
            "Failed verified recovery retained a backup after successful rollback.");
        Assert(!Directory.EnumerateFiles(failedVerifiedRepository.Dist, "_package-output-transaction.json").Any(),
            "Failed verified recovery retained its journal after successful rollback.");
    }

    private static void SmokeStrictAcknowledgement()
    {
        var smoke = SmokeWorkspace.Create();
        var root = smoke.Root;
        try
        {
            var appData = smoke.CreateDirectory("appdata");
            var acknowledgement = Path.Combine(appData, "isolated-discovery.ack");
            File.WriteAllText(
                acknowledgement,
                PackageLayout.DiscoveryAckContent + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert(PackageCommand.ReadAcknowledgement(acknowledgement, smoke), "Exact acknowledgement was rejected.");
            File.WriteAllText(
                acknowledgement,
                PackageLayout.DiscoveryAckContent + "\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ExpectThrows<InvalidDataException>(() => PackageCommand.ReadAcknowledgement(acknowledgement, smoke));
        }
        finally
        {
            smoke.Dispose();
        }
        Assert(!Directory.Exists(root), "The package-smoke workspace was not removed.");
        ExpectThrows<ObjectDisposedException>(() => smoke.RequireChild("recreated"));
        Assert(!Directory.Exists(root), "A disposed package-smoke workspace was recreated.");
    }

    private static void JobActiveProcessLimit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var job = WindowsKillOnCloseJob.Create();
        Assert(job.GetActiveProcessLimit() == 1, "Job Object does not enforce a one-process limit.");
        var accounting = job.GetAccounting();
        Assert(accounting.TotalProcesses == 0 && accounting.ActiveProcesses == 0, "Fresh Job Object accounting was not empty.");
    }

    private static void ProcessTreePidReuseIdentity()
    {
        const ulong parentCreationTime = 1_000;
        WindowsProcessTree.ProcessRecord[] records =
        [
            new(1676, 1660, "csrss.exe"),
            new(1768, 1660, "winlogon.exe"),
            new(1320, 1768, "fontdrvhost.exe"),
            new(2240, 1768, "dwm.exe"),
            new(2000, 1660, "actual-direct.exe"),
            new(2001, 2000, "actual-grandchild.exe")
        ];
        var creationTimes = new Dictionary<int, ulong>
        {
            [1676] = 100,
            [1768] = 110,
            [1320] = 120,
            [2240] = 130,
            [2000] = 1_100,
            [2001] = 1_200
        };

        var descendants = WindowsProcessTree.FindDescendantsForTesting(
            1660,
            parentCreationTime,
            records,
            record => creationTimes[record.ProcessId]);

        Assert(descendants.Select(record => record.ProcessId).SequenceEqual([2000, 2001]),
            "PID-reuse filtering did not prune the stale pre-parent branches or retain the real descendant branch.");

        ExpectThrows<InvalidOperationException>(() => WindowsProcessTree.FindDescendantsForTesting(
            1660,
            parentCreationTime,
            [new WindowsProcessTree.ProcessRecord(3000, 1660, "unreadable.exe")],
            _ => throw new InvalidOperationException("synthetic identity failure")));
    }

    private static void CreateArchive(string path, IEnumerable<string> names, string contentPrefix = "synthetic")
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        var index = 0;
        foreach (var name in names.OrderBy(value => value, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write($"{contentPrefix}-{index++}-{name}");
        }
    }

    private static void CreateRecoveryArchive(string path, string version)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        foreach (var name in PackageLayout.AllFiles.OrderBy(value => value, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(name.Equals("VERSION", StringComparison.Ordinal) ? version + "\n" : "synthetic package content");
        }
    }

    private static void PatchEndOfCentralDirectory(string path, Action<byte[], int> patch)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = FindEndOfCentralDirectory(bytes);
        patch(bytes, offset);
        File.WriteAllBytes(path, bytes);
    }

    private static int FindEndOfCentralDirectory(byte[] bytes)
    {
        for (var offset = bytes.Length - 22; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint))) == 0x06054b50)
                return offset;
        }
        throw new InvalidDataException("Synthetic archive has no EOCD record.");
    }

    private static Dictionary<string, string> CaptureFlatFileHashes(string root)
    {
        if (Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidDataException("Synthetic package-output state unexpectedly contains a directory.");
        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .ToDictionary(
                path => Path.GetFileName(path),
                PackageCommand.ComputeSha256,
                StringComparer.OrdinalIgnoreCase);
    }

    private static TException ExpectThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void ExpectAuditFailure(Action action)
    {
        var previous = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            ExpectThrows<InvalidDataException>(action);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestWorkspace : IDisposable
    {
        private const string Prefix = "RimWorldAiTranslator-tooling-self-test-";
        private readonly string tempRoot;
        private bool disposed;

        private TestWorkspace(string root, string tempRoot)
        {
            Root = root;
            this.tempRoot = tempRoot;
        }

        public string Root { get; }

        public static TestWorkspace Create()
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(temp) || (File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Self-test temporary root is missing or redirected.");
            var root = Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestWorkspace(root, temp);
        }

        public string Child(string relative)
        {
            if (Path.IsPathFullyQualified(relative))
                throw new InvalidOperationException("Self-test child path must be relative.");
            var full = Path.GetFullPath(Path.Combine(Root, relative));
            var child = Path.GetRelativePath(Root, full);
            if (Path.IsPathRooted(child)
                || child.Equals("..", StringComparison.Ordinal)
                || child.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || child.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Self-test child escaped its owned root.");
            }
            return full;
        }

        public RepositoryLayout CreateRepository()
        {
            Write("RimWorldAiTranslator.sln", string.Empty);
            Write("NuGet.config", "<configuration><packageSources><clear /></packageSources></configuration>\n");
            Write("VERSION", "1.2.3-rc.1\n");
            Write(Path.Combine("src", "RimWorldAiTranslator.App", "RimWorldAiTranslator.App.csproj"), "<Project />\n");
            Write(Path.Combine("tests", "RimWorldAiTranslator.Tests", "RimWorldAiTranslator.Tests.csproj"), "<Project />\n");
            Write(Path.Combine("tools", "RimWorldAiTranslator.GlossaryTool", "RimWorldAiTranslator.GlossaryTool.csproj"), "<Project />\n");
            return RepositoryLayout.Find(Root);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var relative = Path.GetRelativePath(tempRoot, Root);
            if (Path.IsPathRooted(relative)
                || relative.Contains(Path.DirectorySeparatorChar)
                || relative.Contains(Path.AltDirectorySeparatorChar)
                || !relative.StartsWith(Prefix, StringComparison.Ordinal)
                || relative.Length != Prefix.Length + 32
                || !Guid.TryParseExact(relative[Prefix.Length..], "N", out _))
            {
                throw new InvalidOperationException("Refusing to delete an unverified self-test root.");
            }
            if (!Directory.Exists(Root)) return;
            AssertNoReparseTree(Root);
            Directory.Delete(Root, recursive: true);
        }

        private void Write(string relative, string content)
        {
            var path = Child(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        private static void AssertNoReparseTree(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Self-test workspace contains a reparse point.");
                if (!Directory.Exists(current)) continue;
                foreach (var child in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
                    pending.Push(child);
            }
        }
    }
}
