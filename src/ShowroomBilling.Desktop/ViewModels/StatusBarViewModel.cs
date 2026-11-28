using System;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Desktop.ViewModels;

/// <summary>What the right-hand end of the bottom bar says: fiscal year, workstation and the time.</summary>
public partial class StatusBarViewModel : ObservableObject
{
    private readonly DispatcherTimer _clock;

    [ObservableProperty]
    private string fiscalYear;

    [ObservableProperty]
    private string workstation;

    [ObservableProperty]
    private string time = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

    public StatusBarViewModel()
    {
        fiscalYear = CurrentFiscalYear();
        workstation = $"Workstation {Environment.MachineName}";

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            Time = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            // An app left open across 1 April moves to the new year with the clock.
            FiscalYear = CurrentFiscalYear();
        };
        _clock.Start();
    }

    private static string CurrentFiscalYear()
        => $"FY {InvoiceNumberFormatter.ComputeFiscalYear(DateTimeOffset.UtcNow)}";
}
