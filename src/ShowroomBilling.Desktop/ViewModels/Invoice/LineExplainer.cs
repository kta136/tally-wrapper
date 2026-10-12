using ShowroomBilling.Application.Bills;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

/// <summary>
/// How the focused line was priced, in the three sentences of the strip under the table:
/// the net weight, the rate and what it is made of, and the line total. When the line cannot
/// be priced yet, <see cref="Problem"/> says what to fill in instead.
/// </summary>
public sealed record LineExplanation(
    string RowLabel,
    string ItemName,
    string NetValue,
    string NetFormula,
    string RateValue,
    string RateFormula,
    string LineValue,
    string LineFormula,
    string? Problem);

internal static class LineExplainer
{
    public static LineExplanation? Describe(BillLineViewModel? line, decimal rate24Kt, decimal purityPercent)
    {
        if (line is null || line.IsEmpty)
        {
            return null;
        }

        var unit = UnitLabel(line.Unit);
        var net = line.NetWeight;
        var netFormula = line.IsGoldLine && (line.LessWeight ?? 0m) > 0m
            ? $" = {UiCulture.Weight(line.GrossWeight ?? 0m)} − {UiCulture.Weight(line.LessWeight ?? 0m)}"
            : string.Empty;

        var inputs = InvoicePayloadMapper.BuildCalculatorInputs(line, rate24Kt, purityPercent);
        var breakdown = BillCalculator.ComputeRateBreakdown(inputs);
        var result = BillCalculator.ComputeLine(inputs);

        var rateFormula = line.IsDiamond
            ? $"/{unit}, one flat rate with GST included"
            : $"/{unit} = {UiCulture.Amount(breakdown.Gold)} gold"
              + (breakdown.Wastage > 0m ? $" + {UiCulture.Amount(breakdown.Wastage)} wastage" : string.Empty)
              + (breakdown.Labour > 0m ? $" + {UiCulture.Amount(breakdown.Labour)} labour" : string.Empty);

        var extra = line.Extra ?? 0m;
        var lineFormula = $" = {UiCulture.Weight(net)} × {UiCulture.Amount(breakdown.Total)}"
            + (extra != 0m ? $" + {UiCulture.Amount(extra)} extra" : string.Empty);

        return new LineExplanation(
            $"Row {line.RowNumber}",
            line.ItemName.Trim(),
            $"{UiCulture.Weight(net)} {unit}",
            netFormula,
            $"₹{UiCulture.Amount(breakdown.Total)}",
            rateFormula,
            $"₹{UiCulture.Amount(result.LineTotalInclusive)}",
            lineFormula,
            ProblemFor(line, rate24Kt, purityPercent));
    }

    private static string? ProblemFor(BillLineViewModel line, decimal rate24Kt, decimal purityPercent)
    {
        if (line.IsDiamond)
        {
            return line.DiamondRate is > 0m ? null : "Enter the flat rate per carat to price this line.";
        }

        if (string.IsNullOrWhiteSpace(line.Karat) || purityPercent <= 0m)
        {
            return "Pick a karat to price this line.";
        }

        return rate24Kt > 0m ? null : "Enter today's 24K rate to price this line.";
    }

    internal static string UnitLabel(string? unit)
        => string.Equals(unit, "gm", StringComparison.OrdinalIgnoreCase) ? "g" : unit ?? string.Empty;
}
