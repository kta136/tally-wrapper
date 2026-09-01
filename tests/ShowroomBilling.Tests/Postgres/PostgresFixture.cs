using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ShowroomBilling.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace ShowroomBilling.Tests.Postgres;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ExternalConnectionStringVariable = "SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION";
    private readonly SemaphoreSlim databaseGate = new(1, 1);
    private PostgreSqlContainer? container;
    private string? externalConnectionString;

    public async ValueTask InitializeAsync()
    {
        externalConnectionString = Environment.GetEnvironmentVariable(ExternalConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(externalConnectionString))
        {
            return;
        }

        container = new PostgreSqlBuilder("postgres:18")
            .Build();

        await container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        databaseGate.Dispose();

        if (container is not null)
        {
            await container.DisposeAsync().AsTask();
        }
    }

    public async Task<PostgresTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken = default)
        => await CreateDatabaseCoreAsync(targetMigration: null, cancellationToken);

    public async Task<PostgresTestDatabase> CreateDatabaseAtMigrationAsync(
        string targetMigration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMigration);
        return await CreateDatabaseCoreAsync(targetMigration, cancellationToken);
    }

    private async Task<PostgresTestDatabase> CreateDatabaseCoreAsync(
        string? targetMigration,
        CancellationToken cancellationToken)
    {
        var databaseName = $"tw_test_{Guid.NewGuid():N}";
        await databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var admin = new NpgsqlConnection(GetAdminConnectionString());
            await admin.OpenAsync(cancellationToken);
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            databaseGate.Release();
        }

        var connectionString = GetConnectionString(databaseName);
        var options = BuildOptions(connectionString);
        await using (var db = new ShowroomBillingDbContext(options))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(targetMigration, cancellationToken);
        }

        return new PostgresTestDatabase(this, databaseName, connectionString, options);
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        NpgsqlConnection.ClearAllPools();
        await databaseGate.WaitAsync();
        try
        {
            await using var admin = new NpgsqlConnection(GetAdminConnectionString());
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            databaseGate.Release();
        }
    }

    private string GetAdminConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder(GetBaseConnectionString())
        {
            Database = "postgres"
        };
        return builder.ConnectionString;
    }

    private string GetConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(GetBaseConnectionString())
        {
            Database = databaseName
        };
        return builder.ConnectionString;
    }

    private string GetBaseConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(externalConnectionString))
        {
            return externalConnectionString;
        }

        return GetContainer().GetConnectionString();
    }

    private PostgreSqlContainer GetContainer() =>
        container ?? throw new InvalidOperationException("Postgres container has not been started.");

    private static DbContextOptions<ShowroomBillingDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.SetPostgresVersion(18, 0))
            .Options;

    public sealed class PostgresTestDatabase : IAsyncDisposable
    {
        private readonly PostgresFixture owner;
        private readonly string databaseName;

        internal PostgresTestDatabase(
            PostgresFixture owner,
            string databaseName,
            string connectionString,
            DbContextOptions<ShowroomBillingDbContext> options)
        {
            this.owner = owner;
            this.databaseName = databaseName;
            ConnectionString = connectionString;
            Options = options;
        }

        public string ConnectionString { get; }

        public DbContextOptions<ShowroomBillingDbContext> Options { get; }

        public ShowroomBillingDbContext CreateContext() => new(Options);

        public async ValueTask DisposeAsync()
        {
            await owner.DropDatabaseAsync(databaseName);
        }
    }
}
