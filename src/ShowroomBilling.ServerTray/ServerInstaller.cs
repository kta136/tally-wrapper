using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.ServiceProcess;
using Microsoft.Win32;
using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Maintenance;

namespace ShowroomBilling.ServerTray;

public static class ServerInstaller
{
    public const string DefaultLanCidr = "192.168.0.0/16";

    // This class is the only server installer. tools/uninstall-server-api-service.ps1
    // undoes it and repeats the next three names: change them together.
    private const string RunValueName = "ShowroomBilling.ServerTray";
    private const string FirewallDisplayName = "Tally Wrapper API LAN";
    // The rule name used before the product was renamed; removed on repair.
    private const string LegacyFirewallDisplayName = "Showroom Billing API LAN";
    private const string ApiResourceName = "ShowroomBilling.Api.exe";
    private const string TrustedNetworkVariable = "DeviceAuth__TrustedNetworks__0";

    public static bool IsServiceInstalled(string serviceName) => TryGetServiceStatus(serviceName) is not null;

    /// <summary>
    /// The service's current status, or null when it is not installed.
    /// Constructing a <see cref="ServiceController"/> never fails for a
    /// missing service; only reading its status does.
    /// </summary>
    public static ServiceControllerStatus? TryGetServiceStatus(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
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

        // An automatic repair (the tray carries a newer API than the one
        // installed) keeps the range the operator chose. Only a first install,
        // or Install / Repair pressed by hand, asks for it, and then starts
        // from the current value rather than the default.
        var configuredCidr = ReadConfiguredLanCidr(options.ServiceName);
        var lanCidr = !forceRepair && configuredCidr is not null
            ? configuredCidr
            : PromptForLanCidr(configuredCidr ?? DefaultLanCidr);
        if (string.IsNullOrWhiteSpace(lanCidr))
        {
            return false;
        }

        var exitCode = RunSelfElevated("--install-service", "--lan-cidr", lanCidr);
        EnsureTrayStartup(options);
        if (exitCode != 0 && File.Exists(options.InstallLogPath))
        {
            CopyableMessageDialog.ShowMessage(
                null,
                $"Setup failed.\n\nThe end of the install log, {options.InstallLogPath}:\n\n{Tail(options.InstallLogPath, 3000)}",
                MessageBoxIcon.Error);
        }

        return exitCode == 0;
    }

    public static void InstallOrRepairElevated(ServerTrayOptions options, string lanCidr)
    {
        // Checked before anything is written: a bad range would otherwise fail
        // at the firewall step, after the service and its settings exist.
        if (!TryNormalizeLanCidr(lanCidr, out var normalizedCidr))
        {
            throw new ArgumentException($"'{lanCidr}' is not a network range such as {DefaultLanCidr}.", nameof(lanCidr));
        }
        lanCidr = normalizedCidr;

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

    /// <summary>Accepts an IPv4 or IPv6 range in CIDR form and returns it trimmed.</summary>
    internal static bool TryNormalizeLanCidr(string? text, out string normalized)
    {
        normalized = text?.Trim() ?? string.Empty;
        return normalized.Contains('/') && System.Net.IPNetwork.TryParse(normalized, out _);
    }

    /// <summary>The trusted LAN range the installed service is running with, if any.</summary>
    internal static string? ReadConfiguredLanCidr(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return ReadLanCidr(key?.GetValue("Environment") as string[]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static string? ReadLanCidr(IEnumerable<string>? serviceEnvironment)
    {
        var value = serviceEnvironment?
            .Where(line => line.StartsWith(TrustedNetworkVariable + "=", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(TrustedNetworkVariable.Length + 1)..])
            .FirstOrDefault();
        return TryNormalizeLanCidr(value, out var normalized) ? normalized : null;
    }

    public static void EnsureTrayStartup(ServerTrayOptions options)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.SetValue(RunValueName, $"\"{options.TrayExecutablePath}\"", RegistryValueKind.String);
    }

    private static bool ApiExecutableChanged(string targetPath)
    {
        using var embedded = OpenApiResource();
        return FileDiffersFrom(embedded, targetPath);
    }

    /// <summary>True when <paramref name="path"/> is missing or its content differs from <paramref name="expected"/>.</summary>
    internal static bool FileDiffersFrom(Stream expected, string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        using var file = File.OpenRead(path);
        if (expected.CanSeek && expected.Length != file.Length)
        {
            return true;
        }

        return !SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(file));
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
            $"{AppPaths.DataDirectoryEnvironmentVariable}={options.ConfigRoot}",
            $"Logging__File__Directory={options.LogsPath}",
            "Database__AutoMigrateOnStartup=true",
            "DeviceAuth__Mode=TrustedLan",
            $"{TrustedNetworkVariable}={lanCidr}"
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

        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var error = errorTask.GetAwaiter().GetResult();
        if (requireSuccess && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} failed with exit code {process.ExitCode}.\n{output}\n{error}".Trim());
        }
    }

    private static string? PromptForLanCidr(string defaultValue)
    {
        var entered = Ui.TrayDialog.Prompt(
            owner: null,
            title: "Which PCs may use this server?",
            message: "Counters whose address is inside this network range can save bills. Any other PC is refused.",
            value: defaultValue,
            hint: $"For a network where every address starts with 192.168.1, that is {DefaultLanCidr}.",
            actionText: "Install",
            problemWith: LanCidrProblem);
        return entered is not null && TryNormalizeLanCidr(entered, out var normalized) ? normalized : null;
    }

    /// <summary>What is wrong with a range the operator typed, or null when it is one.</summary>
    internal static string? LanCidrProblem(string text) =>
        TryNormalizeLanCidr(text, out _)
            ? null
            : $"'{text.Trim()}' is not a network range. Enter one such as {DefaultLanCidr}.";

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string EscapePowerShell(string value) => value.Replace("'", "''");

    internal static void Log(ServerTrayOptions options, string message)
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
