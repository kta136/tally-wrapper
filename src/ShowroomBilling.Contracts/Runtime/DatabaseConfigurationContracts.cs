namespace ShowroomBilling.Contracts.Runtime;

public sealed record DatabaseConfigurationResponse(
    string Provider,
    string ConnectionString,
    string MaskedConnectionString,
    string ConfigPath,
    bool IsLocalOverridePresent,
    bool RequiresApiRestart,
    string EnvironmentName = "",
    bool IsEnvironmentOverridePresent = false,
    string StorageProtection = "");

public sealed record UpdateDatabaseConfigurationRequest(string ConnectionString);

public sealed record TestDatabaseConfigurationRequest(string ConnectionString);

public sealed record DatabaseConfigurationTestResponse(
    bool Success,
    string Message);
