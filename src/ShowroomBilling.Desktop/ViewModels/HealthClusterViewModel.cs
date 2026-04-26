using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels;

public partial class HealthClusterViewModel : ObservableObject
{
    [ObservableProperty]
    private string cloudDot = "warn";

    [ObservableProperty]
    private string cloudTooltip = "Cloud status unknown";

    [ObservableProperty]
    private string tallyDot = "neutral";

    [ObservableProperty]
    private string tallyTooltip = "Tally posting is manual. Click Push to verify reachability.";

    [ObservableProperty]
    private string databaseDot = "neutral";

    [ObservableProperty]
    private string databaseLabel = "DB ?";

    [ObservableProperty]
    private string databaseTooltip = "Database identity has not been checked yet.";

    public void Apply(SystemState state)
    {
        switch (state)
        {
            case SystemState.Healthy:
                CloudDot = "ok";
                CloudTooltip = "Cloud synced";
                TallyDot = "neutral";
                TallyTooltip = "Tally posting is manual. Click Push to verify reachability.";
                break;
            case SystemState.Degraded:
                // "Degraded" is the startup/pre-check state: we haven't yet
                // confirmed cloud/DB reachability. Show warn (yellow) to
                // distinguish from a confirmed-healthy session.
                CloudDot = "warn";
                CloudTooltip = "Cloud status unverified — waiting for the first health check.";
                TallyDot = "neutral";
                TallyTooltip = "Tally posting is manual. Click Push to verify reachability.";
                break;
            case SystemState.Limited:
                CloudDot = "err";
                CloudTooltip = "Cloud unreachable";
                TallyDot = "err";
                TallyTooltip = "Tally unreachable while cloud is down.";
                break;
        }
    }

    public void Apply(SystemHealthSnapshot snapshot)
    {
        if (!snapshot.ApiReachable)
        {
            CloudDot = "err";
            CloudTooltip = "Cloud unreachable — API offline";
            TallyDot = "err";
            TallyTooltip = "Tally status unknown (API offline)";
            DatabaseDot = "err";
            DatabaseLabel = "DB ?";
            DatabaseTooltip = "Database status unknown (API offline)";
            return;
        }

        ApplyDatabase(snapshot.Runtime);

        CloudDot = "ok";
        CloudTooltip = "Cloud reachable";

        if (snapshot.TallyCompany is not { } tally)
        {
            TallyDot = "neutral";
            TallyTooltip = "Tally status has not been checked yet.";
            return;
        }

        TallyTooltip = tally.Message;
        TallyDot = tally.Status switch
        {
            "healthy" => "ok",
            "warning" => "warn",
            "unhealthy" => "err",
            _ => tally.TallyReachable ? "warn" : "err",
        };
    }

    private void ApplyDatabase(Contracts.Runtime.RuntimeHealthResponse? runtime)
    {
        if (runtime is null)
        {
            DatabaseDot = "warn";
            DatabaseLabel = "DB ?";
            DatabaseTooltip = "Database status has not been checked yet.";
            return;
        }

        var identity = NormalizeDatabaseIdentity(runtime.DatabaseIdentity);
        DatabaseLabel = string.IsNullOrWhiteSpace(identity)
            ? "DB ?"
            : $"DB {identity}";

        if (!runtime.DatabaseReachable)
        {
            DatabaseDot = "err";
            DatabaseTooltip = runtime.Message;
            return;
        }

        if (runtime.DatabaseIdentityMatches == false)
        {
            DatabaseDot = "warn";
            DatabaseTooltip = runtime.Message;
            return;
        }

        DatabaseDot = runtime.DatabaseIdentityMatches == true ? "ok" : "warn";
        DatabaseTooltip = runtime.DatabaseIdentityMatches == true
            ? $"Database marker {DatabaseLabel} matches this API."
            : runtime.Message;
    }

    private static string NormalizeDatabaseIdentity(string? databaseIdentity)
    {
        if (string.IsNullOrWhiteSpace(databaseIdentity))
        {
            return string.Empty;
        }

        return databaseIdentity.Trim().ToUpperInvariant();
    }
}
