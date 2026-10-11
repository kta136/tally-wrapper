using System.Net.Sockets;

namespace ShowroomBilling.Desktop.Services;

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
