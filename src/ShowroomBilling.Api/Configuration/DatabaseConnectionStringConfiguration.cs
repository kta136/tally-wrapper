using Microsoft.Extensions.Configuration;

namespace ShowroomBilling.Api.Configuration;

public static class DatabaseConnectionStringConfiguration
{
    private const string PostgresConfigurationKey = "ConnectionStrings:Postgres";

    public static void NormalizePostgresConnectionString(ConfigurationManager configuration)
    {
        var configured = configuration.GetConnectionString("Postgres");
        var normalized = NormalizeOrOriginal(configured);
        if (string.IsNullOrWhiteSpace(normalized)
            || string.Equals(configured, normalized, StringComparison.Ordinal))
        {
            return;
        }

        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PostgresConfigurationKey] = normalized
        });
    }

    public static string GetPostgresConnectionString(IConfiguration configuration) =>
        NormalizeOrOriginal(configuration.GetConnectionString("Postgres"));

    public static string NormalizeOrOriginal(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        return PostgresConnectionStringNormalizer.TryNormalize(
            connectionString,
            out var normalized,
            out _)
            ? normalized
            : connectionString.Trim();
    }
}
