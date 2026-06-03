using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Contracts.Clients;

namespace ShowroomBilling.Tests.Contracts;

public sealed class DeviceAuthContractTests
{
    [Fact]
    public async Task LocalFile_mode_still_rejects_mutating_endpoint_without_device_token()
    {
        await using var factory = new TestApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/clients/heartbeat",
            Heartbeat());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TrustedLan_mode_accepts_loopback_mutating_endpoint_without_device_token()
    {
        await using var factory = new TestApiFactory(new Dictionary<string, string?>
        {
            ["DeviceAuth:Mode"] = "TrustedLan",
            ["DeviceAuth:TrustedNetworks:0"] = "127.0.0.0/8"
        });
        var options = factory.Services.GetRequiredService<IOptions<DeviceAuthOptions>>().Value;
        Assert.True(options.IsTrustedLan);

        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/clients/heartbeat",
            Heartbeat());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var presence = await response.Content.ReadFromJsonAsync<ClientPresenceResponse>();
        Assert.NotNull(presence);
        Assert.Equal("test-device", presence!.DeviceId);
    }

    [Fact]
    public void TrustedLan_matcher_rejects_untrusted_remote_address()
    {
        var options = new ShowroomBilling.Api.Options.DeviceAuthOptions
        {
            Mode = "TrustedLan",
            TrustedNetworks = ["192.168.10.0/24"]
        };

        Assert.False(ShowroomBilling.Api.Security.TrustedNetworkMatcher.IsTrusted(
            IPAddress.Parse("10.0.0.50"),
            options));
    }

    private static ClientHeartbeatRequest Heartbeat() =>
        new(
            DeviceId: "test-device",
            CounterName: "Test Counter",
            AppVersion: "1.0",
            ConnectionMode: "Server",
            MachineName: "TEST-PC",
            UserName: "tester");
}
