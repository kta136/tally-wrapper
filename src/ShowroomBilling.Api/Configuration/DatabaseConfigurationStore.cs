using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShowroomBilling.Api.Configuration;

public static class DatabaseConfigurationStore
{
    private const string AppDataOverrideEnvironmentVariable = "SHOWROOM_BILLING_APPDATA";
    public const string PostgresConnectionStringEnvironmentVariable = "SHOWROOM_BILLING_POSTGRES";
    private const string ProtectionScheme = "windows-dpapi-current-user";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DirectoryPath =>
        Environment.GetEnvironmentVariable(AppDataOverrideEnvironmentVariable) is { Length: > 0 } appDataOverride
            ? appDataOverride
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShowroomBilling");

    public static string ConfigPath => ConfigPathForEnvironment(GetCurrentEnvironmentName());

    public static bool Exists => File.Exists(ConfigPath);

    public static string ConfigPathForEnvironment(string? environmentName) =>
        Path.Combine(DirectoryPath, $"database.{NormalizeEnvironmentName(environmentName)}.local.json");

    public static bool ExistsForEnvironment(string? environmentName) =>
        File.Exists(ConfigPathForEnvironment(environmentName));

    public static string? GetEnvironmentConnectionString() =>
        Normalize(Environment.GetEnvironmentVariable(PostgresConnectionStringEnvironmentVariable));

    public static string? LoadPostgresConnectionString(string? environmentName)
    {
        return TryLoadPostgresConnectionString(ConfigPathForEnvironment(environmentName), out var connectionString)
            ? connectionString
            : null;
    }

    public static async Task SavePostgresConnectionStringAsync(
        string connectionString,
        string? environmentName = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);

        var protectedBytes = Protect(connectionString.Trim());
        var payload = new DatabaseConfigurationPayload(
            Version: 1,
            Environment: NormalizeEnvironmentName(environmentName),
            Protection: ProtectionScheme,
            ConnectionStringProtected: Convert.ToBase64String(protectedBytes));

        await using var stream = File.Create(ConfigPathForEnvironment(environmentName));
        await JsonSerializer.SerializeAsync(
            stream,
            payload,
            JsonOptions,
            cancellationToken);
    }

    private static bool TryLoadPostgresConnectionString(string path, out string? connectionString)
    {
        connectionString = null;
        if (!File.Exists(path))
        {
            return false;
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
                connectionString = Normalize(Unprotect(protectedBytes));
                return connectionString is not null;
            }
        }
        catch
        {
            return false;
        }

        return false;
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

    private static string GetCurrentEnvironmentName() =>
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? "Production";

    private static string NormalizeEnvironmentName(string? environmentName)
    {
        var normalized = string.IsNullOrWhiteSpace(environmentName)
            ? GetCurrentEnvironmentName()
            : environmentName.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        return new string(normalized.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record DatabaseConfigurationPayload(
        int Version,
        string Environment,
        string Protection,
        string ConnectionStringProtected);
}
