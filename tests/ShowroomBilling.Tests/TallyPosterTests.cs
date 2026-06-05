using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests;

public sealed class TallyPosterTests
{
    [Fact]
    public async Task PostAsync_CreateSucceedsWhenCreatedOrAlteredIsPositive()
    {
        var poster = BuildPoster(Response(created: 0, altered: 1, lastVoucherId: "101"));

        var response = await poster.PostAsync(Request());

        Assert.Equal(TallyPostOutcome.Posted, response.Outcome);
        Assert.Equal("101", response.RemoteId);
        Assert.Equal("101", response.TallyMasterId);
    }

    [Fact]
    public async Task PostAsync_AlterSucceedsOnlyWhenAlteredIsPositive()
    {
        var poster = BuildPoster(Response(created: 0, altered: 1, lastVoucherId: null));

        var response = await poster.PostAsync(Request(
            TallyPostOperation.Alter,
            targetTagName: "MASTER ID",
            targetTagValue: "101"));

        Assert.Equal(TallyPostOutcome.Posted, response.Outcome);
        Assert.Equal("101", response.TallyMasterId);
    }

    [Fact]
    public async Task PostAsync_AlterNoEffectFails()
    {
        var poster = BuildPoster(Response(created: 0, altered: 0));

        var response = await poster.PostAsync(Request(
            TallyPostOperation.Alter,
            targetTagName: "MASTER ID",
            targetTagValue: "101"));

        Assert.Equal(TallyPostOutcome.Failed, response.Outcome);
        Assert.Equal("TALLY_NO_EFFECT", response.ErrorCode);
    }

    [Fact]
    public async Task PostAsync_AlterTreatsCreatedVoucherAsFailure()
    {
        var poster = BuildPoster(Response(created: 1, altered: 0, lastVoucherId: "202"));

        var response = await poster.PostAsync(Request(
            TallyPostOperation.Alter,
            targetTagName: "MASTER ID",
            targetTagValue: "101"));

        Assert.Equal(TallyPostOutcome.Failed, response.Outcome);
        Assert.Equal("TALLY_UNEXPECTED_CREATE_ON_ALTER", response.ErrorCode);
    }

    [Fact]
    public async Task PostAsync_SendsCompanyStateAndCountryFromSettings()
    {
        var (poster, client) = BuildPosterWithClient(
            Response(created: 1, altered: 0, lastVoucherId: "101"),
            companyState: " Uttar Pradesh ",
            companyCountry: " India ");

        await poster.PostAsync(Request());

        var voucher = client.LastRequest!.Descendants("VOUCHER").Single();
        Assert.Equal("Cash", voucher.Element("PARTYNAME")?.Value);
        Assert.Equal("Unregistered/Consumer", voucher.Element("GSTREGISTRATIONTYPE")?.Value);
        Assert.Equal("Uttar Pradesh", voucher.Element("STATENAME")?.Value);
        Assert.Equal("Uttar Pradesh", voucher.Element("PLACEOFSUPPLY")?.Value);
        Assert.Equal("Uttar Pradesh", voucher.Element("CONSIGNEESTATENAME")?.Value);
        Assert.Equal("India", voucher.Element("COUNTRYNAME")?.Value);
        Assert.Equal("India", voucher.Element("COUNTRYOFRESIDENCE")?.Value);
        Assert.Equal("India", voucher.Element("CONSIGNEECOUNTRYNAME")?.Value);
        var partyEntry = voucher.Elements("ALLLEDGERENTRIES.LIST")
            .Single(x => x.Element("LEDGERNAME")?.Value == "Cash");
        Assert.Equal("Yes", partyEntry.Element("ISPARTYLEDGER")?.Value);
    }

    private static TallyPoster BuildPoster(XElement response) =>
        BuildPosterWithClient(response).Poster;

    private static (TallyPoster Poster, FakeXmlClient Client) BuildPosterWithClient(
        XElement response,
        string? companyState = null,
        string? companyCountry = null)
    {
        var client = new FakeXmlClient(response);
        return (
            new TallyPoster(
                client,
                new FakeCloudSettingsService(companyState, companyCountry),
                NullLogger<TallyPoster>.Instance),
            client);
    }

    private static XElement Response(int created, int altered, string? lastVoucherId = null) =>
        new("ENVELOPE",
            new XElement("BODY",
                new XElement("DATA",
                    new XElement("IMPORTRESULT",
                        new XElement("CREATED", created),
                        new XElement("ALTERED", altered),
                        new XElement("ERRORS", 0),
                        new XElement("EXCEPTIONS", 0),
                        new XElement("LASTVCHID", lastVoucherId ?? string.Empty)))));

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

    private sealed class FakeXmlClient(XElement response) : ITallyXmlClient
    {
        public string EndpointDescription => "fake";

        public XElement? LastRequest { get; private set; }

        public Task<XElement> SendAsync(XElement request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class FakeCloudSettingsService(string? companyState = null, string? companyCountry = null) : ICloudSettingsService
    {
        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveSettingsResponse(
                SettingsSource: "cloud",
                Summary: "test",
                Settings: new EffectiveCloudSettingsDto(
                    new ConnectionSettingsDto("127.0.0.1", 9000, 30, "Acme Jewellers"),
                    new NumberingSettingsDto("DEV-", "", 4),
                    new PrintSettingsDto("Acme", null, null, null, companyState, companyCountry, null, null, null, null, null, true, false, false, 11, 9),
                    new LedgerMappingsDto("Sales", "Cash", "Card", "CGST", "SGST", "Round Off", "Discount", "Sales"),
                    new MasterDataSettingsDto("[]", "[]")),
                CloudOwnedCategories: [],
                LocalOnlyCategories: [],
                UpdatedAtUtc: DateTimeOffset.UtcNow));

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
            UpdateEffectiveSettingsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
            string companyName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
            UpdatePrintLayoutRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
