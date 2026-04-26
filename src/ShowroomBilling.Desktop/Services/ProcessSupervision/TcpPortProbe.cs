using System.Net.Sockets;

namespace ShowroomBilling.Desktop.Services.ProcessSupervision;

internal static class TcpPortProbe
{
    public static async Task<bool> WaitUntilOpenAsync(
        string host,
        int port,
        TimeSpan pollInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (await CanConnectAsync(host, port, pollInterval, cancellationToken))
            {
                return true;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(pollInterval.TotalMilliseconds, remaining.TotalMilliseconds)), cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Pure-synchronous TCP probe. Must NOT delegate to the async overload via
    /// .GetAwaiter().GetResult() — Start() is called from the WPF dispatcher
    /// thread during App.OnStartup, and a sync-over-async wait there deadlocks
    /// the dispatcher when the async continuation tries to resume on the same
    /// captured SynchronizationContext (Release-build hang on first launch).
    /// </summary>
    public static bool CanConnect(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            var ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeout)) return false;
            client.EndConnect(ar);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CanConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
