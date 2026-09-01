using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
public sealed class Postgres18SchemaTests(PostgresFixture fixture)
{
    private const string PreviousMigration = "20260724065618_AllowWatermarkPrintAssets";

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task MigratedSchema_UsesPostgres18AndValidBillSearchIndexes()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "SHOW server_version_num";
            var serverVersion = Convert.ToInt32(await versionCommand.ExecuteScalarAsync());
            Assert.True(serverVersion >= 180000, $"Expected PostgreSQL 18+, got server_version_num={serverVersion}.");
        }

        Assert.Equal(2L, await CountValidSearchIndexesAsync(connection));
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task BillSearchIndexMigration_RecoversFromAPartialConcurrentRun()
    {
        await using var database = await fixture.CreateDatabaseAtMigrationAsync(PreviousMigration);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var extensionCommand = connection.CreateCommand())
            {
                extensionCommand.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_trgm;";
                await extensionCommand.ExecuteNonQueryAsync();
            }

            await using var partialIndexCommand = connection.CreateCommand();
            partialIndexCommand.CommandText =
                """
                CREATE INDEX CONCURRENTLY "IX_bills_InvoiceNumber"
                ON public.bills USING gin ("InvoiceNumber" gin_trgm_ops);
                """;
            await partialIndexCommand.ExecuteNonQueryAsync();
        }

        await using (var db = database.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync();
        }

        await using var verificationConnection = new NpgsqlConnection(database.ConnectionString);
        await verificationConnection.OpenAsync();
        Assert.Equal(2L, await CountValidSearchIndexesAsync(verificationConnection));
    }

    private static async Task<long> CountValidSearchIndexesAsync(NpgsqlConnection connection)
    {
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText =
            """
            SELECT count(*)
            FROM pg_index AS index_state
            JOIN pg_class AS index_class ON index_class.oid = index_state.indexrelid
            JOIN pg_namespace AS index_schema ON index_schema.oid = index_class.relnamespace
            JOIN pg_indexes AS index_definition
              ON index_definition.schemaname = index_schema.nspname
             AND index_definition.indexname = index_class.relname
            WHERE index_schema.nspname = 'public'
              AND index_class.relname IN ('IX_bills_InvoiceNumber', 'IX_bill_revisions_PartyName')
              AND index_state.indisready
              AND index_state.indisvalid
              AND index_definition.indexdef LIKE '%USING gin%'
              AND index_definition.indexdef LIKE '%gin_trgm_ops%';
            """;

        return Convert.ToInt64(await indexCommand.ExecuteScalarAsync());
    }
}
