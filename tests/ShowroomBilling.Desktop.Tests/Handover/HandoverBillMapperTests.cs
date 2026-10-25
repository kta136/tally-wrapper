using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Handover;
using ShowroomBilling.Desktop.ViewModels.Invoice;

namespace ShowroomBilling.Desktop.Tests.Handover;

public sealed class HandoverBillMapperTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static HandoverLine Line(
        string item = "Gold Ornaments",
        string? karat = "22K",
        int pieces = 1,
        decimal gross = 10.5m,
        decimal less = 0.2m,
        decimal net = 10.3m,
        decimal wastage = 2.5m,
        decimal? rate = 7000m,
        decimal labour = 3090m,
        decimal stones = 1500m,
        decimal hallmark = 45m,
        decimal discount = 0m,
        decimal lineTotal = 72569.3m) => new()
        {
            ItemName = item,
            Karat = karat,
            Pieces = pieces,
            GrossWeight = gross,
            LessWeight = less,
            NetWeight = net,
            WastagePercent = wastage,
            Rate = rate,
            LabourAmount = labour,
            StoneValue = stones,
            HallmarkAmount = hallmark,
            DiscountAmount = discount,
            LineTotal = lineTotal,
        };

    private static HandoverBill Bill(params HandoverLine[] lines) => new()
    {
        SaveId = Guid.NewGuid(),
        PartyName = "  Buyer A ",
        PartyPhone = "9000000000",
        PartyAddress = "Somewhere",
        BillDate = Today,
        Payment = PaymentMode.Cash,
        Lines = lines,
    };

    private static BillPayloadDto Map(params HandoverLine[] lines)
    {
        var result = HandoverBillMapper.Map(Bill(lines), HandoverTestMasters.Masters);
        Assert.True(result.Payload is not null, result.Problem);
        return result.Payload!;
    }

    [Fact]
    public void The_vector_line_keeps_its_total_and_reads_as_gold_wastage_labour_and_extra()
    {
        var payload = Map(Line());

        var line = Assert.Single(payload.Lines);
        Assert.Equal(72569.30m, line.LineTotal);
        Assert.Equal(7000m, payload.Rate24Kt);
        Assert.Equal(10.3m, line.Quantity);
        Assert.Equal(10.5m, line.GrossWeight);
        Assert.Equal(0.2m, line.LessWeight);
        Assert.Equal(2.5m, line.WastagePercent);
        Assert.Equal(300m, line.LabourPerUnit);
        // 7,000 × 91.6 % + 2.5 % of 7,000 + 300 = 6,887 a gram; 10.3 g of it is 70,936.10.
        Assert.Equal(6887m, line.Rate);
        Assert.Equal(72569.30m - 70936.10m, line.Extra);
        Assert.Equal("Gold Ornaments", line.ItemName);
        Assert.Equal("22K", line.Karat);
        Assert.Equal(ItemUnits.Gram, line.Unit);
        Assert.Equal(JewelleryGst.HsnCode, line.HsnCode);
        Assert.Equal("Gold 22K", line.StockName);
        Assert.Equal(ItemCategories.GoldBased, line.ItemCategory);
        Assert.Equal(PricingModes.Both, line.PricingMode);
        Assert.Null(line.RawJson);
        Assert.Null(line.DiamondRate);
    }

    [Fact]
    public async Task The_bill_is_what_the_Invoice_tab_would_save_for_the_same_lines()
    {
        var handedOver = Map(
            Line(),
            Line(item: "Gold Chain", karat: "22 k", gross: 5.25m, less: 0m, net: 5.25m, wastage: 6m, labour: 500m, stones: 0m, hallmark: 45m, lineTotal: 39_999.99m),
            Line(item: "Diamond Ring", karat: null, pieces: 2, gross: 3m, less: 0m, net: 3m, wastage: 0m, rate: null, labour: 0m, stones: 80_000m, hallmark: 0m, lineTotal: 1_00_001.01m));

        var typed = await TypeIntoInvoiceTabAsync(handedOver);

        Assert.True(HandoverPayloads.SameContent(typed, handedOver), $"typed: {Describe(typed)}{Environment.NewLine}handed over: {Describe(handedOver)}");
        // A counter's bill has no phone, address or note, so a handed-over one has none either.
        Assert.Null(handedOver.PartyPhone);
        Assert.Null(handedOver.PartyAddress);
        Assert.Null(handedOver.PartyGstin);
        Assert.Null(handedOver.Notes);
        Assert.Equal("Buyer A", handedOver.PartyName);
        Assert.Equal(PaymentMode.Cash, handedOver.Payment);
        Assert.Equal(Today, handedOver.BillDate);
    }

    [Fact]
    public async Task Opening_the_bill_for_edit_recomputes_every_line_to_the_same_total()
    {
        var payload = Map(
            Line(lineTotal: 72_569.37m),
            Line(item: "Gold Coin", karat: "24K", gross: 8m, less: 0m, net: 8m, wastage: 0m, rate: 7_000m, labour: 1_200m, stones: 0m, hallmark: 0m, lineTotal: 58_999.01m),
            Line(item: "Gold Ornaments", karat: "18K", gross: 4m, less: 0m, net: 4m, wastage: -8m, rate: null, labour: 0m, stones: 0m, hallmark: 0m, lineTotal: 17_000.03m),
            Line(item: "Diamond Pendant", karat: "18K", gross: 4.5m, less: 1m, net: 3.5m, wastage: 0m, rate: null, labour: 0m, stones: 25_000m, hallmark: 0m, lineTotal: 45_678.91m));

        var invoice = await OpenForEditAsync(payload);

        var lines = invoice.Lines.Where(line => !line.IsEmpty).ToList();
        Assert.Equal(payload.Lines.Select(line => line.LineTotal), lines.Select(line => line.LineTotal));
        Assert.Equal(payload.Totals.GrandTotal, invoice.GrandTotal);
        Assert.Equal(payload.Totals.Subtotal, invoice.Subtotal);
    }

    [Fact]
    public void A_wastage_only_item_takes_the_labour_into_extra_and_a_labour_only_item_the_wastage()
    {
        var wastageOnly = Assert.Single(Map(Line(item: "Gold Chain", lineTotal: 72_569.31m)).Lines);
        var labourOnly = Assert.Single(Map(Line(item: "Gold Coin", karat: "22K", lineTotal: 72_569.31m)).Lines);

        Assert.Equal(2.5m, wastageOnly.WastagePercent);
        Assert.Equal(0m, wastageOnly.LabourPerUnit);
        Assert.Equal(72_569.31m, wastageOnly.LineTotal);
        Assert.True(wastageOnly.Extra > 3_090m);
        Assert.Equal(0m, labourOnly.WastagePercent);
        Assert.Equal(300m, labourOnly.LabourPerUnit);
        Assert.Equal(72_569.31m, labourOnly.LineTotal);
    }

    [Fact]
    public void A_negative_wastage_gives_no_wastage_and_no_input_below_zero()
    {
        // Sold under its purity: 10 g at 7,000 with 92 % less 10 %, GST on top.
        var underPurity = Assert.Single(Map(Line(gross: 10m, less: 0m, net: 10m, wastage: -10m, labour: 0m, stones: 0m, hallmark: 0m, lineTotal: 59_122m)).Lines);

        Assert.Equal(0m, underPurity.WastagePercent);
        Assert.Equal(0m, underPurity.LabourPerUnit);
        Assert.Equal(59_122m, underPurity.LineTotal);
        Assert.True(underPurity.Extra is null or >= 0m);
    }

    [Fact]
    public void A_line_that_comes_to_less_than_its_gold_lowers_the_bill_rate_until_every_line_fits()
    {
        var payload = Map(
            Line(gross: 10m, less: 0m, net: 10m, wastage: 2m, labour: 1_000m, stones: 0m, hallmark: 0m, lineTotal: 73_000m),
            Line(gross: 10m, less: 0m, net: 10m, wastage: -100m, labour: 500m, stones: 0m, hallmark: 0m, lineTotal: 600m));

        Assert.True(payload.Rate24Kt < 7_000m);
        Assert.Equal(Math.Floor(payload.Rate24Kt!.Value), payload.Rate24Kt);
        Assert.Equal([73_000m, 600m], payload.Lines.Select(line => line.LineTotal));
        Assert.All(payload.Lines, AssertNoNegativeInput);
        Assert.True(HandoverBillMapper.ReopensToSameTotals(payload, HandoverTestMasters.Masters));
    }

    [Fact]
    public void A_bill_with_different_rates_takes_the_first_lines_rate()
    {
        var payload = Map(
            Line(rate: 7_100m, lineTotal: 75_000m),
            Line(rate: 7_000m, lineTotal: 72_569.3m));

        Assert.Equal(7_100m, payload.Rate24Kt);
        Assert.Equal([75_000m, 72_569.3m], payload.Lines.Select(line => line.LineTotal));
    }

    [Fact]
    public void A_bill_priced_by_the_piece_has_no_24K_rate_and_keeps_its_totals()
    {
        var payload = Map(Line(rate: null, wastage: 0m, labour: 500m, lineTotal: 12_345.67m));

        var line = Assert.Single(payload.Lines);
        Assert.Null(payload.Rate24Kt);
        Assert.Equal(12_345.67m, line.LineTotal);
        Assert.Equal(0m, line.WastagePercent);
        AssertNoNegativeInput(line);
    }

    [Fact]
    public void A_diamond_item_counted_in_carats_is_priced_by_the_piece_with_its_paise_in_extra()
    {
        var line = Assert.Single(Map(Line(item: "Diamond Ring", karat: "18K", pieces: 3, gross: 4m, less: 0.5m, net: 3.5m, rate: null, lineTotal: 1_00_000.01m)).Lines);

        Assert.Equal(3m, line.Quantity);
        Assert.Equal(3m, line.GrossWeight);
        Assert.Equal(0m, line.LessWeight);
        Assert.Equal(33_333.33m, line.DiamondRate);
        Assert.Equal(33_333.33m, line.Rate);
        Assert.Equal(0.02m, line.Extra);
        Assert.Equal(1_00_000.01m, line.LineTotal);
        Assert.Equal(string.Empty, line.Karat);
        Assert.Equal(ItemCategories.Diamond, line.ItemCategory);
        Assert.Equal(ItemUnits.Carat, line.Unit);
        Assert.Equal("Diamond Ring", line.StockName);
    }

    [Fact]
    public void A_diamond_item_counted_in_grams_is_priced_on_its_net_weight()
    {
        var line = Assert.Single(Map(Line(item: "Diamond Pendant", karat: null, gross: 4.5m, less: 1m, net: 3.5m, rate: null, lineTotal: 45_678.91m)).Lines);

        Assert.Equal(3.5m, line.Quantity);
        Assert.Equal(13_051.11m, line.DiamondRate);
        Assert.Equal(45_678.91m, line.LineTotal);
        // 3.5 × 13,051.11 = 45,678.885, which the line rounds to 45,678.89.
        Assert.Equal(0.02m, line.Extra);
    }

    [Fact]
    public void An_unknown_item_or_karat_fails_the_bill_and_says_which()
    {
        var item = HandoverBillMapper.Map(Bill(Line(item: " Silver Bowl ")), HandoverTestMasters.Masters);
        var karat = HandoverBillMapper.Map(Bill(Line(karat: "14K")), HandoverTestMasters.Masters);

        Assert.Null(item.Payload);
        Assert.Equal("No item named “Silver Bowl” in Tally Wrapper’s items.", item.Problem);
        Assert.Null(karat.Payload);
        Assert.Equal("No karat “14K” in Tally Wrapper’s karats.", karat.Problem);
    }

    [Fact]
    public void Item_and_karat_are_matched_ignoring_case_and_spaces()
    {
        var line = Assert.Single(Map(Line(item: "  gold ornaments ", karat: " 22 k ")).Lines);

        Assert.Equal("Gold Ornaments", line.ItemName);
        Assert.Equal("22K", line.Karat);
    }

    [Fact]
    public void A_line_with_no_stamp_takes_the_karat_its_item_maps_to_in_Tally_or_fails()
    {
        var mapped = Assert.Single(Map(Line(karat: null)).Lines);
        var noKarat = HandoverBillMapper.Map(Bill(Line(item: "Gold Chain", karat: null)), HandoverTestMasters.Masters);

        Assert.Equal("22K", mapped.Karat);
        Assert.Null(noKarat.Payload);
        Assert.Contains("no karat", noKarat.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_without_a_quantity_fails_the_bill()
    {
        var weightless = HandoverBillMapper.Map(Bill(Line(gross: 0m, less: 0m, net: 0m)), HandoverTestMasters.Masters);
        var pieceless = HandoverBillMapper.Map(Bill(Line(item: "Diamond Ring", pieces: 0)), HandoverTestMasters.Masters);

        Assert.Equal("A line of “Gold Ornaments” has no net weight.", weightless.Problem);
        Assert.Equal("A line of “Diamond Ring” has no pieces.", pieceless.Problem);
    }

    [Fact]
    public void The_totals_are_the_Invoice_tabs_with_no_discount()
    {
        var payload = Map(Line(), Line(lineTotal: 10_000.01m));

        var expected = BillCalculator.BuildTotals([72_569.3m, 10_000.01m], discount: 0m);
        Assert.Equal(new BillTotalsDto(expected.SubtotalBase, 0m, expected.Cgst + expected.Sgst, expected.RoundOff, expected.GrandTotal), payload.Totals);
    }

    [Fact]
    public void Any_line_keeps_its_total_to_the_paisa_with_no_input_below_zero()
    {
        var random = new Random(20261025);
        string[] items = ["Gold Ornaments", "Gold Chain", "Gold Coin", "Diamond Ring", "Diamond Pendant"];
        string?[] karats = ["22K", "18K", "24K", null];
        for (var bill = 0; bill < 300; bill++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 5)).Select(_ =>
            {
                var net = Math.Round((decimal)(random.NextDouble() * 60 + 0.001), 3);
                var rate = random.Next(4) == 0 ? (decimal?)null : random.Next(5_000, 9_000);
                var gold = (rate ?? 7_000m) * net * 0.9m;
                // From far below the gold value (an item sold under its purity) to well above it.
                var total = Math.Round(gold * (decimal)(random.NextDouble() * 1.6 + 0.05), 2);
                return Line(
                    item: items[random.Next(items.Length)],
                    karat: karats[random.Next(karats.Length)] is { } k ? k : "22K",
                    pieces: random.Next(1, 6),
                    gross: net,
                    less: 0m,
                    net: net,
                    wastage: Math.Round((decimal)(random.NextDouble() * 40 - 20), 2),
                    rate: rate,
                    labour: random.Next(0, 5_000),
                    lineTotal: total);
            }).ToArray();

            var payload = Map(lines);

            Assert.Equal(lines.Select(line => line.LineTotal), payload.Lines.Select(line => line.LineTotal));
            Assert.All(payload.Lines, AssertNoNegativeInput);
            Assert.True(payload.Rate24Kt is null or >= 0m);
            Assert.True(HandoverBillMapper.ReopensToSameTotals(payload, HandoverTestMasters.Masters));
        }
    }

    private static void AssertNoNegativeInput(BillLineItemDto line)
    {
        Assert.True(line.Quantity > 0m);
        Assert.True(line.Rate >= 0m);
        Assert.True(line.GrossWeight is null or >= 0m);
        Assert.True(line.LessWeight is null or >= 0m);
        Assert.True(line.WastagePercent is null or >= 0m);
        Assert.True(line.LabourPerUnit is null or >= 0m);
        Assert.True(line.DiamondRate is null or >= 0m);
        Assert.True(line.Extra is null or >= 0m);
    }

    /// <summary>Types the handed-over lines into the Invoice tab, as an operator would, and returns what its Save sends.</summary>
    private static async Task<BillPayloadDto> TypeIntoInvoiceTabAsync(BillPayloadDto handedOver)
    {
        var api = new FakeBillsApi();
        var settings = HandoverTestMasters.Settings();
        var invoice = new InvoiceViewModel(api, null, settings)
        {
            BillDate = new DateTimeOffset(handedOver.BillDate.ToDateTime(new TimeOnly(12, 0)), DateTimeOffset.Now.Offset),
            PartyName = handedOver.PartyName!,
            Payment = handedOver.Payment!,
            Rate24Kt = handedOver.Rate24Kt,
        };

        foreach (var typed in handedOver.Lines)
        {
            var line = invoice.Lines[^1];
            line.ItemMaster = settings.Draft.ItemMasterRows.First(item => item.Name == typed.ItemName);
            if (!string.IsNullOrEmpty(typed.Karat))
            {
                line.KaratMaster = settings.Draft.KaratRows.First(karat => karat.Label == typed.Karat);
            }

            line.GrossWeight = typed.GrossWeight;
            line.LessWeight = typed.LessWeight;
            line.WastagePercent = typed.WastagePercent;
            line.LabourPerUnit = typed.LabourPerUnit;
            line.DiamondRate = typed.DiamondRate;
            line.Extra = typed.Extra;
        }

        await invoice.SaveDraftCommand.ExecuteAsync(null);
        Assert.True(api.LastCreate is not null, invoice.SaveStatus);
        return api.LastCreate!.Payload;
    }

    private static async Task<InvoiceViewModel> OpenForEditAsync(BillPayloadDto payload)
    {
        var api = new FakeBillsApi { GetResponse = FakeBillsApi.Bill(Guid.NewGuid(), BillStates.Pending, payload) };
        var invoice = new InvoiceViewModel(api, null, HandoverTestMasters.Settings());
        await invoice.LoadBillForEditAsync(api.GetResponse.Id);
        return invoice;
    }

    private static string Describe(BillPayloadDto payload)
        => System.Text.Json.JsonSerializer.Serialize(payload);
}
