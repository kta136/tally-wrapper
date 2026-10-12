using System.ServiceProcess;
using ShowroomBilling.Contracts.Clients;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.ServerTray.Ui;

namespace ShowroomBilling.ServerTray;

/// <summary>What the operator can do to the Windows service from where it stands.</summary>
internal enum ServiceAction
{
    None,
    Install,
    Start,
    RestartOrStop,
}

/// <summary>One line of the dashboard's health card: a sentence, and the same in a word.</summary>
internal sealed record HealthLine(Tone Tone, string Description, string Chip);

/// <summary>
/// Everything the server dashboard shows, worked out from what the checks
/// found. The window only draws it; the wording and the tones are decided
/// here, where they can be tested without a window.
/// </summary>
internal sealed record DashboardState(
    Tone Tone,
    string Verdict,
    string Detail,
    HealthLine Service,
    ServiceAction ServiceAction,
    HealthLine Server,
    HealthLine Database,
    Tone CountersTone,
    string CountersChip,
    IReadOnlyList<CounterRow> Counters,
    string CountersEmptyText)
{
    private static readonly HealthLine NotCheckedYet = new(Tone.Neutral, "Checking.", "Checking");

    /// <summary>What the window shows until the first check comes back.</summary>
    public static DashboardState Checking { get; } = new(
        Tone.Neutral,
        "Checking the server",
        "One moment.",
        NotCheckedYet,
        ServiceAction.None,
        NotCheckedYet,
        NotCheckedYet,
        Tone.Neutral,
        "Checking",
        [],
        "Checking.");

    /// <param name="service">The Windows service's status; null when it is not installed.</param>
    /// <param name="serverProblem">Why the server did not answer on this PC; null when it did.</param>
    /// <param name="runtime">What the server reported about its database; null when it reported nothing.</param>
    /// <param name="counters">The counters the server has heard from; null when the list could not be read.</param>
    /// <param name="countersProblem">Why that list could not be read.</param>
    /// <param name="checkedAt">When these were read.</param>
    public static DashboardState From(
        ServiceControllerStatus? service,
        string? serverProblem,
        RuntimeHealthResponse? runtime,
        IReadOnlyList<ClientPresenceResponse>? counters,
        string? countersProblem,
        DateTimeOffset checkedAt)
    {
        var serverAnswers = serverProblem is null;
        var (verdictTone, verdict, detail) = Judge(service, serverAnswers, runtime, counters);
        var (countersTone, countersChip, countersEmpty) = DescribeCounters(serverAnswers, counters, countersProblem);

        return new DashboardState(
            verdictTone,
            verdict,
            $"{detail} Checked {checkedAt.LocalDateTime:HH:mm:ss}.",
            ServiceLine(service),
            service switch
            {
                null => ServiceAction.Install,
                ServiceControllerStatus.Running => ServiceAction.RestartOrStop,
                ServiceControllerStatus.Stopped => ServiceAction.Start,
                _ => ServiceAction.None,
            },
            ServerLine(service, serverProblem),
            DatabaseLine(serverAnswers, runtime),
            countersTone,
            countersChip,
            (counters ?? [])
                .Select(counter => new CounterRow(
                    string.IsNullOrWhiteSpace(counter.CounterName) ? "(no name)" : counter.CounterName,
                    $@"{counter.MachineName}\{counter.UserName}",
                    counter.RemoteAddress,
                    counter.LastSeenAtUtc.LocalDateTime.ToString("HH:mm:ss")))
                .ToList(),
            countersEmpty);
    }

    private static (Tone Tone, string Verdict, string Detail) Judge(
        ServiceControllerStatus? service,
        bool serverAnswers,
        RuntimeHealthResponse? runtime,
        IReadOnlyList<ClientPresenceResponse>? counters)
    {
        const string cannotBill = "Counters cannot bill";

        switch (service)
        {
            case null:
                return (Tone.Err, cannotBill, "The server is not installed on this PC.");
            case ServiceControllerStatus.StartPending:
                return (Tone.Warn, "Starting", "The Windows service is starting.");
            case ServiceControllerStatus.StopPending:
                return (Tone.Warn, "Stopping", "The Windows service is stopping.");
            case ServiceControllerStatus.Stopped:
                return (Tone.Err, cannotBill, "The Windows service is stopped.");
            case not ServiceControllerStatus.Running:
                return (Tone.Err, cannotBill, "The Windows service is not running.");
        }

        if (!serverAnswers)
        {
            return (Tone.Err, cannotBill, "The service is running, but the server does not answer.");
        }

        if (runtime is null)
        {
            return (Tone.Warn, "Database not checked", "The server answered, but did not report on its database.");
        }

        if (runtime.PendingMigrationCount > 0)
        {
            return (Tone.Err, cannotBill, "The database needs an upgrade before this version can use it.");
        }

        if (!runtime.DatabaseReachable)
        {
            return (Tone.Err, cannotBill, "The server cannot reach its database.");
        }

        if (runtime.DatabaseIdentityMatches == false)
        {
            return (Tone.Warn, "Check the database", "It is not the database this server expects.");
        }

        return (Tone.Ok, "Ready for billing", counters?.Count switch
        {
            null => "The server and its database are answering.",
            0 => "No counter is connected right now.",
            1 => "1 counter is connected.",
            var count => $"{count} counters are connected.",
        });
    }

    private static HealthLine ServiceLine(ServiceControllerStatus? service) => service switch
    {
        null => new HealthLine(Tone.Err, "Not installed on this PC. Choose Install to set it up.", "Not installed"),
        ServiceControllerStatus.Running => new HealthLine(
            Tone.Ok,
            "Runs in the background and starts with Windows, whether or not anyone is signed in.",
            "Running"),
        ServiceControllerStatus.Stopped => new HealthLine(
            Tone.Err,
            "Counters cannot reach the server until it is started.",
            "Stopped"),
        ServiceControllerStatus.StartPending => new HealthLine(Tone.Warn, "Starting.", "Starting"),
        ServiceControllerStatus.StopPending => new HealthLine(Tone.Warn, "Stopping.", "Stopping"),
        _ => new HealthLine(Tone.Warn, "Paused. Restart it from Windows Services.", "Paused"),
    };

    private static HealthLine ServerLine(ServiceControllerStatus? service, string? problem)
    {
        if (problem is null)
        {
            return new HealthLine(Tone.Ok, "Answering on this PC.", "OK");
        }

        return service == ServiceControllerStatus.Running
            ? new HealthLine(Tone.Err, $"The service is running, but the server does not answer: {problem}", "No answer")
            : new HealthLine(Tone.Neutral, "Not running.", "Off");
    }

    /// <summary>
    /// A database that has not had this build's migrations comes first: it
    /// answers, and "OK" would hide that no bill can be read.
    /// </summary>
    private static HealthLine DatabaseLine(bool serverAnswers, RuntimeHealthResponse? runtime)
    {
        if (!serverAnswers)
        {
            return new HealthLine(Tone.Neutral, "Not known while the server does not answer.", "Unknown");
        }

        if (runtime is null)
        {
            return new HealthLine(Tone.Warn, "The server answered, but did not report on its database.", "Unknown");
        }

        if (runtime.PendingMigrationCount is > 0 and var pending)
        {
            var missing = pending == 1
                ? "1 migration of this version has not been applied."
                : $"{pending} migrations of this version have not been applied.";
            return new HealthLine(
                Tone.Err,
                $"{missing} Bills cannot be read or saved until the database owner applies them.",
                "Upgrade needed");
        }

        if (!runtime.DatabaseReachable)
        {
            return new HealthLine(
                Tone.Err,
                "Not answering. Check this PC's internet link first, then the connection below.",
                "Not answering");
        }

        if (runtime.DatabaseIdentityMatches == false)
        {
            var marked = string.IsNullOrWhiteSpace(runtime.DatabaseIdentity) ? "not marked" : $"marked {runtime.DatabaseIdentity}";
            return new HealthLine(
                Tone.Warn,
                $"Answering, but it is {marked} and this server expects {runtime.ExpectedDatabaseIdentity}. Check the connection below.",
                "Check");
        }

        return new HealthLine(
            Tone.Ok,
            string.IsNullOrWhiteSpace(runtime.DatabaseIdentity)
                ? "Answering and up to date."
                : $"Answering and up to date. Marked {runtime.DatabaseIdentity}.",
            "OK");
    }

    private static (Tone Tone, string Chip, string EmptyText) DescribeCounters(
        bool serverAnswers,
        IReadOnlyList<ClientPresenceResponse>? counters,
        string? problem)
    {
        if (counters is null)
        {
            return (
                Tone.Neutral,
                "Unknown",
                serverAnswers ? $"The list could not be read: {problem}" : "Not known while the server does not answer.");
        }

        return counters.Count == 0
            ? (Tone.Neutral, "None", "No counter has been in touch in the last 2 minutes.")
            : (Tone.Ok, counters.Count.ToString(), string.Empty);
    }
}
