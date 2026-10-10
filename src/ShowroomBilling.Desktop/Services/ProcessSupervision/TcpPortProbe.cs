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
            var connect = client.ConnectAsync(host, port);

            // Windows takes about two seconds to refuse a loopback connection,
            // so on a closed port the wait below gives up first and disposing
            // the client aborts the attempt. Look at that failure here, or it
            // comes back from the finalizer as an unobserved task exception and
            // the app logs an error on every start.
            connect.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Waiting on this task cannot deadlock the dispatcher: TcpClient
            // completes it without returning to the caller's context.
            return connect.Wait(timeout) && client.Connected;
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
