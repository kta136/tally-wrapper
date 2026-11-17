using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Health;

namespace ShowroomBilling.Desktop.Services;

public sealed class HealthApiClient(HttpClient httpClient) : IHealthApiClient
{
    public async Task<SystemHealthSnapshot> GetSnapshotAsync(
        bool includeTallyCompany,
        bool includeMasterFreshness = true,
        CancellationToken cancellationToken = default)
    {
        // The Tally check takes as long as Tally takes, so it is a request of its own, sent
        // alongside: the database's answer does not wait for it.
        var tallyCompany = includeTallyCompany
            ? TryGetTallyCompanyAsync(cancellationToken)
            : Task.FromResult<TallyCompanyHealthResponse?>(null);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(
                includeMasterFreshness ? "/api/health" : "/api/health?masters=false", cancellationToken);
        }
        catch
        {
            return SystemHealthSnapshot.Unreachable();
        }

        using (response)
        {
            // Something answered, so the server is there. If the answer is not the one expected
            // (a server older than this Desktop has no such address), the rest is left unknown.
            HealthResponse? health = null;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    health = await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken);
                }
                catch
                {
                    // Left unknown.
                }
            }

            return new SystemHealthSnapshot(
                ApiReachable: true,
                Masters: health?.Masters,
                TallyCompany: await tallyCompany,
                Runtime: health?.Database);
        }
    }

    private async Task<TallyCompanyHealthResponse?> TryGetTallyCompanyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<TallyCompanyHealthResponse>("/api/health/tally-company", cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
