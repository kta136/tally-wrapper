using System.Buffers.Binary;
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

    private static string? GetFirstLanAddress() =>
        ChooseLanAddress(LanCandidates().Select(candidate => (candidate.Address, candidate.HasGateway)))?.ToString();

    /// <summary>
    /// The network range this PC's LAN adapter is on, such as 10.0.0.0/24: the
    /// counters beside it are inside it. Null when no adapter gives one.
    /// </summary>
    public static string? GetLanNetwork()
    {
        try
        {
            var candidates = LanCandidates();
            var chosen = ChooseLanAddress(candidates.Select(candidate => (candidate.Address, candidate.HasGateway)));
            return chosen is null
                ? null
                : NetworkOf(chosen, candidates.First(candidate => candidate.Address.Equals(chosen)).PrefixLength);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The range an IPv4 address belongs to, given its adapter's prefix length.
    /// Null for a prefix that is no LAN: 31 and 32 are point-to-point links
    /// (a VPN or dial-up adapter), whose "network" holds no other PC.
    /// </summary>
    internal static string? NetworkOf(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 8 or > 30)
        {
            return null;
        }

        var mask = uint.MaxValue << (32 - prefixLength);
        Span<byte> network = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            network,
            BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes()) & mask);
        return $"{new IPAddress(network)}/{prefixLength}";
    }

    private static List<(IPAddress Address, bool HasGateway, int PrefixLength)> LanCandidates()
    {
        var candidates = new List<(IPAddress Address, bool HasGateway, int PrefixLength)>();
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
            candidates.AddRange(properties.UnicastAddresses.Select(unicast => (unicast.Address, hasGateway, unicast.PrefixLength)));
        }

        return candidates;
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
