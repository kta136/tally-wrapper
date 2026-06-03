using ShowroomBilling.Contracts.Clients;

namespace ShowroomBilling.Desktop.Services;

public interface IClientPresenceApiClient
{
    Task SendHeartbeatAsync(ClientHeartbeatRequest request, CancellationToken cancellationToken = default);
}
