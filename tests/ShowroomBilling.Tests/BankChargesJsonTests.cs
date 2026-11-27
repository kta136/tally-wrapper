using System.Text.Json;
using System.Text.Json.Nodes;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Tests;

/// <summary>
/// Bills and settings written before bank charges existed, read by this build: the stored revision
/// JSON (camel case, as <c>BillSerialization</c> writes it) and what an older server or counter sends
/// over HTTP (the web defaults) have no such properties at all.
/// </summary>
public sealed class BankChargesJsonTests
{
    private static readonly JsonSerializerOptions Stored = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string PayloadSavedBeforeBankCharges = """
        {"partyName":"Customer","partyGstin":null,"partyPhone":null,"partyAddress":null,"billDate":"2026-04-24",
         "lines":[{"itemName":"Ring","hsnCode":"7113","quantity":10,"unit":"gm","rate":100,"lineTotal":1000,"karat":"22K","rawJson":null}],
         "totals":{"subtotal":970.87,"discountTotal":0,"taxTotal":29.12,"roundOff":0.01,"grandTotal":1000},
         "notes":null,"payment":"Cash","rate24Kt":7780}
        """;

    [Fact]
    public void A_bill_saved_before_bank_charges_reads_as_having_none()
    {
        foreach (var options in new[] { Stored, Web })
        {
            var payload = JsonSerializer.Deserialize<BillPayloadDto>(PayloadSavedBeforeBankCharges, options)!;

            Assert.Equal(0m, payload.Totals.BankCharges);
            Assert.Equal(1000m, payload.Totals.GrandTotal);
        }
    }

    [Fact]
    public void Bank_charges_go_out_and_come_back_under_their_own_name()
    {
        var totals = new BillTotalsDto(970.87m, 0m, 29.12m, -0.24m, 1050m, BankCharges: 50.25m);

        var json = JsonSerializer.Serialize(totals, Stored);

        Assert.Equal(50.25m, JsonNode.Parse(json)!["bankCharges"]!.GetValue<decimal>());
        Assert.Equal(totals, JsonSerializer.Deserialize<BillTotalsDto>(json, Web));
    }

    [Fact]
    public void Ledgers_from_a_server_older_than_bank_charges_read_as_not_known()
    {
        const string ledgers = """
            {"salesLedger":"Sales","cashLedger":"Cash","creditDebitLedger":"Card","cgstLedger":"CGST",
             "sgstLedger":"SGST","roundOffLedger":"Round Off","discountLedger":"Discount","salesVoucherType":"Sales"}
            """;

        var read = JsonSerializer.Deserialize<LedgerMappingsDto>(ledgers, Web)!;

        Assert.Null(read.BankChargesLedger);
    }
}
