using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Infrastructure.Health;
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
/// Recovery is best-effort and runs in the background: operators cannot push a
/// stuck bill anyway (the claim in <c>PushInternalAsync</c> short-circuits on
/// <c>posting</c>), so a short window before it has run is harmless. Any
/// failure is logged, never thrown.
/// </summary>
public sealed class StuckPostingRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    StartupStatus startupStatus,
    ILogger<StuckPostingRecoveryHostedService> logger) : DatabaseStartupTask(startupStatus)
{
    protected override TimeSpan Timeout => TimeSpan.FromSeconds(30);

    protected override async Task RunAsync(CancellationToken timeboxed, CancellationToken hostStopping)
    {
        var nextCheck = await RecoverOnceAsync(timeboxed);

        // Re-checking young claims waits minutes by design, so it runs on the
        // host token, outside the start-up timeout.
        await WatchYoungClaimsAsync(nextCheck, hostStopping);
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
                nextCheck = await RecoverOnceAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Deferred stuck-posting recovery failed; will retry on next API restart.");
                return;
            }
        }
    }

    /// <returns>When the youngest remaining claim becomes old enough to recover, if any.</returns>
    private async Task<DateTimeOffset?> RecoverOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
        var (recovered, nextCheck) = await AbandonedPostingRecovery.RecoverAsync(db, DateTimeOffset.UtcNow, cancellationToken);
        if (recovered > 0)
        {
            logger.LogWarning(
                "Recovery: {Count} bill(s) were stuck in 'posting' and now require Tally reconciliation.",
                recovered);
        }

        return nextCheck;
    }

    protected override void OnTimedOut() =>
        logger.LogWarning(
            "Stuck-posting recovery timed out after {Seconds}s; will retry on next API restart.",
            (int)Timeout.TotalSeconds);

    protected override void OnDatabaseNotReady(string reason)
    {
        // The database initialiser has already logged why.
    }

    protected override void OnFailed(Exception exception) =>
        logger.LogWarning(exception, "Stuck-posting recovery failed; will retry on next API restart.");
}
