using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;
using static ShowroomBilling.Tests.TestSupport.BillTestData;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class InvoiceCorePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DeleteTrailingBill_WithLetterSuffix_ReusesFreedCoreWithoutDuplicating()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        await AddSettingsAsync(db, suffix: "-A");
        var bills = PostgresBillTestSupport.BuildWorkflows(db);

        var first = await bills.Lifecycle.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await bills.Lifecycle.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        await bills.Admin.DeleteAsync(second.Id, new DeleteBillRequest("test", DryRun: false));
        var third = await bills.Lifecycle.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 100m)));

        Assert.EndsWith("/0001-A", first.InvoiceNumber);
        Assert.EndsWith("/0002-A", third.InvoiceNumber);
    }

    [Fact]
    public async Task Reserve_WithDigitSuffix_DoesNotMistakeSuffixForCore()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        await AddSettingsAsync(db, suffix: "-A2");
        var bills = PostgresBillTestSupport.BuildWorkflows(db);

        var first = await bills.Lifecycle.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await bills.Lifecycle.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));

        Assert.EndsWith("/0001-A2", first.InvoiceNumber);
        Assert.EndsWith("/0002-A2", second.InvoiceNumber);
    }

    private static async Task AddSettingsAsync(ShowroomBillingDbContext db, string suffix)
    {
        db.CloudSettings.Add(new CloudSettingsEntity
        {
            Id = Guid.NewGuid(),
            Host = "localhost",
            Port = 9000,
            TimeoutSeconds = 10,
            ActiveCompanyName = "ACME",
            PrintCompanyName = "ACME",
            InvoicePrefix = string.Empty,
            InvoiceSuffix = suffix,
            PrintFontSize = 11,
            PrintTermsFontSize = 9,
            SalesLedger = "Sales",
            CashLedger = "Cash",
            CreditDebitLedger = "Debtors",
            CgstLedger = "CGST",
            SgstLedger = "SGST",
            RoundOffLedger = "RoundOff",
            DiscountLedger = "Disc",
            SalesVoucherType = "Sales",
            ItemMasterDataJson = "[]",
            KaratMappingDataJson = "[]",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
