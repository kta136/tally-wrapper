using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// What an error nobody caught does to the running app: it stays open, so the
/// bill on screen is not lost, unless it never started or errors keep coming.
/// </summary>
public sealed class UnhandledErrorPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_error_in_the_running_app_does_not_close_it()
    {
        var policy = UnhandledErrorPolicy.Default();

        Assert.True(policy.ShouldKeepRunning(startedUp: true, Start));
    }

    [Fact]
    public void An_error_before_the_app_has_started_closes_it()
    {
        var policy = UnhandledErrorPolicy.Default();

        Assert.False(policy.ShouldKeepRunning(startedUp: false, Start));
    }

    [Fact]
    public void Errors_that_keep_coming_close_it()
    {
        var policy = new UnhandledErrorPolicy(errorsAllowed: 3, within: TimeSpan.FromMinutes(1));

        Assert.True(policy.ShouldKeepRunning(true, Start));
        Assert.True(policy.ShouldKeepRunning(true, Start.AddSeconds(5)));
        Assert.True(policy.ShouldKeepRunning(true, Start.AddSeconds(10)));
        Assert.False(policy.ShouldKeepRunning(true, Start.AddSeconds(15)));
    }

    [Fact]
    public void Errors_spread_over_the_day_never_add_up_to_closing_it()
    {
        var policy = new UnhandledErrorPolicy(errorsAllowed: 3, within: TimeSpan.FromMinutes(1));

        for (var hour = 0; hour < 10; hour++)
        {
            Assert.True(policy.ShouldKeepRunning(true, Start.AddHours(hour)));
        }
    }
}
