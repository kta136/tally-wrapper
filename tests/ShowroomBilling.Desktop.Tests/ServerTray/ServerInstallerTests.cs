using System.IO;
using System.Net;
using ShowroomBilling.ServerTray;

namespace ShowroomBilling.Desktop.Tests.ServerTray;

/// <summary>
/// The parts of the server installer that can be checked without installing
/// anything. Service creation, the firewall rule and elevation are not
/// exercised by any test.
/// </summary>
public sealed class ServerInstallerTests
{
    [Fact]
    public void A_service_that_does_not_exist_is_reported_as_not_installed()
    {
        var name = "TallyWrapper.NoSuchService." + Guid.NewGuid().ToString("N");

        Assert.Null(ServerInstaller.TryGetServiceStatus(name));
        Assert.False(ServerInstaller.IsServiceInstalled(name));
    }

    [Theory]
    [InlineData("192.168.0.0/16", "192.168.0.0/16")]
    [InlineData("  10.0.0.0/24  ", "10.0.0.0/24")]
    [InlineData("fd00::/8", "fd00::/8")]
    public void A_network_range_is_accepted_and_trimmed(string input, string expected)
    {
        Assert.True(ServerInstaller.TryNormalizeLanCidr(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("192.168.1.10")]
    [InlineData("192.168.0.0/33")]
    [InlineData("office-lan")]
    public void Anything_that_is_not_a_network_range_is_rejected(string? input)
    {
        Assert.False(ServerInstaller.TryNormalizeLanCidr(input, out _));
    }

    [Fact]
    public void The_installed_services_own_range_is_read_back_for_a_repair()
    {
        string[] environment =
        [
            "ASPNETCORE_ENVIRONMENT=Production",
            "DeviceAuth__Mode=TrustedLan",
            "DeviceAuth__TrustedNetworks__0=10.20.0.0/24",
        ];

        Assert.Equal("10.20.0.0/24", ServerInstaller.ReadLanCidr(environment));
    }

    [Fact]
    public void A_service_with_no_range_or_a_broken_one_gives_none()
    {
        Assert.Null(ServerInstaller.ReadLanCidr(null));
        Assert.Null(ServerInstaller.ReadLanCidr(["ASPNETCORE_ENVIRONMENT=Production"]));
        Assert.Null(ServerInstaller.ReadLanCidr(["DeviceAuth__TrustedNetworks__0=not-a-range"]));
    }

    [Fact]
    public void The_api_executable_is_rewritten_only_when_its_content_differs()
    {
        var path = Path.Combine(Path.GetTempPath(), "tw-installer-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] installed = [1, 2, 3, 4];
            Assert.True(ServerInstaller.FileDiffersFrom(new MemoryStream(installed), path));

            File.WriteAllBytes(path, installed);
            Assert.False(ServerInstaller.FileDiffersFrom(new MemoryStream(installed), path));
            Assert.True(ServerInstaller.FileDiffersFrom(new MemoryStream([1, 2, 3, 5]), path));
            Assert.True(ServerInstaller.FileDiffersFrom(new MemoryStream([1, 2, 3]), path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_workstation_address_comes_from_the_adapter_with_a_gateway()
    {
        var chosen = ServerUrlHelper.ChooseLanAddress(
        [
            (IPAddress.Parse("172.28.160.1"), false),   // Hyper-V / WSL virtual switch
            (IPAddress.Parse("169.254.12.7"), true),    // adapter that got no address
            (IPAddress.Parse("192.168.1.24"), true),    // the LAN
        ]);

        Assert.Equal(IPAddress.Parse("192.168.1.24"), chosen);
    }

    [Fact]
    public void Without_a_gateway_any_real_address_is_better_than_none()
    {
        var chosen = ServerUrlHelper.ChooseLanAddress(
        [
            (IPAddress.Loopback, false),
            (IPAddress.Parse("169.254.12.7"), false),
            (IPAddress.Parse("10.0.0.5"), false),
        ]);

        Assert.Equal(IPAddress.Parse("10.0.0.5"), chosen);
        Assert.Null(ServerUrlHelper.ChooseLanAddress([(IPAddress.Loopback, true)]));
    }
}
