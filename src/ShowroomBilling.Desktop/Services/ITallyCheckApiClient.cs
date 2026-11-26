using ShowroomBilling.Contracts.Tally;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// Settings → Tally check: the server reads the year's sales vouchers from Tally and sets them
/// against the bills. A read; it changes nothing on either side.
/// </summary>
public interface ITallyCheckApiClient
{
    /// <summary>Compares a fiscal year ("2026-27"), or the current one when none is named.</summary>
    Task<TallyComparisonResponse> CompareAsync(string? fiscalYear = null, CancellationToken cancellationToken = default);
}
