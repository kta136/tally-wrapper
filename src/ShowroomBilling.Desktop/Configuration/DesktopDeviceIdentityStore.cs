using System.IO;
using System.Text.Json;

namespace ShowroomBilling.Desktop.Configuration;

public sealed class DesktopDeviceIdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private string? _cached;

    public DesktopDeviceIdentityStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShowroomBilling");
        _path = Path.Combine(directory, "desktop-identity.local.json");
    }

    public string Resolve(string configuredDeviceId)
    {
        if (!IsGeneratedIdentityRequired(configuredDeviceId))
        {
            return configuredDeviceId.Trim();
        }

        if (_cached is not null)
        {
            return _cached;
        }

        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var existing = TryLoad();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                _cached = existing;
                return _cached;
            }

            _cached = $"desktop-{Guid.NewGuid():N}";
            Save(_cached);
            return _cached;
        }
    }

    private static bool IsGeneratedIdentityRequired(string? configuredDeviceId) =>
        string.IsNullOrWhiteSpace(configuredDeviceId)
        || configuredDeviceId.Trim().Equals("desktop-dev-01", StringComparison.OrdinalIgnoreCase);

    private string? TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.TryGetProperty("deviceId", out var deviceId)
                && deviceId.ValueKind == JsonValueKind.String)
            {
                return deviceId.GetString()?.Trim();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private void Save(string deviceId)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(new Payload(deviceId), JsonOptions));
    }

    private sealed record Payload(string DeviceId);
}
