using System.IO;
using System.Text.Json;

namespace ShowroomBilling.Desktop.Configuration;

public static class DesktopBootstrapLocalOverrideStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShowroomBilling");

    public static string ConfigPath => Path.Combine(DirectoryPath, "desktop-bootstrap.local.json");

    public static IReadOnlyDictionary<string, string?> LoadConfigurationPairs()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(ConfigPath))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = document.RootElement;
            if (TryGetString(root, "connectionMode", out var connectionMode))
            {
                result[$"{DesktopBootstrapOptions.SectionName}:ConnectionMode"] = connectionMode;
            }

            if (TryGetString(root, "serverApiBaseUrl", out var serverApiBaseUrl))
            {
                result[$"{DesktopBootstrapOptions.SectionName}:ServerApiBaseUrl"] = serverApiBaseUrl;
            }
        }
        catch
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    public static async Task SaveAsync(
        string connectionMode,
        string? serverApiBaseUrl,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var payload = new DesktopBootstrapLocalOverride(
            connectionMode,
            string.IsNullOrWhiteSpace(serverApiBaseUrl) ? null : serverApiBaseUrl.Trim());
        await using var stream = File.Create(ConfigPath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private sealed record DesktopBootstrapLocalOverride(
        string ConnectionMode,
        string? ServerApiBaseUrl);
}
