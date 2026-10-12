using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Health;

/// <summary>
/// One check of the database for runtime health: does it answer, which
/// database is it, and has it had this build's migrations. Runtime health
/// keeps the answer for a few seconds in <see cref="DatabaseHealthCache"/>.
/// </summary>
public sealed class DatabaseHealthProbe(
    NpgsqlDataSource dataSource,
    PostgresReadinessHealthCheck readinessCheck,
    DatabaseSchemaState schemaState)
{
    // EF writes every command it runs to the log at Information. These run
    // every few seconds all day, which would be megabytes of the same three
    // statements, so they get a context of their own that has no logger.
    private readonly DbContextOptions<ShowroomBillingDbContext> _unlogged =
        new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.SetPostgresVersion(18, 0))
            .UseLoggerFactory(NullLoggerFactory.Instance)
            .Options;

    /// <returns>What was found, and the readiness result behind "reachable" for whoever logs it.</returns>
    public async Task<(DatabaseHealthReading Reading, HealthCheckResult Check)> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        // Asked directly, not through the health check service: that logs an
        // error for every check that fails, which during an outage is one
        // every few seconds. The caller logs the change once.
        var check = await readinessCheck.CheckAsync(cancellationToken);
        if (check.Status != HealthStatus.Healthy)
        {
            return (new DatabaseHealthReading(false, null, schemaState.PendingMigrationCount), check);
        }

        await using var db = new ShowroomBillingDbContext(_unlogged);
        var reading = new DatabaseHealthReading(
            true,
            await TryGetIdentityAsync(db, cancellationToken),
            await TryCountPendingMigrationsAsync(db, cancellationToken));
        return (reading, check);
    }

    private static async Task<string?> TryGetIdentityAsync(ShowroomBillingDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            return await db.DatabaseIdentity
                .AsNoTracking()
                .Where(x => x.Key == "environment")
                .Select(x => x.Value)
                .SingleOrDefaultAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The last known count if the database cannot be asked right now.</summary>
    private async Task<int?> TryCountPendingMigrationsAsync(ShowroomBillingDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            return await DatabaseSchemaState.CheckAsync(db, schemaState, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return schemaState.PendingMigrationCount;
        }
    }
}
