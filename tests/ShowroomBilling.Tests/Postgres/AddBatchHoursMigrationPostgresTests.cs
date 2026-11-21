using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Settings;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The migration that adds <c>cloud_settings."BatchHoursJson"</c> only adds: the settings row that
/// is already there reads as no hours, a build from before the column can still write the table,
/// and applying it to a database that already has the column does not fail.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class AddBatchHoursMigrationPostgresTests(PostgresFixture fixture)
{
    private const string Previous = "20261115153558_DropShowroomAndDocumentType";
    private const string Migration = "20261121110705_AddBatchHours";
    private const string Column = "BatchHoursJson";

    [Fact]
    public async Task The_settings_row_already_there_gets_the_column_holding_no_hours()
    {
        // The row is made first, by today's build, and the database then taken back to the
        // schema of the build before: what production holds on the day the migration is applied.
        await using var database = await fixture.CreateDatabaseAsync();
        await using (var db = database.CreateContext())
        {
            await new CloudSettingsService(db).UpdateBatchHoursAsync(
                new UpdateBatchHoursRequest(new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 0))));
        }

        await MigrateAsync(database, Previous);
        Assert.False(await ColumnExistsAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.cloud_settings"));

        await MigrateAsync(database, target: null);

        Assert.True(await ColumnExistsAsync(database));
        Assert.Equal("{}", await ScalarAsync<string>(database, $"""SELECT "{Column}" FROM public.cloud_settings"""));
        await using (var db = database.CreateContext())
        {
            var service = new CloudSettingsService(db);
            Assert.Equal(new BatchHoursSettings(), (await service.GetBatchHoursAsync()).Hours);
            // The rest of the row came through both ways.
            Assert.Equal("127.0.0.1", (await service.GetEffectiveSettingsAsync()).Settings.Connection.Host);
        }
    }

    [Fact]
    public async Task A_row_written_without_naming_the_column_gets_the_default()
    {
        // A server build from before the column does not name it when it writes the settings row.
        await using var database = await fixture.CreateDatabaseAsync();
        await using (var db = database.CreateContext())
        {
            await new CloudSettingsService(db).GetEffectiveSettingsAsync();
        }

        var otherColumns = await ScalarAsync<string>(database, $"""
            SELECT string_agg(quote_ident(column_name::text), ', ' ORDER BY ordinal_position)
              FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = 'cloud_settings' AND column_name NOT IN ('Id', '{Column}')
            """);
        await ExecuteAsync(database, $"""
            INSERT INTO public.cloud_settings ("Id", {otherColumns})
            SELECT gen_random_uuid(), {otherColumns} FROM public.cloud_settings
            """);

        Assert.Equal(2L, await ScalarAsync<long>(database, $"""SELECT count(*) FROM public.cloud_settings WHERE "{Column}" = '{"{}"}'"""));
    }

    [Fact]
    public async Task Applying_it_where_the_column_already_is_does_not_fail_and_keeps_what_is_stored()
    {
        // A migration that fails at start-up leaves the API in limited mode, so its statements
        // are conditional: here the column is there but the history does not say so.
        await using var database = await fixture.CreateDatabaseAsync();
        var hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 0), DayOfWeek.Sunday, GapMinutes: 3);
        await using (var db = database.CreateContext())
        {
            await new CloudSettingsService(db).UpdateBatchHoursAsync(new UpdateBatchHoursRequest(hours));
        }

        await ExecuteAsync(database, $"""DELETE FROM public."__EFMigrationsHistory" WHERE "MigrationId" = '{Migration}'""");
        await MigrateAsync(database, target: null);

        await using (var db = database.CreateContext())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(hours, (await new CloudSettingsService(db).GetBatchHoursAsync()).Hours);
        }
    }

    private static async Task MigrateAsync(PostgresFixture.PostgresTestDatabase database, string? target)
    {
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
        NpgsqlConnection.ClearAllPools();
    }

    private static async Task<bool> ColumnExistsAsync(PostgresFixture.PostgresTestDatabase database) =>
        await ScalarAsync<bool>(database, $"""
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                 WHERE table_schema = 'public' AND table_name = 'cloud_settings' AND column_name = '{Column}')
            """);

    private static async Task ExecuteAsync(PostgresFixture.PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(PostgresFixture.PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
