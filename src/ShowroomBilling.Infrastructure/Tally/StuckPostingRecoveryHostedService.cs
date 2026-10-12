using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Tally;

/// <summary>
/// In the sync-post model a bill is flipped to <c>posting</c> just before the
/// HTTP call to Tally. If the API process is killed mid-call, or cannot reach
/// the database to record the outcome once Tally has answered, the bill is
/// left stranded in <c>posting</c>. The write outcome is inherently ambiguous,
/// so this service moves such bills to <c>reconciliation_required</c> and
/// requires an operator to verify Tally before permitting another write.
///
/// It sweeps when the API starts and every <see cref="SweepInterval"/> after
/// that, so a stranded bill does not wait for the next restart. Another API
/// may share the database and be mid-push right now, so only claims older than
/// <see cref="AbandonedPostingRecovery.AbandonAfter"/> are recovered. A younger
/// claim is looked at again as soon as it is old enough; by then a live push
/// has settled and the conditional transition leaves it alone.
///
/// Local state repair only: no sweep contacts Tally. It is best effort, since
/// operators cannot push a stuck bill anyway (the claim in
/// <c>PushInternalAsync</c> short-circuits on <c>posting</c>): a sweep that
/// fails is logged and the next one tries again. Nothing is thrown into the
/// host.
/// </summary>
public sealed class StuckPostingRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    StartupStatus startupStatus,
    DatabaseSchemaState schemaState,
    ILogger<StuckPostingRecoveryHostedService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    // A sweep is a few small statements. This only keeps a connection that
    // hangs from holding up every sweep after it.
    private static readonly TimeSpan SweepTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ShortestDelay = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                // A fresh database has no tables until the initialiser has
                // migrated it.
                await startupStatus.WaitForDatabaseReadyAsync(stoppingToken);
            }
            catch (DatabaseNotReadyException)
            {
                // The initialiser has logged why. The database may answer by
                // the next sweep, so the sweeps go ahead all the same.
            }

            while (true)
            {
                var youngestClaimDue = await SweepAsync(stoppingToken);
                await Task.Delay(
                    DelayUntilNextSweep(youngestClaimDue, _timeProvider.GetUtcNow()),
                    _timeProvider,
                    stoppingToken);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: nothing to report.
        }
    }

    /// <summary>
    /// How long to wait after a sweep: the regular interval, or less when a
    /// claim the sweep left alone becomes old enough to recover before then.
    /// </summary>
    public static TimeSpan DelayUntilNextSweep(DateTimeOffset? youngestClaimDueAtUtc, DateTimeOffset nowUtc)
    {
        if (youngestClaimDueAtUtc is not { } due)
        {
            return SweepInterval;
        }

        // A second past the moment the claim is old enough.
        var untilDue = due - nowUtc + TimeSpan.FromSeconds(1);
        return untilDue < ShortestDelay ? ShortestDelay
            : untilDue > SweepInterval ? SweepInterval
            : untilDue;
    }

    /// <returns>When the youngest remaining claim becomes old enough to recover, if there is one.</returns>
    private async Task<DateTimeOffset?> SweepAsync(CancellationToken stoppingToken)
    {
        if (schemaState.PendingMigrationCount > 0)
        {
            // Bills cannot be read until the migrations have been applied, and
            // health says so on every poll. Nothing to add here.
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(SweepTimeout);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
            var (recovered, youngestClaimDue) = await AbandonedPostingRecovery.RecoverAsync(
                db, _timeProvider.GetUtcNow(), timeout.Token);
            if (recovered > 0)
            {
                logger.LogWarning(
                    "Recovery: {Count} bill(s) were stuck in 'posting' and now require Tally reconciliation.",
                    recovered);
            }

            return youngestClaimDue;
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Stuck-posting recovery failed; it runs again in {Minutes} minutes.",
                (int)SweepInterval.TotalMinutes);
            return null;
        }
    }
}
