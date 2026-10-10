using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ShowroomBilling.ServerTray;

internal static class ServerUrlHelper
{
    public static string GetWorkstationApiBaseUrl(string apiBaseUrl)
    {
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) || !IsLoopbackHost(uri.Host))
        {
            return apiBaseUrl;
        }

        var lanAddress = GetFirstLanAddress();
        if (lanAddress is null)
        {
            return apiBaseUrl;
        }

        var builder = new UriBuilder(uri)
        {
            Host = lanAddress
        };
        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static string? GetFirstLanAddress()
    {
        var candidates = new List<(IPAddress Address, bool HasGateway)>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var properties = networkInterface.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
            candidates.AddRange(properties.UnicastAddresses.Select(unicast => (unicast.Address, hasGateway)));
        }

        return ChooseLanAddress(candidates)?.ToString();
    }

    /// <summary>
    /// Picks the address to show workstations. An adapter with a default
    /// gateway is the real LAN; Hyper-V, WSL and VPN adapters usually have
    /// none, and a 169.254.x.x address means the adapter got no address at all.
    /// </summary>
    internal static IPAddress? ChooseLanAddress(IEnumerable<(IPAddress Address, bool HasGateway)> candidates)
    {
        var usable = candidates
            .Where(candidate => candidate.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(candidate.Address)
                && !IsLinkLocal(candidate.Address))
            .ToList();
        return usable.Where(candidate => candidate.HasGateway).Select(candidate => candidate.Address).FirstOrDefault()
            ?? usable.Select(candidate => candidate.Address).FirstOrDefault();
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
