using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence.Entities;
using ShowroomBilling.Infrastructure.Settings;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The shared settings are one row, made by whoever reads them first. A Desktop that starts
/// against a new database asks for several things at once, and each of those requests used to
/// find no row and add its own.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class CloudSettingsSeedingPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task First_reads_that_arrive_together_leave_one_settings_row()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int count = 16;

        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await using var db = database.CreateContext();
                // Connected before the start, so that the reads themselves arrive together.
                await db.Database.OpenConnectionAsync();
                await start.Task;

                // The three reads a Desktop makes as it starts.
                var service = new CloudSettingsService(db);
                switch (i % 3)
                {
                    case 0:
                        return (await service.GetEffectiveSettingsAsync()).UpdatedAtUtc;
                    case 1:
                        return (await service.GetPrintLayoutAsync()).UpdatedAtUtc;
                    default:
                        return (await service.GetBatchHoursAsync()).UpdatedAtUtc;
                }
            }))
            .ToArray();

        start.SetResult();
        var readAs = await Task.WhenAll(tasks);

        await using var verify = database.CreateContext();
        var row = await verify.CloudSettings.SingleAsync();
        Assert.Equal(CloudSettingsEntity.FirstSettingsId, row.Id);
        // Every caller was answered from that row, whoever made it.
        Assert.All(readAs, updatedAt => Assert.Equal(row.UpdatedAtUtc, updatedAt));
        var seeded = await verify.AuditEvents.SingleAsync(e => e.EventType == "settings.seeded");
        Assert.Equal(row.Id.ToString(), seeded.EntityId);
    }

    [Fact]
    public async Task First_writes_that_arrive_together_all_land_on_the_one_row()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] companies = ["Company A", "Company B", "Company C", "Company D"];

        var tasks = companies
            .Select(company => Task.Run(async () =>
            {
                await using var db = database.CreateContext();
                await db.Database.OpenConnectionAsync();
                await start.Task;
                await new CloudSettingsService(db).SelectActiveCompanyAsync(company);
            }))
            .ToArray();

        start.SetResult();
        await Task.WhenAll(tasks);

        await using var verify = database.CreateContext();
        var row = await verify.CloudSettings.SingleAsync();
        Assert.Contains(row.ActiveCompanyName, companies);
        Assert.Equal(1, await verify.AuditEvents.CountAsync(e => e.EventType == "settings.seeded"));
        Assert.Equal(companies.Length, await verify.AuditEvents.CountAsync(e => e.EventType == "settings.company_selected"));
    }

    [Fact]
    public async Task Settings_first_read_inside_a_transaction_are_seeded_in_it()
    {
        // A caller that reads the settings inside its own transaction keeps the say over it: the
        // first row is neither committed behind its back nor does it end the transaction.
        await using var database = await fixture.CreateDatabaseAsync();
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var settings = await new CloudSettingsService(db).GetEffectiveSettingsAsync();
            Assert.True(settings.RequiresInitialSetup);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await transaction.RollbackAsync();
        }

        await using (var verify = database.CreateContext())
        {
            Assert.Equal(0, await verify.CloudSettings.CountAsync());
            Assert.Equal(0, await verify.AuditEvents.CountAsync());
        }

        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await new CloudSettingsService(db).GetEffectiveSettingsAsync();
            await transaction.CommitAsync();
        }

        await using (var verify = database.CreateContext())
        {
            Assert.Equal(1, await verify.CloudSettings.CountAsync());
            Assert.Equal(1, await verify.AuditEvents.CountAsync(e => e.EventType == "settings.seeded"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_first_read_that_meets_a_seeding_not_yet_committed_waits_for_it(bool theSeedingCommits)
    {
        // The race with nothing left to timing: the first caller has added the row and not yet
        // committed, so the second finds no settings, goes to add the row, and has to wait.
        await using var database = await fixture.CreateDatabaseAsync();
        await using var first = database.CreateContext();
        await using var transaction = await first.Database.BeginTransactionAsync();
        await new CloudSettingsService(first).GetEffectiveSettingsAsync();

        var second = Task.Run(async () =>
        {
            await using var db = database.CreateContext();
            return await new CloudSettingsService(db).GetEffectiveSettingsAsync();
        });
        await WaitForACallerHeldByALockAsync(database);
        Assert.False(second.IsCompleted);

        if (theSeedingCommits)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        // Committed, the second adds nothing and reads the first one's row; rolled back, the row
        // is the second's own. Either way there is one, with one event.
        Assert.True((await second).RequiresInitialSetup);
        await using var verify = database.CreateContext();
        Assert.Equal(CloudSettingsEntity.FirstSettingsId, (await verify.CloudSettings.SingleAsync()).Id);
        Assert.Equal(1, await verify.AuditEvents.CountAsync(e => e.EventType == "settings.seeded"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_database_that_already_holds_two_rows_is_read_and_written_as_one_of_them(bool theLowerIdIsNewer)
    {
        // What the old seeding could leave: two rows made in the same instant. They differ in
        // what they hold, so that a read shows which row it took. With the same time on both,
        // the row in use is the one with the higher id; otherwise it is the newer.
        var lower = ("Company A", "A-", 1);
        var higher = ("Company B", "B-", 2);
        await using var database = await fixture.CreateDatabaseAsync();
        await using (var db = database.CreateContext())
        {
            await new CloudSettingsService(db).GetEffectiveSettingsAsync();
            var first = await db.CloudSettings.SingleAsync();
            (first.ActiveCompanyName, first.InvoicePrefix, first.BatchHoursJson) = ("Company A", "A-", HoursJson(1));

            var second = await db.CloudSettings.AsNoTracking().SingleAsync();
            second.Id = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
            (second.ActiveCompanyName, second.InvoicePrefix, second.BatchHoursJson) = ("Company B", "B-", HoursJson(2));
            db.CloudSettings.Add(second);

            if (theLowerIdIsNewer)
            {
                first.UpdatedAtUtc = second.UpdatedAtUtc.AddSeconds(1);
            }

            await db.SaveChangesAsync();
        }

        var inUse = theLowerIdIsNewer ? lower : higher;
        var unused = theLowerIdIsNewer ? higher : lower;
        Assert.Equal(inUse, await ReadAsync(database));

        // A write goes to the row the reads come from, and leaves the other as it was.
        await using (var db = database.CreateContext())
        {
            await new CloudSettingsService(db).SelectActiveCompanyAsync("Company C");
        }

        Assert.Equal(("Company C", inUse.Item2, inUse.Item3), await ReadAsync(database));
        await using (var verify = database.CreateContext())
        {
            var rows = await verify.CloudSettings.ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Company C", rows.Single(r => r.InvoicePrefix == inUse.Item2).ActiveCompanyName);
            Assert.Equal(unused.Item1, rows.Single(r => r.InvoicePrefix == unused.Item2).ActiveCompanyName);
            // Nothing was seeded on the way: there was a row to use.
            Assert.Equal(1, await verify.AuditEvents.CountAsync(e => e.EventType == "settings.seeded"));
        }
    }

    /// <summary>
    /// The settings as each kind of read gives them: the whole row, the two columns of the batch
    /// hours, and the three columns numbering takes. They are separate queries, and must name
    /// the same row.
    /// </summary>
    private static async Task<(string Company, string? Prefix, int GapMinutes)> ReadAsync(
        PostgresFixture.PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        var service = new CloudSettingsService(db);
        var settings = (await service.GetEffectiveSettingsAsync()).Settings;
        var hours = (await service.GetBatchHoursAsync()).Hours;
        var (prefix, _, _) = await new NumberingService(db).GetEffectivePrefixSuffixAsync();

        Assert.Equal(settings.Numbering.InvoicePrefix, prefix);
        return (settings.Connection.ActiveCompanyName, prefix, hours.GapMinutes);
    }

    /// <summary>Returns once a statement on this database is waiting for another transaction.</summary>
    private static async Task WaitForACallerHeldByALockAsync(PostgresFixture.PostgresTestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'";

        var giveUpAt = DateTime.UtcNow.AddSeconds(30);
        while ((long)(await command.ExecuteScalarAsync())! == 0)
        {
            Assert.True(DateTime.UtcNow < giveUpAt, "No caller came to wait for the seeding that had not committed.");
            await Task.Delay(20);
        }
    }

    private static string HoursJson(int gapMinutes) =>
        System.Text.Json.JsonSerializer.Serialize(new BatchHoursSettings(GapMinutes: gapMinutes));
}
