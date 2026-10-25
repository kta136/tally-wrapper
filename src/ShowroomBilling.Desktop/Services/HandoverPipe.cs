using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Contracts;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// How a second start of the app with a hand-over link hands the link to the copy already running:
/// a named pipe that only the same Windows user, in the same session, can open. Its name also
/// carries the settings folder and the build, so a scratch run or a development build never takes
/// a link meant for the real app. A message is the link's length (4 bytes, little-endian) and its
/// UTF-8 text; the running copy answers one byte, 1, once it has the link.
/// </summary>
public static class HandoverPipe
{
    private const int MaxMessageBytes = HandoverLinkReader.MaxLinkLength * 3;

    public static string NameFor(string environmentName)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var session = process.SessionId;
        var folder = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDirectory.ToUpperInvariant())))[..16];
#if DEBUG
        const string Build = "debug";
#else
        const string Build = "release";
#endif
        return $"TallyWrapper.Handover.{environmentName}.{Build}.{user}.{session}.{folder}";
    }

    /// <summary>
    /// Gives <paramref name="link"/> to the copy of the app listening on <paramref name="pipeName"/>.
    /// False when none answers in time; the caller then starts the app itself.
    /// </summary>
    public static bool TrySend(string pipeName, string link, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);

            // The browser let this start take the foreground; pass that on, so the running copy
            // may bring its window to the front if the hand-over needs the operator.
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverProcessId))
            {
                AllowSetForegroundWindow(serverProcessId);
            }

            var text = Encoding.UTF8.GetBytes(link);
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, text.Length);
            client.Write(length);
            client.Write(text);
            client.Flush();

            using var answer = new CancellationTokenSource(timeout);
            var buffer = new byte[1];
            var read = client.ReadAsync(buffer, answer.Token).AsTask().GetAwaiter().GetResult();
            return read == 1 && buffer[0] == 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Listens for links from later starts of the app until disposed. <c>onLink</c> is called on a
    /// pool thread; the caller moves it to the UI thread.
    /// </summary>
    public sealed class Server : IDisposable
    {
        private readonly string _pipeName;
        private readonly Action<string> _onLink;
        private readonly ILogger? _logger;
        private readonly CancellationTokenSource _stop = new();

        public Server(string pipeName, Action<string> onLink, ILogger? logger = null)
        {
            _pipeName = pipeName;
            _onLink = onLink;
            _logger = logger;
        }

        public Task Listening { get; private set; } = Task.CompletedTask;

        public void Start() => Listening = Task.Run(() => ListenAsync(_stop.Token));

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }

        private async Task ListenAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop);

                    using var read = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    read.CancelAfter(TimeSpan.FromSeconds(5));
                    var length = new byte[4];
                    await server.ReadExactlyAsync(length, read.Token);
                    var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(length);
                    if (size is <= 0 or > MaxMessageBytes)
                    {
                        continue;
                    }

                    var text = new byte[size];
                    await server.ReadExactlyAsync(text, read.Token);
                    await server.WriteAsync(new byte[] { 1 }, read.Token);
                    await server.FlushAsync(read.Token);
                    _onLink(Encoding.UTF8.GetString(text));
                }
                catch (Exception) when (stop.IsCancellationRequested)
                {
                    // Stopping: the app is closing.
                    return;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or EndOfStreamException)
                {
                    // A start that gave up half way, or a second listener in another copy of the app.
                    _logger?.LogWarning("A hand-over from another start of the app was not received: {Error}", ex.GetType().Name);
                    await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                }
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
