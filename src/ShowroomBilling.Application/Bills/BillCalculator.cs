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
    public const decimal CgstRate = 0.015m;
    public const decimal SgstRate = 0.015m;
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

    public static LineResult ComputeLine(LineInputs line)
    {
        var effRate = ComputeEffectiveRate(line);
        var lineIncl = Math.Round(line.NetWeight * effRate + line.ExtraCharges,
            2, MidpointRounding.AwayFromZero);
        return new LineResult(effRate, lineIncl);
    }

    public static BillTotals BuildTotals(IEnumerable<decimal> lineInclusiveTotals, decimal discount)
    {
        var subIncl = 0m;
        foreach (var t in lineInclusiveTotals) subIncl += t;

        return BuildTotals(subIncl, discount);
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
