using System.Net;
using System.Net.Sockets;
using ShowroomBilling.Desktop.Services.ProcessSupervision;

namespace ShowroomBilling.Desktop.Tests;

public sealed class TcpPortProbeTests
{
    [Fact]
    public void CanConnect_IsTrue_WhenSomethingListens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(TcpPortProbe.CanConnect("127.0.0.1", port, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Every start probes two ports nothing listens on. Windows takes about two
    /// seconds to refuse a loopback connection, so the probe gives up first and
    /// abandons the attempt; that must not come back later as an unobserved
    /// task exception, which the app logs as an error.
    /// </summary>
    [Fact]
    public void CanConnect_GivingUpOnAClosedPort_LeavesNoUnobservedException()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(inner => inner is SocketException))
            {
                lock (unobserved) unobserved.Add(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            Probe(FreePort());

            // The abandoned attempt fails once its socket is closed; the report
            // would come from the finalizer of its task.
            for (var i = 0; i < 10; i++)
            {
                Thread.Sleep(50);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.Empty(unobserved);
    }

    // Kept out of line so nothing in the test's own frame keeps the attempt alive.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Probe(int port) =>
        Assert.False(TcpPortProbe.CanConnect("127.0.0.1", port, TimeSpan.FromMilliseconds(50)));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
