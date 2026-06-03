using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Services.ProcessSupervision;

public interface IChildProcessSupervisor
{
    bool CanRestartApi { get; }

    bool RestartApi();
}

public sealed class ChildProcessSupervisor : IChildProcessSupervisor, IDisposable
{
    private readonly ILogger<ChildProcessSupervisor> _logger;
    private readonly ChildProcessOptions _options;
    private readonly IApiEndpointResolver _endpointResolver;
    private readonly JobObject? _job;
    private readonly List<Process> _spawned = new();
    private bool _disposed;

    public ChildProcessSupervisor(
        IOptions<ChildProcessOptions> options,
        IApiEndpointResolver endpointResolver,
        ILogger<ChildProcessSupervisor> logger)
    {
        _logger = logger;
        _options = options.Value;
        _endpointResolver = endpointResolver;

        if (!_options.Enabled)
        {
            _logger.LogInformation("Child-process supervision disabled by config; API lifecycle not managed by Desktop.");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("Child-process supervision requires Windows; skipping.");
            return;
        }

        _job = new JobObject();
    }

    public void Start()
    {
        if (_endpointResolver.IsServerMode)
        {
            _logger.LogInformation("Desktop is in server API mode; embedded API child process will not be launched.");
            return;
        }

        if (_options.Enabled is false || _job is null) return;

        TryLaunch("API", _options.Api);
    }

    public bool CanRestartApi =>
        _options.Enabled
        && !_endpointResolver.IsServerMode
        && _options.Api.Enabled
        && _job is not null
        && !string.IsNullOrWhiteSpace(_options.Api.ExecutablePath);

    public bool RestartApi()
    {
        if (!CanRestartApi)
        {
            _logger.LogWarning("API restart requested, but Desktop is not managing the API child process.");
            return false;
        }

        StopSpawnedProcesses();
        WaitForProbePortsToClose(_options.Api, TimeSpan.FromSeconds(3));
        TryLaunch("API", _options.Api);
        return _spawned.Count > 0;
    }

    private void TryLaunch(string label, ChildProcessSpec spec)
    {
        if (!spec.Enabled)
        {
            _logger.LogInformation("{Label}: child launch disabled by config.", label);
            return;
        }

        if (IsAlreadyRunning(spec, out var reason))
        {
            _logger.LogInformation("{Label}: already running ({Reason}); Desktop will not manage its lifecycle.", label, reason);
            return;
        }

        if (string.IsNullOrWhiteSpace(spec.ExecutablePath))
        {
            _logger.LogWarning("{Label}: no ExecutablePath configured; skipping.", label);
            return;
        }

        var resolvedExe = ResolvePath(spec.ExecutablePath);
        if (!File.Exists(resolvedExe))
        {
            _logger.LogError("{Label}: executable not found at {Path}; skipping. Build the project or configure the path.", label, resolvedExe);
            return;
        }

        var workingDir = !string.IsNullOrWhiteSpace(spec.WorkingDirectory)
            ? ResolvePath(spec.WorkingDirectory!)
            : Path.GetDirectoryName(resolvedExe)!;

        var psi = new ProcessStartInfo
        {
            FileName = resolvedExe,
            Arguments = spec.Arguments ?? string.Empty,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (name, value) in spec.EnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            psi.Environment[name] = value ?? string.Empty;
        }

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                _logger.LogError("{Label}: Process.Start returned null for {Path}.", label, resolvedExe);
                return;
            }

            _job!.AssignProcess(proc);
            _spawned.Add(proc);
            _logger.LogInformation("{Label}: spawned PID {Pid} from {Path} (will be killed on Desktop exit).", label, proc.Id, resolvedExe);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Label}: failed to spawn {Path}.", label, resolvedExe);
            try { proc?.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
        }
    }

    private static bool IsAlreadyRunning(ChildProcessSpec spec, out string reason)
    {
        if (!string.IsNullOrWhiteSpace(spec.AlreadyRunningProbeHost))
        {
            foreach (var port in ProbePorts(spec))
            {
                try
                {
                    if (TcpPortProbe.CanConnect(spec.AlreadyRunningProbeHost!, port, TimeSpan.FromMilliseconds(400)))
                    {
                        reason = $"port {spec.AlreadyRunningProbeHost}:{port} already in use";
                        return true;
                    }
                }
                catch
                {
                    // treat any failure as "not running"
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.AlreadyRunningProcessName))
        {
            var matches = Process.GetProcessesByName(spec.AlreadyRunningProcessName);
            try
            {
                if (matches.Length > 0)
                {
                    reason = $"process \"{spec.AlreadyRunningProcessName}\" already running (PID {matches[0].Id})";
                    return true;
                }
            }
            finally
            {
                foreach (var m in matches) m.Dispose();
            }
        }

        reason = string.Empty;
        return false;
    }

    private static IEnumerable<int> ProbePorts(ChildProcessSpec spec)
    {
        if (spec.AlreadyRunningProbePorts is { Length: > 0 })
        {
            foreach (var port in spec.AlreadyRunningProbePorts.Where(port => port > 0).Distinct())
                yield return port;
            yield break;
        }

    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private void StopSpawnedProcesses()
    {
        foreach (var p in _spawned)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop child process PID {Pid}.", SafeProcessId(p));
            }
            try { p.Dispose(); } catch { }
        }
        _spawned.Clear();
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    private static void WaitForProbePortsToClose(ChildProcessSpec spec, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(spec.AlreadyRunningProbeHost))
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var anyOpen = ProbePorts(spec).Any(port =>
                TcpPortProbe.CanConnect(spec.AlreadyRunningProbeHost!, port, TimeSpan.FromMilliseconds(100)));
            if (!anyOpen)
            {
                return;
            }
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Closing the job handle triggers KillOnJobClose — the OS terminates
        // every assigned child. We still dispose our Process handles.
        _job?.Dispose();

        StopSpawnedProcesses();
    }
}
