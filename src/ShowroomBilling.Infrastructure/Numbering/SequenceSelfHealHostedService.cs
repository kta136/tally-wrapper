using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Numbering;

/// <summary>
/// Runs once at API startup. Walks every <c>InvoiceSequences</c> row and calls
/// <see cref="NumberingSequenceReclaimer.ReclaimAsync"/> for each scope so any
/// <c>NextValue</c> that fell out of sync — typically because an older build
/// of <c>ChangeInvoiceNumberAsync</c> didn't reclaim on rename — gets rolled
/// back to <c>max(occupied)+1</c> on the next boot.
///
/// Idempotent: scopes already in sync are no-ops. Best-effort like the
/// stuck-posting recovery: failures log a warning but never throw, so a
/// transient DB hiccup can't take the API offline.
/// </summary>
public sealed class SequenceSelfHealHostedService(
    IServiceScopeFactory scopeFactory,
    IStartupStatus startupStatus,
    ILogger<SequenceSelfHealHostedService> logger) : DatabaseStartupTask(startupStatus)
{
    protected override TimeSpan Timeout => TimeSpan.FromSeconds(30);

    protected override async Task RunAsync(CancellationToken timeboxed, CancellationToken hostStopping)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();

        // Pull (showroom, fiscalYear) pairs from the sequence table; the
        // reclaimer scopes its FOR UPDATE by document type internally.
        var scopes = await db.InvoiceSequences
            .AsNoTracking()
            .Select(s => new { s.ShowroomId, s.FiscalYear })
            .Distinct()
            .ToListAsync(timeboxed);

        var moved = 0;
        foreach (var s in scopes)
        {
            try
            {
                // The reclaimer's FOR UPDATE only holds the sequence row
                // inside a transaction; without one a concurrent
                // reservation could allocate the core being rolled back.
                await using var transaction = await db.Database.BeginTransactionAsync(timeboxed);
                if (await NumberingSequenceReclaimer.ReclaimAsync(
                    db, s.ShowroomId, s.FiscalYear, "startup-self-heal", timeboxed))
                {
                    moved++;
                }
                await transaction.CommitAsync(timeboxed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                // One bad scope shouldn't block the others.
                logger.LogWarning(ex,
                    "Sequence self-heal failed for scope ({ShowroomId}, {FiscalYear}); other scopes will still be reconciled.",
                    s.ShowroomId, s.FiscalYear);
            }
        }

        if (moved > 0)
        {
            logger.LogInformation(
                "Sequence self-heal: rolled back NextValue in {Count} scope(s) to match remaining bills.",
                moved);
        }
    }

    protected override void OnTimedOut() =>
        logger.LogWarning("Sequence self-heal timed out after {Seconds}s; will retry next API restart.",
            (int)Timeout.TotalSeconds);

    protected override void OnDatabaseNotReady(string reason) =>
        logger.LogInformation("Sequence self-heal skipped: database not ready ({Reason}).", reason);

    protected override void OnFailed(Exception exception) =>
        logger.LogWarning(exception, "Sequence self-heal failed; will retry next API restart.");
}
