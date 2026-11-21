using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Settings;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

public sealed class CloudSettingsServiceTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task GetEffectiveSettingsAsync_SeedsDefaultRow_WhenDatabaseIsEmpty()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);

        var response = await service.GetEffectiveSettingsAsync();

        Assert.Equal("cloud", response.SettingsSource);
        Assert.Equal("Development Company", response.Settings.Connection.ActiveCompanyName);
        Assert.Equal("127.0.0.1", response.Settings.Connection.Host);
        Assert.Equal("DEV-", response.Settings.Numbering.InvoicePrefix);
        Assert.True(response.RequiresInitialSetup);
    }

    [Fact]
    public async Task SaveEffectiveSettingsAsync_PersistsUpdatedValues()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);

        var request = new UpdateEffectiveSettingsRequest(
            new EffectiveCloudSettingsDto(
                new ConnectionSettingsDto("192.168.1.20", 9100, 45, "Showroom Alpha"),
                new NumberingSettingsDto("SB-", "/26", 4),
                new PrintSettingsDto(
                    "Alpha Jewellers",
                    "GSTIN123",
                    "9999999999",
                    "Market Road",
                    "Tamil Nadu",
                    "India",
                    "HDFC",
                    "1234567890",
                    "HDFC0001",
                    "upi@bank",
                    "No returns after 7 days.",
                    true,
                    true,
                    false,
                    12,
                    10),
                new LedgerMappingsDto("Sales", "Cash", "Card", "CGST", "SGST", "Round Off", "Discount", "Sales"),
                new MasterDataSettingsDto("[{\"code\":\"ITEM1\"}]", "[{\"karat\":\"22K\"}]")));

        var updateResponse = await service.SaveEffectiveSettingsAsync(request);
        var effectiveResponse = await service.GetEffectiveSettingsAsync();

        Assert.Contains("connection", updateResponse.SavedSections);
        Assert.Equal("192.168.1.20", effectiveResponse.Settings.Connection.Host);
        Assert.Equal("Showroom Alpha", effectiveResponse.Settings.Connection.ActiveCompanyName);
        Assert.Equal("Alpha Jewellers", effectiveResponse.Settings.Print.CompanyName);
        Assert.Equal("[{\"code\":\"ITEM1\"}]", effectiveResponse.Settings.Masters.ItemMasterDataJson);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_ReportsInitialSetupFalse_WhenRealSettingsAreSaved()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);

        await service.SaveEffectiveSettingsAsync(new UpdateEffectiveSettingsRequest(
            new EffectiveCloudSettingsDto(
                new ConnectionSettingsDto("192.168.1.20", 9100, 45, "Showroom Alpha"),
                new NumberingSettingsDto("SB-", "/26", 4),
                new PrintSettingsDto(
                    "Alpha Jewellers",
                    "GSTIN123",
                    "9999999999",
                    "Market Road",
                    "Tamil Nadu",
                    "India",
                    "HDFC",
                    "1234567890",
                    "HDFC0001",
                    "upi@bank",
                    "No returns after 7 days.",
                    true,
                    true,
                    false,
                    12,
                    10),
                new LedgerMappingsDto(
                    "Alpha Sales",
                    "Alpha Cash",
                    "Alpha Card",
                    "Alpha CGST",
                    "Alpha SGST",
                    "Alpha Round Off",
                    "Alpha Discount",
                    "Alpha Sales Voucher"),
                new MasterDataSettingsDto("[{\"name\":\"Gold\"}]", "[{\"label\":\"22K\"}]" ))));

        var response = await service.GetEffectiveSettingsAsync();

        Assert.False(response.RequiresInitialSetup);
    }

    [Fact]
    public async Task SelectActiveCompanyAsync_UpdatesCompanyWithoutResettingOtherSettings()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);
        await service.GetEffectiveSettingsAsync();

        var updateResponse = await service.SelectActiveCompanyAsync("Showroom Beta");
        var effectiveResponse = await service.GetEffectiveSettingsAsync();

        Assert.Equal("Showroom Beta", effectiveResponse.Settings.Connection.ActiveCompanyName);
        Assert.Contains("connection", updateResponse.SavedSections);
        Assert.Equal("127.0.0.1", effectiveResponse.Settings.Connection.Host);
    }

    [Fact]
    public async Task SaveEffectiveSettingsAsync_RejectsOutOfRangeAndMalformedValues()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);
        var seeded = await service.GetEffectiveSettingsAsync();

        var invalidPort = new UpdateEffectiveSettingsRequest(seeded.Settings with
        {
            Connection = seeded.Settings.Connection with { Port = 0 }
        });
        await Assert.ThrowsAsync<RequestValidationException>(() => service.SaveEffectiveSettingsAsync(invalidPort));

        var invalidJson = new UpdateEffectiveSettingsRequest(seeded.Settings with
        {
            Masters = seeded.Settings.Masters with { ItemMasterDataJson = "not-json" }
        });
        await Assert.ThrowsAsync<RequestValidationException>(() => service.SaveEffectiveSettingsAsync(invalidJson));
    }

    [Fact]
    public async Task UpdatePrintLayoutAsync_RejectsNonFiniteOrOversizedGeometry()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);

        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdatePrintLayoutAsync(
            new UpdatePrintLayoutRequest(new PrintLayoutSettings(
                double.NaN, 1, 1, 1, null, null))));

        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdatePrintLayoutAsync(
            new UpdatePrintLayoutRequest(new PrintLayoutSettings(
                1, 1, 1, 1,
                new PrintLayoutAssetPlacement(null, 0, 0, 25, 2),
                null))));
    }

    [Fact]
    public async Task UpdatePrintLayoutAsync_round_trips_watermark_and_structured_page_layout()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);
        var watermarkId = Guid.NewGuid();
        var pageLayout = PrintLayoutDefaults.CreatePageLayout() with
        {
            Density = PrintPageDensity.Comfortable,
            InvoiceBorderThicknessPt = 2.5,
            BottomPinnedFromSectionKey = PrintLayoutSectionKeys.BankDetails,
            Sections = PrintLayoutDefaults.CreatePageLayout().Sections
                .Select(section => section.SectionKey == PrintLayoutSectionKeys.Notes
                    ? section with { IsVisible = false, SpacingBeforeMm = 3, SpacingAfterMm = 4 }
                    : section)
                .Reverse()
                .ToArray()
        };
        var expected = new PrintLayoutSettings(
            0.5, 0.6, 0.7, 0.8,
            null,
            null,
            new PrintLayoutWatermarkPlacement(watermarkId, 2, 3, 10, 11, 22),
            pageLayout);

        await service.UpdatePrintLayoutAsync(new UpdatePrintLayoutRequest(expected));
        var reloaded = await service.GetPrintLayoutAsync();

        Assert.Equal(expected.LeftMarginCm, reloaded.Layout.LeftMarginCm);
        Assert.Equal(expected.BottomMarginCm, reloaded.Layout.BottomMarginCm);
        Assert.Equal(watermarkId, reloaded.Layout.Watermark!.AssetId);
        Assert.Equal(PrintPageDensity.Comfortable, reloaded.Layout.PageLayout!.Density);
        Assert.Equal(PrintLayoutSectionKeys.Signature, reloaded.Layout.PageLayout.Sections[0].SectionKey);
        Assert.False(reloaded.Layout.PageLayout.Sections
            .Single(section => section.SectionKey == PrintLayoutSectionKeys.Notes).IsVisible);
    }

    [Fact]
    public async Task GetPrintLayoutAsync_applies_page_defaults_to_legacy_json()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);
        await service.GetPrintLayoutAsync();
        var entity = await dbContext.CloudSettings.SingleAsync();
        entity.PrintLayoutJson =
            """{"LeftMarginCm":0.4,"RightMarginCm":0.5,"TopMarginCm":0.6,"BottomMarginCm":0.7,"Logo":null,"Signature":null}""";
        await dbContext.SaveChangesAsync();

        var response = await service.GetPrintLayoutAsync();

        Assert.Equal(0.4, response.Layout.LeftMarginCm);
        Assert.Null(response.Layout.Watermark);
        Assert.NotNull(response.Layout.PageLayout);
        Assert.Equal(PrintPageDensity.Standard, response.Layout.PageLayout!.Density);
        Assert.Equal(PrintLayoutSectionKeys.All, response.Layout.PageLayout.Sections.Select(row => row.SectionKey).ToArray());
    }

    [Fact]
    public async Task UpdatePrintLayoutAsync_rejects_invalid_designer_payloads()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);
        var valid = ValidDesignerLayout();
        var defaults = valid.PageLayout!;

        PrintLayoutSettings[] invalidLayouts =
        [
            valid with { PageLayout = defaults with { Density = "dense" } },
            valid with { PageLayout = defaults with { InvoiceBorderThicknessPt = 4.1 } },
            valid with { PageLayout = defaults with { Sections = defaults.Sections.Skip(1).ToArray() } },
            valid with { PageLayout = defaults with { Sections = defaults.Sections.Concat([defaults.Sections[0]]).ToArray() } },
            valid with
            {
                PageLayout = defaults with
                {
                    Sections = defaults.Sections
                        .Select(section => section.SectionKey == PrintLayoutSectionKeys.Logo
                            ? section with { SectionKey = "unknown" }
                            : section)
                        .ToArray()
                }
            },
            valid with
            {
                PageLayout = defaults with
                {
                    Sections = defaults.Sections
                        .Select(section => section.SectionKey == PrintLayoutSectionKeys.ItemsTable
                            ? section with { IsVisible = false }
                            : section)
                        .ToArray()
                }
            },
            valid with
            {
                PageLayout = defaults with
                {
                    Sections = defaults.Sections
                        .Select(section => section.SectionKey == PrintLayoutSectionKeys.Notes
                            ? section with { SpacingAfterMm = 20.1 }
                            : section)
                        .ToArray()
                }
            },
            valid with { PageLayout = defaults with { BottomPinnedFromSectionKey = "unknown" } },
            valid with { Watermark = valid.Watermark! with { AssetId = Guid.Empty } },
            valid with { Watermark = valid.Watermark! with { OffsetXCm = 10, WidthCm = 12 } },
            valid with { Watermark = valid.Watermark! with { OpacityPercent = 101 } },
        ];

        foreach (var invalid in invalidLayouts)
        {
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                service.UpdatePrintLayoutAsync(new UpdatePrintLayoutRequest(invalid)));
        }
    }

    [Fact]
    public async Task Batch_hours_are_none_until_saved_and_are_read_back_as_saved()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);

        Assert.Equal(new BatchHoursSettings(), (await service.GetBatchHoursAsync()).Hours);

        // Seconds are dropped: a time is hours and minutes.
        var saved = await service.UpdateBatchHoursAsync(new UpdateBatchHoursRequest(
            new BatchHoursSettings(new TimeOnly(10, 0, 45), new TimeOnly(20, 30, 59), DayOfWeek.Tuesday, GapMinutes: 5)));

        var expected = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 30), DayOfWeek.Tuesday, GapMinutes: 5);
        Assert.Equal(expected, saved.Hours);

        // Read by another context: what is stored, not what the first one remembers.
        await using var other = CreateDbContext();
        var reloaded = await new CloudSettingsService(other).GetBatchHoursAsync();
        Assert.Equal(expected, reloaded.Hours);
        Assert.Equal(1, await other.AuditEvents.CountAsync(e => e.EntityType == "settings" && e.EventType == "settings.batch_hours_updated"));

        // The other settings are left as they were, and saving them leaves the hours.
        var settings = await new CloudSettingsService(other).GetEffectiveSettingsAsync();
        await new CloudSettingsService(other).SaveEffectiveSettingsAsync(new UpdateEffectiveSettingsRequest(settings.Settings));
        await using var third = CreateDbContext();
        Assert.Equal(expected, (await new CloudSettingsService(third).GetBatchHoursAsync()).Hours);

        // Saving none again clears them.
        await new CloudSettingsService(third).UpdateBatchHoursAsync(new UpdateBatchHoursRequest(new BatchHoursSettings()));
        await using var fourth = CreateDbContext();
        Assert.Equal(new BatchHoursSettings(), (await new CloudSettingsService(fourth).GetBatchHoursAsync()).Hours);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("""{"Opens":"half past ten"}""")]
    [InlineData("[1, 2]")]
    public async Task Batch_hours_that_are_empty_or_cannot_be_read_are_none(string stored)
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);
        await service.GetBatchHoursAsync();
        var entity = await dbContext.CloudSettings.SingleAsync();
        entity.BatchHoursJson = stored;
        await dbContext.SaveChangesAsync();

        await using var other = CreateDbContext();
        var response = await new CloudSettingsService(other).GetBatchHoursAsync();

        Assert.Equal(new BatchHoursSettings(), response.Hours);
    }

    [Fact]
    public async Task UpdateBatchHoursAsync_refuses_bad_hours_and_stores_nothing()
    {
        await using var dbContext = CreateDbContext();
        var service = new CloudSettingsService(dbContext);

        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateBatchHoursAsync(
            new UpdateBatchHoursRequest(new BatchHoursSettings(new TimeOnly(20, 0), new TimeOnly(10, 0)))));

        await using var other = CreateDbContext();
        Assert.Equal(new BatchHoursSettings(), (await new CloudSettingsService(other).GetBatchHoursAsync()).Hours);
        Assert.Equal(0, await other.AuditEvents.CountAsync(e => e.EventType == "settings.batch_hours_updated"));
    }

    private static PrintLayoutSettings ValidDesignerLayout() =>
        new(
            1, 1, 1, 1,
            null,
            null,
            PrintLayoutDefaults.CreateWatermark(Guid.NewGuid()),
            PrintLayoutDefaults.CreatePageLayout());
}
