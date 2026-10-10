using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Health;

namespace ShowroomBilling.Infrastructure.Persistence;

/// <summary>
/// Pre-pays the cold costs of the FIRST DB-backed request after API boot:
/// the Postgres TLS handshake, connection-pool start, query-plan JIT, and PG
/// buffer-cache warmup for the bills/numbering tables. Without this, the
/// operator's first SaveDraft of the session blocks for ~0.5–2 s. (The EF
/// model itself is precompiled, so that cost is no longer paid here.)
///
/// Runs once, in the background, after the database is ready. Any failure is
/// logged at warning and absorbed — warmup is best-effort and must never take
/// the API offline.
///
/// Read-only by design: does NOT open a transaction, NOT call
/// <c>NumberingService.ReserveAsync</c>, and does NOT mutate <see cref="IStartupStatus"/>.
/// </summary>
public sealed class DatabaseWarmupHostedService(
    IServiceScopeFactory scopeFactory,
    IStartupStatus startupStatus,
    ILogger<DatabaseWarmupHostedService> logger) : DatabaseStartupTask(startupStatus)
{
    protected override TimeSpan Timeout => TimeSpan.FromSeconds(5);

    protected override async Task RunAsync(CancellationToken timeboxed, CancellationToken hostStopping)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();

        // (1) Touches the same row NumberingService.ReserveAsync locks via FOR UPDATE.
        //     Pays the Npgsql connection + TLS costs.
        _ = await db.InvoiceSequences
            .AsNoTracking()
            .Take(1)
            .ToListAsync(timeboxed);

        // (2) Primes PG's buffer cache for the partition the numbering
        //     forward-skip scan reads. Cheap (LIMIT 1, indexed) but lands
        //     the bills index pages in shared_buffers.
        _ = await db.Bills
            .AsNoTracking()
            .Where(b => b.InvoiceNumber != null)
            .OrderByDescending(b => b.CreatedAtUtc)
            .Select(b => b.InvoiceNumber)
            .Take(1)
            .ToListAsync(timeboxed);

        logger.LogInformation("Database warmup complete; first request will skip cold-pool latency.");
    }

    protected override void OnTimedOut() =>
        logger.LogWarning(
            "Database warmup timed out after {TimeoutSeconds}s; first user request will pay the cold-pool cost.",
            (int)Timeout.TotalSeconds);

    protected override void OnDatabaseNotReady(string reason) =>
        logger.LogInformation("Database warmup skipped: database not ready ({Reason}).", reason);

    protected override void OnFailed(Exception exception) =>
        logger.LogWarning(exception, "Database warmup failed; first user request will pay the cold-pool cost.");
}
