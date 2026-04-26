using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Settings;

namespace ShowroomBilling.Tests;

public sealed class CloudSettingsServiceTests
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

    private static ShowroomBillingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ShowroomBillingDbContext(options);
    }
}
