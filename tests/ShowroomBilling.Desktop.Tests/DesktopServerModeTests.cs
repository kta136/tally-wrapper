using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Options;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests;

public sealed class DesktopServerModeTests
{
    [Fact]
    public void ApiEndpointResolver_UsesServerUrl_InServerMode()
    {
        var resolver = new ApiEndpointResolver(Options.Create(new DesktopBootstrapOptions
        {
            ApiBaseUrl = "http://localhost:5107",
            ServerApiBaseUrl = "http://tally-server:5107",
            ConnectionMode = DesktopConnectionModes.Server
        }));

        Assert.True(resolver.IsServerMode);
        Assert.Equal("http://tally-server:5107", resolver.BaseUrl);
        Assert.False(resolver.ShouldUseDeviceToken);
    }

    [Fact]
    public async Task DeviceTokenHandler_DoesNotAttachToken_InServerMode()
    {
        var capture = new CaptureHandler();
        using var handler = new DeviceTokenHandler(
            new DeviceTokenProvider(),
            new FakeEndpointResolver(IsServerMode: true))
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync("http://example.test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(capture.SawDeviceToken);
    }

    private sealed class FakeEndpointResolver(bool IsServerMode) : IApiEndpointResolver
    {
        public string BaseUrl => "http://example.test";

        bool IApiEndpointResolver.IsServerMode => IsServerMode;

        public bool ShouldUseDeviceToken => !IsServerMode;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public bool SawDeviceToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SawDeviceToken = request.Headers.Contains(DeviceTokenConstants.HeaderName);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
