using System.Diagnostics;

namespace RimWorldAiTranslator.Tooling;

internal sealed class ExternalProcessPolicy
{
    private static readonly HashSet<string> AllowedInheritedEnvironmentVariables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "NUMBER_OF_PROCESSORS",
            "OS",
            "PROCESSOR_ARCHITECTURE",
            "PROCESSOR_IDENTIFIER",
            "PROCESSOR_LEVEL",
            "PROCESSOR_REVISION"
        };

    private static readonly HashSet<string> AllowedExplicitEnvironmentVariables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DOTNET_NOLOGO",
            "DOTNET_CLI_TELEMETRY_OPTOUT",
            "DOTNET_CLI_HOME",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",
            "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE",
            "DOTNET_MULTILEVEL_LOOKUP",
            "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
            "TEMP",
            "TMP",
            "HOME",
            "USERPROFILE",
            "APPDATA",
            "LOCALAPPDATA",
            PackageLayout.DataRootEnvironmentVariable,
            PackageLayout.DiscoveryRootEnvironmentVariable,
            PackageLayout.DiscoveryAckEnvironmentVariable
        };

    private static readonly Dictionary<string, string> AllowedExplicitEnvironmentVariableValues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_CERT_REVOCATION_MODE"] = "offline"
        };

    private ExternalProcessPolicy(string dotnetHost)
    {
        DotnetHost = Path.GetFullPath(dotnetHost);
    }

    public string DotnetHost { get; }

    public static ExternalProcessPolicy FromCurrentDotnetHost()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            throw new InvalidOperationException("The current .NET host path could not be verified.");

        var hostName = Path.GetFileName(processPath) ?? string.Empty;
        if (!hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && !hostName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Packaging must be run by the current dotnet host. Invoke the tooling DLL with dotnet; the tooling project sets UseAppHost=false.");
        }

        return new ExternalProcessPolicy(processPath);
    }

    public async Task RunDotnetAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        await RunDotnetAsync(
            arguments,
            workingDirectory,
            environment,
            Console.Out,
            Console.Error,
            cancellationToken);
    }

    public async Task RunDotnetQuietAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        await RunDotnetAsync(
            arguments,
            workingDirectory,
            environment,
            TextWriter.Null,
            TextWriter.Null,
            cancellationToken);
    }

    public async Task<string> ReadDotnetSingleLineAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(DotnetHost, workingDirectory, environment);
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("The verified dotnet host did not start.");
        var stdout = ReadBoundedAsync(process.StandardOutput, 4096, cancellationToken);
        var stderr = PumpAsync(process.StandardError, TextWriter.Null, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await stderr;
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }

        var output = await stdout;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet exited with code {process.ExitCode}.");
        var lines = output.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
            throw new InvalidDataException("dotnet did not return exactly one bounded output line.");
        return lines[0];
    }

    private async Task RunDotnetAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(DotnetHost, workingDirectory, environment);
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("The verified dotnet host did not start.");

        var stdout = PumpAsync(process.StandardOutput, standardOutput, cancellationToken);
        var stderr = PumpAsync(process.StandardError, standardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet exited with code {process.ExitCode}.");
    }

    public static Process StartGeneratedApplication(
        string executable,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        WindowsKillOnCloseJob containment)
    {
        try
        {
            executable = Path.GetFullPath(executable);
            workingDirectory = Path.GetFullPath(workingDirectory);
            var executableName = Path.GetFileName(executable);
            if (!File.Exists(executable)
                || !string.Equals(executableName, "RimWorldAiTranslator.exe", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(executable), workingDirectory, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(workingDirectory)
                || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(workingDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"The generated application executable is not allowlisted: {executable}");
            }

            var startInfo = CreateStartInfo(executable, workingDirectory, environment);
            startInfo.CreateNoWindow = false;
            return WindowsSuspendedProcessLauncher.Start(
                startInfo.FileName,
                startInfo.WorkingDirectory,
                startInfo.Environment,
                containment);
        }
        catch
        {
            containment.Dispose();
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executable),
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false
        };

        var sanitized = BuildChildEnvironment(startInfo.Environment, environment);
        startInfo.Environment.Clear();
        foreach (var pair in sanitized) startInfo.Environment.Add(pair.Key, pair.Value);
        return startInfo;
    }

    internal static IReadOnlyDictionary<string, string> BuildChildEnvironment(
        IEnumerable<KeyValuePair<string, string?>> inherited,
        IReadOnlyDictionary<string, string?>? requested)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in inherited)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)
                || pair.Value is null
                || !AllowedInheritedEnvironmentVariables.Contains(pair.Key))
            {
                continue;
            }
            result[pair.Key] = pair.Value;
        }
        AddTrustedWindowsEnvironment(result);

        if (requested is null) return result;
        foreach (var pair in requested)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key.Contains('=')
                || pair.Key.Contains('\0'))
            {
                throw new InvalidDataException("Child environment contains an invalid variable name.");
            }
            if (pair.Value?.Contains('\0') == true)
                throw new InvalidDataException($"Child environment variable contains a NUL value: {pair.Key}");
            if (AllowedExplicitEnvironmentVariableValues.TryGetValue(pair.Key, out var allowedValue))
            {
                if (!string.Equals(pair.Value, allowedValue, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Child environment variable has a non-allowlisted value: {pair.Key}");
                }
                result[pair.Key] = allowedValue;
                continue;
            }
            if (!AllowedExplicitEnvironmentVariables.Contains(pair.Key))
                throw new InvalidOperationException($"Child environment variable is not explicitly allowlisted: {pair.Key}");

            if (pair.Value is null) result.Remove(pair.Key);
            else result[pair.Key] = pair.Value;
        }
        return result;
    }

    internal static string GetTrustedWindowsDirectoryForSelfTest() => GetTrustedWindowsDirectory();

    internal static string GetTrustedProgramFilesDirectoryForSelfTest() => GetTrustedProgramFilesDirectory();

    private static void AddTrustedWindowsEnvironment(IDictionary<string, string> environment)
    {
        if (!OperatingSystem.IsWindows()) return;
        var windowsDirectory = GetTrustedWindowsDirectory();
        var programFilesDirectory = GetTrustedProgramFilesDirectory();
        var root = Path.GetPathRoot(windowsDirectory);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new InvalidOperationException("The trusted Windows system drive could not be verified.");
        environment["SystemRoot"] = windowsDirectory;
        environment["windir"] = windowsDirectory;
        environment["SystemDrive"] = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        environment["ProgramFiles"] = programFilesDirectory;
    }

    private static string GetTrustedWindowsDirectory()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A trusted Windows directory is available only on Windows.");
        var path = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolderOption.DoNotVerify);
        return ValidateTrustedWindowsDirectory(path, "Windows");
    }

    private static string GetTrustedProgramFilesDirectory()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A trusted Program Files directory is available only on Windows.");
        var path = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolderOption.DoNotVerify);
        return ValidateTrustedWindowsDirectory(path, "Program Files");
    }

    private static string ValidateTrustedWindowsDirectory(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"The {description} directory could not be resolved from the operating system.");

        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)
            || root.Length != 3
            || !char.IsLetter(root[0])
            || root[1] != Path.VolumeSeparatorChar
            || (root[2] != Path.DirectorySeparatorChar && root[2] != Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException($"The {description} directory is not on a local Windows drive.");
        }

        var current = root;
        ValidateTrustedDirectoryComponent(current, description);
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
                throw new InvalidOperationException($"The {description} directory contains an unsafe path component.");
            current = Path.Combine(current, component);
            ValidateTrustedDirectoryComponent(current, description);
        }

        if (!string.Equals(
                current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                fullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {description} directory could not be verified exactly.");
        }
        return fullPath;
    }

    private static void ValidateTrustedDirectoryComponent(string path, string description)
    {
        if (!Directory.Exists(path))
            throw new InvalidOperationException($"The {description} directory is missing.");
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidOperationException($"The {description} directory is redirected or is not a regular directory.");
        }
    }

    private static async Task PumpAsync(StreamReader reader, TextWriter writer, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            await writer.WriteLineAsync(line);
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        var buffer = new char[256];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (builder.Length > maximumCharacters - read)
                throw new InvalidDataException("Child process output exceeded its bounded capture limit.");
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }
}
