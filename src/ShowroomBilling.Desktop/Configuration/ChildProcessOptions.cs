namespace ShowroomBilling.Desktop.Configuration;

public sealed class ChildProcessOptions
{
    public const string SectionName = "ChildProcesses";

    public bool Enabled { get; set; } = true;
    public ChildProcessSpec Api { get; set; } = new();
}

public sealed class ChildProcessSpec
{
    public bool Enabled { get; set; } = true;
    public string? ExecutablePath { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Arguments { get; set; }
    public Dictionary<string, string?> EnvironmentVariables { get; set; } = new();

    public string? AlreadyRunningProbeHost { get; set; }
    public int[]? AlreadyRunningProbePorts { get; set; }

    public string? AlreadyRunningProcessName { get; set; }
}
