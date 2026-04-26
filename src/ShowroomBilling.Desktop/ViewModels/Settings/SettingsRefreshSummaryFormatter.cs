using ShowroomBilling.Application.Tally;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal static class SettingsRefreshSummaryFormatter
{
    public static string Summarize(IReadOnlyList<TallyMasterRefreshResult> results)
    {
        if (results is null || results.Count == 0)
        {
            return "Refresh returned no results.";
        }

        var failed = results.Where(r => !r.Succeeded).ToList();
        if (failed.Count > 0)
        {
            var first = failed[0];
            var rest = failed.Count > 1 ? $" (+{failed.Count - 1} more)" : string.Empty;
            return $"Refresh failed for {first.MasterType}: {first.ErrorMessage}{rest}";
        }

        var parts = results.Select(r => $"{r.MasterType} ({r.ItemCount})");
        return $"Refreshed · {string.Join(", ", parts)}";
    }
}
