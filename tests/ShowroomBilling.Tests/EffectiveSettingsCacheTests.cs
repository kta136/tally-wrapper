using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Settings;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

public sealed class EffectiveSettingsCacheTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task Settings_are_kept_for_five_seconds_and_no_longer()
    {
        var clock = new FakeTimeProvider();
        var cache = new EffectiveSettingsCache(clock);
        await using var db = CreateDbContext();
        var settings = await new CloudSettingsService(db).GetEffectiveSettingsAsync();

        Assert.False(cache.TryGet(out _));

        cache.Set(settings);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(cache.TryGet(out var cached));
        Assert.Same(settings, cached);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(cache.TryGet(out _));
    }

    [Fact]
    public async Task A_save_is_visible_to_the_next_read_through_the_same_cache()
    {
        var cache = new EffectiveSettingsCache();
        await using var db = CreateDbContext();
        var service = new CloudSettingsService(db, cache: cache);
        var before = await service.GetEffectiveSettingsAsync();
        Assert.True(cache.TryGet(out _));

        await service.SaveEffectiveSettingsAsync(new UpdateEffectiveSettingsRequest(
            before.Settings with { Numbering = before.Settings.Numbering with { InvoicePrefix = "SR/" } }));

        var after = await service.GetEffectiveSettingsAsync();
        Assert.Equal("SR/", after.Settings.Numbering.InvoicePrefix);
    }

    [Fact]
    public async Task Ledgers_called_sales_and_cash_do_not_mean_setup_is_still_needed()
    {
        await using var db = CreateDbContext();
        var service = new CloudSettingsService(db);
        var seeded = await service.GetEffectiveSettingsAsync();
        Assert.True(seeded.RequiresInitialSetup);
        Assert.Equal("Sales", seeded.Settings.Ledgers.SalesLedger);
        Assert.Equal("Cash", seeded.Settings.Ledgers.CashLedger);

        // Everything a showroom must replace is replaced; the ledgers keep
        // their seeded names because that is what they are called in Tally.
        await service.SaveEffectiveSettingsAsync(new UpdateEffectiveSettingsRequest(seeded.Settings with
        {
            Connection = seeded.Settings.Connection with { ActiveCompanyName = "DDA Jewels" },
            Numbering = seeded.Settings.Numbering with { InvoicePrefix = "SR/" },
            Print = seeded.Settings.Print with { CompanyName = "DDA Jewels" },
            Masters = new MasterDataSettingsDto("""[{"name":"Gold"}]""", """[{"label":"22K"}]"""),
        }));

        Assert.False((await service.GetEffectiveSettingsAsync()).RequiresInitialSetup);
    }

    [Theory]
    [InlineData("Development Company", "DDA Jewels", "SR/")]
    [InlineData("DDA Jewels", "Tally Wrapper", "SR/")]
    [InlineData("DDA Jewels", "Showroom Billing V2", "SR/")]
    [InlineData("DDA Jewels", "DDA Jewels", "DEV-")]
    public async Task A_placeholder_left_in_place_still_asks_for_setup(string activeCompany, string printCompany, string prefix)
    {
        await using var db = CreateDbContext();
        var service = new CloudSettingsService(db);
        var seeded = await service.GetEffectiveSettingsAsync();

        await service.SaveEffectiveSettingsAsync(new UpdateEffectiveSettingsRequest(seeded.Settings with
        {
            Connection = seeded.Settings.Connection with { ActiveCompanyName = activeCompany },
            Numbering = seeded.Settings.Numbering with { InvoicePrefix = prefix },
            Print = seeded.Settings.Print with { CompanyName = printCompany },
            Masters = new MasterDataSettingsDto("""[{"name":"Gold"}]""", """[{"label":"22K"}]"""),
        }));

        Assert.True((await service.GetEffectiveSettingsAsync()).RequiresInitialSetup);
    }
}
