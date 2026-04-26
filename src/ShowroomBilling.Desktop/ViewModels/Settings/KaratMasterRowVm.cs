using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class KaratMasterRowVm : ObservableObject
{
    [ObservableProperty] private string label = string.Empty;
    [ObservableProperty] private string purityPercent = "0";
    [ObservableProperty] private string tallyItem = string.Empty;

    public static KaratMasterRowVm FromEntry(KaratMasterEntry e) => new()
    {
        Label = e.Label,
        PurityPercent = e.PurityPercent.ToString(CultureInfo.InvariantCulture),
        TallyItem = e.TallyItem ?? string.Empty,
    };

    public bool TryBuildEntry(out KaratMasterEntry entry, out string error)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(Label)) { error = "Karat row: label is required."; return false; }
        if (!decimal.TryParse(PurityPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var purity) || purity is < 0 or > 100)
        { error = $"Karat '{Label}': purity % must be between 0 and 100."; return false; }
        if (string.IsNullOrWhiteSpace(TallyItem))
        { error = $"Karat '{Label}': Tally stock item is required."; return false; }

        entry = new KaratMasterEntry(
            Label: Label.Trim(),
            PurityPercent: purity,
            TallyItem: TallyItem.Trim());
        error = string.Empty;
        return true;
    }
}
