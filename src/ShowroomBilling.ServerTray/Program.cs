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
                MessageBox.Show(
                    $"Server setup failed:\n\n{ex.Message}\n\nLog: {options.InstallLogPath}",
                    ServerTrayOptions.ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }

            return;
        }

        var installed = ServerInstaller.EnsureInstalledInteractive(options);
        if (!installed)
        {
            MessageBox.Show(
                "Server setup did not complete. The tray will open, but the API service may be unavailable.",
                ServerTrayOptions.ProductName,
                MessageBoxButtons.OK,
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
