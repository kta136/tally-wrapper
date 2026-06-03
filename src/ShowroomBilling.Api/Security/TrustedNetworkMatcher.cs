using System.Net;
using System.Net.Sockets;
using ShowroomBilling.Api.Options;

namespace ShowroomBilling.Api.Security;

public static class TrustedNetworkMatcher
{
    public static bool IsTrusted(IPAddress? remoteAddress, DeviceAuthOptions options)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        var address = Normalize(remoteAddress);
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        foreach (var configured in options.TrustedNetworks)
        {
            if (NetworkRange.TryParse(configured, out var range) && range.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        return address;
    }

    private sealed class NetworkRange
    {
        private readonly IPAddress _network;
        private readonly int _prefixLength;

        private NetworkRange(IPAddress network, int prefixLength)
        {
            _network = Normalize(network);
            _prefixLength = prefixLength;
        }

        public static bool TryParse(string? value, out NetworkRange range)
        {
            range = null!;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var text = value.Trim();
            if (!text.Contains('/'))
            {
                if (!IPAddress.TryParse(text, out var singleAddress))
                {
                    return false;
                }

                var normalized = Normalize(singleAddress);
                var bits = normalized.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                range = new NetworkRange(normalized, bits);
                return true;
            }

            var parts = text.Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var network)
                || !int.TryParse(parts[1], out var prefixLength))
            {
                return false;
            }

            var normalizedNetwork = Normalize(network);
            var maxPrefix = normalizedNetwork.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefixLength < 0 || prefixLength > maxPrefix)
            {
                return false;
            }

            range = new NetworkRange(normalizedNetwork, prefixLength);
            return true;
        }

        public bool Contains(IPAddress address)
        {
            var normalized = Normalize(address);
            if (normalized.AddressFamily != _network.AddressFamily)
            {
                return false;
            }

            var addressBytes = normalized.GetAddressBytes();
            var networkBytes = _network.GetAddressBytes();
            var fullBytes = _prefixLength / 8;
            var remainingBits = _prefixLength % 8;

            for (var i = 0; i < fullBytes; i++)
            {
                if (addressBytes[i] != networkBytes[i])
                {
                    return false;
                }
            }

            if (remainingBits == 0)
            {
                return true;
            }

            var mask = (byte)(0xFF << (8 - remainingBits));
            return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
        }
    }
}
