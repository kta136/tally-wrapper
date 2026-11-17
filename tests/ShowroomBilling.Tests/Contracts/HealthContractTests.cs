using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

[Trait("Category", "Postgres")]
public sealed class HealthContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Health_answers_for_the_database_and_the_lists_in_one_call()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/health", TestContext.Current.CancellationToken);
        var health = json.Deserialize<HealthResponse>(JsonSerializerOptions.Web)!;

        Assert.Equal(["database", "masters"], json.EnumerateObject().Select(property => property.Name).Order());
        Assert.True(health.Database.DatabaseReachable);
        Assert.Equal("Healthy", health.Database.Status);
        Assert.Equal(0, health.Database.PendingMigrationCount);
        Assert.NotNull(health.Database.DatabaseIdentity);
        Assert.Contains("PostgreSQL is reachable", health.Database.Message);
        Assert.NotNull(health.Masters);
    }

    [Fact]
    public async Task The_lists_are_left_out_for_a_caller_that_does_not_want_them()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<HealthResponse>("/api/health?masters=false", TestContext.Current.CancellationToken);

        Assert.True(health!.Database.DatabaseReachable);
        Assert.Null(health.Masters);
    }

    /// <summary>
    /// What a Desktop built before <c>GET /api/health</c> asks on every poll, and from Settings →
    /// Server. They are answered as before until no counter runs such a build.
    /// </summary>
    [Fact]
    public async Task The_addresses_an_older_Desktop_asks_are_still_answered()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();

        // forceDatabase is what such a Desktop sends so that a still older server checks; ignored here.
        foreach (var uri in new[] { "/api/runtime/health", "/api/runtime/health?forceDatabase=true" })
        {
            var runtime = await client.GetFromJsonAsync<RuntimeHealthResponse>(uri, TestContext.Current.CancellationToken);
            Assert.True(runtime!.DatabaseReachable);
            Assert.Equal("Healthy", runtime.Status);
            Assert.Equal(0, runtime.PendingMigrationCount);
        }

        Assert.NotNull(await client.GetFromJsonAsync<MasterFreshnessSummaryResponse>(
            "/api/health/masters", TestContext.Current.CancellationToken));

        using var ready = await client.GetAsync("/api/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Health_writes_nothing_to_the_log_while_the_database_answers()
    {
        var log = new RecordingLoggerProvider();
        await using var factory = new TestApiFactory(postgres);
        var client = factory
            .WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(log)))
            .CreateClient();

        var database = await GetDatabaseAsync(client);

        Assert.True(database.DatabaseReachable);
        Assert.Equal(0, database.PendingMigrationCount);
        Assert.NotNull(database.DatabaseIdentity);
        // Every counter asks for health every 15 seconds, so a line or two per check is megabytes
        // a day. The check's two statements must not reach the log.
        Assert.DoesNotContain(log.Entries, entry =>
            entry.Message.Contains("database_identity") || entry.Message.Contains("__EFMigrationsHistory"));
    }

    [Fact]
    public async Task Health_says_within_seconds_that_a_silent_database_is_not_answering_and_sees_it_come_back()
    {
        var log = new RecordingLoggerProvider();
        await using var factory = new TestApiFactory(postgres);
        await using var relay = FreezableRelay.To(factory.DatabaseConnectionString);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = factory.WithWebHostBuilder(builder => builder
            .ConfigureLogging(logging => logging.AddProvider(log))
            .ConfigureTestServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton(_ => NpgsqlDataSource.Create(relay.ConnectionString));
                services.RemoveAll<DatabaseHealth>();
                services.AddSingleton(provider => new DatabaseHealth(
                    provider.GetRequiredService<NpgsqlDataSource>(),
                    provider.GetRequiredService<ILogger<DatabaseHealth>>(),
                    clock));
            })).CreateClient();

        Assert.True((await GetDatabaseAsync(client)).DatabaseReachable);

        // The link drops: connections stay open and nothing answers.
        relay.Freeze();
        clock.Advance(TimeSpan.FromSeconds(6));
        var watch = Stopwatch.StartNew();
        // Asked for the lists as well: they are not read from a database that is not answering,
        // or this answer would wait on it.
        var silent = await client.GetFromJsonAsync<HealthResponse>("/api/health", TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.False(silent!.Database.DatabaseReachable);
        Assert.Equal("Unhealthy", silent.Database.Status);
        Assert.Null(silent.Masters);
        // A Desktop gives up on the answer after 5 seconds.
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4.5));

        // The other counters share that check for the next 5 seconds.
        watch.Restart();
        Assert.False((await GetDatabaseAsync(client)).DatabaseReachable);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // An older Desktop's readiness question gets the same answer.
        using (var ready = await client.GetAsync("/api/health/ready", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        }

        // The next check finds it still silent. The log has said so already.
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.False((await GetDatabaseAsync(client)).DatabaseReachable);
        Assert.Single(log.Entries, entry => entry.Message.StartsWith("The database is not answering"));
        Assert.DoesNotContain(log.Entries, entry => entry.Message.StartsWith("The database is answering again"));

        relay.Thaw();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            if ((await GetDatabaseAsync(client)).DatabaseReachable)
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "Health did not see the database come back.");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        Assert.Single(log.Entries, entry => entry.Message.StartsWith("The database is answering again"));
        Assert.Single(log.Entries, entry => entry.Message.StartsWith("The database is not answering"));
    }

    private static async Task<RuntimeHealthResponse> GetDatabaseAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<HealthResponse>("/api/health?masters=false", TestContext.Current.CancellationToken))!.Database;

    /// <summary>Everything the API logs, as the file log would get it.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            string category,
            ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
