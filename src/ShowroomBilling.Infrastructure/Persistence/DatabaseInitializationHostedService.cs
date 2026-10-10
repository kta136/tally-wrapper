using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Infrastructure.Configuration;

namespace ShowroomBilling.Infrastructure.Persistence;

public sealed class DatabaseInitializationHostedService(
    IServiceProvider serviceProvider,
    IOptions<DatabaseOptions> databaseOptions,
    IHostEnvironment hostEnvironment,
    IStartupStatus startupStatus,
    IDatabaseSchemaState schemaState,
    ILogger<DatabaseInitializationHostedService> logger) : IHostedService
{
    // Bounded so a slow / unreachable Postgres can't hold the API's startup
    // hostage indefinitely. Beyond this the failure is logged, the other
    // start-up tasks are told to step aside, and the API carries on.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!databaseOptions.Value.AutoMigrateOnStartup)
        {
            logger.LogInformation("Database auto-migration is disabled.");
            // Treat "we deliberately didn't migrate" as ready from the API's
            // perspective. If the DB is actually broken, the first real query
            // will surface that through the regular request path.
            startupStatus.RecordDatabaseReady();
            return;
        }

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(StartupTimeout);

        var pendingCount = 0;
        try
        {
            logger.LogInformation(
                "Applying database migrations on startup for {EnvironmentName} (timeout {TimeoutSeconds}s).",
                hostEnvironment.EnvironmentName,
                (int)StartupTimeout.TotalSeconds);

            var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync(cts.Token)).ToList();
            pendingCount = pendingMigrations.Count;
            if (pendingCount == 0)
            {
                logger.LogInformation("No pending database migrations found.");
                schemaState.Record(0);
                startupStatus.RecordDatabaseReady();
                return;
            }

            await dbContext.Database.MigrateAsync(cts.Token);
            schemaState.Record(0);
            startupStatus.RecordDatabaseReady();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var msg = $"Database migration on startup timed out after {(int)StartupTimeout.TotalSeconds}s. API will remain online but DB-backed features stay unavailable until PostgreSQL is reachable.";
            logger.LogWarning(msg);
            RecordPending(pendingCount);
            startupStatus.RecordDatabaseFailure(msg);
        }
        catch (Exception exception)
        {
            // Not only "PostgreSQL is down": on production this service's
            // database role may not alter the schema, so its attempt is refused
            // (SQLSTATE 42501) and the migrations have to be applied by hand.
            logger.LogWarning(
                exception,
                "Database migration on startup failed with {PendingCount} migration(s) pending. The API stays online, but bill and numbering requests fail until they are applied. If this service's database role may not alter the schema, apply them by hand as the role that owns the tables.",
                pendingCount);
            RecordPending(pendingCount);
            startupStatus.RecordDatabaseFailure(exception.Message);
        }
    }

    /// <summary>
    /// What was pending stays pending when the attempt failed. Zero means the
    /// failure came before the database could be asked, so nothing is known.
    /// </summary>
    private void RecordPending(int pendingCount)
    {
        if (pendingCount > 0)
        {
            schemaState.Record(pendingCount);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
