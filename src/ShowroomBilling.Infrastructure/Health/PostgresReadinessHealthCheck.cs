using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ShowroomBilling.Infrastructure.Health;

public sealed class PostgresReadinessHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            await using var connection = await dataSource.OpenConnectionAsync(cts.Token);

            await using var command = new NpgsqlCommand("select 1", connection);
            command.CommandTimeout = 2;
            await command.ExecuteScalarAsync(cts.Token);

            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", exception);
        }
    }
}
