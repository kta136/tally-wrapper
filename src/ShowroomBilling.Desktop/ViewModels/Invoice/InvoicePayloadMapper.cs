using System.Globalization;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

internal static class InvoicePayloadMapper
{
    public static BillCalculator.LineInputs BuildCalculatorInputs(
        BillLineViewModel line,
        decimal rate24Kt,
        decimal purityPercent) => new(
            Rate24Kt: rate24Kt,
            PurityPercent: purityPercent,
            WastagePercent: line.WastagePercent ?? 0m,
            LabourPerUnit: line.LabourPerUnit ?? 0m,
            NetWeight: line.NetWeight,
            ExtraCharges: line.Extra ?? 0m,
            PricingMode: line.ResolvedPricingMode,
            IsDiamond: line.IsDiamond,
            DiamondRate: line.DiamondRate ?? 0m);

    public static BillPayloadDto BuildPayload(
        IEnumerable<BillLineViewModel> lines,
        DateTimeOffset billDate,
        string partyName,
        string narration,
        string payment,
        decimal? rate24Kt,
        decimal subtotal,
        decimal discount,
        decimal cgst,
        decimal sgst,
        decimal roundOff,
        decimal grandTotal)
    {
        var mappedLines = lines
            .Where(l => !l.IsEmpty)
            .Select(l => new BillLineItemDto(
                ItemName: l.ItemName,
                HsnCode: "711319",
                Quantity: l.NetWeight,
                Unit: l.Unit,
                Rate: l.EffectiveRate,
                LineTotal: l.LineTotal,
                Karat: l.Karat,
                RawJson: null,
                StockName: ResolveStockName(l),
                GrossWeight: l.GrossWeight,
                LessWeight: l.LessWeight,
                WastagePercent: l.WastagePercent,
                LabourPerUnit: l.LabourPerUnit,
                DiamondRate: l.DiamondRate,
                Extra: l.Extra,
                ItemCategory: l.ResolvedItemCategory,
                PricingMode: l.ResolvedPricingMode))
            .ToList();

        var totals = new BillTotalsDto(
            Subtotal: subtotal,
            DiscountTotal: discount,
            TaxTotal: cgst + sgst,
            RoundOff: roundOff,
            GrandTotal: grandTotal);

        return new BillPayloadDto(
            PartyName: string.IsNullOrWhiteSpace(partyName) ? null : partyName.Trim(),
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: DateOnly.FromDateTime(billDate.LocalDateTime),
            Lines: mappedLines,
            Totals: totals,
            Notes: string.IsNullOrWhiteSpace(narration) ? null : narration,
            Payment: payment,
            Rate24Kt: rate24Kt);
    }

    public static BillPrintContent BuildPrintContent(
        string invoiceNumber,
        BillPayloadDto payload,
        string payment,
        decimal? rate24Kt,
        CompanyProfile company,
        IReadOnlyList<KaratMasterEntry>? karatMappings = null) => new(
            InvoiceNumber: invoiceNumber,
            BillDate: payload.BillDate,
            PartyName: payload.PartyName,
            PartyGstin: payload.PartyGstin,
            PartyPhone: payload.PartyPhone,
            PartyAddress: payload.PartyAddress,
            Payment: payment,
            Rate24Kt: rate24Kt,
            Lines: PrintLineSubstitution.WithTallyMappedItemNames(payload.Lines, karatMappings),
            Totals: payload.Totals,
            Notes: payload.Notes,
            Company: company);

    public static string? ValidateForSave(IEnumerable<BillLineViewModel> lines, decimal? rate24Kt, out bool rateMissing)
    {
        var activeLines = lines.Where(l => !l.IsEmpty).ToList();
        var requiresGoldRate = activeLines.Any(l => !l.IsDiamond);
        rateMissing = requiresGoldRate && rate24Kt is not > 0m;
        if (rateMissing)
        {
            return "24kt rate is required before saving gold lines.";
        }

        var diamondMissingRate = activeLines.Any(l => l.IsDiamond && l.DiamondRate is not > 0m);
        if (diamondMissingRate)
        {
            return "Diamond rate is required for every diamond line.";
        }

        var goldMissingKarat = activeLines.Any(l => !l.IsDiamond && string.IsNullOrWhiteSpace(l.Karat));
        if (goldMissingKarat)
        {
            return "Karat is required for every gold line.";
        }

        return null;
    }

    public static decimal ResolvePurityPercent(
        BillLineViewModel line,
        IEnumerable<KaratMasterRowVm>? karatMasters)
    {
        if (line.KaratMaster is not null
            && decimal.TryParse(line.KaratMaster.PurityPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var p))
            return p;

        if (karatMasters is not null && !string.IsNullOrWhiteSpace(line.Karat))
        {
            var match = karatMasters.FirstOrDefault(k =>
                string.Equals(k.Label, line.Karat, StringComparison.OrdinalIgnoreCase));
            if (match is not null
                && decimal.TryParse(match.PurityPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var mappedPurity))
                return mappedPurity;
        }

        return 0m;
    }

    public static string ResolveLineItemCategory(BillLineItemDto line, ItemMasterRowVm? itemMaster)
    {
        if (HasGoldMakingFields(line))
            return ItemCategories.GoldBased;

        return FirstNonBlank(
            line.ItemCategory,
            itemMaster?.ItemCategory,
            ItemCategories.GoldBased)!;
    }

    public static string ResolveLinePricingMode(BillLineItemDto line, ItemMasterRowVm? itemMaster)
        => FirstNonBlank(
            line.PricingMode,
            itemMaster?.PricingMode,
            PricingModes.Wastage)!;

    private static string? ResolveStockName(BillLineViewModel line)
    {
        if (line.IsDiamond)
        {
            var diamondItemMapped = line.ItemMaster?.DefaultStockMappingLabel;
            if (!string.IsNullOrWhiteSpace(diamondItemMapped)) return diamondItemMapped.Trim();

            if (!string.IsNullOrWhiteSpace(line.StockName)) return line.StockName.Trim();

            return string.IsNullOrWhiteSpace(line.ItemName) ? null : line.ItemName.Trim();
        }

        var karatMapped = line.KaratMaster?.TallyItem;
        if (!string.IsNullOrWhiteSpace(karatMapped)) return karatMapped.Trim();

        var itemMapped = line.ItemMaster?.DefaultStockMappingLabel;
        if (!string.IsNullOrWhiteSpace(itemMapped)) return itemMapped.Trim();

        if (!string.IsNullOrWhiteSpace(line.StockName)) return line.StockName.Trim();

        return null;
    }

    private static bool HasGoldMakingFields(BillLineItemDto line)
        => (line.LessWeight ?? 0m) > 0m
           || (line.WastagePercent ?? 0m) > 0m
           || (line.LabourPerUnit ?? 0m) > 0m;

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
