namespace ShowroomBilling.Contracts.Runtime;

public sealed record DatabaseConfigurationRequest(string ConnectionString);

public sealed record DatabaseConfigurationResponse(string MaskedConnectionString, bool RequiresApiRestart);

public sealed record DatabaseConfigurationTestResponse(bool Success, string Message);
