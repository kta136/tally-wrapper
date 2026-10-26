using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Handover;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

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
        decimal lineTotal = 72569.3m,
        decimal diamondCarats = 0m,
        decimal diamondValue = 0m,
        decimal roundingAllowance = 0m) => new()
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
            DiamondCarats = diamondCarats,
            DiamondValue = diamondValue,
            RoundingAllowance = roundingAllowance,
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
            Line(item: "Diamond Pendant", karat: null, pieces: 2, gross: 3m, less: 0m, net: 3m, wastage: 0m, rate: null, labour: 0m, stones: 80_000m, hallmark: 0m, lineTotal: 1_00_001.01m));

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
    public void A_line_of_an_item_counted_in_carats_fails_the_bill()
    {
        var result = HandoverBillMapper.Map(Bill(Line(item: "Diamond Ring", karat: "18K", pieces: 3, gross: 4m, less: 0.5m, net: 3.5m, rate: null, lineTotal: 1_00_000.01m)), HandoverTestMasters.Masters);

        Assert.Null(result.Payload);
        Assert.Equal("“Diamond Ring” is counted in carats, and a handed-over line is weighed in grams.", result.Problem);
    }

    [Fact]
    public void A_line_is_weighed_by_its_net_weight_whatever_the_items_unit_says()
    {
        // One piece of 1.930 g: the quantity is the weight, never the piece count (as a 1 g line once was).
        var line = Assert.Single(Map(Line(item: "Gold Bangle", karat: "18K", pieces: 1, gross: 1.93m, less: 0m, net: 1.93m, wastage: 3m, rate: 7_650m, labour: 0m, stones: 0m, hallmark: 0m, lineTotal: 11_521.97m)).Lines);

        Assert.Equal(1.93m, line.Quantity);
        Assert.Equal(1.93m, line.GrossWeight);
        Assert.Equal(11_521.97m, line.LineTotal);
        Assert.Equal("g", line.Unit);
        AssertNoNegativeInput(line);
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

    /// <summary>The vector line with 0.45 ct of diamonds at 60,000 a carat on it.</summary>
    private static HandoverLine Studded(string item = "Gold Ornaments")
        => Line(item: item, stones: 28_500m, lineTotal: 99_569.3m, diamondCarats: 0.45m, diamondValue: 27_000m);

    [Fact]
    public async Task A_gold_lines_diamonds_are_a_Diamond_line_right_after_it()
    {
        var payload = Map(Studded(), Line(item: "Gold Chain", lineTotal: 72_569.31m));

        Assert.Equal(["Gold Ornaments", "Diamond", "Gold Chain"], payload.Lines.Select(line => line.ItemName));
        var (gold, diamond) = (payload.Lines[0], payload.Lines[1]);
        // The gold line is the vector line again: its other stones, setting and hallmarking stay in its Extra.
        Assert.Equal((72_569.3m, 6887m, 72_569.30m - 70_936.10m, 300m), (gold.LineTotal, gold.Rate, gold.Extra!.Value, gold.LabourPerUnit!.Value));
        Assert.Equal((0.45m, ItemUnits.Carat, 60_000m, 27_000m), (diamond.Quantity, diamond.Unit, diamond.DiamondRate!.Value, diamond.LineTotal));
        Assert.Null(diamond.Extra);
        Assert.True(string.IsNullOrEmpty(diamond.Karat));
        // GST is worked out on the bill's total, so the bill comes to what it would as one line.
        Assert.Equal(BillCalculator.BuildTotals([99_569.3m, 72_569.31m], discount: 0m).GrandTotal, payload.Totals.GrandTotal);
        Assert.True(HandoverBillMapper.ReopensToSameTotals(payload, HandoverTestMasters.Masters));
        var typed = await TypeIntoInvoiceTabAsync(payload);
        Assert.True(HandoverPayloads.SameContent(typed, payload), $"typed: {Describe(typed)}{Environment.NewLine}handed over: {Describe(payload)}");
    }

    [Fact]
    public void A_line_of_a_Diamond_item_is_not_split_again()
    {
        var line = Assert.Single(Map(Line(item: "Diamond Pendant", karat: null, gross: 4.5m, less: 1m, net: 3.5m, rate: null, lineTotal: 45_678.91m, diamondCarats: 0.5m, diamondValue: 30_000m)).Lines);

        Assert.Equal(("Diamond Pendant", 45_678.91m), (line.ItemName, line.LineTotal));
    }

    [Fact]
    public void Diamonds_with_no_carats_or_no_Diamond_item_fail_the_bill_and_say_why()
    {
        var noCarats = HandoverBillMapper.Map(Bill(Line(stones: 28_500m, lineTotal: 99_569.3m, diamondValue: 27_000m)), HandoverTestMasters.Masters);
        var rows = HandoverTestMasters.Masters.Items;
        var karats = HandoverTestMasters.Masters.Karats;
        var none = HandoverBillMapper.Map(Bill(Studded()), new HandoverMasters(rows.Where(item => item.Unit != ItemUnits.Carat).ToList(), karats));
        var twoOthers = HandoverBillMapper.Map(Bill(Studded()), new HandoverMasters(rows.Where(item => item.Name != "Diamond").Append(Solitaire()).ToList(), karats));
        var oneOther = HandoverBillMapper.Map(Bill(Studded()), new HandoverMasters(rows.Where(item => item.Name != "Diamond").ToList(), karats));

        Assert.Equal("The diamonds on a line of “Gold Ornaments” have no carats.", noCarats.Problem);
        Assert.Equal("A line has diamonds. Tally Wrapper bills them on its Diamond item: one item in the Diamond category counted in ct, or of several, the one named Diamond.", none.Problem);
        Assert.Equal(none.Problem, twoOthers.Problem);
        // The one Diamond item counted in ct, whatever its name.
        Assert.Equal("Diamond Ring", oneOther.Payload!.Lines[1].ItemName);

        static ItemMasterRowVm Solitaire() => ItemMasterRowVm.FromEntry(new("Solitaire", ItemUnits.Carat, 0m, ItemCategories.Diamond, PricingModes.Wastage, 0m, "Solitaire"));
    }

    /// <summary>
    /// 1.85 g of 18K with 8 % wastage at 7,650: a sender that rounds the fine to the milligram (1.5355 g to 1.536 g) and the line
    /// to the rupee says 11,750, give or take its 4.33 of rounding; the Invoice tab makes it 1.85 × 6,349.50 = 11,746.58.
    /// </summary>
    private static HandoverLine Rounded(decimal roundingAllowance = 4.33m)
        => Line(item: "Gold Bangle", karat: "18K", gross: 1.85m, less: 0m, net: 1.85m, wastage: 8m, rate: 7_650m, labour: 0m, stones: 0m, hallmark: 0m,
            lineTotal: 11_750m, roundingAllowance: roundingAllowance);

    [Fact]
    public async Task A_line_within_its_rounding_allowance_is_billed_as_typed_with_no_extra()
    {
        var payload = Map(Rounded());

        var line = Assert.Single(payload.Lines);
        Assert.Equal((7_650m, 6_349.50m, 8m, 11_746.58m), (payload.Rate24Kt!.Value, line.Rate, line.WastagePercent!.Value, line.LineTotal));
        Assert.Null(line.Extra);
        Assert.Equal(BillCalculator.BuildTotals([11_746.58m], discount: 0m).GrandTotal, payload.Totals.GrandTotal);
        var typed = await TypeIntoInvoiceTabAsync(payload);
        Assert.True(HandoverPayloads.SameContent(typed, payload), $"typed: {Describe(typed)}{Environment.NewLine}handed over: {Describe(payload)}");
        // With no allowance the line keeps the sender's total, the gap in Extra.
        var exact = Assert.Single(Map(Rounded(roundingAllowance: 0m)).Lines);
        Assert.Equal((11_750m, 3.42m), (exact.LineTotal, exact.Extra!.Value));
    }

    [Fact]
    public void A_line_further_off_than_its_allowance_or_with_a_discount_keeps_its_total()
    {
        // The vector line comes to 72,481.10 as typed, 88.20 under its total.
        var further = Assert.Single(Map(Line(roundingAllowance: 5.33m)).Lines);
        var discounted = Assert.Single(Map(Line(discount: 100m, lineTotal: 72_484m, roundingAllowance: 5.33m)).Lines);

        Assert.Equal(Assert.Single(Map(Line()).Lines), further);
        Assert.Equal(72_484m, discounted.LineTotal);
    }

    [Fact]
    public void As_typed_the_extra_is_the_stones_and_hallmarking_and_the_labour_of_an_item_priced_without_it()
    {
        // 10.3 g at 7,000 of 22K with 2.5 % wastage, labour 3,090, stones 1,500, hallmarking 45; typed it comes to 72,481.10.
        var both = Assert.Single(Map(Line(lineTotal: 72_484m, roundingAllowance: 5.33m)).Lines);
        var wastageOnly = Assert.Single(Map(Line(item: "Gold Chain", lineTotal: 72_484m, roundingAllowance: 5.33m)).Lines);

        Assert.Equal((72_481.10m, 6_887m, 300m, 1_545m), (both.LineTotal, both.Rate, both.LabourPerUnit!.Value, both.Extra!.Value));
        Assert.Equal((72_481.10m, 6_587m, 0m, 4_635m), (wastageOnly.LineTotal, wastageOnly.Rate, wastageOnly.LabourPerUnit!.Value, wastageOnly.Extra!.Value));
        Assert.Equal(2.5m, wastageOnly.WastagePercent);
    }

    [Fact]
    public void A_typed_line_whose_gold_is_above_its_total_leaves_the_bill_rate_as_it_is()
    {
        // 1.851 g of 18K at 7,650: the sender's fine rounds down (1.38825 g to 1.388 g), so it says 10,618 for 10,620.11.
        var line = Line(item: "Gold Bangle", karat: "18K", gross: 1.851m, less: 0m, net: 1.851m, wastage: 0m, rate: 7_650m, labour: 0m, stones: 0m, hallmark: 0m,
            lineTotal: 10_618m, roundingAllowance: 4.33m);

        var typed = Map(line);
        var exact = Map(line with { RoundingAllowance = 0m });

        Assert.Equal((7_650m, 10_620.11m), (typed.Rate24Kt!.Value, Assert.Single(typed.Lines).LineTotal));
        Assert.Null(typed.Lines[0].Extra);
        Assert.Equal((7_648m, 10_618m), (exact.Rate24Kt!.Value, Assert.Single(exact.Lines).LineTotal));
    }

    [Fact]
    public void As_typed_a_diamond_line_takes_its_rate_to_the_nearest_paisa_and_no_extra()
    {
        var pendant = Assert.Single(Map(Line(item: "Diamond Pendant", karat: null, gross: 4.5m, less: 1m, net: 3.5m, rate: null, lineTotal: 45_678.91m, roundingAllowance: 0.5m)).Lines);
        // A studded line, its gold 2.90 over the typed 72,406.10 (its other stones and setting are 1,425).
        var studded = Map(Line(stones: 28_425m, lineTotal: 99_409m, diamondCarats: 0.45m, diamondValue: 27_000m, roundingAllowance: 5.33m));

        // 45,678.91 / 3.5 = 13,051.117: 3.5 × 13,051.12 = 45,678.92.
        Assert.Equal((13_051.12m, 45_678.92m), (pendant.DiamondRate!.Value, pendant.LineTotal));
        Assert.Null(pendant.Extra);
        Assert.Equal([72_406.10m, 27_000m], studded.Lines.Select(line => line.LineTotal));
        Assert.Equal(new decimal?[] { 1_470m, null }, studded.Lines.Select(line => line.Extra));
        Assert.Equal(60_000m, studded.Lines[1].DiamondRate);
    }

    [Fact]
    public void Lines_a_little_off_their_typed_price_are_billed_at_it()
    {
        var random = new Random(20261026);
        (string Item, string Karat, decimal Purity)[] golds = [("Gold Chain", "22K", 91.6m), ("Gold Bangle", "18K", 75m), ("Gold Chain", "24K", 100m)];
        for (var bill = 0; bill < 300; bill++)
        {
            // One rate for the bill, as a counter types it.
            var rate = (decimal)random.Next(5_000, 9_000);
            var typedTotals = new List<decimal>();
            var lines = Enumerable.Range(0, random.Next(1, 5)).Select(_ =>
            {
                var (item, karat, purity) = golds[random.Next(golds.Length)];
                var net = Math.Round((decimal)(random.NextDouble() * 60 + 0.5), 3);
                var wastage = Math.Round((decimal)(random.NextDouble() * 20), 2);
                var labour = (decimal)random.Next(0, 5_000);
                var hallmark = random.Next(2) == 0 ? 0m : 45m;
                var allowance = Math.Round((decimal)(random.NextDouble() * 10 + 0.01), 2);
                var typed = BillCalculator.ComputeLine(new BillCalculator.LineInputs(rate, purity, wastage, 0m, net, labour + hallmark, PricingModes.Wastage, false, 0m)).LineTotalInclusive;
                typedTotals.Add(typed);
                var gap = Math.Round((decimal)(random.NextDouble() * 2 - 1) * allowance, 2);
                return Line(item: item, karat: karat, gross: net, less: 0m, net: net, wastage: wastage, rate: rate, labour: labour, stones: 0m, hallmark: hallmark,
                    lineTotal: typed + gap, roundingAllowance: allowance);
            }).ToArray();

            var payload = Map(lines);

            Assert.Equal(rate, payload.Rate24Kt);
            Assert.Equal(typedTotals, payload.Lines.Select(line => line.LineTotal));
            Assert.Equal(lines.Select(line => line.WastagePercent), payload.Lines.Select(line => line.WastagePercent ?? 0m));
            Assert.All(payload.Lines, AssertNoNegativeInput);
            Assert.True(HandoverBillMapper.ReopensToSameTotals(payload, HandoverTestMasters.Masters));
        }
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
        var diamondWeightless = HandoverBillMapper.Map(Bill(Line(item: "Diamond Pendant", pieces: 2, gross: 0m, less: 0m, net: 0m)), HandoverTestMasters.Masters);

        Assert.Equal("A line of “Gold Ornaments” has no net weight.", weightless.Problem);
        Assert.Equal("A line of “Diamond Pendant” has no net weight.", diamondWeightless.Problem);
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
        string[] items = ["Gold Ornaments", "Gold Chain", "Gold Coin", "Gold Bangle", "Diamond Pendant"];
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
                // A third of the lines have diamonds: up to 0.9 of the line's total, on 0.001 to 3 ct.
                var diamonds = random.Next(3) == 0 ? Math.Round(total * (decimal)(random.NextDouble() * 0.9), 2) : 0m;
                return Line(
                    diamondCarats: diamonds > 0m ? Math.Round((decimal)(random.NextDouble() * 3 + 0.001), 3) : 0m,
                    diamondValue: diamonds,
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

            // A gold line with diamonds is two: the gold, then its diamonds.
            var expected = lines.SelectMany(line => line.DiamondValue > 0m && line.ItemName != "Diamond Pendant"
                ? new[] { line.LineTotal - line.DiamondValue, line.DiamondValue }
                : [line.LineTotal]);
            Assert.Equal(expected, payload.Lines.Select(line => line.LineTotal));
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
