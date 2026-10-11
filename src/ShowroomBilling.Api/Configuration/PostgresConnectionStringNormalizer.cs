using Npgsql;

namespace ShowroomBilling.Api.Configuration;

public static class PostgresConnectionStringNormalizer
{
    /// <summary>
    /// Accepts an Npgsql keyword string (<c>Host=…;Database=…;Username=…;Password=…</c>)
    /// and returns it in the builder's canonical spelling.
    /// </summary>
    public static bool TryNormalize(string input, out string connectionString, out string error)
    {
        connectionString = string.Empty;
        error = string.Empty;

        var candidate = input.Trim();
        if (candidate.Length == 0)
        {
            error = "PostgreSQL connection string is required.";
            return false;
        }

        // Also catches a pasted command line such as: psql 'postgresql://...'
        if (candidate.Contains("postgres://", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            error = "A postgres:// URL is not accepted. Enter the connection string as Host=...;Port=...;Database=...;Username=...;Password=...";
            return false;
        }

        try
        {
            connectionString = new NpgsqlConnectionStringBuilder(candidate).ConnectionString;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
