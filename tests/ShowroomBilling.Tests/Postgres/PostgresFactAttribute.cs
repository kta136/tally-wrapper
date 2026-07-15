using System.Runtime.CompilerServices;

namespace ShowroomBilling.Tests.Postgres;

public sealed class PostgresFactAttribute : FactAttribute
{
    private const string EnabledValue = "1";
    private const string EnvironmentVariable = "SHOWROOM_BILLING_RUN_POSTGRES_TESTS";

    public PostgresFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentVariable),
                EnabledValue,
                StringComparison.Ordinal))
        {
            Skip = $"Set {EnvironmentVariable}=1 to run Docker-backed Postgres tests.";
        }
    }
}
