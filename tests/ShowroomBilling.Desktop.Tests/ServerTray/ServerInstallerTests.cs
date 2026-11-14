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

    [Fact]
    public void The_install_prompt_says_what_is_wrong_with_a_range_and_nothing_about_a_good_one()
    {
        Assert.Null(ServerInstaller.LanCidrProblem(" 192.168.1.0/24 "));
        Assert.Equal(
            $"'the whole shop' is not a network range. Enter one such as {ServerInstaller.DefaultLanCidr}.",
            ServerInstaller.LanCidrProblem(" the whole shop "));
    }

    [Fact]
    public void A_first_install_starts_from_this_pcs_own_network_not_a_wider_one()
    {
        var (value, hint) = ServerInstaller.LanCidrPrompt(configuredCidr: null, ownNetwork: "192.168.1.0/24");

        Assert.Equal("192.168.1.0/24", value);
        Assert.Equal("This PC's own network is 192.168.1.0/24.", hint);
    }

    [Fact]
    public void A_repair_starts_from_the_range_the_service_already_has()
    {
        var (value, hint) = ServerInstaller.LanCidrPrompt(configuredCidr: "10.20.0.0/24", ownNetwork: "192.168.1.0/24");

        Assert.Equal("10.20.0.0/24", value);
        Assert.Equal("This PC's own network is 192.168.1.0/24.", hint);
    }

    [Fact]
    public void With_no_network_to_read_the_prompt_gives_an_example_that_is_right()
    {
        var (value, hint) = ServerInstaller.LanCidrPrompt(configuredCidr: null, ownNetwork: null);

        Assert.Equal(ServerInstaller.DefaultLanCidr, value);
        Assert.Equal("For a network where every address starts with 192.168.1, that is 192.168.1.0/24.", hint);
    }

    [Theory]
    [InlineData("192.168.1.9", 24, "192.168.1.0/24")]
    [InlineData("192.168.1.130", 25, "192.168.1.128/25")]
    [InlineData("10.20.30.40", 16, "10.20.0.0/16")]
    [InlineData("10.20.30.40", 8, "10.0.0.0/8")]
    public void A_pcs_network_is_its_address_with_the_host_part_cleared(string address, int prefixLength, string expected)
    {
        Assert.Equal(expected, ServerUrlHelper.NetworkOf(IPAddress.Parse(address), prefixLength));
    }

    [Theory]
    [InlineData("10.8.0.2", 32)]    // a VPN's point-to-point link
    [InlineData("10.8.0.2", 31)]
    [InlineData("10.8.0.2", 0)]
    [InlineData("fd00::1", 64)]
    public void An_adapter_that_is_not_on_a_lan_gives_no_network(string address, int prefixLength)
    {
        Assert.Null(ServerUrlHelper.NetworkOf(IPAddress.Parse(address), prefixLength));
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
    public void An_upgrade_swaps_the_api_in_and_keeps_the_one_it_replaced()
    {
        var folder = Directory.CreateTempSubdirectory("tw-installer-test-").FullName;
        var path = Path.Combine(folder, "api.exe");
        try
        {
            // A first install has nothing to keep.
            ServerInstaller.ReplaceKeepingPrevious(new MemoryStream([1, 1, 1]), path);
            Assert.Equal([1, 1, 1], File.ReadAllBytes(path));
            Assert.Equal(["api.exe"], Directory.GetFiles(folder).Select(Path.GetFileName));

            ServerInstaller.ReplaceKeepingPrevious(new MemoryStream([2, 2]), path);
            Assert.Equal([2, 2], File.ReadAllBytes(path));
            Assert.Equal([1, 1, 1], File.ReadAllBytes(path + ServerInstaller.PreviousSuffix));

            // Only the last one replaced is kept.
            ServerInstaller.ReplaceKeepingPrevious(new MemoryStream([3]), path);
            Assert.Equal([3], File.ReadAllBytes(path));
            Assert.Equal([2, 2], File.ReadAllBytes(path + ServerInstaller.PreviousSuffix));
            Assert.Equal(2, Directory.GetFiles(folder).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_copy_that_fails_part_way_leaves_the_installed_api_as_it_was()
    {
        var folder = Directory.CreateTempSubdirectory("tw-installer-test-").FullName;
        var path = Path.Combine(folder, "api.exe");
        try
        {
            File.WriteAllBytes(path, [1, 1, 1]);

            Assert.Throws<IOException>(() =>
                ServerInstaller.ReplaceKeepingPrevious(new FailsPartWay(), path));

            Assert.Equal([1, 1, 1], File.ReadAllBytes(path));
            Assert.Equal(["api.exe"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("start", ServiceCommand.Start)]
    [InlineData(" Stop ", ServiceCommand.Stop)]
    [InlineData("RESTART", ServiceCommand.Restart)]
    public void The_elevated_copy_understands_the_verbs_the_tray_sends(string verb, object expected)
    {
        Assert.True(ServiceCommands.TryParse(verb, out var action));
        Assert.Equal((ServiceCommand)expected, action);
        Assert.True(ServiceCommands.TryParse(ServiceCommands.Verb(action), out var roundTrip));
        Assert.Equal(action, roundTrip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("delete")]
    public void Anything_else_is_not_a_service_action(string? verb)
    {
        Assert.False(ServiceCommands.TryParse(verb, out _));
    }

    [Fact]
    public void Only_a_refusal_for_lack_of_rights_is_answered_by_asking_for_them()
    {
        const int accessDenied = 5;
        const int serviceDoesNotExist = 1060;
        Exception Wrapped(int code) => new InvalidOperationException(
            "Cannot open the service on computer '.'.", new System.ComponentModel.Win32Exception(code));

        Assert.True(ServiceCommands.IsAccessDenied(Wrapped(accessDenied)));
        Assert.False(ServiceCommands.IsAccessDenied(Wrapped(serviceDoesNotExist)));
        Assert.False(ServiceCommands.IsAccessDenied(new System.ServiceProcess.TimeoutException("The service did not stop in time.")));
    }

    /// <summary>A source that gives a little and then fails, as a full disk or a held file would.</summary>
    private sealed class FailsPartWay : Stream
    {
        private bool _gaveSome;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_gaveSome)
            {
                throw new IOException("There is not enough space on the disk.");
            }

            _gaveSome = true;
            buffer[offset] = 9;
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
