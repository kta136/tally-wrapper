using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ShowroomBilling.Infrastructure.Health;

public sealed class PostgresReadinessHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    // How long the database gets to answer. The wait is ended here and not
    // left to Npgsql: on a connection that has gone silent (the link dropped,
    // so nothing refuses and nothing resets) Npgsql answers a timeout by
    // sending a cancel request on a second connection and waiting for the
    // server to act on it, for as long as the connection's command timeout.
    // Measured: 32 seconds for a 2-second timeout, longer than a Desktop waits
    // for health at all.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) => CheckAsync(cancellationToken);

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var attempt = SelectOneAsync();
        try
        {
            await attempt.WaitAsync(Budget, cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception exception)
        {
            // An attempt left behind (too slow, or the caller went away) ends
            // when Npgsql gives up on it. Nobody is waiting for that failure.
            _ = attempt.ContinueWith(
                static abandoned => _ = abandoned.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

            return exception is TimeoutException
                ? HealthCheckResult.Unhealthy($"PostgreSQL did not answer within {Budget.TotalSeconds:0} seconds.", exception)
                : HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", exception);
        }
    }

    private async Task SelectOneAsync()
    {
        using var cts = new CancellationTokenSource(Budget);
        await using var connection = await dataSource.OpenConnectionAsync(cts.Token);

        await using var command = new NpgsqlCommand("select 1", connection);
        command.CommandTimeout = (int)Budget.TotalSeconds;
        await command.ExecuteScalarAsync(cts.Token);
    }
}
