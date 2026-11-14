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
                ServerInstaller.Log(options, $"FAILED {ex}");
                CopyableMessageDialog.ShowMessage(
                    null,
                    $"Setup failed.\n\n{ex.Message}\n\nThe install log has the rest: {options.InstallLogPath}",
                    MessageBoxIcon.Error);
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

        var installed = ServerInstaller.EnsureInstalledInteractive(options);
        if (!installed)
        {
            CopyableMessageDialog.ShowMessage(
                null,
                "Setup did not finish.\n\nThe tray icon will still appear, but counters may not be able to bill. Open the dashboard from it to see what is missing.",
                MessageBoxIcon.Warning);
        }

        Application.Run(new TrayApplicationContext(options));
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
