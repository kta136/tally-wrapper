using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Application.Bills;

/// <summary>
/// Pure, stateless pricing math shared between the Desktop Invoice tab and the
/// synthetic batch planner. Every line total is GST-inclusive; the aggregate
/// subtotal back-calcs the ex-GST base as Σline_incl / (1 + CGST% + SGST%).
/// Byte-for-byte equivalent to <c>InvoiceViewModel.Recompute()</c> — do not
/// change rounding semantics without updating both sides in lockstep.
/// </summary>
public static class BillCalculator
{
    public const decimal CgstRate = JewelleryGst.CgstRate;
    public const decimal SgstRate = JewelleryGst.SgstRate;
    public static readonly decimal GstFactor = 1m + CgstRate + SgstRate;

    public readonly record struct LineInputs(
        decimal Rate24Kt,
        decimal PurityPercent,
        decimal WastagePercent,
        decimal LabourPerUnit,
        decimal NetWeight,
        decimal ExtraCharges,
        string PricingMode,
        bool IsDiamond,
        decimal DiamondRate);

    public readonly record struct LineResult(decimal EffectiveRate, decimal LineTotalInclusive);

    /// <summary>
    /// What a line's rate per unit is made of. <c>Gold</c> is the rest once wastage and labour
    /// are taken off, so the three parts always add up to <c>Total</c>, the rate that is billed.
    /// A diamond line is all <c>Gold</c>: its flat rate has no parts.
    /// </summary>
    public readonly record struct RateBreakdown(decimal Gold, decimal Wastage, decimal Labour, decimal Total);

    public readonly record struct BillTotals(
        decimal SubtotalBase,
        decimal Cgst,
        decimal Sgst,
        decimal Discount,
        decimal RoundOff,
        decimal GrandTotal);

    public static decimal ComputeEffectiveRate(LineInputs line)
    {
        if (line.IsDiamond)
            return Math.Round(line.DiamondRate, 2, MidpointRounding.AwayFromZero);

        var rate24 = line.Rate24Kt;
        var purityComponent = rate24 * line.PurityPercent / 100m;
        var wastageComponent = line.PricingMode == PricingModes.Labour
            ? 0m
            : rate24 * line.WastagePercent / 100m;
        var labourComponent = line.PricingMode == PricingModes.Labour
                              || line.PricingMode == PricingModes.Both
            ? line.LabourPerUnit
            : 0m;

        return Math.Round(purityComponent + wastageComponent + labourComponent,
            2, MidpointRounding.AwayFromZero);
    }

    public static RateBreakdown ComputeRateBreakdown(LineInputs line)
    {
        var total = ComputeEffectiveRate(line);
        if (line.IsDiamond)
            return new RateBreakdown(total, 0m, 0m, total);

        var wastage = line.PricingMode == PricingModes.Labour
            ? 0m
            : Math.Round(line.Rate24Kt * line.WastagePercent / 100m, 2, MidpointRounding.AwayFromZero);
        var labour = line.PricingMode == PricingModes.Labour || line.PricingMode == PricingModes.Both
            ? Math.Round(line.LabourPerUnit, 2, MidpointRounding.AwayFromZero)
            : 0m;
        return new RateBreakdown(total - wastage - labour, wastage, labour, total);
    }

    public static LineResult ComputeLine(LineInputs line)
    {
        var effRate = ComputeEffectiveRate(line);
        var lineIncl = Math.Round(line.NetWeight * effRate + line.ExtraCharges,
            2, MidpointRounding.AwayFromZero);
        return new LineResult(effRate, lineIncl);
    }

    public static BillTotals BuildTotals(IEnumerable<decimal> lineInclusiveTotals, decimal discount)
    {
        return BuildTotals(lineInclusiveTotals.Sum(), discount);
    }

    public static BillTotals BuildTotals(decimal lineInclusiveTotal, decimal discount)
    {
        var subBase = Math.Round(lineInclusiveTotal / GstFactor, 2, MidpointRounding.AwayFromZero);
        var cgst = Math.Round(subBase * CgstRate, 2, MidpointRounding.AwayFromZero);
        var sgst = Math.Round(subBase * SgstRate, 2, MidpointRounding.AwayFromZero);
        var preRound = subBase + cgst + sgst - discount;
        var grand = Math.Round(preRound, 0, MidpointRounding.AwayFromZero);
        var roundOff = grand - preRound;

        return new BillTotals(subBase, cgst, sgst, discount, roundOff, grand);
    }
}
