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
    ILogger<DatabaseInitializationHostedService> logger) : IHostedService
{
    // Bounded so a slow / unreachable Postgres can't hold the API's startup
    // hostage indefinitely. Beyond this we record a degraded status and
    // continue — `/api/health/startup` will surface the failure to operators.
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

        try
        {
            logger.LogInformation(
                "Applying database migrations on startup for {EnvironmentName} (timeout {TimeoutSeconds}s).",
                hostEnvironment.EnvironmentName,
                (int)StartupTimeout.TotalSeconds);

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cts.Token);
            if (!pendingMigrations.Any())
            {
                logger.LogInformation("No pending database migrations found.");
                startupStatus.RecordDatabaseReady();
                return;
            }

            await dbContext.Database.MigrateAsync(cts.Token);
            startupStatus.RecordDatabaseReady();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var msg = $"Database migration on startup timed out after {(int)StartupTimeout.TotalSeconds}s. API will remain online but DB-backed features stay unavailable until PostgreSQL is reachable.";
            logger.LogWarning(msg);
            startupStatus.RecordDatabaseFailure(msg);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database migration on startup failed. API will remain online but DB-backed features stay unavailable until PostgreSQL is reachable.");
            startupStatus.RecordDatabaseFailure(exception.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
