using System.IO;
using System.Text.Json;
using ShowroomBilling.Contracts;

namespace ShowroomBilling.Desktop.Configuration;

/// <summary>
/// The server address this workstation saved in Settings → Server, kept in
/// <c>desktop-bootstrap.local.json</c> under the app's data folder.
/// </summary>
public static class DesktopBootstrapLocalOverrideStore
{
    // Builds from before the embedded API was removed also wrote and read a
    // connection mode. It is still written, as "Server", so that such a build
    // reading this file keeps calling the server instead of starting its own API.
    // An address such a build saved while in its other mode was not in use,
    // and is not taken up now: the workstation asks for the server address.
    private const string LegacyServerMode = "Server";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DirectoryPath => AppPaths.DataDirectory;

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
            if (document.RootElement.TryGetProperty("connectionMode", out var mode)
                && mode.ValueKind == JsonValueKind.String
                && !string.Equals(mode.GetString()?.Trim(), LegacyServerMode, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            if (document.RootElement.TryGetProperty("serverApiBaseUrl", out var property)
                && property.ValueKind == JsonValueKind.String
                && property.GetString()?.Trim() is { Length: > 0 } serverApiBaseUrl)
            {
                result[$"{DesktopBootstrapOptions.SectionName}:{nameof(DesktopBootstrapOptions.ApiBaseUrl)}"] = serverApiBaseUrl;
            }
        }
        catch
        {
            // An unreadable file means no saved address; the defaults apply.
        }

        return result;
    }

    public static async Task SaveAsync(string serverApiBaseUrl, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var payload = new DesktopBootstrapLocalOverride(LegacyServerMode, serverApiBaseUrl.Trim());
        await using var stream = File.Create(ConfigPath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
    }

    private sealed record DesktopBootstrapLocalOverride(
        string ConnectionMode,
        string ServerApiBaseUrl);
}
