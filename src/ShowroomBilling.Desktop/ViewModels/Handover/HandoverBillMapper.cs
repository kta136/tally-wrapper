using System.Globalization;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Handover;

/// <summary>The item and karat masters a hand-over is matched against: the ones in the saved settings.</summary>
internal sealed record HandoverMasters(IReadOnlyList<ItemMasterRowVm> Items, IReadOnlyList<KaratMasterRowVm> Karats)
{
    public static HandoverMasters From(MasterDataSettingsDto? masters) => new(
        MasterDataJson.ParseItemsOrEmpty(masters?.ItemMasterDataJson).Select(ItemMasterRowVm.FromEntry).ToList(),
        MasterDataJson.ParseKaratsOrEmpty(masters?.KaratMappingDataJson).Select(KaratMasterRowVm.FromEntry).ToList());
}

/// <summary>A handed-over bill as the payload the Invoice tab would have saved, or why it cannot be one.</summary>
internal sealed record HandoverMapResult(BillPayloadDto? Payload, string? Problem)
{
    public static HandoverMapResult Refused(string problem) => new(null, problem);
}

/// <summary>
/// Turns a handed-over bill into the <see cref="BillPayloadDto"/> the Invoice tab would build for
/// the same lines, through the Invoice tab's own line view model, calculator and payload mapper, so
/// the saved bill is an ordinary bill in every field.
/// <para>A line with a rounding allowance and no discount is first priced as the counter would type
/// it: the hand-over's wastage and labour as they are, and as Extra only the other stones, setting
/// and hallmarking (and the labour of an item priced without labour); a diamond line at its total's
/// rate a unit, to the paisa. That stands when it comes within the allowance (and this program's own
/// paise) of the line's total, and the line then has the Invoice tab's total.</para>
/// <para>Any other line keeps the hand-over's line total to the paisa. The inputs are chosen so that
/// the Invoice tab's recompute gives that same total when the bill is opened for edit, and none of
/// them is below zero:</para>
/// <list type="number">
/// <item>Quantity: the net weight, as the Invoice tab takes it whatever the item's unit, which only labels it.
/// A hand-over's weights are grams, so a line of an item counted in carats fails the bill.</item>
/// <item>A gold line is priced on the bill's 24K rate and its karat's purity. That gold part is
/// fixed, so it comes first; if a line's total is less than its gold at the bill's rate, the bill's
/// rate is lowered (to whole rupees) until every gold line fits.</item>
/// <item>Then the hand-over's wastage % (if the item is priced with wastage; a wastage below zero
/// counts as none), then its labour as rupees a gram (if the item is priced with labour), each cut
/// down to what still fits.</item>
/// <item>Whatever is left (other stones, setting, hallmarking, GST, the difference between the two programs'
/// purities, the discount, the paise) is the line's Extra.</item>
/// <item>A diamond line is one flat rate a unit, rounded down to the paisa, and the paise left go to Extra.</item>
/// <item>A gold line's diamonds (their carats and value, if the hand-over gives them) become a diamond
/// line of their own on the Diamond item, right after it, and the gold line is priced to what is left.</item>
/// </list>
/// The result is checked by opening it the way Bills → Edit does and recomputing every line.
/// </summary>
internal static class HandoverBillMapper
{
    public static HandoverMapResult Map(HandoverBill bill, HandoverMasters masters)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(masters);

        var resolved = new List<ResolvedLine>(bill.Lines.Count);
        foreach (var line in bill.Lines)
        {
            var itemName = line.ItemName.Trim();
            var item = masters.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Name?.Trim(), itemName, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                return HandoverMapResult.Refused($"No item named “{itemName}” in Tally Wrapper’s items.");
            }

            var isDiamond = string.Equals(item.ItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase);
            KaratMasterRowVm? karat = null;
            var purity = 0m;
            if (!isDiamond)
            {
                if (line.Karat is { } label)
                {
                    karat = masters.Karats.FirstOrDefault(candidate => SameKarat(candidate.Label, label));
                    if (karat is null)
                    {
                        return HandoverMapResult.Refused($"No karat “{label.Trim()}” in Tally Wrapper’s karats.");
                    }
                }
                else
                {
                    karat = DefaultKaratOf(item, masters.Karats);
                    if (karat is null)
                    {
                        return HandoverMapResult.Refused($"“{item.Name.Trim()}” has no karat on the bill, and Tally Wrapper has no karat for that item.");
                    }
                }

                purity = ParsePercent(karat.PurityPercent);
            }

            // The Invoice tab's quantity is the net weight, whatever the item's unit says. The hand-over's
            // weights are grams, so an item counted in carats cannot take them.
            if (string.Equals(item.Unit?.Trim(), ItemUnits.Carat, StringComparison.OrdinalIgnoreCase))
            {
                return HandoverMapResult.Refused($"“{item.Name.Trim()}” is counted in carats, and a handed-over line is weighed in grams.");
            }

            var quantity = line.NetWeight;
            if (quantity <= 0m)
            {
                return HandoverMapResult.Refused($"A line of “{item.Name.Trim()}” has no net weight.");
            }

            // A gold line's diamonds are a line of their own, on the diamond item, right after it.
            if (isDiamond || line.DiamondValue <= 0m)
            {
                resolved.Add(new ResolvedLine(line, item, karat, isDiamond, purity, quantity, line.LineTotal));
                continue;
            }

            if (line.DiamondCarats <= 0m)
            {
                return HandoverMapResult.Refused($"The diamonds on a line of “{item.Name.Trim()}” have no carats.");
            }

            var diamondItem = DiamondItemOf(masters.Items);
            if (diamondItem is null)
            {
                return HandoverMapResult.Refused("A line has diamonds. Tally Wrapper bills them on its Diamond item: one item in the Diamond category counted in ct, or of several, the one named Diamond.");
            }

            resolved.Add(new ResolvedLine(line, item, karat, false, purity, quantity, line.LineTotal - line.DiamondValue));
            resolved.Add(new ResolvedLine(line, diamondItem, null, true, 0m, line.DiamondCarats, line.DiamondValue));
        }

        var rate24Kt = ChooseRate(resolved, masters.Karats);
        var rows = new List<BillLineViewModel>(resolved.Count);
        foreach (var line in resolved)
        {
            if (AsTyped(line, rate24Kt, masters.Karats) is { } typed)
            {
                rows.Add(typed);
                continue;
            }

            var row = line.IsDiamond ? DiamondRow(line, MaxRatePerUnit(line)) : GoldRow(line, rate24Kt ?? 0m, cutToFit: true);
            Price(row, line.Total, rate24Kt, masters.Karats);
            if (row.LineTotal != line.Total)
            {
                return HandoverMapResult.Refused($"A line of “{line.Item.Name.Trim()}” could not be priced to its total.");
            }

            rows.Add(row);
        }

        var totals = BillCalculator.BuildTotals(rows.Select(row => row.LineTotal), discount: 0m);
        var payload = InvoicePayloadMapper.BuildPayload(
            rows,
            DateTimeOffset.Now,
            bill.PartyName,
            narration: string.Empty,
            bill.Payment,
            rate24Kt,
            totals.SubtotalBase,
            totals.Discount,
            totals.Cgst,
            totals.Sgst,
            totals.RoundOff,
            totals.GrandTotal) with
        {
            BillDate = bill.BillDate,
        };

        if (!ReopensToSameTotals(payload, masters))
        {
            return HandoverMapResult.Refused("This bill could not be priced so that it reopens with the same totals.");
        }

        try
        {
            BillValidator.Validate(payload);
        }
        catch (BillValidationException ex)
        {
            return HandoverMapResult.Refused(ex.Errors.FirstOrDefault() ?? ex.Message);
        }

        return new HandoverMapResult(payload, null);
    }

    /// <summary>
    /// The bill's 24K rate: the rate its gold lines share, else the first one's that has one; then
    /// lowered, if need be, until every gold line either is billed as typed at it or has a gold part
    /// no more than its total. Lowering it can take a line out of its rounding allowance, so the lines
    /// are gone through again until none lowers it.
    /// </summary>
    private static decimal? ChooseRate(IReadOnlyList<ResolvedLine> lines, IReadOnlyList<KaratMasterRowVm> karats)
    {
        var gold = lines.Where(line => !line.IsDiamond).ToList();
        var rate = gold.Select(line => line.Source.Rate).FirstOrDefault(value => value is > 0m);
        if (rate is not { } chosen)
        {
            return null;
        }

        chosen = Math.Round(chosen, 2, MidpointRounding.AwayFromZero);
        for (var lowered = true; lowered;)
        {
            lowered = false;
            foreach (var line in gold)
            {
                if (line.Purity <= 0m || AsTyped(line, chosen, karats) is not null)
                {
                    continue;
                }

                var ceiling = MaxRatePerUnit(line);
                if (Paise(chosen * line.Purity / 100m) > ceiling)
                {
                    chosen = Math.Floor(ceiling * 100m / line.Purity);
                    lowered = true;
                }
            }
        }

        return chosen;
    }

    /// <summary>
    /// The line as the counter would type it (see the class summary), or null when the hand-over gives
    /// it no rounding allowance, it has a discount, or it does not come within the allowance of its total.
    /// </summary>
    private static BillLineViewModel? AsTyped(ResolvedLine line, decimal? rate24Kt, IReadOnlyList<KaratMasterRowVm> karats)
    {
        var source = line.Source;
        if (source.RoundingAllowance <= 0m || source.DiscountAmount > 0m)
        {
            return null;
        }

        BillLineViewModel row;
        var extra = 0m;
        if (line.IsDiamond)
        {
            row = DiamondRow(line, Paise(line.Total / line.Quantity));
        }
        else
        {
            row = GoldRow(line, rate24Kt ?? 0m, cutToFit: false);
            extra = Math.Max(0m, source.StoneValue - source.DiamondValue) + source.HallmarkAmount
                    + (TakesLabour(PricingModeOf(line.Item)) ? 0m : source.LabourAmount);
        }

        row.Extra = extra > 0m ? Paise(extra) : null;
        var result = Compute(row, rate24Kt, karats);
        // The Invoice tab's own rounding: the rate a unit and the labour a unit to the paisa, then the line.
        if (Math.Abs(result.LineTotalInclusive - line.Total) > source.RoundingAllowance + 0.01m * line.Quantity + 0.01m)
        {
            return null;
        }

        row.EffectiveRate = result.EffectiveRate;
        row.LineTotal = result.LineTotalInclusive;
        return row;
    }

    /// <summary>
    /// A gold line's row: the hand-over's wastage % and its labour as rupees a unit, each cut down to
    /// what still fits under the line's total when <paramref name="cutToFit"/>.
    /// </summary>
    private static BillLineViewModel GoldRow(ResolvedLine line, decimal rate24Kt, bool cutToFit)
    {
        var mode = PricingModeOf(line.Item);
        var ceiling = MaxRatePerUnit(line);
        var gold = rate24Kt * line.Purity / 100m;

        var wastagePercent = 0m;
        if (mode != PricingModes.Labour && rate24Kt > 0m)
        {
            var wanted = Math.Max(0m, Paise(line.Source.WastagePercent));
            var fits = Math.Floor((ceiling - gold) * 100m / rate24Kt * 100m) / 100m;
            wastagePercent = cutToFit ? Math.Clamp(wanted, 0m, Math.Max(0m, fits)) : wanted;
        }

        var wastage = rate24Kt * wastagePercent / 100m;
        var labourPerUnit = 0m;
        if (TakesLabour(mode))
        {
            var wanted = Paise(line.Source.LabourAmount / line.Quantity);
            var fits = Math.Floor((ceiling - gold - wastage) * 100m) / 100m;
            labourPerUnit = cutToFit ? Math.Clamp(wanted, 0m, Math.Max(0m, fits)) : wanted;
        }

        // As the Invoice tab does it: the item first, which fills its name, unit, category and
        // pricing mode, then the karat, then what was weighed and charged.
        var row = new BillLineViewModel { ItemMaster = line.Item };
        row.KaratMaster = line.Karat;
        var gross = Math.Max(line.Source.GrossWeight, line.Quantity);
        row.GrossWeight = gross;
        row.LessWeight = gross > line.Quantity ? gross - line.Quantity : null;

        row.WastagePercent = wastagePercent;
        row.LabourPerUnit = labourPerUnit;
        row.DiamondRate = null;
        return row;
    }

    private static BillLineViewModel DiamondRow(ResolvedLine line, decimal rate)
    {
        var row = new BillLineViewModel { ItemMaster = line.Item };
        row.GrossWeight = line.Quantity;
        row.DiamondRate = rate;
        return row;
    }

    /// <summary>Works the line out as the Invoice tab does, then puts what is left of the hand-over's total in Extra.</summary>
    private static void Price(BillLineViewModel row, decimal target, decimal? rate24Kt, IReadOnlyList<KaratMasterRowVm> karats)
    {
        row.Extra = null;
        var withoutExtra = Compute(row, rate24Kt, karats);
        var extra = target - withoutExtra.LineTotalInclusive;
        row.Extra = extra > 0m ? extra : null;

        var result = Compute(row, rate24Kt, karats);
        row.EffectiveRate = result.EffectiveRate;
        row.LineTotal = result.LineTotalInclusive;
    }

    private static BillCalculator.LineResult Compute(BillLineViewModel row, decimal? rate24Kt, IReadOnlyList<KaratMasterRowVm> karats)
        => BillCalculator.ComputeLine(InvoicePayloadMapper.BuildCalculatorInputs(
            row,
            rate24Kt ?? 0m,
            InvoicePayloadMapper.ResolvePurityPercent(row, karats)));

    private static string PricingModeOf(ItemMasterRowVm item)
        => string.IsNullOrWhiteSpace(item.PricingMode) ? PricingModes.Wastage : item.PricingMode;

    private static bool TakesLabour(string mode) => mode is PricingModes.Labour or PricingModes.Both;

    /// <summary>Opens the payload the way Bills → Edit does and recomputes each line with the Invoice tab's calculator.</summary>
    internal static bool ReopensToSameTotals(BillPayloadDto payload, HandoverMasters masters)
    {
        var rows = InvoiceEditMapper.CreateRows(payload, masters.Items, masters.Karats);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var result = BillCalculator.ComputeLine(InvoicePayloadMapper.BuildCalculatorInputs(
                row,
                payload.Rate24Kt ?? 0m,
                InvoicePayloadMapper.ResolvePurityPercent(row, masters.Karats)));
            if (result.LineTotalInclusive != payload.Lines[index].LineTotal
                || result.EffectiveRate != payload.Lines[index].Rate)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The highest rate a unit the line can carry without its total going over the hand-over's.</summary>
    private static decimal MaxRatePerUnit(ResolvedLine line)
        => Math.Floor(line.Total / line.Quantity * 100m) / 100m;

    /// <summary>
    /// The item a gold line's diamonds are billed on: the one Diamond item counted in carats, or of
    /// several, the one named Diamond.
    /// </summary>
    private static ItemMasterRowVm? DiamondItemOf(IReadOnlyList<ItemMasterRowVm> items)
    {
        var diamonds = items
            .Where(item => string.Equals(item.ItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(item.Unit?.Trim(), ItemUnits.Carat, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (diamonds.Count == 1)
        {
            return diamonds[0];
        }

        var named = diamonds.Where(item => string.Equals(item.Name?.Trim(), "Diamond", StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>
    /// A line with no stamp takes the item's own karat. Tally Wrapper's items have no karat field,
    /// so the item's karat is the one karat whose Tally stock item is the item's Tally stock item.
    /// </summary>
    private static KaratMasterRowVm? DefaultKaratOf(ItemMasterRowVm item, IReadOnlyList<KaratMasterRowVm> karats)
    {
        var stock = item.DefaultStockMappingLabel?.Trim();
        if (string.IsNullOrEmpty(stock))
        {
            return null;
        }

        var matches = karats
            .Where(karat => string.Equals(karat.TallyItem?.Trim(), stock, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Karat labels match ignoring case and spaces, so "22 K" finds "22K".</summary>
    private static bool SameKarat(string? master, string handedOver)
        => string.Equals(WithoutSpaces(master), WithoutSpaces(handedOver), StringComparison.OrdinalIgnoreCase);

    private static string WithoutSpaces(string? value)
        => string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));

    private static decimal ParsePercent(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

    private static decimal Paise(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>A bill row to make: a hand-over line, or the gold or the diamond part of one, priced to <paramref name="Total"/>.</summary>
    private sealed record ResolvedLine(
        HandoverLine Source,
        ItemMasterRowVm Item,
        KaratMasterRowVm? Karat,
        bool IsDiamond,
        decimal Purity,
        decimal Quantity,
        decimal Total);
}
