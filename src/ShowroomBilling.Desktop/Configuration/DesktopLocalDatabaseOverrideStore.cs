using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShowroomBilling.Desktop.Configuration;

public static class DesktopLocalDatabaseOverrideStore
{
    private const string AppDataOverrideEnvironmentVariable = "SHOWROOM_BILLING_APPDATA";

    public static string DirectoryPath =>
        Environment.GetEnvironmentVariable(AppDataOverrideEnvironmentVariable) is { Length: > 0 } appDataOverride
            ? appDataOverride
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShowroomBilling");

    public static string ConfigPath => ConfigPathForEnvironment(GetCurrentEnvironmentName());

    public static string ConfigPathForEnvironment(string? environmentName) =>
        Path.Combine(DirectoryPath, $"database.{NormalizeEnvironmentName(environmentName)}.local.json");

    public static LocalDatabaseOverrideSnapshot Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path))
        {
            return new LocalDatabaseOverrideSnapshot(path, null, false);
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
                var connectionString = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    protectedBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser));
                return new LocalDatabaseOverrideSnapshot(path, connectionString.Trim(), true);
            }
        }
        catch
        {
            return new LocalDatabaseOverrideSnapshot(path, null, true);
        }

        return new LocalDatabaseOverrideSnapshot(path, null, true);
    }

    public static string MaskConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "—";
        }

        var parts = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var equals = part.IndexOf('=');
                if (equals <= 0)
                {
                    return part;
                }

                var key = part[..equals].Trim();
                return key.Equals("Password", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Pwd", StringComparison.OrdinalIgnoreCase)
                    ? $"{key}=***"
                    : part;
            });
        return string.Join(';', parts);
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
}

public sealed record LocalDatabaseOverrideSnapshot(
    string ConfigPath,
    string? ConnectionString,
    bool Exists);
