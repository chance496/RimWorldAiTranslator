namespace RimWorldAiTranslator.Tooling;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var invocation = Invocation.Parse(args);
            if (invocation.ShowHelp)
            {
                Invocation.WriteHelp();
                return 0;
            }

            var repository = RepositoryLayout.Find(invocation.RepositoryRoot);
            return invocation.Command switch
            {
                "package" => await PackageCommand.RunAsync(repository, cancellation.Token),
                "verify-zero" => ZeroAudit.Run(repository, invocation.PackagePath),
                "self-test" => SelfTest.Run(repository),
                "generate-icon" => IconAssetGenerator.Run(repository),
                _ => throw new ToolUsageException($"Unknown command: {invocation.Command}")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (ToolUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Invocation.WriteHelp(Console.Error);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR ({ex.GetType().Name}): release tooling failed; inspect the bounded step output above.");
            return 1;
        }
    }
}

internal sealed record Invocation(
    string Command,
    string? RepositoryRoot,
    string? PackagePath,
    bool ShowHelp)
{
    public static Invocation Parse(string[] args)
    {
        if (args.Length == 0 || args is ["-h"] or ["--help"])
            return new Invocation(string.Empty, null, null, true);

        var command = args[0].Trim().ToLowerInvariant();
        if (command is not ("package" or "verify-zero" or "self-test" or "generate-icon"))
            throw new ToolUsageException($"Unknown command: {args[0]}");

        string? repositoryRoot = null;
        string? packagePath = null;
        for (var index = 1; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "-h" or "--help")
                return new Invocation(command, repositoryRoot, packagePath, true);

            if (option == "--repository-root")
            {
                repositoryRoot = ReadValue(args, ref index, option);
                continue;
            }

            if (option == "--package" && command == "verify-zero")
            {
                packagePath = ReadValue(args, ref index, option);
                continue;
            }

            throw new ToolUsageException($"Unsupported option for {command}: {option}");
        }

        return new Invocation(command, repositoryRoot, packagePath, false);
    }

    public static void WriteHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("RimWorld AI Translator release tooling");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  dotnet run --project tools/RimWorldAiTranslator.Tooling -- package [--repository-root PATH]");
        writer.WriteLine("  dotnet run --project tools/RimWorldAiTranslator.Tooling -- verify-zero [--repository-root PATH] [--package ZIP_OR_DIRECTORY]");
        writer.WriteLine("  dotnet run --project tools/RimWorldAiTranslator.Tooling -- self-test [--repository-root PATH]");
        writer.WriteLine("  dotnet run --project tools/RimWorldAiTranslator.Tooling -- generate-icon [--repository-root PATH]");
        writer.WriteLine();
        writer.WriteLine("package is fixed to Release, self-contained single-file win-x64, and always runs the console regression suite.");
        writer.WriteLine("verify-zero checks the active source tree and the expected package when --package is omitted.");
        writer.WriteLine("self-test runs offline synthetic security and recovery checks without packaging or network access.");
        writer.WriteLine("generate-icon deterministically rebuilds the fixed application ICO from its repository SVG.");
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ToolUsageException($"{option} requires a value.");
        return args[index];
    }
}

internal sealed class ToolUsageException(string message) : Exception(message);
