using RimWorldAiTranslator.Core.Discovery;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.App;

internal sealed class AppStartupState : IDisposable, IAsyncDisposable
{
    private const int OwnedByStartup = 0;
    private const int OwnedByMainForm = 1;
    private const int Disposed = 2;
    private int ownership = OwnedByStartup;

    private AppStartupState(
        AppServices services,
        ProjectStatsCacheDocument projectStats,
        string? isolationAcknowledgementPath)
    {
        Services = services;
        ProjectStats = projectStats;
        IsolationAcknowledgementPath = isolationAcknowledgementPath;
    }

    internal AppServices Services { get; }
    internal ProjectStatsCacheDocument ProjectStats { get; }
    internal string? IsolationAcknowledgementPath { get; }
    internal bool WasDisposedForTesting => Volatile.Read(ref ownership) == Disposed;

    internal static AppStartupState Create(
        string? dataRoot,
        RimWorldModDiscoveryService? discovery,
        string? isolationAcknowledgementPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppServices? services = null;
        try
        {
            services = new AppServices(
                dataRoot,
                discovery,
                loggerFactory: null,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var projectStats = services.ProjectStats.Load(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new AppStartupState(services, projectStats, isolationAcknowledgementPath);
        }
        catch
        {
            try { services?.Dispose(); }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Partial startup disposal failed ({exception.GetType().Name}).");
            }
            throw;
        }
    }

    internal void TransferOwnershipToMainForm()
    {
        if (Interlocked.CompareExchange(ref ownership, OwnedByMainForm, OwnedByStartup) != OwnedByStartup)
            throw new InvalidOperationException("Startup service ownership is no longer available.");
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref ownership, Disposed, OwnedByStartup) == OwnedByStartup)
            Services.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return Interlocked.CompareExchange(ref ownership, Disposed, OwnedByStartup) == OwnedByStartup
            ? Services.DisposeAsync()
            : ValueTask.CompletedTask;
    }
}
