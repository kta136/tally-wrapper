namespace ShowroomBilling.Contracts.Runtime;

public sealed record RuntimeBootstrapResponse(
    string ProductName,
    string EnvironmentName,
    string ApiVersion,
    string SettingsSource,
    string ActiveShowroom,
    string? CounterName,
    IReadOnlyList<string> Notes,
    string? DatabaseIdentity = null,
    string? ExpectedDatabaseIdentity = null,
    bool? DatabaseIdentityMatches = null);
