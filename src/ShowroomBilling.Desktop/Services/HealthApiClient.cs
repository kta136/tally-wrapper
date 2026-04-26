using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.Desktop.Services;

public sealed class HealthApiClient(HttpClient httpClient) : IHealthApiClient
{
    public async Task<SystemHealthSnapshot> GetSnapshotAsync(
        bool includeTallyCompany,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await httpClient.GetAsync("/api/health/live", cancellationToken);
        }
        catch
        {
            return SystemHealthSnapshot.Unreachable();
        }

        var runtime = await TryGetAsync<RuntimeHealthResponse>("/api/runtime/health", cancellationToken);
        var masters = await TryGetAsync<MasterFreshnessSummaryResponse>("/api/health/masters", cancellationToken);
        var tallyCompany = includeTallyCompany
            ? await TryGetAsync<TallyCompanyHealthResponse>("/api/health/tally-company", cancellationToken)
            : null;

        return new SystemHealthSnapshot(ApiReachable: true, Masters: masters, TallyCompany: tallyCompany, Runtime: runtime);
    }

    private async Task<T?> TryGetAsync<T>(string requestUri, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await httpClient.GetFromJsonAsync<T>(requestUri, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
