using System.Security.Cryptography;
using ShowroomBilling.Contracts.Device;

namespace ShowroomBilling.Api.Security;

/// <summary>
/// Owns the local device-token secret. On first boot the API generates a
/// 32-byte random token and writes it to the per-user LocalApplicationData
/// path (see <see cref="DeviceTokenConstants.ResolveTokenFilePath"/>). The
/// Desktop reads the same file and attaches the token as the
/// <c>X-Device-Token</c> header on every API call.
/// </summary>
public sealed class DeviceTokenStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private string? _cached;

    public DeviceTokenStore()
        : this(DeviceTokenConstants.ResolveTokenFilePath())
    {
    }

    public DeviceTokenStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Device-token path is required.", nameof(path));
        }

        _path = path;
    }

    public string GetOrCreateToken()
    {
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

            _cached = DeviceTokenFile.ReadOrCreate(_path);
            return _cached;
        }
    }

    public bool Validate(string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var expected = GetOrCreateToken();
        // Constant-time compare to avoid timing oracles on a local box. Overkill
        // against physical-access threats, but cheap and right.
        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(provided.Trim());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
