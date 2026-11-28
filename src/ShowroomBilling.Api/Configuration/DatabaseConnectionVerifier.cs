using Npgsql;
using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.Api.Configuration;

public interface IDatabaseConnectionVerifier
{
    Task<DatabaseConfigurationTestResponse> VerifyAsync(
        string connectionString,
        string expectedDatabaseIdentity,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseConnectionVerifier : IDatabaseConnectionVerifier
{
    public async Task<DatabaseConfigurationTestResponse> VerifyAsync(
        string connectionString,
        string expectedDatabaseIdentity,
        CancellationToken cancellationToken = default)
    {
        // Limit the probe independently of the connection settings saved for the service.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = 5 };
        builder.Timeout = Math.Clamp(builder.Timeout, 1, 5);

        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var databaseIdentity = await ReadDatabaseIdentityAsync(connection, cancellationToken);
            if (!DatabaseIdentityMatches(databaseIdentity, expectedDatabaseIdentity))
            {
                return new DatabaseConfigurationTestResponse(false,
                    $"Connection succeeded, but database identity is {databaseIdentity ?? "unavailable"}; expected {expectedDatabaseIdentity}.");
            }

            return new DatabaseConfigurationTestResponse(true,
                $"Connection succeeded. Database identity: {databaseIdentity}.");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return new DatabaseConfigurationTestResponse(false, $"Connection failed: {FormatConnectionFailure(ex)}");
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

    /// <summary>The <c>environment</c> marker the database of this environment must carry.</summary>
    internal static string ExpectedIdentityFor(string environmentName) =>
        environmentName.Trim().Equals("Development", StringComparison.OrdinalIgnoreCase)
            ? "DEV"
            : environmentName.Trim().Equals("Production", StringComparison.OrdinalIgnoreCase)
                ? "PROD"
                : environmentName.Trim().ToUpperInvariant();

    /// <summary>
    /// True when the database says which environment it belongs to and it is
    /// the one this API runs as. A production API must not be pointed at the
    /// development database, or the other way round.
    /// </summary>
    internal static bool DatabaseIdentityMatches(string? databaseIdentity, string expectedDatabaseIdentity) =>
        !string.IsNullOrWhiteSpace(databaseIdentity)
        && !databaseIdentity.Trim().Equals("UNSET", StringComparison.OrdinalIgnoreCase)
        && databaseIdentity.Trim().Equals(expectedDatabaseIdentity, StringComparison.OrdinalIgnoreCase);

    private static string FormatConnectionFailure(Exception exception)
    {
        if (exception is PostgresException postgresException
            && postgresException.SqlState == PostgresErrorCodes.UndefinedTable
            && (string.Equals(postgresException.TableName, "database_identity", StringComparison.OrdinalIgnoreCase)
                || postgresException.MessageText.Contains("database_identity", StringComparison.OrdinalIgnoreCase)))
        {
            return "connected to PostgreSQL, but this database has no Tally Wrapper schema. "
                + "Check the database name in the connection string: it must be the application database, "
                + "not the server's default one. If the name is right, run the EF migrations and set "
                + "public.database_identity before saving this database.";
        }

        return exception.Message;
    }
}
