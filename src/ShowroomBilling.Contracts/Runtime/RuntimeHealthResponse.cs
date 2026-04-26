namespace ShowroomBilling.Contracts.Runtime;

public sealed record RuntimeHealthResponse(
    string Status,
    bool ApiAvailable,
    bool DatabaseConfigured,
    bool DatabaseReachable,
    bool SettingsLoadedFromApi,
    string Message,
    string? DatabaseIdentity = null,
    string? ExpectedDatabaseIdentity = null,
    bool? DatabaseIdentityMatches = null);
