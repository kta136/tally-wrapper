namespace ShowroomBilling.Contracts.Admin;

public sealed record AdminPasscodeStatusResponse(bool IsConfigured);

public sealed record AdminSetPasscodeRequest(
    string? CurrentPasscode,
    string NewPasscode);

public sealed record AdminUnlockRequest(
    string Passcode,
    string? ActorLabel);

public sealed record AdminUnlockResponse(
    string Token,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string ActorLabel);

public sealed record AdminSessionInfoResponse(
    Guid SessionId,
    string ActorLabel,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record AdminLogoutRequest(string Token);
