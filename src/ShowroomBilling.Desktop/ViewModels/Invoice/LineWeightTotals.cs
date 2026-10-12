using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

/// <summary>
/// The Gross, Less and Net columns added up, one sum for the grams and one for the carats,
/// because adding a gold chain's grams to a diamond's carats gives a number that means nothing.
/// The totals row shows a grams line unless the bill is carats only, and a carats line only when
/// a line uses carats; an empty bill shows zero grams.
/// </summary>
public sealed record LineWeightTotals(
    string GramGross,
    string GramLess,
    string GramNet,
    string CaratGross,
    string CaratNet,
    bool HasGrams,
    bool HasCarats)
{
    private static readonly string Zero = UiCulture.Weight(0m);

    public static LineWeightTotals Empty { get; } = new(Zero, Zero, Zero, Zero, Zero, false, false);

    public bool ShowGramLine => HasGrams || !HasCarats;
    public bool ShowCaratLine => HasCarats;

    public static LineWeightTotals Of(IEnumerable<BillLineViewModel> lines)
    {
        decimal gramGross = 0m, gramLess = 0m, gramNet = 0m, caratGross = 0m, caratNet = 0m;
        var anyGram = false;
        var anyCarat = false;

        foreach (var line in lines.Where(line => !line.IsEmpty))
        {
            if (string.Equals(line.Unit, ItemUnits.Carat, StringComparison.OrdinalIgnoreCase))
            {
                anyCarat = true;
                caratGross += line.GrossWeight ?? 0m;
                caratNet += line.NetWeight;
            }
            else
            {
                anyGram = true;
                gramGross += line.GrossWeight ?? 0m;
                gramLess += line.LessWeight ?? 0m;
                gramNet += line.NetWeight;
            }
        }

        return new LineWeightTotals(
            UiCulture.Weight(gramGross),
            UiCulture.Weight(gramLess),
            UiCulture.Weight(gramNet),
            UiCulture.Weight(caratGross),
            UiCulture.Weight(caratNet),
            anyGram,
            anyCarat);
    }
}
