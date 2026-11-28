using System.Net;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Api.Security;

namespace ShowroomBilling.Tests;

public sealed class TrustedNetworkMatcherTests
{
    [Theory]
    [InlineData("192.0.2.17/27", "192.0.2.31", true)]
    [InlineData("192.0.2.17/27", "192.0.2.32", false)]
    [InlineData(" 192.0.2.17 ", "::ffff:192.0.2.17", true)]
    [InlineData("192.0.2.17", "192.0.2.18", false)]
    [InlineData("::ffff:192.0.2.17/24", "192.0.2.30", true)]
    [InlineData("::ffff:192.0.2.17/24", "2001:db8::1", false)]
    [InlineData("::ffff:192.0.2.17/120", "192.0.2.30", false)]
    [InlineData("192.0.2.17/ +27 ", "192.0.2.31", true)]
    [InlineData("2001:db8::6/127", "2001:db8::7", true)]
    [InlineData("2001:db8::6/127", "2001:db8::8", false)]
    [InlineData("::/0", "::ffff:192.0.2.1", false)]
    [InlineData("0.0.0.0/0", "2001:db8::1", false)]
    [InlineData("192.0.2.0/33", "192.0.2.1", false)]
    [InlineData("invalid", "192.0.2.1", false)]
    public void Matches_only_hosts_in_the_configured_range(string entry, string address, bool expected)
    {
        var options = new DeviceAuthOptions { TrustedNetworks = [entry] };
        Assert.Equal(expected, TrustedNetworkMatcher.IsTrusted(IPAddress.Parse(address), options));
    }
}
