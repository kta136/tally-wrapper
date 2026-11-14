using System.Xml.Linq;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests;

/// <summary>
/// Finding a voucher in Tally by its number. The answers here are written by
/// hand in the shape TallyPrime exports a collection; none of this has been
/// run against TallyPrime itself.
/// </summary>
public sealed class TallyVoucherLookupTests
{
    private const string Number = "DEV-26/0001";
    private const string FiscalYear = "2026-27";

    [Fact]
    public async Task It_asks_for_sales_vouchers_with_that_number_in_that_fiscal_year()
    {
        var client = new TallyPosterTests.FakeXmlClient(Collection());

        await Lookup(client).FindMasterIdsAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        var request = client.LastRequest!;
        Assert.Equal("Export", request.Descendants("TALLYREQUEST").Single().Value);
        Assert.Equal("Collection", request.Descendants("TYPE").First().Value);
        Assert.Equal("Acme Jewellers", request.Descendants("SVCURRENTCOMPANY").Single().Value);
        Assert.Equal("20260401", request.Descendants("SVFROMDATE").Single().Value);
        Assert.Equal("20270331", request.Descendants("SVTODATE").Single().Value);

        var collection = request.Descendants("COLLECTION").Single();
        Assert.Equal(request.Descendants("ID").Single().Value, collection.Attribute("NAME")?.Value);
        Assert.Equal("Voucher", collection.Element("TYPE")?.Value);
        Assert.Contains("MasterID", collection.Element("FETCH")?.Value);

        var filter = request.Descendants("SYSTEM").Single();
        Assert.Equal(collection.Element("FILTER")?.Value, filter.Attribute("NAME")?.Value);
        Assert.Equal("Formulae", filter.Attribute("TYPE")?.Value);
        Assert.Equal("$VoucherNumber = \"DEV-26/0001\" AND $VoucherTypeName = \"Sales\"", filter.Value);
    }

    [Fact]
    public async Task One_voucher_with_the_number_gives_its_master_id()
    {
        var found = await Lookup(Collection(Voucher(" 4521", Number, "Sales")))
            .FindMasterIdsAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal(["4521"], found);
    }

    [Fact]
    public async Task No_voucher_with_the_number_gives_none()
    {
        var found = await Lookup(Collection())
            .FindMasterIdsAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public async Task Two_vouchers_with_the_number_are_both_reported()
    {
        var found = await Lookup(Collection(Voucher("4521", Number, "Sales"), Voucher("4777", Number, "Sales")))
            .FindMasterIdsAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal(["4521", "4777"], found);
    }

    [Fact]
    public async Task A_voucher_tally_lists_without_being_asked_for_is_not_taken()
    {
        // As if Tally had ignored the filter: another number, and the same
        // number on a voucher of another type.
        var found = await Lookup(Collection(
                Voucher("4400", "DEV-26/0002", "Sales"),
                Voucher("4521", Number, "Sales"),
                Voucher("4600", Number, "Receipt")))
            .FindMasterIdsAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal(["4521"], found);
    }

    [Fact]
    public async Task A_matching_voucher_without_a_master_id_is_an_error_not_a_miss()
    {
        var lookup = Lookup(Collection(Voucher("0", Number, "Sales")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lookup.FindMasterIdsAsync(Number, FiscalYear, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("DEV\"26/0001", "2026-27")]   // cannot be written into the filter
    [InlineData("DEV-26/0001", "next year")]  // no period to search
    public async Task What_cannot_be_asked_is_refused_before_tally_is_called(string number, string fiscalYear)
    {
        var client = new TallyPosterTests.FakeXmlClient(Collection());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup(client).FindMasterIdsAsync(number, fiscalYear, TestContext.Current.CancellationToken));
        Assert.Null(client.LastRequest);
    }

    private static TallyVoucherLookup Lookup(XElement response) =>
        Lookup(new TallyPosterTests.FakeXmlClient(response));

    private static TallyVoucherLookup Lookup(ITallyXmlClient client) =>
        new(client, new TallyPosterTests.FakeCloudSettingsService());

    private static XElement Collection(params XElement[] vouchers) =>
        new("ENVELOPE",
            new XElement("HEADER", new XElement("VERSION", 1), new XElement("STATUS", 1)),
            new XElement("BODY", new XElement("DATA", new XElement("COLLECTION", vouchers))));

    private static XElement Voucher(string masterId, string number, string type) =>
        new("VOUCHER",
            new XAttribute("VCHTYPE", type),
            new XElement("MASTERID", masterId),
            new XElement("VOUCHERNUMBER", number),
            new XElement("VOUCHERTYPENAME", type),
            new XElement("DATE", "20260415"));
}
