using System.Xml.Linq;
using ShowroomBilling.Infrastructure.Tally;
using ShowroomBilling.Tests.TestSupport;

namespace ShowroomBilling.Tests;

/// <summary>
/// Finding a voucher in Tally by its number or by its master id. The answers
/// here are written by hand, in the shape TallyPrime gave when these requests
/// were run against it (14 and 25 November 2026): a <c>CMPINFO</c> block of
/// counts (one of them named VOUCHER) before the collection, and each voucher
/// with its native fields beside the ones asked for.
/// </summary>
public sealed class TallyVoucherLookupTests
{
    private const string Number = "DEV-26/0001";
    private const string FiscalYear = "2026-27";
    private static readonly DateOnly April15 = new(2026, 4, 15);

    [Fact]
    public async Task It_asks_for_sales_vouchers_with_that_number_in_that_fiscal_year()
    {
        var client = new FakeXmlClient(Collection());

        await Lookup(client).FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

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
        Assert.Contains("Date", collection.Element("FETCH")?.Value);

        var filter = request.Descendants("SYSTEM").Single();
        Assert.Equal(collection.Element("FILTER")?.Value, filter.Attribute("NAME")?.Value);
        Assert.Equal("Formulae", filter.Attribute("TYPE")?.Value);
        Assert.Equal("$VoucherNumber = \"DEV-26/0001\" AND $VoucherTypeName = \"Sales\"", filter.Value);
    }

    [Fact]
    public async Task One_voucher_with_the_number_gives_its_master_id_date_total_and_company()
    {
        var found = await Lookup(Collection(Voucher(" 4521", Number, "Sales", amount: "-1000.000")))
            .FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal([new TallyVoucherRef("4521", April15, 1000m, "Acme Jewellers", Number)], found);
    }

    [Fact]
    public async Task The_years_sales_vouchers_are_asked_for_by_type_and_read_with_their_numbers()
    {
        var client = new FakeXmlClient(Collection(
            Voucher("4521", "DEV-26/0001", "Sales"),
            Voucher("4522", "DEV-26/0002", "Sales", date: "20260416"),
            Voucher("4600", "R/1", "Receipt"),
            Voucher("4400", "DEV-25/0099", "Sales", date: "20260331")));

        var (destination, found) = await Lookup(client).ListSalesVouchersAsync(FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal("Sales", destination.VoucherType);
        Assert.Equal("$VoucherTypeName = \"Sales\"", client.LastRequest!.Descendants("SYSTEM").Single().Value);
        Assert.Contains("Amount", client.LastRequest.Descendants("FETCH").Single().Value);
        // Another type, and a voucher of the year before, are not the year's sales.
        Assert.Equal([("4521", "DEV-26/0001"), ("4522", "DEV-26/0002")], found.Select(v => (v.MasterId, v.Number!)));
    }

    [Fact]
    public async Task A_voucher_tally_gives_no_total_for_is_still_found_without_one()
    {
        var voucher = Voucher("4521", Number, "Sales");
        voucher.Element("AMOUNT")!.Remove();

        var found = await Lookup(Collection(voucher))
            .FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(found).Amount);
    }

    [Fact]
    public async Task A_voucher_dated_outside_the_fiscal_year_asked_for_is_not_taken()
    {
        // Tally does not always keep to the period asked for, and a number
        // can come round again in another fiscal year.
        var found = await Lookup(Collection(
                Voucher("4400", Number, "Sales", date: "20260331"),
                Voucher("4521", Number, "Sales"),
                Voucher("4600", Number, "Sales", date: "20270401")))
            .FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);
        var byId = await Lookup(Collection(Voucher("4400", Number, "Sales", date: "20260331")))
            .FindByMasterIdAsync("4400", FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal(["4521"], found.Select(x => x.MasterId));
        Assert.Null(byId);
    }

    [Fact]
    public async Task One_master_id_listed_with_two_dates_is_an_error()
    {
        // Naming the wrong date would make Tally create a second voucher.
        var lookup = Lookup(Collection(
            Voucher("4521", Number, "Sales", date: "20260415"),
            Voucher("4521", Number, "Sales", date: "20260416")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lookup.FindByMasterIdAsync("4521", FiscalYear, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_matching_voucher_without_a_type_is_an_error_not_a_match()
    {
        // The read by id does not filter on the type, so it has to be checked here.
        var voucher = Voucher("4521", Number, "Sales");
        voucher.Element("VOUCHERTYPENAME")!.Remove();
        voucher.Attribute("VCHTYPE")!.Remove();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup(Collection(voucher)).FindByMasterIdAsync("4521", FiscalYear, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_destination_is_the_company_and_voucher_type_in_settings()
    {
        var lookup = new TallyVoucherLookup(
            new FakeXmlClient(Collection()),
            new FakeCloudSettingsService { SalesVoucherType = " Retail Sale " });

        var destination = await lookup.DestinationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TallyVoucherDestination("Acme Jewellers", "Retail Sale"), destination);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_list_of_vouchers_is_an_error_not_a_miss()
    {
        // Tally answers HTTP 200 to a request it refused or did not understand.
        var unknownRequest = new XElement("RESPONSE", "Unknown Request, cannot be processed");
        var refused = Collection();
        refused.Element("HEADER")!.Element("STATUS")!.Value = "0";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup(unknownRequest).FindByMasterIdAsync("4521", FiscalYear, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup(unknownRequest).FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup(refused).FindByMasterIdAsync("4521", FiscalYear, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_voucher_with_the_number_gives_none()
    {
        var found = await Lookup(Collection())
            .FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public async Task Two_vouchers_with_the_number_are_both_reported()
    {
        var found = await Lookup(Collection(Voucher("4521", Number, "Sales"), Voucher("4777", Number, "Sales")))
            .FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal(["4521", "4777"], found.Select(x => x.MasterId));
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
            .FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal(["4521"], found.Select(x => x.MasterId));
    }

    [Fact]
    public async Task The_voucher_count_in_tallys_answer_is_not_taken_for_a_voucher()
    {
        // Collection(...) puts the CMPINFO counts in, as TallyPrime does: its
        // VOUCHER element is a number, and an answer with no voucher still has it.
        var none = await Lookup(Collection())
            .FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);
        var one = await Lookup(Collection(Voucher("4521", Number, "Sales")))
            .FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);
        var byId = await Lookup(Collection())
            .FindByMasterIdAsync("1", FiscalYear, TestContext.Current.CancellationToken);

        Assert.Empty(none);
        Assert.Equal(["4521"], one.Select(x => x.MasterId));
        Assert.Null(byId);
    }

    [Fact]
    public async Task A_voucher_tally_keeps_only_as_deleted_is_not_one_to_change()
    {
        var deleted = Voucher("4400", Number, "Sales");
        deleted.Element("ISDELETED")!.Value = "Yes";

        var found = await Lookup(Collection(deleted, Voucher("4521", Number, "Sales")))
            .FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken);
        var byId = await Lookup(Collection(deleted))
            .FindByMasterIdAsync("4400", FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal(["4521"], found.Select(x => x.MasterId));
        Assert.Null(byId);
    }

    [Fact]
    public async Task A_matching_voucher_without_a_master_id_is_an_error_not_a_miss()
    {
        var lookup = Lookup(Collection(Voucher("0", Number, "Sales")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lookup.FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_matching_voucher_without_a_date_is_an_error_not_a_miss()
    {
        // An Alter has to name the voucher's date, so it cannot go ahead without one.
        var voucher = Voucher("4521", Number, "Sales");
        voucher.Element("DATE")!.Remove();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup(Collection(voucher)).FindByNumberAsync(Number, FiscalYear, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup(Collection(voucher)).FindByMasterIdAsync("4521", FiscalYear, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("DEV\"26/0001", "2026-27")]   // cannot be written into the filter
    [InlineData("DEV-26/0001", "next year")]  // no period to search
    public async Task What_cannot_be_asked_is_refused_before_tally_is_called(string number, string fiscalYear)
    {
        var client = new FakeXmlClient(Collection());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup(client).FindByNumberAsync(number, fiscalYear, TestContext.Current.CancellationToken));
        Assert.Null(client.LastRequest);
    }

    [Fact]
    public async Task By_master_id_it_asks_for_the_sales_voucher_with_that_id_in_that_fiscal_year()
    {
        var client = new FakeXmlClient(Collection());

        await Lookup(client).FindByMasterIdAsync(" 4521 ", FiscalYear, TestContext.Current.CancellationToken);

        var request = client.LastRequest!;
        Assert.Equal("Export", request.Descendants("TALLYREQUEST").Single().Value);
        Assert.Equal("Acme Jewellers", request.Descendants("SVCURRENTCOMPANY").Single().Value);
        Assert.Equal("20260401", request.Descendants("SVFROMDATE").Single().Value);
        Assert.Equal("20270331", request.Descendants("SVTODATE").Single().Value);
        var collection = request.Descendants("COLLECTION").Single();
        Assert.Equal(request.Descendants("ID").Single().Value, collection.Attribute("NAME")?.Value);
        Assert.Contains("Date", collection.Element("FETCH")?.Value);
        var filter = request.Descendants("SYSTEM").Single();
        Assert.Equal(collection.Element("FILTER")?.Value, filter.Attribute("NAME")?.Value);
        // The id alone: the type is checked on this side.
        Assert.Equal("$MasterID = 4521", filter.Value);
    }

    [Fact]
    public async Task By_master_id_the_voucher_gives_the_date_it_has_in_tally()
    {
        // With Auto Renumber the number Tally shows can differ from the bill's;
        // the master id is what names the voucher.
        var found = await Lookup(Collection(Voucher(" 4521", "DEV-26/0002", "Sales", date: "20260430")))
            .FindByMasterIdAsync("4521", FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal(new TallyVoucherRef("4521", new DateOnly(2026, 4, 30), 100m, "Acme Jewellers", "DEV-26/0002"), found);
    }

    [Fact]
    public async Task By_master_id_a_voucher_type_a_formula_cannot_carry_is_still_looked_up()
    {
        const string type = "Sale \"Retail\"";
        var client = new FakeXmlClient(Collection(Voucher("4521", Number, type)));
        var lookup = new TallyVoucherLookup(client, new FakeCloudSettingsService { SalesVoucherType = type });

        var found = await lookup.FindByMasterIdAsync("4521", FiscalYear, TestContext.Current.CancellationToken);

        Assert.Equal("4521", found?.MasterId);
        Assert.DoesNotContain("\"", client.LastRequest!.Descendants("SYSTEM").Single().Value);
    }

    [Fact]
    public async Task By_master_id_a_voucher_tally_lists_without_being_asked_for_is_not_taken()
    {
        // As if Tally had ignored the filter: another id, and the id on a voucher of another type.
        var found = await Lookup(Collection(Voucher("4400", Number, "Sales"), Voucher("4521", Number, "Receipt")))
            .FindByMasterIdAsync("4521", FiscalYear, TestContext.Current.CancellationToken);

        Assert.Null(found);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("4521 OR 1")]
    public async Task By_master_id_what_is_not_an_id_is_refused_before_tally_is_called(string masterId)
    {
        var client = new FakeXmlClient(Collection());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup(client).FindByMasterIdAsync(masterId, FiscalYear, TestContext.Current.CancellationToken));
        Assert.Null(client.LastRequest);
    }

    private static TallyVoucherLookup Lookup(XElement response) =>
        Lookup(new FakeXmlClient(response));

    private static TallyVoucherLookup Lookup(ITallyXmlClient client) =>
        new(client, new FakeCloudSettingsService());

    private static XElement Collection(params XElement[] vouchers) =>
        new("ENVELOPE",
            new XElement("HEADER", new XElement("VERSION", 1), new XElement("STATUS", 1)),
            new XElement("BODY",
                new XElement("DESC",
                    new XElement("CMPINFO",
                        new XElement("COMPANY", 0),
                        new XElement("LEDGER", 0),
                        new XElement("VOUCHERTYPE", 0),
                        new XElement("VOUCHER", vouchers.Length))),
                new XElement("DATA",
                    new XElement("COLLECTION",
                        new XAttribute("ISCMPDEPTYPE", "No"),
                        vouchers))));

    private static XElement Voucher(
        string masterId,
        string number,
        string type,
        string date = "20260415",
        string amount = "-100.000") =>
        new("VOUCHER",
            new XAttribute("REMOTEID", "post-key"),
            new XAttribute("VCHTYPE", type),
            new XElement("DATE", new XAttribute("TYPE", "Date"), date),
            new XElement("VOUCHERTYPENAME", type),
            new XElement("VOUCHERNUMBER", number),
            new XElement("ISDELETED", "No"),
            new XElement("ISINVOICE", "Yes"),
            new XElement("AMOUNT", amount),
            new XElement("MASTERID", new XAttribute("TYPE", "Number"), masterId));
}
