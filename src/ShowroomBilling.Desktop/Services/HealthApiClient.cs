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
        bool includeMasterFreshness = true,
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

        // forceDatabase is for a server older than this Desktop, which answers
        // without checking its database unless asked to. A current server
        // always checks and ignores it.
        var runtime = await TryGetAsync<RuntimeHealthResponse>("/api/runtime/health?forceDatabase=true", cancellationToken);
        var masters = includeMasterFreshness
            ? await TryGetAsync<MasterFreshnessSummaryResponse>("/api/health/masters", cancellationToken)
            : null;
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
