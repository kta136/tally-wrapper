using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShowroomBilling.Contracts;

namespace ShowroomBilling.Api.Configuration;

public static class DatabaseConfigurationStore
{
    public const string PostgresConnectionStringEnvironmentVariable = "SHOWROOM_BILLING_POSTGRES";
    private const string ProtectionScheme = "windows-dpapi-current-user";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DirectoryPath => AppPaths.DataDirectory;

    public static string ConfigPathForEnvironment(string environmentName) =>
        Path.Combine(DirectoryPath, $"database.{NormalizeEnvironmentName(environmentName)}.local.json");

    public static string? GetEnvironmentConnectionString() =>
        Normalize(Environment.GetEnvironmentVariable(PostgresConnectionStringEnvironmentVariable));

    /// <param name="loadError">
    /// Why an override file that exists could not be used (for example it was
    /// encrypted by a different Windows user), or null when there was no file
    /// or it loaded. The caller logs it: without that, the API starts on the
    /// fallback connection string and nothing explains why.
    /// </param>
    public static string? LoadPostgresConnectionString(string environmentName, out string? loadError)
    {
        var path = ConfigPathForEnvironment(environmentName);
        loadError = null;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if ((root.TryGetProperty("connectionStringProtected", out var protectedValue)
                    || root.TryGetProperty("ConnectionStringProtected", out protectedValue))
                && protectedValue.ValueKind == JsonValueKind.String)
            {
                var protectedBytes = Convert.FromBase64String(protectedValue.GetString() ?? string.Empty);
                var connectionString = Normalize(Unprotect(protectedBytes));
                if (connectionString is null)
                {
                    loadError = "it holds an empty connection string";
                }
                return connectionString;
            }

            loadError = "it has no protected connection string";
        }
        catch (Exception ex)
        {
            // Never fail start-up over this file; report it instead.
            loadError = ex is CryptographicException
                ? "it could not be decrypted; it was probably saved by a different Windows user"
                : ex.Message;
        }

        return null;
    }

    public static async Task SavePostgresConnectionStringAsync(
        string connectionString,
        string environmentName,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);

        var protectedBytes = Protect(connectionString.Trim());
        var payload = new DatabaseConfigurationPayload(
            Version: 1,
            Environment: NormalizeEnvironmentName(environmentName),
            Protection: ProtectionScheme,
            ConnectionStringProtected: Convert.ToBase64String(protectedBytes));

        var path = ConfigPathForEnvironment(environmentName);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static byte[] Protect(string connectionString)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Encrypted database settings require Windows DPAPI. Use SHOWROOM_BILLING_POSTGRES on non-Windows hosts.");
        }

        return ProtectedData.Protect(
            Encoding.UTF8.GetBytes(connectionString),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
    }

    private static string Unprotect(byte[] protectedBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Encrypted database settings require Windows DPAPI. Use SHOWROOM_BILLING_POSTGRES on non-Windows hosts.");
        }

        var bytes = ProtectedData.Unprotect(
            protectedBytes,
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string NormalizeEnvironmentName(string environmentName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(environmentName.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record DatabaseConfigurationPayload(
        int Version,
        string Environment,
        string Protection,
        string ConnectionStringProtected);
}
