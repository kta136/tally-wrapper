namespace ShowroomBilling.Contracts.Observability;

public static class CorrelationConstants
{
    public const string HeaderName = "X-Correlation-Id";

    public static string NewId() => Guid.NewGuid().ToString("N");
}
