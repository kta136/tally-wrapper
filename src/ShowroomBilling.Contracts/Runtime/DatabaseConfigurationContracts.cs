namespace ShowroomBilling.Contracts.Runtime;

/// <summary>
/// The state of the API's database configuration. It never carries the
/// connection string itself, only <see cref="MaskedConnectionString"/>.
/// </summary>
public sealed record DatabaseConfigurationResponse(
    string Provider,
    string MaskedConnectionString,
    string ConfigPath,
    bool IsLocalOverridePresent,
    bool RequiresApiRestart,
    string EnvironmentName = "",
    bool IsEnvironmentOverridePresent = false,
    string StorageProtection = "",
    bool CanBootstrapWithoutAdmin = false);

public sealed record UpdateDatabaseConfigurationRequest(string ConnectionString);

public sealed record TestDatabaseConfigurationRequest(string ConnectionString);

public sealed record DatabaseConfigurationTestResponse(
    bool Success,
    string Message);
