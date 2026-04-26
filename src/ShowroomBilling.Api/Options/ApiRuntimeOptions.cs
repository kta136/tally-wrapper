namespace ShowroomBilling.Api.Options;

public sealed class ApiRuntimeOptions
{
    public const string SectionName = "Runtime";

    public string ProductName { get; init; } = "Showroom Billing V2";

    public string ApiVersion { get; init; } = "1.0";

    public string DefaultShowroomName { get; init; } = "Development Showroom";
}
