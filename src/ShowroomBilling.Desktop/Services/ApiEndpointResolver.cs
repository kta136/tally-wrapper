using Microsoft.Extensions.Options;
using ShowroomBilling.Desktop.Configuration;

namespace ShowroomBilling.Desktop.Services;

public interface IApiEndpointResolver
{
    string BaseUrl { get; }

    bool IsServerMode { get; }

    bool ShouldUseDeviceToken { get; }
}

public sealed class ApiEndpointResolver(IOptions<DesktopBootstrapOptions> options) : IApiEndpointResolver
{
    private readonly DesktopBootstrapOptions _options = options.Value;

    public string BaseUrl => _options.EffectiveApiBaseUrl;

    public bool IsServerMode => _options.IsServerMode;

    public bool ShouldUseDeviceToken => !IsServerMode;
}
