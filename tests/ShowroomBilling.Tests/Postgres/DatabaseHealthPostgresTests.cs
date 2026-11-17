using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using ShowroomBilling.Infrastructure.Health;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The one check behind every health answer: does the database answer, which database is it, and
/// has it had this build's migrations.
/// <para>
/// The database is across the internet from the server. When the link drops, its connections do
/// not fail: they go silent. Health has to say so within seconds, because every counter asks every
/// 15 seconds and gives up on the answer after 5.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class DatabaseHealthPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_check_says_which_database_it_is_and_that_it_has_had_every_migration()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var health = Health(dataSource);
        Assert.Null(health.PendingMigrationCount);

        var reading = await health.CheckAsync();

        Assert.True(reading.Reachable);
        Assert.False(string.IsNullOrWhiteSpace(reading.Identity));
        Assert.Equal(0, reading.PendingMigrationCount);
        Assert.Equal(0, health.PendingMigrationCount);
    }

    /// <summary>
    /// An error PostgreSQL itself sends back is an answer. A database with no migration history has
    /// had none of them, and one with no identity table has no identity; neither is "unreachable".
    /// </summary>
    [Fact]
    public async Task A_database_without_the_two_tables_still_answers_and_is_behind_by_every_migration()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        int all;
        await using (var db = database.CreateContext())
        {
            all = db.Database.GetMigrations().Count();
            await db.Database.ExecuteSqlRawAsync(
                """DROP TABLE "__EFMigrationsHistory"; DROP TABLE public.database_identity;""",
                TestContext.Current.CancellationToken);
        }

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var reading = await Health(dataSource).CheckAsync();

        Assert.True(reading.Reachable);
        Assert.Null(reading.Identity);
        Assert.Equal(all, reading.PendingMigrationCount);
    }

    [Fact]
    public async Task One_check_is_shared_for_five_seconds_and_by_everyone_who_asks_while_it_is_out()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var health = Health(dataSource, clock);

        var first = await health.GetAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Same(first, await health.GetAsync(TestContext.Current.CancellationToken));

        // Stale now. Twenty callers at once get one new check between them, not twenty.
        clock.Advance(TimeSpan.FromSeconds(2));
        var together = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() => health.GetAsync(TestContext.Current.CancellationToken))));

        Assert.NotSame(first, together[0]);
        Assert.All(together, reading => Assert.Same(together[0], reading));
    }

    [Fact]
    public async Task A_silent_database_is_given_up_on_within_the_budget_and_found_again()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var relay = FreezableRelay.To(database.ConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(relay.ConnectionString);
        var health = Health(dataSource);

        Assert.True((await health.CheckAsync()).Reachable);

        // The link drops: connections stay open and nothing answers.
        relay.Freeze();
        var watch = Stopwatch.StartNew();
        var silent = await health.CheckAsync();
        watch.Stop();

        Assert.False(silent.Reachable);
        Assert.Null(silent.Identity);
        // What was last found is what is still reported.
        Assert.Equal(0, silent.PendingMigrationCount);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));

        // Asked again while it is still silent, the answer is as quick.
        watch.Restart();
        Assert.False((await health.CheckAsync()).Reachable);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));

        relay.Thaw();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(await health.CheckAsync()).Reachable)
        {
            Assert.True(DateTime.UtcNow < deadline, "The check did not find the database again.");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_fail_the_check_for_the_others()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var relay = FreezableRelay.To(database.ConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(relay.ConnectionString);
        var health = Health(dataSource);
        relay.Freeze();

        using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var gaveUp = health.GetAsync(impatient.Token);
        var waited = health.GetAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);
        Assert.False((await waited).Reachable);
    }

    private static DatabaseHealth Health(NpgsqlDataSource dataSource, TimeProvider? clock = null) =>
        new(dataSource, NullLogger<DatabaseHealth>.Instance, clock);
}
