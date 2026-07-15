using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    ILogger<OperationalDataRetentionHostedService> logger) : IHostedService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private CancellationTokenSource? _cts;
    private Task? _task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _task = Task.Run(() => PurgeAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await startupStatus.WaitForDatabaseReadyAsync(timeout.Token);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
            var cutoff = DateTimeOffset.UtcNow.Subtract(Retention);

            int sessionsDeleted;
            int leasesDeleted;
            if (string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
            {
                var sessions = await db.AdminSessions.Where(x => x.ExpiresAtUtc < cutoff).ToListAsync(timeout.Token);
                var leases = await db.DraftEditLeases
                    .Where(x => x.ExpiresAtUtc < cutoff || x.ReleasedAtUtc != null && x.ReleasedAtUtc < cutoff)
                    .ToListAsync(timeout.Token);
                db.AdminSessions.RemoveRange(sessions);
                db.DraftEditLeases.RemoveRange(leases);
                await db.SaveChangesAsync(timeout.Token);
                sessionsDeleted = sessions.Count;
                leasesDeleted = leases.Count;
            }
            else
            {
                sessionsDeleted = await db.AdminSessions
                    .Where(x => x.ExpiresAtUtc < cutoff)
                    .ExecuteDeleteAsync(timeout.Token);
                leasesDeleted = await db.DraftEditLeases
                    .Where(x => x.ExpiresAtUtc < cutoff || x.ReleasedAtUtc != null && x.ReleasedAtUtc < cutoff)
                    .ExecuteDeleteAsync(timeout.Token);
            }

            if (sessionsDeleted > 0 || leasesDeleted > 0)
            {
                logger.LogInformation(
                    "Operational retention removed {SessionCount} expired admin session(s) and {LeaseCount} stale draft lease(s).",
                    sessionsDeleted,
                    leasesDeleted);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Operational retention timed out after {TimeoutSeconds}s; it will retry on the next API boot.", (int)Timeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException ex) when (ex.Message.Length > 0)
        {
            logger.LogInformation("Operational retention skipped: database not ready ({Reason}).", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Operational retention failed; it will retry on the next API boot.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { }
        if (_task is not null)
        {
            try { await _task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); } catch { }
        }
        _cts?.Dispose();
    }
}
