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
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(unicast.Address))
                {
                    return unicast.Address.ToString();
                }
            }
        }

        return null;
    }
}
