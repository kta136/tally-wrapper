namespace ShowroomBilling.Contracts.Clients;

public sealed record ClientHeartbeatRequest(
    string DeviceId,
    string AppVersion,
    string MachineName,
    string UserName);

public sealed record ClientPresenceResponse(
    string DeviceId,
    string AppVersion,
    string MachineName,
    string UserName,
    string RemoteAddress,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record ClientPresenceListResponse(
    IReadOnlyList<ClientPresenceResponse> Clients);
