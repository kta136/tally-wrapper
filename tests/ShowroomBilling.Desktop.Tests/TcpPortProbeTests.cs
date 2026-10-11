using System.Net;
using System.Net.Sockets;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests;

public sealed class TcpPortProbeTests
{
    [Fact]
    public async Task WaitUntilOpen_IsTrue_WhenSomethingListens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var open = await TcpPortProbe.WaitUntilOpenAsync(
                "127.0.0.1", port, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(open);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task WaitUntilOpen_GivesUpAfterItsTimeout_WhenNothingListens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var open = await TcpPortProbe.WaitUntilOpenAsync(
            "127.0.0.1", port, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        Assert.False(open);
    }
}
