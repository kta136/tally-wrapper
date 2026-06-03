using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ShowroomBilling.Contracts.Device;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// Attaches the local device token to every outbound API request. Paired with
/// <see cref="DeviceTokenProvider"/>; see that type for the file-ownership
/// story. Missing token falls through without a header so the operator sees
/// a 401 rather than a silent failure.
/// </summary>
public sealed class DeviceTokenHandler(
    DeviceTokenProvider tokenProvider,
    IApiEndpointResolver endpointResolver) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (endpointResolver.ShouldUseDeviceToken
            && !request.Headers.Contains(DeviceTokenConstants.HeaderName))
        {
            var token = tokenProvider.GetOrCreateToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Add(DeviceTokenConstants.HeaderName, token);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
