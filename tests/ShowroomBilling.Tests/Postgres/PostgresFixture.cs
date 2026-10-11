using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Tests.Postgres;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// Keeps the tests that race several connections against each other (the
/// classes in this folder) from running at the same time as one another.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection
{
    public const string Name = "Postgres";
}

/// <summary>
/// One PostgreSQL 18 server for the whole test run, shared by every test
/// class that asks for this fixture. Nothing is started until the first test
/// asks for a database, so a run that touches no database needs no server.
///
/// A test gets its own database: a copy of a template that was migrated once,
/// which takes a fraction of the time of migrating each one.
/// </summary>
public sealed class PostgresFixture : IAsyncDisposable
{
    private readonly Lazy<Task<PostgresTestServer>> server = new(static () => Task.Run(PostgresTestServer.StartAsync));
    private readonly Lazy<Task<string>> template;

    public PostgresFixture()
    {
        template = new Lazy<Task<string>>(() => Task.Run(CreateTemplateAsync));
    }

    public async ValueTask DisposeAsync()
    {
        if (!server.IsValueCreated)
        {
            return;
        }

        PostgresTestServer started;
        try
        {
            started = await server.Value;
        }
        catch
        {
            return;
        }

        if (template.IsValueCreated && template.Value.IsCompletedSuccessfully)
        {
            await DropDatabaseAsync(started, await template.Value);
        }

        await started.DisposeAsync();
    }

    /// <summary>A new database with every migration applied.</summary>
    public async Task<PostgresTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var started = await server.Value;
        var templateName = await template.Value;
        var databaseName = NewDatabaseName();
        await ExecuteOnServerAsync(
            started, $"CREATE DATABASE \"{databaseName}\" TEMPLATE \"{templateName}\"", cancellationToken);
        return new PostgresTestDatabase(started, databaseName);
    }

    /// <summary>
    /// <see cref="CreateDatabaseAsync"/> for callers that cannot await, such
    /// as the host builder of the contract tests' API.
    /// </summary>
    public PostgresTestDatabase CreateDatabase() =>
        Task.Run(() => CreateDatabaseAsync()).GetAwaiter().GetResult();

    /// <summary>A new database migrated only as far as <paramref name="targetMigration"/>.</summary>
    public async Task<PostgresTestDatabase> CreateDatabaseAtMigrationAsync(
        string targetMigration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMigration);

        var started = await server.Value;
        var databaseName = NewDatabaseName();
        await ExecuteOnServerAsync(started, $"CREATE DATABASE \"{databaseName}\"", cancellationToken);

        var database = new PostgresTestDatabase(started, databaseName);
        await using (var db = database.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(targetMigration, cancellationToken);
        }

        return database;
    }

    private async Task<string> CreateTemplateAsync()
    {
        var started = await server.Value;
        var templateName = $"tw_template_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync(started, $"CREATE DATABASE \"{templateName}\"", CancellationToken.None);

        // Unpooled, so no connection stays open on the template: PostgreSQL
        // refuses to copy a database that anyone is connected to.
        var options = BuildOptions(started.ConnectionStringFor(templateName, pooling: false));
        await using var db = new ShowroomBillingDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync();

        return templateName;
    }

    private static async Task DropDatabaseAsync(PostgresTestServer started, string databaseName)
    {
        // This database's idle connections only; other tests keep theirs.
        using (var pooled = new NpgsqlConnection(started.ConnectionStringFor(databaseName)))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        try
        {
            await ExecuteOnServerAsync(
                started, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", CancellationToken.None);
        }
        catch (NpgsqlException)
        {
            // A database left behind goes with the container. A failed drop
            // must not turn a passed test into a failed one.
        }
    }

    private static async Task ExecuteOnServerAsync(
        PostgresTestServer started,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(started.ConnectionStringFor("postgres"));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NewDatabaseName() => $"tw_test_{Guid.NewGuid():N}";

    private static DbContextOptions<ShowroomBillingDbContext> BuildOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<ShowroomBillingDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }

    private static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.SetPostgresVersion(18, 0));

    public sealed class PostgresTestDatabase : IAsyncDisposable, IDisposable
    {
        private readonly PostgresTestServer server;
        private readonly string databaseName;
        private int dropped;

        internal PostgresTestDatabase(PostgresTestServer server, string databaseName)
        {
            this.server = server;
            this.databaseName = databaseName;
            ConnectionString = server.ConnectionStringFor(databaseName);
            Options = BuildOptions(ConnectionString);
        }

        public string ConnectionString { get; }

        public DbContextOptions<ShowroomBillingDbContext> Options { get; }

        public ShowroomBillingDbContext CreateContext() => new(Options);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref dropped, 1) == 0)
            {
                await DropDatabaseAsync(server, databaseName);
            }
        }

        public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();
    }
}
