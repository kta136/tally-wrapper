using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>The Desktop is a client of one server API and never starts its own.</summary>
public sealed class DesktopServerModeTests
{
    [Fact]
    public void A_server_on_the_lan_gets_no_device_token()
    {
        var resolver = new ApiEndpointResolver(Options.Create(new DesktopBootstrapOptions
        {
            ApiBaseUrl = "http://tally-server:5107"
        }));

        Assert.Equal("http://tally-server:5107", resolver.BaseUrl);
        Assert.False(resolver.ShouldUseDeviceToken);
    }

    [Theory]
    [InlineData("http://localhost:5108")]
    [InlineData("http://127.0.0.1:5108")]
    public void An_api_on_this_machine_gets_the_device_token(string address)
    {
        var resolver = new ApiEndpointResolver(Options.Create(new DesktopBootstrapOptions { ApiBaseUrl = address }));

        Assert.True(resolver.ShouldUseDeviceToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_handler_attaches_the_token_only_when_the_resolver_says_so(bool useToken)
    {
        var capture = new CaptureHandler();
        using var handler = new DeviceTokenHandler(new DeviceTokenProvider(), new FakeEndpointResolver(useToken))
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync("http://example.test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(useToken, capture.SawDeviceToken);
    }

    /// <summary>
    /// Counters were set up by builds that saved a connection mode beside the
    /// server address. The address must still be picked up, and what is saved
    /// now must still read as "Server" to such a build.
    /// </summary>
    [Fact]
    public async Task The_saved_server_address_is_read_back_in_the_shape_older_builds_wrote()
    {
        var path = DesktopBootstrapLocalOverrideStore.ConfigPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(
                path,
                """{ "connectionMode": "Server", "serverApiBaseUrl": "http://192.168.1.50:5107" }""",
                TestContext.Current.CancellationToken);

            var fromOlderBuild = DesktopBootstrapLocalOverrideStore.LoadConfigurationPairs();

            Assert.Equal("http://192.168.1.50:5107", fromOlderBuild["DesktopBootstrap:ApiBaseUrl"]);

            // An address saved while that build ran its own API was not in use.
            await File.WriteAllTextAsync(
                path,
                """{ "connectionMode": "LocalEmbedded", "serverApiBaseUrl": "http://192.168.1.70:5107" }""",
                TestContext.Current.CancellationToken);
            Assert.Empty(DesktopBootstrapLocalOverrideStore.LoadConfigurationPairs());

            await DesktopBootstrapLocalOverrideStore.SaveAsync(" http://192.168.1.60:5107 ", TestContext.Current.CancellationToken);

            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal("Server", saved.RootElement.GetProperty("connectionMode").GetString());
            Assert.Equal("http://192.168.1.60:5107", saved.RootElement.GetProperty("serverApiBaseUrl").GetString());
            Assert.Equal(
                "http://192.168.1.60:5107",
                DesktopBootstrapLocalOverrideStore.LoadConfigurationPairs()["DesktopBootstrap:ApiBaseUrl"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeEndpointResolver(bool useDeviceToken) : IApiEndpointResolver
    {
        public string BaseUrl => "http://example.test";

        public bool ShouldUseDeviceToken => useDeviceToken;
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
