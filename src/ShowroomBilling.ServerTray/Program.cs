namespace ShowroomBilling.ServerTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
                TryWriteInstallFailure(options, ex);
                MessageBox.Show(
                    $"Server setup failed:\n\n{ex.Message}\n\nLog: {options.InstallLogPath}",
                    "Tally Wrapper Server",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        var installed = ServerInstaller.EnsureInstalledInteractive(options);
        if (!installed)
        {
            MessageBox.Show(
                "Server setup did not complete. The tray will open, but the API service may be unavailable.",
                "Tally Wrapper Server",
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

    private static void TryWriteInstallFailure(ServerTrayOptions options, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(options.LogsPath);
            File.AppendAllText(
                options.InstallLogPath,
                $"{DateTimeOffset.Now:O} FAILED {exception}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort install diagnostics.
        }
    }
}
