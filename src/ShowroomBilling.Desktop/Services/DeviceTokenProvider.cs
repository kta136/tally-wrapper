using System;
using System.IO;
using System.Security.Cryptography;
using ShowroomBilling.Contracts.Device;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// Desktop-side mirror of <c>DeviceTokenStore</c> in the API. Both processes
/// read-or-create the same file at <see cref="DeviceTokenConstants.ResolveTokenFilePath"/>,
/// so whichever starts first writes the token and the other reads it. Only
/// used against an API on this machine; see <see cref="ApiEndpointResolver"/>.
/// </summary>
public sealed class DeviceTokenProvider
{
    private readonly string _path;
    private readonly object _gate = new();
    private string? _cached;

    public DeviceTokenProvider()
    {
        _path = DeviceTokenConstants.ResolveTokenFilePath();
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
}
