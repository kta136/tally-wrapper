using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.ServiceProcess;
using Microsoft.Win32;
using ShowroomBilling.Contracts.Maintenance;

namespace ShowroomBilling.ServerTray;

public static class ServerInstaller
{
    public const string DefaultLanCidr = "192.168.0.0/16";
    private const string RunValueName = "ShowroomBilling.ServerTray";
    private const string FirewallDisplayName = "Tally Wrapper API LAN";
    private static readonly string LegacyFirewallDisplayName = string.Concat("Showroom", " Billing API LAN");
    private const string ApiResourceName = "ShowroomBilling.Api.exe";

    public static bool IsServiceInstalled(string serviceName)
    {
        try
        {
            using var _ = new ServiceController(serviceName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static long GetEmbeddedApiLength()
    {
        using var stream = OpenApiResource();
        return stream.Length;
    }

    public static bool EnsureInstalledInteractive(ServerTrayOptions options, bool forceRepair = false)
    {
        if (!forceRepair
            && IsServiceInstalled(options.ServiceName)
            && File.Exists(options.ApiExecutablePath)
            && !ApiExecutableChanged(options.ApiExecutablePath))
        {
            EnsureTrayStartup(options);
            EnsureServiceRunning(options.ServiceName);
            return true;
        }

        var lanCidr = PromptForLanCidr(DefaultLanCidr);
        if (string.IsNullOrWhiteSpace(lanCidr))
        {
            return false;
        }

        var exitCode = RunSelfElevated("--install-service", "--lan-cidr", lanCidr);
        EnsureTrayStartup(options);
        if (exitCode != 0 && File.Exists(options.InstallLogPath))
        {
            MessageBox.Show(
                $"Server setup failed. Install log:\n{options.InstallLogPath}\n\n{Tail(options.InstallLogPath, 3000)}",
                "Tally Wrapper Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        return exitCode == 0;
    }

    public static void InstallOrRepairElevated(ServerTrayOptions options, string lanCidr)
    {
        Directory.CreateDirectory(options.ConfigRoot);
        Directory.CreateDirectory(options.LogsPath);
        Directory.CreateDirectory(options.BinPath);
        Log(options, "Install/repair started.");
        Log(options, $"Service: {options.ServiceName}");
        Log(options, $"Config root: {options.ConfigRoot}");
        Log(options, $"API target: {options.ApiExecutablePath}");
        Log(options, $"LAN CIDR: {lanCidr}");

        var apiChanged = ApiExecutableChanged(options.ApiExecutablePath);
        if (apiChanged && IsServiceInstalled(options.ServiceName))
        {
            Log(options, "Embedded API differs; stopping service before replacement.");
            StopService(options.ServiceName);
        }
        if (apiChanged)
        {
            Log(options, "Writing embedded API executable.");
            WriteApiExecutable(options.ApiExecutablePath);
        }

        Log(options, "Ensuring maintenance token.");
        EnsureMaintenanceToken(options.ConfigRoot);
        Log(options, "Ensuring Windows Service.");
        EnsureService(options);
        Log(options, "Ensuring service environment.");
        EnsureServiceEnvironment(options, lanCidr);
        Log(options, "Ensuring service recovery settings.");
        EnsureServiceFailureRecovery(options.ServiceName);
        Log(options, "Ensuring firewall rule.");
        EnsureFirewallRule(options, lanCidr);
        Log(options, "Starting service if needed.");
        EnsureServiceRunning(options.ServiceName);
        Log(options, "Install/repair completed.");
    }

    public static void EnsureTrayStartup(ServerTrayOptions options)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.SetValue(RunValueName, $"\"{options.TrayExecutablePath}\"", RegistryValueKind.String);
    }

    private static bool ApiExecutableChanged(string targetPath)
    {
        using var stream = OpenApiResource();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();

        return !File.Exists(targetPath) || !BytesEqual(targetPath, bytes);
    }

    private static void WriteApiExecutable(string targetPath)
    {
        using var stream = OpenApiResource();
        using var file = File.Create(targetPath);
        stream.CopyTo(file);
    }

    private static Stream OpenApiResource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(ApiResourceName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException("Embedded API executable was not found in this server installer.");
        }

        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded API executable could not be opened.");
    }

    private static bool BytesEqual(string path, byte[] expected)
    {
        using var current = SHA256.Create();
        using var file = File.OpenRead(path);
        var existingHash = current.ComputeHash(file);
        var expectedHash = SHA256.HashData(expected);
        return existingHash.SequenceEqual(expectedHash);
    }

    private static void EnsureMaintenanceToken(string configRoot)
    {
        var tokenPath = Path.Combine(configRoot, MaintenanceTokenConstants.FileName);
        if (File.Exists(tokenPath))
        {
            return;
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(tokenPath, Convert.ToBase64String(bytes));
    }

    private static void EnsureService(ServerTrayOptions options)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $serviceName = '{{EscapePowerShell(options.ServiceName)}}'
            $displayName = 'Tally Wrapper API'
            $apiPath = '{{EscapePowerShell(options.ApiExecutablePath)}}'
            $binaryPath = '"' + $apiPath + '"'

            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($null -eq $service) {
                New-Service -Name $serviceName -DisplayName $displayName -BinaryPathName $binaryPath -StartupType Automatic | Out-Null
            }

            & sc.exe config $serviceName binPath= $binaryPath start= auto DisplayName= $displayName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "sc config failed with exit code $LASTEXITCODE" }

            & sc.exe description $serviceName 'Tally Wrapper API service hosted on the Tally server.' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "sc description failed with exit code $LASTEXITCODE" }
            """;

        RunPowerShellScript(script, "showroom-service");
    }

    private static void EnsureServiceEnvironment(ServerTrayOptions options, string lanCidr)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{options.ServiceName}", writable: true)
            ?? throw new InvalidOperationException($"Service registry key for '{options.ServiceName}' was not found.");

        var values = new[]
        {
            "ASPNETCORE_ENVIRONMENT=Production",
            "DOTNET_ENVIRONMENT=Production",
            "ASPNETCORE_URLS=http://0.0.0.0:5107",
            $"SHOWROOM_BILLING_SERVICE_NAME={options.ServiceName}",
            $"SHOWROOM_BILLING_APPDATA={options.ConfigRoot}",
            $"Logging__File__Directory={options.LogsPath}",
            "Database__AutoMigrateOnStartup=true",
            "DeviceAuth__Mode=TrustedLan",
            $"DeviceAuth__TrustedNetworks__0={lanCidr}"
        };
        key.SetValue("Environment", values, RegistryValueKind.MultiString);
    }

    private static void EnsureServiceFailureRecovery(string serviceName)
    {
        RunProcess(
            "sc.exe",
            $"failure \"{serviceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000",
            requireSuccess: false);
    }

    private static void EnsureFirewallRule(ServerTrayOptions options, string lanCidr)
    {
        var script = $"""
            $ErrorActionPreference = 'Stop'
            Get-NetFirewallRule -DisplayName '{EscapePowerShell(FirewallDisplayName)}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            Get-NetFirewallRule -DisplayName '{EscapePowerShell(LegacyFirewallDisplayName)}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            New-NetFirewallRule -DisplayName '{EscapePowerShell(FirewallDisplayName)}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5107 -RemoteAddress '{EscapePowerShell(lanCidr)}' -Program '{EscapePowerShell(options.ApiExecutablePath)}' | Out-Null
            """;

        RunPowerShellScript(script, "showroom-firewall");
    }

    private static void RunPowerShellScript(string script, string name)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, script);
        try
        {
            RunProcess(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                requireSuccess: true);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private static void EnsureServiceRunning(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (service.Status == ServiceControllerStatus.Running)
            {
                return;
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Service status is also shown in the tray; do not prevent the tray from opening.
        }
    }

    private static void StopService(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                return;
            }

            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Replacement will fail if the binary is still locked; that error is more useful.
        }
    }

    private static int RunSelfElevated(params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? Application.ExecutablePath,
            Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
            UseShellExecute = true,
            Verb = "runas"
        });

        process?.WaitForExit();
        return process?.ExitCode ?? -1;
    }

    private static void RunProcess(string fileName, string arguments, bool requireSuccess)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (requireSuccess && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} failed with exit code {process.ExitCode}.\n{output}\n{error}".Trim());
        }
    }

    private static string? PromptForLanCidr(string defaultValue)
    {
        using var form = new Form
        {
            Text = "Tally Wrapper Server Setup",
            Width = 430,
            Height = 155,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var label = new Label
        {
            Text = "Trusted LAN range for billing workstations:",
            Left = 12,
            Top = 14,
            Width = 380
        };
        var input = new TextBox
        {
            Text = defaultValue,
            Left = 12,
            Top = 42,
            Width = 386
        };
        var ok = new Button
        {
            Text = "Install",
            DialogResult = DialogResult.OK,
            Left = 242,
            Width = 75,
            Top = 78
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 323,
            Width = 75,
            Top = 78
        };

        form.Controls.AddRange([label, input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string EscapePowerShell(string value) => value.Replace("'", "''");

    private static void Log(ServerTrayOptions options, string message)
    {
        try
        {
            Directory.CreateDirectory(options.LogsPath);
            File.AppendAllText(
                options.InstallLogPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Install logging must never block setup.
        }
    }

    private static string Tail(string path, int maxChars)
    {
        try
        {
            var text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch (Exception ex)
        {
            return $"Could not read log: {ex.Message}";
        }
    }
}
