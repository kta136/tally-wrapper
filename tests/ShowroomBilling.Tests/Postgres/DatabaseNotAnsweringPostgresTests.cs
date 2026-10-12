using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ShowroomBilling.Infrastructure.Health;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The database is across the internet from the server. When the link drops,
/// its connections do not fail: they go silent. Health has to say so within
/// seconds, because every counter asks every 15 seconds and gives up on the
/// answer after 5.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class DatabaseNotAnsweringPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_readiness_check_gives_up_on_a_silent_database_within_its_budget_and_finds_it_again()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var relay = FreezableRelay.To(database.ConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(relay.ConnectionString);
        var check = new PostgresReadinessHealthCheck(dataSource);

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(check)).Status);

        relay.Freeze();
        var watch = Stopwatch.StartNew();
        var silent = await CheckAsync(check);
        watch.Stop();

        Assert.Equal(HealthStatus.Unhealthy, silent.Status);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));

        // Asked again while it is still silent, the answer is as quick.
        watch.Restart();
        Assert.Equal(HealthStatus.Unhealthy, (await CheckAsync(check)).Status);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));

        relay.Thaw();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await CheckAsync(check)).Status != HealthStatus.Healthy)
        {
            Assert.True(DateTime.UtcNow < deadline, "The check did not find the database again.");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private static Task<HealthCheckResult> CheckAsync(PostgresReadinessHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
}
