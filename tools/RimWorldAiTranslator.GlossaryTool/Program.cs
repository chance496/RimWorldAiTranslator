namespace RimWorldAiTranslator.GlossaryTool;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            return args[0] switch
            {
                "build" => RunBuild(CommandLine.ParseBuild(args[1..]), cancellation.Token),
                "self-test" when args.Length == 1 => SelfTest.Run(cancellation.Token),
                "self-test" => throw new GlossaryToolException("self-test does not accept additional arguments."),
                _ => throw new GlossaryToolException("Unknown command. Use --help for supported commands.")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled at a safe checkpoint; no partial output was committed.");
            return 130;
        }
        catch (GlossaryToolException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unexpected failure ({exception.GetType().Name}); no partial output was committed.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int RunBuild(BuildOptions options, CancellationToken cancellationToken)
    {
        options = Discovery.ResolveIfRequested(options, cancellationToken);
        options.Validate();
        var result = new GlossaryBuilder(options).Build(cancellationToken);
        var json = OutputFormatter.FormatJson(result);
        var csv = OutputFormatter.FormatConflictsCsv(result.Conflicts);
        AtomicOutput.CommitPair(options.OutputPath, json, options.ConflictPath, csv, cancellationToken);

        Console.WriteLine("Glossary build completed.");
        Console.WriteLine($"Terms: {result.Document.Stats.Terms}");
        Console.WriteLine($"Conflicts: {result.Document.Stats.Conflicts}");
        Console.WriteLine($"Observations: {result.Document.Stats.Observations}");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            RimWorld AI Translator glossary tool

            Commands:
              build       Build deterministic glossary JSON and conflict CSV.
              self-test   Run synthetic, offline safety and determinism checks.

            Required build options:
              --output <path>
              --rimworld-data-root <path>       Unless --skip-official or --discover.

            Optional build options:
              --conflicts <path>                Defaults beside --output.
              --include-rmk
              --rmk-root <path>
              --workshop-root <path>
              --workshop-id <digits>            Repeatable.
              --game-version <major.minor>       Default: 1.6.
              --max-source-chars <number>        Default: 80.
              --min-rmk-occurrences <number>     Default: 1.
              --max-rmk-mods <number>            Default: 0 (unlimited).
              --include-sentences
              --skip-official
              --skip-rmk
              --def-field-rules <path>
              --discover                         Explicitly permit local Steam discovery.

            Patch XML is intentionally unsupported. Inputs are never auto-discovered
            unless --discover is present. Output metadata never contains source paths.
            """);
    }
}
