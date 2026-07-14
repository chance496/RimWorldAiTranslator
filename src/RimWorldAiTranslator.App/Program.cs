using RimWorldAiTranslator.Core.Diagnostics;
using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Logging;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.App;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main()
    {
        StartupBootstrapForm? bootstrap = null;
        SingleInstanceLease? instanceLease = null;
        string? startupDataRoot = null;
        Exception? startupLogFailure = null;
        var exitCode = 0;
        try
        {
            ApplicationConfiguration.Initialize();
            var dataRoot = new AppDataPaths(
                Environment.GetEnvironmentVariable("RIMWORLD_TRANSLATOR_DATA_ROOT")).Root;
            startupDataRoot = dataRoot;
            if (!SingleInstanceLease.TryAcquire(dataRoot, out instanceLease))
            {
                MessageBox.Show(
                    "동일한 데이터 폴더를 사용하는 RimWorld AI Translator가 이미 실행 중입니다.\n\n"
                    + "먼저 실행 중인 창을 닫은 뒤 다시 시도하세요.",
                    "RimWorld AI Translator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button1);
                return 2;
            }

            bootstrap = new StartupBootstrapForm(
                CreateStartupStateAsync,
                startupFailurePresenter: exception => PresentStartupFailure(exception, startupLogFailure),
                startupFailureRecorder: exception =>
                    startupLogFailure = RecordStartupFailure(dataRoot, exception));
            Application.Run(bootstrap);
            if (bootstrap.StartupFailed) exitCode = 1;
        }
        catch (Exception exception)
        {
            exitCode = 1;
            startupLogFailure = startupDataRoot is null
                ? new InvalidOperationException("Startup data root unavailable.")
                : RecordStartupFailure(startupDataRoot, exception);
            PresentStartupFailure(exception, startupLogFailure);
        }
        finally
        {
            if (bootstrap is not null) await bootstrap.DisposeAfterRunAsync().ConfigureAwait(false);
            instanceLease?.Dispose();
        }

        return exitCode;
    }

    internal static Exception? RecordStartupFailure(string dataRoot, Exception exception)
    {
        try
        {
            var paths = new AppDataPaths(dataRoot);
            paths.EnsureExists();
            var logPath = Path.Combine(paths.Logs, "startup-failures.log");
            var safeMessage = AppLogger.Redact(
                OperationErrorPresentation.CreateLogMessage("프로그램 시작", exception));
            SafeLogFile.AppendUtf8Line(
                logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR {safeMessage}",
                flushToDisk: true);
            return null;
        }
        catch (Exception loggingException) when (loggingException is IOException
                                                 or UnauthorizedAccessException
                                                 or System.Security.SecurityException
                                                 or InvalidDataException
                                                 or ArgumentException
                                                 or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Persistent startup failure logging unavailable ({loggingException.GetType().Name}).");
            return loggingException;
        }
    }

    private static void PresentStartupFailure(Exception exception, Exception? loggingFailure)
    {
        try
        {
            var detail = OperationErrorPresentation.CreateUserDetail(exception);
            if (loggingFailure is not null)
                detail += "\n상세 시작 로그도 기록하지 못했습니다.";
            MessageBox.Show(
                "RimWorld AI Translator를 시작하지 못했습니다.\n\n" + detail,
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1);
        }
        catch (Exception dialogException)
        {
            System.Diagnostics.Debug.WriteLine($"Fatal startup dialog unavailable ({dialogException.GetType().Name}).");
            try
            {
                MessageBox.Show(
                    "RimWorld AI Translator를 시작하지 못했습니다. 입력 데이터와 저장 위치를 확인하세요.",
                    "RimWorld AI Translator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1);
            }
            catch (Exception fallbackException)
            {
                System.Diagnostics.Debug.WriteLine($"Fatal startup fallback unavailable ({fallbackException.GetType().Name}).");
            }
        }
    }

    private static Task<AppStartupState> CreateStartupStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataRoot = Environment.GetEnvironmentVariable("RIMWORLD_TRANSLATOR_DATA_ROOT");
        var discoveryRoot = Environment.GetEnvironmentVariable("RIMWORLD_TRANSLATOR_DISCOVERY_ROOT");
        var acknowledgementPath = Environment.GetEnvironmentVariable("RIMWORLD_TRANSLATOR_DISCOVERY_ACK_PATH");
        RimWorldModDiscoveryService? discovery = null;

        if (!string.IsNullOrWhiteSpace(discoveryRoot))
        {
            if (string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(acknowledgementPath))
                throw new InvalidDataException("Isolated discovery requires an explicit data root and acknowledgement path.");
            if (!Path.IsPathFullyQualified(dataRoot) || !Path.IsPathFullyQualified(acknowledgementPath))
                throw new InvalidDataException("Isolated discovery paths must be absolute.");

            dataRoot = PathSafety.Normalize(dataRoot);
            acknowledgementPath = Path.GetFullPath(acknowledgementPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(dataRoot)
                || !Path.GetDirectoryName(acknowledgementPath)!.Equals(dataRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(acknowledgementPath).Equals("isolated-discovery.ack", StringComparison.Ordinal)
                || File.Exists(acknowledgementPath))
            {
                throw new InvalidDataException("The isolated discovery acknowledgement path is not a new file directly inside the data root.");
            }

            PathSafety.EnsureNoReparsePoints(dataRoot, dataRoot);
            PathSafety.EnsureNoReparsePoints(acknowledgementPath, dataRoot);
            cancellationToken.ThrowIfCancellationRequested();
            discovery = RimWorldModDiscoveryService.CreateIsolated(discoveryRoot);
        }
        else if (!string.IsNullOrWhiteSpace(acknowledgementPath))
        {
            throw new InvalidDataException("An isolated discovery acknowledgement path was supplied without an isolated discovery root.");
        }

        return Task.FromResult(AppStartupState.Create(
            dataRoot,
            discovery,
            acknowledgementPath,
            cancellationToken));
    }
}
