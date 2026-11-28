using System.ComponentModel;
using System.ServiceProcess;

namespace ShowroomBilling.ServerTray;

internal enum ServiceCommand
{
    Start,
    Stop,
    Restart,
}

/// <summary>
/// Starting and stopping the API's Windows service. The same code runs in two
/// places: in the tray itself, which is enough when the tray was started as
/// administrator, and in the elevated copy the tray starts when Windows says
/// no (<see cref="ServerInstaller.ControlServiceElevated"/>).
/// </summary>
internal static class ServiceCommands
{
    /// <summary>The word for an action on the command line of the elevated copy.</summary>
    public static string Verb(ServiceCommand action) => action switch
    {
        ServiceCommand.Start => "start",
        ServiceCommand.Stop => "stop",
        _ => "restart",
    };

    public static bool TryParse(string? verb, out ServiceCommand action)
    {
        switch (verb?.Trim().ToLowerInvariant())
        {
            case "start":
                action = ServiceCommand.Start;
                return true;
            case "stop":
                action = ServiceCommand.Stop;
                return true;
            case "restart":
                action = ServiceCommand.Restart;
                return true;
            default:
                action = default;
                return false;
        }
    }

    /// <summary>Does it and waits for the service to get there. Throws when Windows refuses or the service does not follow.</summary>
    public static void Apply(string serviceName, ServiceCommand action, int timeoutSeconds = 20)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var service = new ServiceController(serviceName);
        service.Refresh();

        if (action is ServiceCommand.Stop or ServiceCommand.Restart && service.Status != ServiceControllerStatus.Stopped)
        {
            if (service.Status != ServiceControllerStatus.StopPending)
            {
                service.Stop();
            }

            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            service.Refresh();
        }

        if (action is ServiceCommand.Start or ServiceCommand.Restart && service.Status != ServiceControllerStatus.Running)
        {
            if (service.Status == ServiceControllerStatus.StopPending)
            {
                service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                service.Refresh();
            }

            if (service.Status != ServiceControllerStatus.StartPending)
            {
                service.Start();
            }

            service.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }
    }

    /// <summary>
    /// True when Windows refused because of rights. By default only an
    /// administrator may start or stop a service, and the tray starts with
    /// the signed-in user's ordinary rights.
    /// </summary>
    public static bool IsAccessDenied(Exception exception)
    {
        const int AccessDenied = 5;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception { NativeErrorCode: AccessDenied } or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}
