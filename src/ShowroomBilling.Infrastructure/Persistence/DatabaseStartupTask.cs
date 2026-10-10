using Microsoft.Extensions.Hosting;
using ShowroomBilling.Application.Health;

namespace ShowroomBilling.Infrastructure.Persistence;

/// <summary>
/// Base for one-shot work the API does in the background at start-up, once
/// the database is ready. It adds nothing to API boot, is bounded by a
/// timeout, never throws into the host, and drains briefly on stop. See
/// CLAUDE.md, "Startup hosted-service resilience".
/// </summary>
public abstract class DatabaseStartupTask(IStartupStatus startupStatus) : IHostedService
{
    private static readonly TimeSpan StopDrain = TimeSpan.FromSeconds(2);

    private Task? _task;
    private CancellationTokenSource? _cts;

    /// <summary>Budget for the database wait plus <see cref="RunAsync"/>.</summary>
    protected abstract TimeSpan Timeout { get; }

    /// <summary>
    /// The work, started once migrations have completed.
    /// <paramref name="timeboxed"/> is cancelled at the timeout or on host
    /// stop; <paramref name="hostStopping"/> only on host stop, for work that
    /// deliberately outlives the timeout.
    /// </summary>
    protected abstract Task RunAsync(CancellationToken timeboxed, CancellationToken hostStopping);

    protected abstract void OnTimedOut();

    /// <summary>
    /// The database never became ready this boot. The database initialiser has
    /// already logged why; this task just steps aside.
    /// </summary>
    protected abstract void OnDatabaseNotReady(string reason);

    protected abstract void OnFailed(Exception exception);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _task = Task.Run(() => RunGuardedAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunGuardedAsync(CancellationToken hostStopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
        timeout.CancelAfter(Timeout);

        try
        {
            // Wait for migrations so a fresh database is not queried before
            // its schema exists. This faults if the initialiser failed.
            await startupStatus.WaitForDatabaseReadyAsync(timeout.Token);
            await RunAsync(timeout.Token, hostStopping);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !hostStopping.IsCancellationRequested)
        {
            OnTimedOut();
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: nothing to report.
        }
        catch (DatabaseNotReadyException ex)
        {
            OnDatabaseNotReady(ex.Message);
        }
        catch (Exception ex)
        {
            OnFailed(ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel in-flight work and give it a moment to finish, so the DI
        // scope is not torn down in the middle of a query.
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_task is not null)
        {
            try
            {
                await _task.WaitAsync(StopDrain, cancellationToken);
            }
            catch
            {
                // Do not hold up shutdown for slow start-up work.
            }
        }

        _cts?.Dispose();
    }
}
