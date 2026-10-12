using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests;

/// <summary>
/// How long the stuck-posting recovery waits between sweeps. The sweeps
/// themselves run against a database in <c>PostingRecoveryPostgresTests</c>.
/// </summary>
public sealed class StuckPostingSweepDelayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void With_no_claim_waiting_the_next_sweep_is_a_full_interval_away()
    {
        Assert.Equal(
            StuckPostingRecoveryHostedService.SweepInterval,
            StuckPostingRecoveryHostedService.DelayUntilNextSweep(null, Now));
    }

    [Fact]
    public void A_claim_that_is_old_enough_before_then_is_looked_at_a_second_after_it_is()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(61),
            StuckPostingRecoveryHostedService.DelayUntilNextSweep(Now.AddMinutes(1), Now));
    }

    [Fact]
    public void A_claim_that_is_old_enough_only_later_does_not_put_the_regular_sweep_off()
    {
        Assert.Equal(
            StuckPostingRecoveryHostedService.SweepInterval,
            StuckPostingRecoveryHostedService.DelayUntilNextSweep(Now.AddMinutes(9), Now));
    }

    [Fact]
    public void A_claim_that_became_old_enough_during_the_sweep_does_not_start_a_tight_loop()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            StuckPostingRecoveryHostedService.DelayUntilNextSweep(Now.AddSeconds(-30), Now));
    }
}
