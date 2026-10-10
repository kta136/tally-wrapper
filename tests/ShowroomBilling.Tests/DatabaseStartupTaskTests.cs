using ShowroomBilling.Application.Health;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests;

/// <summary>
/// The shared scaffold for start-up work: each outcome must reach the right
/// hook, and nothing may escape into the host.
/// </summary>
public sealed class DatabaseStartupTaskTests
{
    [Fact]
    public async Task Work_runs_once_the_database_is_ready()
    {
        var status = new StartupStatus();
        var task = new ProbeTask(status, (_, _) => Task.CompletedTask);

        await task.StartAsync(CancellationToken.None);
        Assert.False(task.Ran.Task.IsCompleted);

        status.RecordDatabaseReady();
        await task.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await task.StopAsync(CancellationToken.None);

        Assert.Equal("ran", task.Outcome);
    }

    [Fact]
    public async Task A_database_that_never_became_ready_is_reported_as_not_ready()
    {
        var status = new StartupStatus();
        var task = new ProbeTask(status, (_, _) => Task.CompletedTask);

        await task.StartAsync(CancellationToken.None);
        status.RecordDatabaseFailure("migration timed out");
        await task.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("not-ready: migration timed out", task.Outcome);
    }

    [Fact]
    public async Task An_invalid_operation_in_the_work_itself_is_a_failure_not_a_database_that_is_not_ready()
    {
        // The old services caught every InvalidOperationException as "database
        // not ready", which hid real bugs (EF and LINQ throw this type too).
        var status = new StartupStatus();
        status.RecordDatabaseReady();
        var task = new ProbeTask(status, (_, _) => throw new InvalidOperationException("Sequence contains no elements"));

        await task.StartAsync(CancellationToken.None);
        await task.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("failed: Sequence contains no elements", task.Outcome);
    }

    [Fact]
    public async Task Work_that_outlives_its_budget_is_reported_as_timed_out()
    {
        var status = new StartupStatus();
        status.RecordDatabaseReady();
        var task = new ProbeTask(
            status,
            (timeboxed, _) => Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, timeboxed),
            timeout: TimeSpan.FromMilliseconds(50));

        await task.StartAsync(CancellationToken.None);
        await task.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("timed-out", task.Outcome);
    }

    [Fact]
    public async Task Stopping_the_host_mid_work_reports_nothing_and_does_not_throw()
    {
        var status = new StartupStatus();
        status.RecordDatabaseReady();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new ProbeTask(status, async (timeboxed, _) =>
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, timeboxed);
        });

        await task.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await task.StopAsync(CancellationToken.None);

        Assert.Null(task.Outcome);
    }

    private sealed class ProbeTask(
        IStartupStatus startupStatus,
        Func<CancellationToken, CancellationToken, Task> work,
        TimeSpan? timeout = null) : DatabaseStartupTask(startupStatus)
    {
        public string? Outcome { get; private set; }

        public TaskCompletionSource Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(30);

        protected override async Task RunAsync(CancellationToken timeboxed, CancellationToken hostStopping)
        {
            await work(timeboxed, hostStopping);
            Outcome = "ran";
            Ran.TrySetResult();
        }

        protected override void OnTimedOut() => Settle("timed-out");

        protected override void OnDatabaseNotReady(string reason) => Settle($"not-ready: {reason}");

        protected override void OnFailed(Exception exception) => Settle($"failed: {exception.Message}");

        private void Settle(string outcome)
        {
            Outcome = outcome;
            Settled.TrySetResult();
        }
    }
}
