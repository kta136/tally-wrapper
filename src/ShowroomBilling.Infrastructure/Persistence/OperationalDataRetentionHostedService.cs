using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Health;

namespace ShowroomBilling.Infrastructure.Persistence;

/// <summary>
/// Performs a bounded, one-shot startup purge of expired authentication sessions and
/// stale draft-lease rows. Bill, revision, posting, and audit history is deliberately
/// excluded: those records are operational and accounting evidence, not disposable cache.
/// </summary>
public sealed class OperationalDataRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    IStartupStatus startupStatus,
    ILogger<OperationalDataRetentionHostedService> logger) : DatabaseStartupTask(startupStatus)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    protected override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    protected override async Task RunAsync(CancellationToken timeboxed, CancellationToken hostStopping)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
        var cutoff = DateTimeOffset.UtcNow.Subtract(Retention);

        int sessionsDeleted;
        int leasesDeleted;
        if (db.UsesInMemoryProvider())
        {
            var sessions = await db.AdminSessions.Where(x => x.ExpiresAtUtc < cutoff).ToListAsync(timeboxed);
            var leases = await db.DraftEditLeases
                .Where(x => x.ExpiresAtUtc < cutoff || x.ReleasedAtUtc != null && x.ReleasedAtUtc < cutoff)
                .ToListAsync(timeboxed);
            db.AdminSessions.RemoveRange(sessions);
            db.DraftEditLeases.RemoveRange(leases);
            await db.SaveChangesAsync(timeboxed);
            sessionsDeleted = sessions.Count;
            leasesDeleted = leases.Count;
        }
        else
        {
            sessionsDeleted = await db.AdminSessions
                .Where(x => x.ExpiresAtUtc < cutoff)
                .ExecuteDeleteAsync(timeboxed);
            leasesDeleted = await db.DraftEditLeases
                .Where(x => x.ExpiresAtUtc < cutoff || x.ReleasedAtUtc != null && x.ReleasedAtUtc < cutoff)
                .ExecuteDeleteAsync(timeboxed);
        }

        if (sessionsDeleted > 0 || leasesDeleted > 0)
        {
            logger.LogInformation(
                "Operational retention removed {SessionCount} expired admin session(s) and {LeaseCount} stale draft lease(s).",
                sessionsDeleted,
                leasesDeleted);
        }
    }

    protected override void OnTimedOut() =>
        logger.LogWarning(
            "Operational retention timed out after {TimeoutSeconds}s; it will retry on the next API boot.",
            (int)Timeout.TotalSeconds);

    protected override void OnDatabaseNotReady(string reason) =>
        logger.LogInformation("Operational retention skipped: database not ready ({Reason}).", reason);

    protected override void OnFailed(Exception exception) =>
        logger.LogWarning(exception, "Operational retention failed; it will retry on the next API boot.");
}
