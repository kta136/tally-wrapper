namespace ShowroomBilling.ServerTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var options = new ServerTrayOptions();
        if (args.Any(arg => string.Equals(arg, "--verify-embedded-api", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"Embedded API bytes: {ServerInstaller.GetEmbeddedApiLength()}");
            return;
        }

        if (args.Any(arg => string.Equals(arg, "--install-service", StringComparison.OrdinalIgnoreCase)))
        {
            var lanCidr = GetArgumentValue(args, "--lan-cidr") ?? ServerInstaller.DefaultLanCidr;
            try
            {
                ServerInstaller.InstallOrRepairElevated(options, lanCidr);
            }
            catch (Exception ex)
            {
                // No message from here: the tray that started this copy reads
                // the exit code and reports, with the end of this log.
                ServerInstaller.Log(options, $"FAILED {ex}");
                Environment.ExitCode = 1;
            }

            return;
        }

        // The elevated copy the tray starts when Windows will not let it start
        // or stop the service itself. It says nothing: the tray that started it
        // reads the exit code and reports.
        if (GetArgumentValue(args, ServerInstaller.ServiceControlArgument) is { } verb)
        {
            try
            {
                if (!ServiceCommands.TryParse(verb, out var action))
                {
                    throw new ArgumentException($"'{verb}' is not start, stop or restart.");
                }

                ServiceCommands.Apply(options.ServiceName, action);
            }
            catch (Exception ex)
            {
                ServerInstaller.Log(options, $"Service {verb} FAILED {ex}");
                Environment.ExitCode = 1;
            }

            return;
        }

        var outcome = ServerInstaller.EnsureInstalledInteractive(options);
        if (outcome != InstallOutcome.Done)
        {
            var (message, icon) = ServerTrayActions.InstallReport(
                outcome,
                ServerInstaller.TryGetServiceStatus(options.ServiceName),
                options.InstallLogPath,
                ServerInstaller.InstallLogTail(options));
            CopyableMessageDialog.ShowMessage(
                null,
                $"{message}\n\nThe tray icon will still appear, but counters may not be able to bill. Open the dashboard from it to see what is missing.",
                icon);
        }

        // One tray for this Windows session. Running the exe again is how the
        // server is repaired and upgraded, so a copy that is already running is
        // asked to close and this one takes its place; two used to run side by
        // side, each with its own icon and timers.
        using var instance = SingleInstance.Acquire(TimeSpan.FromSeconds(15), out var tookOver);
        if (instance is null)
        {
            // The other copy did not close. It keeps the tray; this one is done.
            return;
        }

        using var tray = new TrayApplicationContext(options, showDashboard: tookOver);
        instance.OnAskedToHandOver(tray.Close);
        Application.Run(tray);
    }

    private static string? GetArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
