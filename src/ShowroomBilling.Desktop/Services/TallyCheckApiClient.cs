using System.Net.Http;
using ShowroomBilling.Contracts.Tally;

namespace ShowroomBilling.Desktop.Services;

public sealed class TallyCheckApiClient(HttpClient httpClient) : ITallyCheckApiClient
{
    /// <summary>
    /// How long a compare may take. Tally answers with every sales voucher of the year in one
    /// reply, which on a busy year takes longer than the usual call is given. The client is
    /// registered with this limit.
    /// </summary>
    public static readonly TimeSpan CompareTimeout = TimeSpan.FromSeconds(90);

    public Task<TallyComparisonResponse> CompareAsync(string? fiscalYear = null, CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<TallyComparisonResponse>(
            string.IsNullOrWhiteSpace(fiscalYear)
                ? "/api/tally-check/compare"
                : $"/api/tally-check/compare?fiscalYear={Uri.EscapeDataString(fiscalYear)}",
            cancellationToken);
}
