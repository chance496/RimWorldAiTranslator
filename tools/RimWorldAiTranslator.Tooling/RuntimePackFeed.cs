using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace RimWorldAiTranslator.Tooling;

internal sealed class PreparedRuntimeFeed : IDisposable
{
    private readonly PinnedFileTree pinnedFiles;

    public PreparedRuntimeFeed(
        string root,
        IReadOnlyDictionary<string, string> expectedContentHashes,
        PinnedFileTree pinnedFiles)
    {
        Root = root;
        ExpectedContentHashes = expectedContentHashes;
        this.pinnedFiles = pinnedFiles;
    }

    public string Root { get; }
    public IReadOnlyDictionary<string, string> ExpectedContentHashes { get; }

    public void Verify() => pinnedFiles.Verify();
    public void Dispose() => pinnedFiles.Dispose();
}

internal static class RuntimePackFeed
{
    internal const string PinnedSdkVersion = "8.0.422";
    internal const string RuntimePackVersion = "8.0.28";

    private const string MicrosoftAuthorCertificateSha256 =
        "566A31882BE208BE4422F7CFD66ED09F5D4524A5994F50CCC8B05EC0528C1353";
    private const string NetCoreRawPackageSha512 =
        "SRdQnoumUQkUjrO/IKeC9joscXGWFtxOxhImwiHfBCaRyiQCZSOX7G+NvrpY1HKaeDEdk3plYaXAM2U2Sa59zQ==";
    private const string WindowsDesktopRawPackageSha512 =
        "ooWLItKGmK5MGOfwWtrW69HFzra1rZndhoAVzN+Ir3H9AYYQWiCONs86GmJ02YjqwB+LLUfUrSZypgbzujeNqg==";
    private const string AspNetRawPackageSha512 =
        "Uex6/8s52jeHAgeT01k6VKCAmf8YqrUb4D103LIZgyI9T4cZKyPj8ymWOkBt7bQP1EDwp1ctxJckvzaubjJvXA==";
    private const string NetCoreMetadataContentHash =
        "G2SWebJKnBkixQcJlVkCV0EbqdoAhqAf6evVKDcY6CmFjPOFeC+gZeO6/dlm4v1fbKERjpjJzMg0mnKA1il1Zg==";
    private const string WindowsDesktopMetadataContentHash =
        "sKbAXRze+wBxtj2zexZfpR4vGe2yKZqRn79wFpwa6Ev7be81noXIewGpWvoy7s41y1bTPFzt2YDl3pGFFsXYmQ==";
    private const string AspNetMetadataContentHash =
        "a97O/kRRTEX+VUL66tyQ2Lgl+As+AEw08f9Qwu8+p7k909Zk+L1dyqZECkF5vqxiEpc9sOtK2+NrS946/uuITw==";

    private const long MaximumPackageBytes = 64L * 1024 * 1024;
    private const long MaximumAggregatePackageBytes = 128L * 1024 * 1024;
    private const long MaximumEntryBytes = 128L * 1024 * 1024;
    private const long MaximumAggregateEntryBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 4096;
    private const int MaximumCentralDirectoryBytes = 2 * 1024 * 1024;
    private const int MaximumPathCharacters = 512;
    private const long MaximumCompressionRatio = 200;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
    private const uint CentralDirectoryFileHeaderSignature = 0x02014b50;
    private const ushort Zip64ExtraFieldIdentifier = 0x0001;

    private static readonly RuntimePackageSpec[] Packages =
    [
        new(
            "Microsoft.NETCore.App.Runtime.win-x64",
            RuntimePackVersion,
            NetCoreRawPackageSha512,
            NetCoreMetadataContentHash),
        new(
            "Microsoft.WindowsDesktop.App.Runtime.win-x64",
            RuntimePackVersion,
            WindowsDesktopRawPackageSha512,
            WindowsDesktopMetadataContentHash),
        new(
            "Microsoft.AspNetCore.App.Runtime.win-x64",
            RuntimePackVersion,
            AspNetRawPackageSha512,
            AspNetMetadataContentHash)
    ];

    public static async Task<PreparedRuntimeFeed> CreateAsync(
        RepositoryLayout sourceRepository,
        RepositoryLayout workRepository,
        string workRoot,
        ExternalProcessPolicy processes,
        IReadOnlyDictionary<string, string?> buildEnvironment,
        CancellationToken cancellationToken)
    {
        VerifyPinnedSdkConfig(sourceRepository.RequireFile("global.json"));
        var resolvedSdk = await processes.ReadDotnetSingleLineAsync(
            ["--version"],
            sourceRepository.Root,
            buildEnvironment,
            cancellationToken);
        if (!resolvedSdk.Equals(PinnedSdkVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The resolved .NET SDK is {resolvedSdk}; packaging requires exactly {PinnedSdkVersion}.");
        }

        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(userProfile))
            throw new DirectoryNotFoundException("The standard operating-system user profile could not be resolved.");
        userProfile = Path.GetFullPath(userProfile)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var globalPackages = Path.GetFullPath(Path.Combine(userProfile, ".nuget", "packages"));
        if (!Directory.Exists(globalPackages))
            throw new DirectoryNotFoundException("The standard user NuGet global package cache is missing.");
        AssertNoReparseComponents(globalPackages);

        var candidates = new List<VerifiedCandidate>(Packages.Length);
        try
        {
            var aggregatePackageBytes = 0L;
            foreach (var package in Packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = OpenAndValidateCandidate(globalPackages, package);
                candidates.Add(candidate);
                aggregatePackageBytes = checked(aggregatePackageBytes + candidate.Length);
                if (aggregatePackageBytes > MaximumAggregatePackageBytes)
                {
                    throw new InvalidDataException(
                        $"Runtime packages exceed the {MaximumAggregatePackageBytes}-byte aggregate limit.");
                }
            }

            var verificationEnvironment = new Dictionary<string, string?>(buildEnvironment, StringComparer.OrdinalIgnoreCase)
            {
                ["NUGET_CERT_REVOCATION_MODE"] = "offline"
            };
            var verifyArguments = new List<string>
            {
                "nuget", "verify"
            };
            verifyArguments.AddRange(candidates.Select(candidate => candidate.SourcePath));
            verifyArguments.AddRange(
            [
                "--all",
                "--certificate-fingerprint", MicrosoftAuthorCertificateSha256,
                "--configfile", sourceRepository.NuGetConfig,
                "--verbosity", "minimal"
            ]);
            Console.WriteLine("Verifying cached runtime-pack signatures with offline certificate revocation...");
            await processes.RunDotnetQuietAsync(
                verifyArguments,
                sourceRepository.Root,
                verificationEnvironment,
                cancellationToken);

            var feedRoot = workRepository.RequireRepositoryPath(Path.Combine(workRoot, "verified-runtime-feed"));
            if (File.Exists(feedRoot) || Directory.Exists(feedRoot))
                throw new IOException("The run-owned runtime feed path already exists.");
            Directory.CreateDirectory(feedRoot);
            workRepository.AssertNoReparseComponents(feedRoot);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = workRepository.RequireRepositoryPath(
                    Path.Combine(feedRoot, candidate.Package.FeedFileName));
                candidate.CopyTo(destination);
                workRepository.AssertNoReparseComponents(destination);
            }

            AssertExactFeed(feedRoot, workRepository);
            var expectedHashes = candidates.ToDictionary(
                candidate => candidate.Package.LibraryName,
                candidate => candidate.ContentHash,
                StringComparer.OrdinalIgnoreCase);
            var pinnedFeed = PinnedFileTree.CaptureExact(feedRoot, "verified run-owned runtime feed");
            try
            {
                pinnedFeed.Verify();
                return new PreparedRuntimeFeed(feedRoot, expectedHashes, pinnedFeed);
            }
            catch
            {
                pinnedFeed.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    internal static void VerifyPinnedSdkConfig(string path)
    {
        var bytes = ReadSmallRegularFile(path, 4096);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                is not { Count: 1 } rootNames
            || !rootNames.Contains("sdk")
            || !root.TryGetProperty("sdk", out var sdk)
            || sdk.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("global.json must contain only one sdk object.");
        }

        var sdkNames = sdk.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!sdkNames.SetEquals(["version", "rollForward", "allowPrerelease"])
            || sdk.GetProperty("version").GetString() != PinnedSdkVersion
            || sdk.GetProperty("rollForward").GetString() != "latestPatch"
            || sdk.GetProperty("allowPrerelease").ValueKind is not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"global.json does not match the audited {PinnedSdkVersion} SDK policy.");
        }
    }

    internal static string VerifyCacheCandidateForSelfTest(
        string packagePath,
        string sidecarPath,
        string metadataPath,
        string packageId,
        string version,
        string expectedRawPackageHash,
        string expectedContentHash)
    {
        var package = new RuntimePackageSpec(
            packageId,
            version,
            expectedRawPackageHash,
            expectedContentHash);
        using var candidate = OpenAndValidateCandidate(packagePath, sidecarPath, metadataPath, package);
        return candidate.ContentHash;
    }

    internal static (string RawPackageHash, string MetadataContentHash) GetPinnedIdentityForSelfTest(
        string packageId)
    {
        var package = Packages.SingleOrDefault(candidate => candidate.Id.Equals(packageId, StringComparison.Ordinal))
                      ?? throw new InvalidDataException("The requested runtime package is not pinned.");
        return (package.ExpectedRawPackageHash, package.ExpectedContentHash);
    }

    private static VerifiedCandidate OpenAndValidateCandidate(string globalPackages, RuntimePackageSpec package)
    {
        var packageDirectory = Path.GetFullPath(Path.Combine(
            globalPackages,
            package.Id.ToLowerInvariant(),
            package.Version));
        if (!IsLexicallyInside(globalPackages, packageDirectory) || !Directory.Exists(packageDirectory))
            throw new DirectoryNotFoundException($"Required cached runtime package is missing: {package.LibraryName}");
        AssertNoReparseComponents(packageDirectory);

        return OpenAndValidateCandidate(
            Path.Combine(packageDirectory, package.FeedFileName),
            Path.Combine(packageDirectory, package.FeedFileName + ".sha512"),
            Path.Combine(packageDirectory, ".nupkg.metadata"),
            package);
    }

    private static VerifiedCandidate OpenAndValidateCandidate(
        string packagePath,
        string sidecarPath,
        string metadataPath,
        RuntimePackageSpec package)
    {
        packagePath = Path.GetFullPath(packagePath);
        sidecarPath = Path.GetFullPath(sidecarPath);
        metadataPath = Path.GetFullPath(metadataPath);
        AssertNoReparseComponents(packagePath);
        AssertNoReparseComponents(sidecarPath);
        AssertNoReparseComponents(metadataPath);

        var sidecarRawHash = ParseHashSidecar(ReadSmallRegularFile(sidecarPath, 256));
        var contentHash = ReadCacheMetadata(metadataPath);
        var pinnedRawHash = ParseBase64Sha512(package.ExpectedRawPackageHash, "pinned package hash");
        var pinnedContentHash = ParseBase64Sha512(package.ExpectedContentHash, "pinned metadata content hash");
        var actualContentHash = ParseBase64Sha512(contentHash, "cache metadata content hash");
        if (!CryptographicOperations.FixedTimeEquals(sidecarRawHash, pinnedRawHash))
            throw new InvalidDataException($"Runtime package SHA-512 sidecar differs from the pinned identity: {package.LibraryName}");
        if (!CryptographicOperations.FixedTimeEquals(actualContentHash, pinnedContentHash))
            throw new InvalidDataException($"Runtime package metadata differs from the pinned identity: {package.LibraryName}");
        var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        try
        {
            AssertRegularFile(stream.Name);
            if (stream.Length <= 0 || stream.Length > MaximumPackageBytes)
            {
                throw new InvalidDataException(
                    $"Runtime package size is outside the allowed range: {package.LibraryName} ({stream.Length} bytes).");
            }
            ValidateArchive(stream, package);
            stream.Position = 0;
            var actualRawHash = SHA512.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(actualRawHash, pinnedRawHash))
                throw new InvalidDataException($"Runtime package bytes differ from the pinned identity: {package.LibraryName}");
            stream.Position = 0;
            return new VerifiedCandidate(package, stream, actualRawHash, package.ExpectedContentHash);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ValidateArchive(FileStream stream, RuntimePackageSpec package)
    {
        ValidateClassicZipMetadata(stream);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
        if (archive.Entries.Count is <= 0 or > MaximumEntries)
            throw new InvalidDataException($"Runtime package has an invalid entry count: {package.LibraryName}");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? nuspec = null;
        var signatureCount = 0;
        var aggregateBytes = 0L;
        foreach (var entry in archive.Entries)
        {
            ValidateEntryName(entry.FullName);
            if (!names.Add(entry.FullName))
                throw new InvalidDataException($"Runtime package contains a duplicate entry: {package.LibraryName}");
            if (IsReparseEntry(entry))
                throw new InvalidDataException($"Runtime package contains a link/reparse entry: {package.LibraryName}");

            var isDirectory = entry.FullName.EndsWith('/');
            if (isDirectory && (entry.Length != 0 || entry.CompressedLength != 0))
                throw new InvalidDataException($"Runtime package contains a non-empty directory entry: {package.LibraryName}");
            if (!isDirectory)
            {
                if (entry.Length < 0 || entry.Length > MaximumEntryBytes || entry.CompressedLength < 0)
                    throw new InvalidDataException($"Runtime package entry exceeds its size policy: {package.LibraryName}");
                if (entry.Length > 0 && entry.CompressedLength == 0)
                    throw new InvalidDataException($"Runtime package entry has an invalid compressed size: {package.LibraryName}");
                if (entry.CompressedLength > 0
                    && entry.Length > entry.CompressedLength * MaximumCompressionRatio)
                {
                    throw new InvalidDataException($"Runtime package entry exceeds its compression-ratio policy: {package.LibraryName}");
                }
                aggregateBytes = checked(aggregateBytes + entry.Length);
                if (aggregateBytes > MaximumAggregateEntryBytes)
                    throw new InvalidDataException($"Runtime package expands beyond its aggregate size policy: {package.LibraryName}");
                ReadEntryExactly(entry, package);
            }

            if (entry.FullName.Equals(package.NuspecFileName, StringComparison.Ordinal)) nuspec = entry;
            if (entry.FullName.Equals(".signature.p7s", StringComparison.Ordinal)) signatureCount++;
        }

        if (nuspec is null || signatureCount != 1)
            throw new InvalidDataException($"Runtime package lacks its exact nuspec or signature entry: {package.LibraryName}");
        VerifyNuspec(nuspec, package);
        stream.Position = 0;
    }

    private static void ValidateClassicZipMetadata(FileStream stream)
    {
        const int minimumEocdBytes = 22;
        var length = stream.Length;
        var tailLength = checked((int)Math.Min(length, minimumEocdBytes + ushort.MaxValue));
        if (tailLength < minimumEocdBytes)
            throw new InvalidDataException("Runtime package is too small to be a ZIP archive.");
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);

        var eocdInTail = -1;
        for (var offset = tail.Length - minimumEocdBytes; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset, sizeof(uint))) != EndOfCentralDirectorySignature)
                continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 20, sizeof(ushort)));
            if (offset + minimumEocdBytes + commentLength == tail.Length)
            {
                eocdInTail = offset;
                break;
            }
        }
        if (eocdInTail < 0)
            throw new InvalidDataException("Runtime package has no bounded end-of-central-directory record.");

        var eocd = tail.AsSpan(eocdInTail, minimumEocdBytes);
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]);
        var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..]);
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        var centralBytes = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        if (disk != 0
            || centralDisk != 0
            || entriesOnDisk != entries
            || entries is 0 or ushort.MaxValue
            || entries > MaximumEntries
            || centralBytes is 0 or uint.MaxValue
            || centralBytes > MaximumCentralDirectoryBytes
            || centralOffset == uint.MaxValue)
        {
            throw new InvalidDataException("Runtime package uses unsupported split, Zip64, or unbounded ZIP metadata.");
        }

        var eocdOffset = checked(length - tailLength + eocdInTail);
        if (checked((long)centralOffset + centralBytes) != eocdOffset)
            throw new InvalidDataException("Runtime package central-directory bounds are inconsistent.");
        if (eocdOffset >= 20)
        {
            Span<byte> locator = stackalloc byte[sizeof(uint)];
            stream.Position = eocdOffset - 20;
            stream.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == Zip64EndOfCentralDirectoryLocatorSignature)
                throw new InvalidDataException("Runtime package uses unsupported Zip64 metadata.");
        }

        var central = new byte[checked((int)centralBytes)];
        stream.Position = centralOffset;
        stream.ReadExactly(central);
        var cursor = 0;
        for (var index = 0; index < entries; index++)
        {
            const int fixedHeaderBytes = 46;
            if (cursor > central.Length - fixedHeaderBytes
                || BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor, sizeof(uint)))
                != CentralDirectoryFileHeaderSignature)
            {
                throw new InvalidDataException("Runtime package central directory has an invalid entry header.");
            }
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor + 20, sizeof(uint)));
            var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor + 24, sizeof(uint)));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 28, sizeof(ushort)));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 30, sizeof(ushort)));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 32, sizeof(ushort)));
            var diskStart = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 34, sizeof(ushort)));
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor + 42, sizeof(uint)));
            if (compressed == uint.MaxValue
                || uncompressed == uint.MaxValue
                || diskStart != 0
                || localOffset == uint.MaxValue)
            {
                throw new InvalidDataException("Runtime package central directory uses unsupported Zip64 metadata.");
            }
            var recordBytes = checked(fixedHeaderBytes + nameLength + extraLength + commentLength);
            if (recordBytes > central.Length - cursor)
                throw new InvalidDataException("Runtime package central-directory entry exceeds its record.");
            ValidateNoZip64ExtraField(central.AsSpan(cursor + fixedHeaderBytes + nameLength, extraLength));
            cursor += recordBytes;
        }
        if (cursor != central.Length)
            throw new InvalidDataException("Runtime package central directory has trailing or missing records.");
    }

    private static void ValidateNoZip64ExtraField(ReadOnlySpan<byte> extraFields)
    {
        var cursor = 0;
        while (cursor < extraFields.Length)
        {
            if (cursor > extraFields.Length - 4)
                throw new InvalidDataException("Runtime package has a truncated ZIP extra field.");
            var identifier = BinaryPrimitives.ReadUInt16LittleEndian(extraFields[cursor..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extraFields[(cursor + 2)..]);
            cursor += 4;
            if (length > extraFields.Length - cursor)
                throw new InvalidDataException("Runtime package has an out-of-bounds ZIP extra field.");
            if (identifier == Zip64ExtraFieldIdentifier)
                throw new InvalidDataException("Runtime package uses an unsupported Zip64 extra field.");
            cursor += length;
        }
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name.Length > MaximumPathCharacters
            || name[0] == '/'
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal)
            || name.Contains('\0', StringComparison.Ordinal)
            || name.Split('/', StringSplitOptions.None).Any(segment => segment is "" or "." or "..")
                && !name.EndsWith('/'))
        {
            throw new InvalidDataException("Runtime package contains an unsafe entry path.");
        }

        var segments = name.Split('/', StringSplitOptions.None);
        var count = name.EndsWith('/') ? segments.Length - 1 : segments.Length;
        if (count <= 0 || segments.Take(count).Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Runtime package contains an unsafe entry path.");
    }

    private static bool IsReparseEntry(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        return unixMode == unixSymbolicLink
            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static void ReadEntryExactly(ZipArchiveEntry entry, RuntimePackageSpec package)
    {
        using var input = entry.Open();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var actual = 0L;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                actual = checked(actual + read);
                if (actual > entry.Length || actual > MaximumEntryBytes)
                    throw new InvalidDataException($"Runtime package entry expanded beyond its declaration: {package.LibraryName}");
            }
            if (actual != entry.Length)
                throw new InvalidDataException($"Runtime package entry length does not match its declaration: {package.LibraryName}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void VerifyNuspec(ZipArchiveEntry entry, RuntimePackageSpec package)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 256 * 1024
        };
        using var input = entry.Open();
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var metadata = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
        var id = metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == "id")?.Value;
        var version = metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == "version")?.Value;
        if (!string.Equals(id, package.Id, StringComparison.Ordinal)
            || !string.Equals(version, package.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Runtime package nuspec identity mismatch: {package.LibraryName}");
        }
    }

    private static byte[] ParseHashSidecar(byte[] bytes)
    {
        if (bytes.Length != 88 || bytes.Any(value => value > 0x7f))
            throw new InvalidDataException("Runtime package SHA-512 sidecar is not one exact base64 digest.");
        try
        {
            var digest = Convert.FromBase64String(Encoding.ASCII.GetString(bytes));
            if (digest.Length != SHA512.HashSizeInBytes)
                throw new InvalidDataException("Runtime package SHA-512 sidecar has an invalid digest length.");
            return digest;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Runtime package SHA-512 sidecar is malformed.", ex);
        }
    }

    private static byte[] ParseBase64Sha512(string value, string label)
    {
        if (value.Length != 88 || value.Any(character => character > 0x7f))
            throw new InvalidDataException($"Runtime package {label} is not one exact base64 digest.");
        try
        {
            var digest = Convert.FromBase64String(value);
            if (digest.Length != SHA512.HashSizeInBytes)
                throw new InvalidDataException($"Runtime package {label} has an invalid digest length.");
            return digest;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"Runtime package {label} is malformed.", ex);
        }
    }

    private static string ReadCacheMetadata(string path)
    {
        var bytes = ReadSmallRegularFile(path, 4096);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Runtime package cache metadata is not an object.");
        var names = root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.SetEquals(["version", "contentHash", "source"])
            || root.GetProperty("version").GetInt32() != 2
            || root.GetProperty("source").GetString() != "https://api.nuget.org/v3/index.json")
        {
            throw new InvalidDataException("Runtime package cache metadata has an unexpected shape or source.");
        }

        var contentHash = root.GetProperty("contentHash").GetString();
        if (string.IsNullOrEmpty(contentHash))
            throw new InvalidDataException("Runtime package cache metadata has no content hash.");
        try
        {
            if (Convert.FromBase64String(contentHash).Length != SHA512.HashSizeInBytes)
                throw new InvalidDataException("Runtime package cache metadata content hash has an invalid length.");
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Runtime package cache metadata content hash is malformed.", ex);
        }
        return contentHash;
    }

    private static byte[] ReadSmallRegularFile(string path, int maximumBytes)
    {
        path = Path.GetFullPath(path);
        AssertNoReparseComponents(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        AssertRegularFile(path);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Runtime package sidecar or metadata has an invalid size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        AssertRegularFile(path);
        return bytes;
    }

    private static void AssertExactFeed(string root, RepositoryLayout repository)
    {
        repository.AssertNoReparseTree(root);
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();
        var expected = Packages.Select(package => package.FeedFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (directories.Length != 0 || !expected.SetEquals(files))
            throw new InvalidDataException("The run-owned runtime feed does not contain exactly the verified packages.");
    }

    private static bool IsLexicallyInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void AssertNoReparseComponents(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            throw new InvalidDataException("Runtime package path has no filesystem root.");
        var cursor = root;
        if (Directory.Exists(cursor)) AssertNoReparse(cursor);
        var relative = Path.GetRelativePath(root, full);
        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, part);
            if (File.Exists(cursor) || Directory.Exists(cursor)) AssertNoReparse(cursor);
        }
    }

    private static void AssertRegularFile(string path)
    {
        if (!File.Exists(path) || Directory.Exists(path))
            throw new FileNotFoundException("Required runtime package cache file is missing.", path);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Runtime package cache input is not a regular file.");
    }

    private static void AssertNoReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Runtime package cache path contains a reparse point.");
    }

    private sealed record RuntimePackageSpec(
        string Id,
        string Version,
        string ExpectedRawPackageHash,
        string ExpectedContentHash)
    {
        public string LibraryName => $"{Id}/{Version}";
        public string FeedFileName => $"{Id.ToLowerInvariant()}.{Version}.nupkg";
        public string NuspecFileName => $"{Id}.nuspec";
    }

    private sealed class VerifiedCandidate : IDisposable
    {
        private readonly FileStream source;
        private readonly byte[] rawHash;

        public VerifiedCandidate(
            RuntimePackageSpec package,
            FileStream source,
            byte[] rawHash,
            string contentHash)
        {
            Package = package;
            this.source = source;
            this.rawHash = rawHash;
            ContentHash = contentHash;
        }

        public RuntimePackageSpec Package { get; }
        public string SourcePath => source.Name;
        public long Length => source.Length;
        public string ContentHash { get; }

        public void CopyTo(string destination)
        {
            source.Position = 0;
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            using var copied = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            var copiedHash = SHA512.HashData(copied);
            if (!CryptographicOperations.FixedTimeEquals(copiedHash, rawHash))
                throw new InvalidDataException($"Copied runtime package hash mismatch: {Package.LibraryName}");
        }

        public void Dispose() => source.Dispose();
    }
}
