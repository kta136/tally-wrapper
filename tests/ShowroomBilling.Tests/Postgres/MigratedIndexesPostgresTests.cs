using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
public sealed class MigratedIndexesPostgresTests(PostgresFixture fixture)
{
    /// <summary>
    /// Catches a migration EF cannot discover (missing [Migration] attribute
    /// or Designer file): the model would list indexes no database has.
    /// </summary>
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task EveryIndexInTheModelExistsAfterMigrating()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();

        var expected = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes()
                .Select(index => index.GetDatabaseName(StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema()))))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        var actual = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actual.Add(reader.GetString(0));
            }
        }

        Assert.Empty(expected.Except(actual).Order());
        Assert.Contains("IX_tally_ledger_snapshots_Name_trgm", actual);
    }
}
