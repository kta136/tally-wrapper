using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Tally;

/// <summary>
/// Runs at API startup. In the sync-post model a bill is flipped to <c>posting</c>
/// just before the HTTP call to Tally. If the API process is killed mid-call, the bill
/// is left stranded in <c>posting</c>. The write outcome is inherently ambiguous, so
/// this service moves such bills to <c>reconciliation_required</c> and requires an
/// operator to verify Tally before permitting another write.
///
/// Another counter's API may share the database and be mid-push right now, so
/// only claims older than <see cref="AbandonedPostingRecovery.AbandonAfter"/>
/// are recovered. Younger claims are re-checked once they are old enough; by
/// then a live push has settled and the conditional transition leaves it alone.
///
/// Recovery is best-effort: any failure is logged + recorded on
/// <see cref="IStartupStatus"/> but never throws, so a transient DB hiccup can't
/// take the API offline.
/// </summary>
public sealed class StuckPostingRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    IStartupStatus startupStatus,
    ILogger<StuckPostingRecoveryHostedService> logger) : IHostedService
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private Task? _recoveryTask;
    private CancellationTokenSource? _recoveryCts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Recovery used to run synchronously inside StartAsync, which serialised
        // it after DatabaseInitializationHostedService and could add up to 30 s
        // to API boot on top of the 15 s migration window. It's safe to defer:
        // - operators can't push a stuck bill anyway (the conditional UPDATE in
        //   PushInternalAsync short-circuits when state='posting'), so a brief
        //   window where recovery hasn't run yet is benign;
        // - the IStartupStatus snapshot continues to surface the "RecoveryRan"
        //   flag via /api/health/startup, so the Desktop's limited-mode banner
        //   still works.
        // We wait for the database to be ready first so a fresh DB whose
        // migrations are still applying doesn't trigger spurious recovery
        // failures against a missing schema.
        _recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _recoveryTask = Task.Run(() => RunRecoveryAsync(_recoveryCts.Token), _recoveryCts.Token);
        return Task.CompletedTask;
    }

    private async Task RunRecoveryAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(StartupTimeout);

        try
        {
            // Block until migrations have completed (or fail). This faults if
            // RecordDatabaseFailure was called, which we treat as "skip recovery
            // this boot — it'll re-run next time the API comes up healthy".
            await startupStatus.WaitForDatabaseReadyAsync(cts.Token);

            DateTimeOffset? nextCheck;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
                (var recovered, nextCheck) = await AbandonedPostingRecovery.RecoverAsync(db, DateTimeOffset.UtcNow, cts.Token);
                startupStatus.RecordRecoveryComplete(recovered);
                LogRecovered(recovered);
            }

            await WatchYoungClaimsAsync(nextCheck, cancellationToken);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var msg = $"Stuck-posting recovery timed out after {(int)StartupTimeout.TotalSeconds}s; will retry on next API restart.";
            logger.LogWarning(msg);
            startupStatus.RecordRecoveryFailure(msg);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown — nothing to record.
        }
        catch (InvalidOperationException ex) when (ex.Message.Length > 0)
        {
            // Faulted via WaitForDatabaseReadyAsync because RecordDatabaseFailure was called.
            // DatabaseInit has already logged the underlying cause.
            startupStatus.RecordRecoveryFailure($"Skipped: database not ready ({ex.Message}).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stuck-posting recovery failed; will retry on next API restart.");
            startupStatus.RecordRecoveryFailure(ex.Message);
        }
    }

    private async Task WatchYoungClaimsAsync(DateTimeOffset? nextCheck, CancellationToken cancellationToken)
    {
        while (nextCheck is { } due && !cancellationToken.IsCancellationRequested)
        {
            var delay = due - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay + TimeSpan.FromSeconds(1), cancellationToken);
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
                (var recovered, nextCheck) = await AbandonedPostingRecovery.RecoverAsync(db, DateTimeOffset.UtcNow, cancellationToken);
                LogRecovered(recovered);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Deferred stuck-posting recovery failed; will retry on next API restart.");
                return;
            }
        }
    }

    private void LogRecovered(int recovered)
    {
        if (recovered > 0)
        {
            logger.LogWarning(
                "Recovery: {Count} bill(s) were stuck in 'posting' and now require Tally reconciliation.",
                recovered);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel any in-flight recovery and give it a moment to drain so we
        // don't tear down the DI scope mid-query.
        try { _recoveryCts?.Cancel(); } catch { }
        if (_recoveryTask is not null)
        {
            try
            {
                await _recoveryTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch
            {
                // Don't block shutdown for a slow recovery.
            }
        }
        _recoveryCts?.Dispose();
    }
}
