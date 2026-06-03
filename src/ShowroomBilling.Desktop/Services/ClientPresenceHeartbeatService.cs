using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShowroomBilling.Contracts.Clients;
using ShowroomBilling.Desktop.Configuration;

namespace ShowroomBilling.Desktop.Services;

public sealed class ClientPresenceHeartbeatService(
    IClientPresenceApiClient client,
    IOptions<DesktopBootstrapOptions> options,
    DesktopDeviceIdentityStore identityStore,
    ILogger<ClientPresenceHeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await SendOnceAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested
               && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SendOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configured = options.Value;
            var request = new ClientHeartbeatRequest(
                DeviceId: identityStore.Resolve(configured.DeviceId),
                CounterName: configured.CounterName,
                AppVersion: typeof(ClientPresenceHeartbeatService).Assembly.GetName().Version?.ToString() ?? "unknown",
                ConnectionMode: configured.ConnectionMode,
                MachineName: Environment.MachineName,
                UserName: Environment.UserName);
            await client.SendHeartbeatAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Client presence heartbeat failed.");
        }
    }
}
