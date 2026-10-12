using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

[Trait("Category", "Postgres")]
public sealed class RuntimeHealthContractTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("/api/runtime/health")]
    // What a Desktop sends so that a server older than it checks too; ignored here.
    [InlineData("/api/runtime/health?forceDatabase=true")]
    public async Task RuntimeHealth_ChecksTheDatabaseOnEveryCall(string uri)
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<RuntimeHealthResponse>(uri);

        Assert.NotNull(response);
        Assert.True(response!.ApiAvailable);
        Assert.True(response.DatabaseReachable);
        Assert.Equal("Healthy", response.Status);
        Assert.Equal(0, response.PendingMigrationCount);
        Assert.Contains("PostgreSQL is reachable", response.Message);
    }

    [Fact]
    public async Task RuntimeHealth_WritesNothingToTheLogWhileTheDatabaseAnswers()
    {
        var log = new RecordingLoggerProvider();
        await using var factory = new TestApiFactory(postgres);
        var client = factory
            .WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(log)))
            .CreateClient();

        var response = await GetAsync(client);

        Assert.True(response.DatabaseReachable);
        Assert.Equal(0, response.PendingMigrationCount);
        Assert.NotNull(response.DatabaseIdentity);
        // EF writes every command it runs to the log. Every counter asks for
        // health every 15 seconds, so a few lines per check is megabytes a day.
        Assert.DoesNotContain(log.Entries, entry =>
            entry.Message.Contains("database_identity") || entry.Message.Contains("__EFMigrationsHistory"));
    }

    [Fact]
    public async Task RuntimeHealth_SaysWithinSecondsThatASilentDatabaseIsNotAnswering_AndSeesItComeBack()
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
                services.RemoveAll<DatabaseHealthCache>();
                services.AddSingleton(new DatabaseHealthCache(clock));
            })).CreateClient();

        Assert.True((await GetAsync(client)).DatabaseReachable);

        // The link drops: connections stay open and nothing answers.
        relay.Freeze();
        clock.Advance(TimeSpan.FromSeconds(6));
        var watch = Stopwatch.StartNew();
        var silent = await GetAsync(client);
        watch.Stop();

        Assert.False(silent.DatabaseReachable);
        Assert.Equal("Unhealthy", silent.Status);
        // A Desktop gives up on the answer after 5 seconds.
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4.5));

        // The other counters share that check for the next 5 seconds.
        watch.Restart();
        Assert.False((await GetAsync(client)).DatabaseReachable);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // The next check finds it still silent. The log has said so already.
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.False((await GetAsync(client)).DatabaseReachable);
        Assert.Single(log.Entries, entry => entry.Message.StartsWith("The database is not answering"));
        Assert.DoesNotContain(log.Entries, entry => entry.Message.StartsWith("The database is answering again"));

        relay.Thaw();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            if ((await GetAsync(client)).DatabaseReachable)
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "Health did not see the database come back.");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        Assert.Single(log.Entries, entry => entry.Message.StartsWith("The database is answering again"));
        Assert.Single(log.Entries, entry => entry.Message.StartsWith("The database is not answering"));
    }

    private static async Task<RuntimeHealthResponse> GetAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<RuntimeHealthResponse>("/api/runtime/health", TestContext.Current.CancellationToken))!;

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
