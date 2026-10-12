using System.ServiceProcess;
using ShowroomBilling.Contracts.Clients;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.ServerTray;
using ShowroomBilling.ServerTray.Ui;

namespace ShowroomBilling.Desktop.Tests.ServerTray;

/// <summary>
/// What the server dashboard says: the one sentence at the top, and the three
/// lines under it. This is what the person who has just installed a new server
/// build looks at.
/// </summary>
public sealed class DashboardStateTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 41, 7, TimeSpan.FromMinutes(330));

    [Fact]
    public void Everything_answering_is_ready_for_billing_and_says_how_many_counters_are_on()
    {
        var state = From(ServiceControllerStatus.Running, Runtime(reachable: true), counters: 3);

        Assert.Equal(Tone.Ok, state.Tone);
        Assert.Equal("Ready for billing", state.Verdict);
        Assert.StartsWith("3 counters are connected.", state.Detail);
        Assert.EndsWith($"Checked {At.LocalDateTime:HH:mm:ss}.", state.Detail);
        Assert.Equal(ServiceAction.RestartOrStop, state.ServiceAction);
        Assert.All([state.Service, state.Server, state.Database], line => Assert.Equal(Tone.Ok, line.Tone));
        Assert.Equal("Answering and up to date. Marked PROD.", state.Database.Description);
        Assert.Equal("3", state.CountersChip);
        Assert.Equal(3, state.Counters.Count);
    }

    [Theory]
    [InlineData(0, "No counter is connected right now.")]
    [InlineData(1, "1 counter is connected.")]
    public void The_count_is_said_in_words_that_fit_it(int counters, string expected)
    {
        Assert.StartsWith(expected, From(ServiceControllerStatus.Running, Runtime(reachable: true), counters).Detail);
    }

    [Fact]
    public void A_database_that_has_not_had_its_migrations_is_named_before_ready_or_not_answering()
    {
        // The database answers, so "OK" alone would hide that no bill can be
        // read. When it does not answer, the count is the last one found.
        var answering = From(ServiceControllerStatus.Running, Runtime(reachable: true, pending: 6), counters: 0);
        var silent = From(ServiceControllerStatus.Running, Runtime(reachable: false, pending: 6), counters: 0);

        Assert.Equal("Upgrade needed", answering.Database.Chip);
        Assert.Equal(Tone.Err, answering.Database.Tone);
        Assert.StartsWith("6 migrations of this version have not been applied.", answering.Database.Description);
        Assert.Equal("Counters cannot bill", answering.Verdict);
        Assert.Equal(answering.Database, silent.Database);
    }

    [Fact]
    public void One_missing_migration_is_one_migration()
    {
        var state = From(ServiceControllerStatus.Running, Runtime(reachable: true, pending: 1), counters: 0);

        Assert.StartsWith("1 migration of this version has not been applied.", state.Database.Description);
    }

    [Fact]
    public void A_database_that_does_not_answer_stops_billing_and_says_where_to_look()
    {
        var state = From(ServiceControllerStatus.Running, Runtime(reachable: false), counters: 2);

        Assert.Equal(Tone.Err, state.Tone);
        Assert.Equal("Counters cannot bill", state.Verdict);
        Assert.StartsWith("The server cannot reach its database.", state.Detail);
        Assert.Equal("Not answering", state.Database.Chip);
        Assert.Contains("internet link", state.Database.Description);
        // The service and the server are fine, and say so.
        Assert.Equal(Tone.Ok, state.Service.Tone);
        Assert.Equal(Tone.Ok, state.Server.Tone);
    }

    [Fact]
    public void The_wrong_database_is_a_warning_that_names_both_markers()
    {
        var runtime = Runtime(reachable: true) with { DatabaseIdentity = "DEV", DatabaseIdentityMatches = false };

        var state = From(ServiceControllerStatus.Running, runtime, counters: 1);

        Assert.Equal(Tone.Warn, state.Tone);
        Assert.Equal("Check the database", state.Verdict);
        Assert.Equal(Tone.Warn, state.Database.Tone);
        Assert.Contains("marked DEV", state.Database.Description);
        Assert.Contains("expects PROD", state.Database.Description);
    }

    [Fact]
    public void A_stopped_service_offers_start_and_does_not_guess_at_what_it_cannot_see()
    {
        var state = DashboardState.From(
            ServiceControllerStatus.Stopped, "Connection refused (127.0.0.1:5107)", null, null, null, At);

        Assert.Equal("Counters cannot bill", state.Verdict);
        Assert.StartsWith("The Windows service is stopped.", state.Detail);
        Assert.Equal(ServiceAction.Start, state.ServiceAction);
        Assert.Equal("Stopped", state.Service.Chip);
        Assert.Equal(Tone.Neutral, state.Server.Tone);
        Assert.Equal("Off", state.Server.Chip);
        Assert.Equal(Tone.Neutral, state.Database.Tone);
        Assert.Equal("Unknown", state.Database.Chip);
        Assert.Equal("Unknown", state.CountersChip);
        Assert.Equal("Not known while the server does not answer.", state.CountersEmptyText);
    }

    [Fact]
    public void A_service_that_is_not_installed_offers_install()
    {
        var state = DashboardState.From(null, "Connection refused (127.0.0.1:5107)", null, null, null, At);

        Assert.Equal("Counters cannot bill", state.Verdict);
        Assert.StartsWith("The server is not installed on this PC.", state.Detail);
        Assert.Equal(ServiceAction.Install, state.ServiceAction);
        Assert.Equal("Not installed", state.Service.Chip);
    }

    [Fact]
    public void A_running_service_whose_server_does_not_answer_says_why()
    {
        var state = DashboardState.From(ServiceControllerStatus.Running, "HTTP 503", null, null, null, At);

        Assert.Equal("Counters cannot bill", state.Verdict);
        Assert.Equal(Tone.Err, state.Server.Tone);
        Assert.EndsWith("the server does not answer: HTTP 503", state.Server.Description);
        // Restarting is the thing to try.
        Assert.Equal(ServiceAction.RestartOrStop, state.ServiceAction);
    }

    [Theory]
    [InlineData(ServiceControllerStatus.StartPending, "Starting")]
    [InlineData(ServiceControllerStatus.StopPending, "Stopping")]
    public void A_service_on_its_way_offers_nothing_until_it_gets_there(ServiceControllerStatus status, string expected)
    {
        var state = DashboardState.From(status, "Connection refused", null, null, null, At);

        Assert.Equal(Tone.Warn, state.Tone);
        Assert.Equal(expected, state.Verdict);
        Assert.Equal(expected, state.Service.Chip);
        Assert.Equal(ServiceAction.None, state.ServiceAction);
    }

    [Fact]
    public void A_list_of_counters_that_could_not_be_read_says_so_in_its_place()
    {
        var state = DashboardState.From(
            ServiceControllerStatus.Running, null, Runtime(reachable: true), null, "HTTP 403", At);

        Assert.Equal("Ready for billing", state.Verdict);
        Assert.Equal("Unknown", state.CountersChip);
        Assert.Equal("The list could not be read: HTTP 403", state.CountersEmptyText);
        Assert.Empty(state.Counters);
    }

    [Fact]
    public void A_counter_is_listed_with_its_pc_its_user_and_when_it_was_last_heard()
    {
        var state = From(ServiceControllerStatus.Running, Runtime(reachable: true), counters: 1);

        var row = Assert.Single(state.Counters);
        Assert.Equal("Counter 1", row.Counter);
        Assert.Equal(@"SHOWROOM-PC1\ravi", row.PcAndUser);
        Assert.Equal("192.168.1.21", row.Address);
        Assert.Equal(At.AddSeconds(-9).LocalDateTime.ToString("HH:mm:ss"), row.LastSeen);
    }

    private static DashboardState From(ServiceControllerStatus service, RuntimeHealthResponse runtime, int counters) =>
        DashboardState.From(
            service,
            serverProblem: null,
            runtime,
            Enumerable.Range(1, counters)
                .Select(n => new ClientPresenceResponse(
                    $"device-{n}",
                    $"Counter {n}",
                    "1.0",
                    $"SHOWROOM-PC{n}",
                    "ravi",
                    $"192.168.1.{20 + n}",
                    At.AddHours(-1),
                    At.AddSeconds(-9),
                    At.AddMinutes(2)))
                .ToList(),
            countersProblem: null,
            At);

    private static RuntimeHealthResponse Runtime(bool reachable, int pending = 0) => new(
        reachable && pending == 0 ? "Healthy" : "Unhealthy",
        ApiAvailable: true,
        DatabaseConfigured: true,
        DatabaseReachable: reachable,
        SettingsLoadedFromApi: true,
        Message: "from the server",
        DatabaseIdentity: reachable ? "PROD" : null,
        ExpectedDatabaseIdentity: "PROD",
        DatabaseIdentityMatches: reachable ? true : null,
        PendingMigrationCount: pending);
}
