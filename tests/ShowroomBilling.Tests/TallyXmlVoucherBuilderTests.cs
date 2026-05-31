using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests;

public sealed class TallyXmlVoucherBuilderTests
{
    [Fact]
    public void Build_CreateVoucherUsesRemoteIdAndCreateAction()
    {
        var xml = TallyXmlVoucherBuilder.Build(Request(), Ledgers(), "Acme Jewellers");

        var voucher = xml.Descendants("VOUCHER").Single();
        Assert.Equal("post-key", voucher.Attribute("REMOTEID")?.Value);
        Assert.Equal("Create", voucher.Attribute("ACTION")?.Value);
        Assert.Equal("Sales", voucher.Attribute("VCHTYPE")?.Value);
        Assert.Null(voucher.Attribute("TAGNAME"));
        Assert.Null(voucher.Attribute("TAGVALUE"));
    }

    [Fact]
    public void Build_AlterVoucherUsesTargetTagAndNoRemoteId()
    {
        var xml = TallyXmlVoucherBuilder.Build(
            Request(TallyPostOperation.Alter, "MASTER ID", "101"),
            Ledgers(),
            "Acme Jewellers");

        var voucher = xml.Descendants("VOUCHER").Single();
        Assert.Null(voucher.Attribute("REMOTEID"));
        Assert.Equal("Alter", voucher.Attribute("ACTION")?.Value);
        Assert.Equal("Sales", voucher.Attribute("VCHTYPE")?.Value);
        Assert.Equal("MASTER ID", voucher.Attribute("TAGNAME")?.Value);
        Assert.Equal("101", voucher.Attribute("TAGVALUE")?.Value);
    }

    private static TallyPostRequest Request(
        TallyPostOperation operation = TallyPostOperation.Create,
        string? targetTagName = null,
        string? targetTagValue = null) =>
        new(
            BillId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RevisionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            RevisionNo: 1,
            BillType: "sales",
            InvoiceNumber: "DEV-26/0001",
            FiscalYear: "2026-27",
            IdempotencyKey: "post-key",
            Payload: Payload(),
            Operation: operation,
            TargetTagName: targetTagName,
            TargetTagValue: targetTagValue);

    private static LedgerMappingsDto Ledgers() =>
        new("Sales", "Cash", "Card", "CGST", "SGST", "Round Off", "Discount", "Sales");

    private static BillPayloadDto Payload() =>
        new(
            PartyName: "Walk-in",
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: new DateOnly(2026, 4, 1),
            Lines:
            [
                new BillLineItemDto("22K Gold Ring", "7113", 10m, "grams", 100m, 1000m, "22K", null)
            ],
            Totals: new BillTotalsDto(1000m, 0m, 0m, 0m, 1000m),
            Notes: null,
            Payment: PaymentMode.Cash);
}
