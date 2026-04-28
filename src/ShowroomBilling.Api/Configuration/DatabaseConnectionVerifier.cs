using Npgsql;

namespace ShowroomBilling.Api.Configuration;

public interface IDatabaseConnectionVerifier
{
    Task<DatabaseConnectionVerificationResult> VerifyAsync(
        string connectionString,
        string expectedDatabaseIdentity,
        CancellationToken cancellationToken = default);
}

public sealed record DatabaseConnectionVerificationResult(
    bool Success,
    string Message,
    string? DatabaseIdentity = null)
{
    public static DatabaseConnectionVerificationResult Failed(string message, string? databaseIdentity = null) =>
        new(false, message, databaseIdentity);

    public static DatabaseConnectionVerificationResult Succeeded(string message, string? databaseIdentity = null) =>
        new(true, message, databaseIdentity);
}

public sealed class DatabaseConnectionVerifier : IDatabaseConnectionVerifier
{
    public async Task<DatabaseConnectionVerificationResult> VerifyAsync(
        string connectionString,
        string expectedDatabaseIdentity,
        CancellationToken cancellationToken = default)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            if (!PostgresConnectionStringNormalizer.TryNormalize(connectionString, out var normalized, out var error))
            {
                return DatabaseConnectionVerificationResult.Failed(error);
            }

            builder = new NpgsqlConnectionStringBuilder(normalized);
            builder.Timeout = Math.Min(Math.Max(builder.Timeout, 1), 5);
            builder.CommandTimeout = 5;
        }
        catch (ArgumentException ex)
        {
            return DatabaseConnectionVerificationResult.Failed(ex.Message);
        }

        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var databaseIdentity = await ReadDatabaseIdentityAsync(connection, cancellationToken);
            if (!DatabaseIdentityMatches(databaseIdentity, expectedDatabaseIdentity))
            {
                return DatabaseConnectionVerificationResult.Failed(
                    $"Connection succeeded, but database identity is {databaseIdentity ?? "unavailable"}; expected {expectedDatabaseIdentity}.",
                    databaseIdentity);
            }

            return DatabaseConnectionVerificationResult.Succeeded(
                $"Connection succeeded. Database identity: {databaseIdentity}.",
                databaseIdentity);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return DatabaseConnectionVerificationResult.Failed($"Connection failed: {ex.Message}");
        }
    }

    private static async Task<string?> ReadDatabaseIdentityAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select value from public.database_identity where key = 'environment' limit 1",
            connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    private static bool DatabaseIdentityMatches(string? databaseIdentity, string expectedDatabaseIdentity) =>
        !string.IsNullOrWhiteSpace(databaseIdentity)
        && !databaseIdentity.Trim().Equals("UNSET", StringComparison.OrdinalIgnoreCase)
        && databaseIdentity.Trim().Equals(expectedDatabaseIdentity, StringComparison.OrdinalIgnoreCase);
}
