using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TestDatabase = ShowroomBilling.Tests.Postgres.PostgresFixture.PostgresTestDatabase;

namespace ShowroomBilling.Tests.Postgres;

internal static class MigrationSql
{
    internal static async Task MigrateAsync(TestDatabase database, string? target)
    {
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
        NpgsqlConnection.ClearAllPools();
    }

    internal static async Task ExecuteAsync(TestDatabase database, string sql, int? expectedRows = null)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = await command.ExecuteNonQueryAsync();
        if (expectedRows.HasValue)
        {
            Assert.Equal(expectedRows.Value, rows);
        }
    }

    internal static async Task<T> ScalarAsync<T>(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    internal static Task<bool> TableExistsAsync(TestDatabase database, string table) =>
        ScalarAsync<bool>(database, $"""SELECT to_regclass('public."{table}"') IS NOT NULL""");
}
