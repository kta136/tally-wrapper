using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.ServerTray;

namespace ShowroomBilling.Desktop.Tests.ServerTray;

/// <summary>
/// The database line of the server dashboard: what the person who has just
/// installed a new server build looks at.
/// </summary>
public sealed class StatusFormDatabaseTests
{
    [Fact]
    public void A_database_that_has_not_had_its_migrations_is_named_before_idle_or_ready()
    {
        // The dashboard uses the cheap probe, which never asks the database:
        // it would say "idle" while no bill can be read.
        var (cheap, _) = StatusForm.DescribeDatabase(Health(skipped: true, reachable: false, pending: 6));
        var (full, _) = StatusForm.DescribeDatabase(Health(skipped: false, reachable: true, pending: 6));

        Assert.Equal("upgrade needed: 6 migration(s) not applied", cheap);
        Assert.Equal(cheap, full);
    }

    [Theory]
    [InlineData(true, false, null, "idle")]
    [InlineData(true, false, 0, "idle")]
    [InlineData(false, true, 0, "ready")]
    [InlineData(false, true, null, "ready")]
    [InlineData(false, false, null, "not ready")]
    public void Otherwise_it_reads_as_before(bool skipped, bool reachable, int? pending, string expected)
    {
        var (text, _) = StatusForm.DescribeDatabase(Health(skipped, reachable, pending));

        Assert.Equal(expected, text);
    }

    [Fact]
    public void No_answer_is_unknown()
    {
        Assert.Equal("unknown", StatusForm.DescribeDatabase(null).Text);
    }

    private static RuntimeHealthResponse Health(bool skipped, bool reachable, int? pending) => new(
        skipped ? "Skipped" : "Healthy",
        ApiAvailable: true,
        DatabaseConfigured: true,
        DatabaseReachable: reachable,
        SettingsLoadedFromApi: !skipped,
        Message: "from the server",
        DatabaseHealthSkipped: skipped,
        PendingMigrationCount: pending);
}
