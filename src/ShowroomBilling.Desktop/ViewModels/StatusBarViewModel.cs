using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ShowroomBilling.Desktop.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    private readonly DispatcherTimer _clock;

    [ObservableProperty]
    private string statusText = "READY";

    [ObservableProperty]
    private string rate24Kt = "7,780";

    [ObservableProperty]
    private string rate22Kt = "7,125";

    [ObservableProperty]
    private string rate18Kt = "5,830";

    [ObservableProperty]
    private int lineCount = 0;

    [ObservableProperty]
    private string lastSaved = "—";

    [ObservableProperty]
    private string databaseEnvironment = "DB ?";

    [ObservableProperty]
    private string fiscalYear = "FY 2025-26";

    [ObservableProperty]
    private string workstation = "WS: COUNTER-01";

    [ObservableProperty]
    private string time = DateTime.Now.ToString("HH:mm:ss");

    public StatusBarViewModel()
    {
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

    private static string NormalizeDatabaseEnvironment(string? environmentName) =>
        string.IsNullOrWhiteSpace(environmentName)
            ? string.Empty
            : environmentName.Trim();
}
