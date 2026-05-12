using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// transient DB hiccup can't take the API offline. Defers behind
/// <see cref="IStartupStatus.WaitForDatabaseReadyAsync"/> so a fresh DB whose
/// migrations are still applying doesn't trigger spurious failures.
/// </summary>
public sealed class SequenceSelfHealHostedService(
    IServiceScopeFactory scopeFactory,
    IStartupStatus startupStatus,
    ILogger<SequenceSelfHealHostedService> logger) : IHostedService
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private Task? _task;
    private CancellationTokenSource? _cts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _task = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(StartupTimeout);

        try
        {
            await startupStatus.WaitForDatabaseReadyAsync(cts.Token);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();

            // Pull (showroom, fiscalYear) pairs from the sequence table; the
            // reclaimer scopes its FOR UPDATE by document type internally.
            var scopes = await db.InvoiceSequences
                .AsNoTracking()
                .Select(s => new { s.ShowroomId, s.FiscalYear })
                .Distinct()
                .ToListAsync(cts.Token);

            var moved = 0;
            foreach (var s in scopes)
            {
                try
                {
                    if (await NumberingSequenceReclaimer.ReclaimAsync(
                        db, s.ShowroomId, s.FiscalYear, "startup-self-heal", cts.Token))
                    {
                        moved++;
                    }
                }
                catch (Exception ex)
                {
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
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Sequence self-heal timed out after {Seconds}s; will retry next API restart.",
                (int)StartupTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown — nothing to log.
        }
        catch (InvalidOperationException ex) when (ex.Message.Length > 0)
        {
            // WaitForDatabaseReadyAsync faulted because RecordDatabaseFailure
            // was called. DatabaseInit has already surfaced the root cause.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sequence self-heal failed; will retry next API restart.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { }
        if (_task is not null)
        {
            try
            {
                await _task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch
            {
                // Don't block shutdown for a slow self-heal.
            }
        }
        _cts?.Dispose();
    }
}
