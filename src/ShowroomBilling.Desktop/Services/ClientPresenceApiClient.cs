using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Clients;

namespace ShowroomBilling.Desktop.Services;

public sealed class ClientPresenceApiClient(HttpClient httpClient) : IClientPresenceApiClient
{
    public async Task SendHeartbeatAsync(
        ClientHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/clients/heartbeat",
            request,
            cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
    }
}
