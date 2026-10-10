using System;
using System.IO;
using System.Security.Cryptography;
using ShowroomBilling.Contracts.Device;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// Desktop-side mirror of <c>DeviceTokenStore</c> in the API. Both processes
/// read-or-create the same file at <see cref="DeviceTokenConstants.ResolveTokenFilePath"/>.
/// The Desktop starts first and spawns the API as a child, so the Desktop
/// normally wins the race and writes the token; the API then reads it.
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
