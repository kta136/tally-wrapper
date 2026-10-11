using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Infrastructure.Configuration;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// 10 October 2026, replayed. A new server build started against a database
/// that was missing migrations, connected as a role that may not alter the
/// schema. The start-up migration was refused, the API stayed up, and every
/// bill request failed while health read "Healthy".
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaBehindPostgresTests(PostgresFixture fixture)
{
    // Two migrations short of the current schema.
    private const string Older = "20261009161104_PerformanceReadOptimizationsApplied";

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task A_service_role_that_may_not_migrate_leaves_the_api_up_and_the_shortfall_on_record()
    {
        await using var database = await fixture.CreateDatabaseAtMigrationAsync(Older);
        var role = $"tw_app_{Guid.NewGuid():N}";
        await ExecuteAsync(database.ConnectionString, $"""
            CREATE ROLE "{role}" LOGIN PASSWORD 'app';
            GRANT USAGE ON SCHEMA public TO "{role}";
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO "{role}";
            """);
        try
        {
            var asApplication = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Username = role,
                Password = "app",
                Pooling = false,
            }.ConnectionString;
            var services = new ServiceCollection();
            services.AddDbContext<ShowroomBillingDbContext>(options =>
                options.UseNpgsql(asApplication, npgsql => npgsql.SetPostgresVersion(18, 0)));
            await using var provider = services.BuildServiceProvider();
            var startupStatus = new StartupStatus();
            var schemaState = new DatabaseSchemaState();
            var service = new DatabaseInitializationHostedService(
                provider,
                Options.Create(new DatabaseOptions { AutoMigrateOnStartup = true }),
                new HostingEnvironment { EnvironmentName = "Production" },
                startupStatus,
                schemaState,
                NullLogger<DatabaseInitializationHostedService>.Instance);

            // The host must come up regardless.
            await service.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, schemaState.PendingMigrationCount);
            await Assert.ThrowsAsync<DatabaseNotReadyException>(
                () => startupStatus.WaitForDatabaseReadyAsync(TestContext.Current.CancellationToken));

            // Nothing was applied, and the build's own SQL is what PostgreSQL rejects.
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
            Assert.Equal(2, (await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).Count());
            Assert.NotNull(await ScalarAsync(database.ConnectionString, "SELECT to_regclass('public.draft_edit_leases')::text"));

            // A full health check finds the same shortfall...
            Assert.Equal(2, await DatabaseSchemaState.CheckAsync(db, new DatabaseSchemaState(), TestContext.Current.CancellationToken));

            // ...and sees it gone once the owner has applied the migrations by hand.
            await using (var asOwner = database.CreateContext())
            {
                await asOwner.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(0, await DatabaseSchemaState.CheckAsync(db, schemaState, TestContext.Current.CancellationToken));
            Assert.Equal(0, schemaState.PendingMigrationCount);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(database.ConnectionString, $"""DROP OWNED BY "{role}"; DROP ROLE "{role}";""");
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task An_up_to_date_database_reports_nothing_pending()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var state = new DatabaseSchemaState();
        Assert.Null(state.PendingMigrationCount);

        Assert.Equal(0, await DatabaseSchemaState.CheckAsync(db, state, TestContext.Current.CancellationToken));
        Assert.Equal(0, state.PendingMigrationCount);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
