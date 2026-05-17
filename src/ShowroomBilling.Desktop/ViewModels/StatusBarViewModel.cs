using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    private const string EmptyDisplay = "—";
    // Standard Indian jewellery purities used as fallback when the karat-master
    // sheet doesn't expose a matching row (e.g. before Settings has loaded).
    private const decimal Default22KtPurity = 91.6m;
    private const decimal Default18KtPurity = 75.0m;

    private readonly DispatcherTimer _clock;
    private decimal? _activeRate24Kt;
    private decimal _purity22 = Default22KtPurity;
    private decimal _purity18 = Default18KtPurity;

    [ObservableProperty]
    private string statusText = "READY";

    [ObservableProperty]
    private string rate24Kt = EmptyDisplay;

    [ObservableProperty]
    private string rate22Kt = EmptyDisplay;

    [ObservableProperty]
    private string rate18Kt = EmptyDisplay;

    [ObservableProperty]
    private int lineCount = 0;

    [ObservableProperty]
    private string lastSaved = EmptyDisplay;

    [ObservableProperty]
    private string databaseEnvironment = "DB ?";

    [ObservableProperty]
    private string fiscalYear;

    [ObservableProperty]
    private string workstation;

    [ObservableProperty]
    private string time = DateTime.Now.ToString("HH:mm:ss");

    public StatusBarViewModel()
    {
        fiscalYear = $"FY {InvoiceNumberFormatter.ComputeFiscalYear(DateTimeOffset.UtcNow)}";
        workstation = $"WS: {Environment.MachineName}";

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Time = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();
    }

    public void ApplyDatabaseIdentity(string? databaseIdentity, string? environmentName)
    {
        _ = environmentName;
        var identity = NormalizeDatabaseEnvironment(databaseIdentity);
        if (identity.Equals("UNSET", StringComparison.OrdinalIgnoreCase))
        {
            DatabaseEnvironment = "DB UNSET";
            return;
        }

        if (!string.IsNullOrWhiteSpace(identity))
        {
            DatabaseEnvironment = $"DB {identity.ToUpperInvariant()}";
            return;
        }

        DatabaseEnvironment = "DB ?";
    }

    /// <summary>
    /// Pushes the operator's typed 24kt rate into the status bar and re-derives
    /// the lower-purity displays. <paramref name="karatMasters"/> is consulted
    /// for the purity %; missing/unparseable rows fall back to standard Indian
    /// jewellery purities (91.6 % / 75.0 %).
    /// </summary>
    public void ApplyRate24Kt(decimal? rate24Kt, IEnumerable<KaratMasterRowVm>? karatMasters)
    {
        _activeRate24Kt = rate24Kt;
        (_purity22, _purity18) = ResolvePurities(karatMasters);
        RecomputeRateDisplays();
    }

    public void ApplyLineCount(int count) => LineCount = Math.Max(0, count);

    public void ApplyLastSaved(DateTimeOffset? savedAt)
    {
        LastSaved = savedAt is { } at
            ? at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : EmptyDisplay;
    }

    private void RecomputeRateDisplays()
    {
        if (_activeRate24Kt is not { } rate24 || rate24 <= 0m)
        {
            Rate24Kt = EmptyDisplay;
            Rate22Kt = EmptyDisplay;
            Rate18Kt = EmptyDisplay;
            return;
        }

        Rate24Kt = FormatRate(rate24);
        Rate22Kt = FormatRate(rate24 * _purity22 / 100m);
        Rate18Kt = FormatRate(rate24 * _purity18 / 100m);
    }

    private static string FormatRate(decimal value) =>
        Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture);

    private static (decimal Purity22, decimal Purity18) ResolvePurities(IEnumerable<KaratMasterRowVm>? karatMasters)
    {
        if (karatMasters is null) return (Default22KtPurity, Default18KtPurity);

        var p22 = Default22KtPurity;
        var p18 = Default18KtPurity;
        foreach (var row in karatMasters)
        {
            if (!decimal.TryParse(row.PurityPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var purity))
                continue;
            if (purity is <= 0m or > 100m) continue;

            if (LabelMatchesKarat(row.Label, 22)) p22 = purity;
            else if (LabelMatchesKarat(row.Label, 18)) p18 = purity;
        }
        return (p22, p18);
    }

    private static bool LabelMatchesKarat(string? label, int karat)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var span = label.AsSpan().Trim();
        // Pull the leading digits — accepts "22", "22KT", "22kt", "22 K", "22-karat" etc.
        var digits = 0;
        var consumed = 0;
        foreach (var ch in span)
        {
            if (!char.IsDigit(ch)) break;
            digits = digits * 10 + (ch - '0');
            consumed++;
        }
        return consumed > 0 && digits == karat;
    }

    private static string NormalizeDatabaseEnvironment(string? environmentName) =>
        string.IsNullOrWhiteSpace(environmentName)
            ? string.Empty
            : environmentName.Trim();
}
