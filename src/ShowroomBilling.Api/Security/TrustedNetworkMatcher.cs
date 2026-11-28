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

        return options.TrustedNetworks.Any(entry => TryParseNetwork(entry, out var network) && network.Contains(address));
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryParseNetwork(string? entry, out IPNetwork network)
    {
        var text = entry.AsSpan().Trim();
        var slash = text.IndexOf('/');
        var host = slash < 0 ? text : text[..slash];
        network = default;
        if (!IPAddress.TryParse(host, out var configuredAddress)) return false;

        var address = Normalize(configuredAddress);
        if (slash < 0)
        {
            network = new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        // Older settings use IPv4 prefix lengths on mapped addresses. Normalize before parsing
        // so the framework applies the length to the IPv4 address.
        var suffix = text[slash..];
        if (configuredAddress.IsIPv4MappedToIPv6) text = $"{address}{suffix}";
        if (IPNetwork.TryParse(text, out network)) return true;

        // The old parser also accepted a leading sign and whitespace on the prefix length.
        return int.TryParse(suffix[1..], out var prefix)
            && IPNetwork.TryParse($"{address}/{prefix}", out network);
    }
}
