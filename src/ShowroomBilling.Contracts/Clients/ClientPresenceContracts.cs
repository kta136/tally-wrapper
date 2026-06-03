namespace ShowroomBilling.Contracts.Clients;

public sealed record ClientHeartbeatRequest(
    string DeviceId,
    string CounterName,
    string AppVersion,
    string ConnectionMode,
    string MachineName,
    string UserName);

public sealed record ClientPresenceResponse(
    string DeviceId,
    string CounterName,
    string AppVersion,
    string ConnectionMode,
    string MachineName,
    string UserName,
    string RemoteAddress,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record ClientPresenceListResponse(
    DateTimeOffset ServerTimeUtc,
    IReadOnlyList<ClientPresenceResponse> Clients);
