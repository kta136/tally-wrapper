using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// A TCP relay in front of the test PostgreSQL server that can stop passing
/// bytes on. While frozen, connections stay open and nothing answers, which is
/// what a database across the internet looks like when the link drops: no
/// refusal, no reset, just silence.
/// </summary>
internal sealed class FreezableRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly Lock _gate = new();
    private readonly List<TcpClient> _sockets = [];
    private TaskCompletionSource _thawed = Completed();

    private FreezableRelay(string connectionString)
    {
        var upstream = new NpgsqlConnectionStringBuilder(connectionString);
        _upstreamHost = upstream.Host!;
        _upstreamPort = upstream.Port;
        _listener.Start();
        ConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = IPAddress.Loopback.ToString(),
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
        }.ConnectionString;
        _ = AcceptAsync();
    }

    /// <summary>The upstream database, reached through this relay.</summary>
    public string ConnectionString { get; }

    public static FreezableRelay To(string connectionString) => new(connectionString);

    public void Freeze()
    {
        lock (_gate)
        {
            if (_thawed.Task.IsCompleted)
            {
                _thawed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Thaw()
    {
        lock (_gate)
        {
            _thawed.TrySetResult();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private Task WhenThawed()
    {
        lock (_gate)
        {
            return _thawed.Task;
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                _ = RelayAsync(client);
            }
        }
        catch (Exception) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        var upstream = new TcpClient { NoDelay = true };
        client.NoDelay = true;
        lock (_gate)
        {
            _sockets.Add(client);
            _sockets.Add(upstream);
        }

        try
        {
            await WhenThawed().WaitAsync(_stopping.Token);
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _stopping.Token);
            await Task.WhenAny(
                PumpAsync(client.GetStream(), upstream.GetStream()),
                PumpAsync(upstream.GetStream(), client.GetStream()));
        }
        catch
        {
            // Either side went away; the test decides what that means.
        }
        finally
        {
            client.Dispose();
            upstream.Dispose();
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await from.ReadAsync(buffer, _stopping.Token);
            if (read == 0)
            {
                return;
            }

            // Bytes that arrive while frozen are held, not dropped: a thaw is
            // the link coming back, not a new conversation.
            await WhenThawed().WaitAsync(_stopping.Token);
            await to.WriteAsync(buffer.AsMemory(0, read), _stopping.Token);
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        lock (_gate)
        {
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }
        }

        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }
}
