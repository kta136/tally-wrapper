using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Health;

namespace ShowroomBilling.Infrastructure.Persistence;

/// <summary>
/// Pre-pays the cold costs of the FIRST DB-backed request after API boot:
/// Neon TLS handshake, EF Core model build, query-plan JIT, and PG buffer-cache
/// warmup for the bills/numbering tables. Without this, the operator's first
/// SaveDraft of the session blocks for ~0.5–2 s while the connection pool warms
/// and EF builds its model on the request thread.
///
/// Runs once, in the background, after <see cref="IStartupStatus.WaitForDatabaseReadyAsync"/>
/// resolves so we don't hit an unmigrated schema. Any failure is logged at
/// warning and absorbed — warmup is best-effort and must never take the API
/// offline. Mirrors the fire-and-forget pattern used by
/// <see cref="ShowroomBilling.Infrastructure.Tally.StuckPostingRecoveryHostedService"/>.
///
/// Read-only by design: does NOT open a transaction, NOT call
/// <c>NumberingService.ReserveAsync</c>, and does NOT mutate <see cref="IStartupStatus"/>.
/// </summary>
public sealed class DatabaseWarmupHostedService(
    IServiceScopeFactory scopeFactory,
    IStartupStatus startupStatus,
    ILogger<DatabaseWarmupHostedService> logger) : IHostedService
{
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(5);
    private Task? _warmupTask;
    private CancellationTokenSource? _warmupCts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Defer to a worker so StartAsync doesn't add to API boot time.
        _warmupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _warmupTask = Task.Run(() => RunWarmupAsync(_warmupCts.Token), _warmupCts.Token);
        return Task.CompletedTask;
    }

    private async Task RunWarmupAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(WarmupTimeout);

        try
        {
            // Block until migrations have completed (or fail). A faulted wait
            // means the database isn't usable this boot — skip warmup.
            await startupStatus.WaitForDatabaseReadyAsync(cts.Token);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();

            // (1) Touches the same row NumberingService.ReserveAsync locks via FOR UPDATE.
            //     Pays Npgsql connection + TLS + EF model-build costs.
            _ = await db.InvoiceSequences
                .AsNoTracking()
                .Take(1)
                .ToListAsync(cts.Token);

            // (2) Primes PG's buffer cache for the partition the numbering
            //     forward-skip scan reads. Cheap (LIMIT 1, indexed) but lands
            //     the bills index pages in shared_buffers.
            _ = await db.Bills
                .AsNoTracking()
                .Where(b => b.InvoiceNumber != null)
                .OrderByDescending(b => b.CreatedAtUtc)
                .Select(b => b.InvoiceNumber)
                .Take(1)
                .ToListAsync(cts.Token);

            logger.LogInformation("Database warmup complete; first request will skip cold-pool latency.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Database warmup timed out after {TimeoutSeconds}s; first user request will pay the cold-pool cost.",
                (int)WarmupTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown — nothing to do.
        }
        catch (InvalidOperationException ex) when (ex.Message.Length > 0)
        {
            // WaitForDatabaseReadyAsync faulted because RecordDatabaseFailure
            // was called. DatabaseInitializationHostedService has already logged
            // the underlying cause; warmup just steps aside.
            logger.LogInformation("Database warmup skipped: database not ready ({Reason}).", ex.Message);
        }
        catch (Exception ex)
        {
            // Any other failure is non-fatal. Warmup is best-effort.
            logger.LogWarning(ex, "Database warmup failed; first user request will pay the cold-pool cost.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel any in-flight warmup and give it a moment to drain so we
        // don't tear down the DI scope mid-query.
        try { _warmupCts?.Cancel(); } catch { }
        if (_warmupTask is not null)
        {
            try
            {
                await _warmupTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch
            {
                // Don't block shutdown for a slow warmup.
            }
        }
        _warmupCts?.Dispose();
    }
}
