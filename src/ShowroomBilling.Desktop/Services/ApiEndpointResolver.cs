using Microsoft.Extensions.Options;
using ShowroomBilling.Desktop.Configuration;

namespace ShowroomBilling.Desktop.Services;

public interface IApiEndpointResolver
{
    string BaseUrl { get; }

    bool ShouldUseDeviceToken { get; }
}

public sealed class ApiEndpointResolver(IOptions<DesktopBootstrapOptions> options) : IApiEndpointResolver
{
    public string BaseUrl { get; } = options.Value.ApiBaseUrl;

    /// <summary>
    /// The device token is a file the API and the Desktop both read, so it
    /// only means something to an API on this machine: a development run. The
    /// server trusts its LAN instead and never sees a workstation's token.
    /// </summary>
    public bool ShouldUseDeviceToken { get; } =
        Uri.TryCreate(options.Value.ApiBaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
