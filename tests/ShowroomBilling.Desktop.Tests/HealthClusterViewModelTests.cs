using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop.Tests;

public sealed class HealthClusterViewModelTests
{
    [Fact]
    public void Apply_MapsHealthyTallyCompanyHealthToOk()
    {
        var vm = new HealthClusterViewModel();

        vm.Apply(Snapshot(new TallyCompanyHealthResponse(
            "healthy",
            TallyReachable: true,
            ActiveCompanyName: "Acme Jewellers",
            ActiveCompanyOpen: true,
            CompanyCount: 1,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Message: "Tally OK - active company 'Acme Jewellers' is open.")));

        Assert.Equal("ok", vm.TallyDot);
        Assert.Contains("Tally OK", vm.TallyTooltip);
    }

    [Fact]
    public void Apply_MapsActiveCompanyMismatchToWarn()
    {
        var vm = new HealthClusterViewModel();

        vm.Apply(Snapshot(new TallyCompanyHealthResponse(
            "warning",
            TallyReachable: true,
            ActiveCompanyName: "Acme Jewellers",
            ActiveCompanyOpen: false,
            CompanyCount: 1,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Message: "Tally answered, but active company 'Acme Jewellers' is not open.")));

        Assert.Equal("warn", vm.TallyDot);
        Assert.Contains("not open", vm.TallyTooltip);
    }

    [Fact]
    public void Apply_MapsUnhealthyTallyCompanyHealthToErr()
    {
        var vm = new HealthClusterViewModel();

        vm.Apply(Snapshot(new TallyCompanyHealthResponse(
            "unhealthy",
            TallyReachable: false,
            ActiveCompanyName: "Acme Jewellers",
            ActiveCompanyOpen: false,
            CompanyCount: 0,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Message: "Tally is unreachable.")));

        Assert.Equal("err", vm.TallyDot);
        Assert.Contains("unreachable", vm.TallyTooltip);
    }

    [Fact]
    public void Apply_MapsApiOfflineToErr()
    {
        var vm = new HealthClusterViewModel();

        vm.Apply(SystemHealthSnapshot.Unreachable());

        Assert.Equal("err", vm.TallyDot);
        Assert.Equal("Tally status unknown (API offline)", vm.TallyTooltip);
        Assert.Equal("err", vm.DatabaseDot);
        Assert.Equal("DB ?", vm.DatabaseLabel);
    }

    [Fact]
    public void Apply_MapsMatchingDatabaseIdentityToOk()
    {
        var vm = new HealthClusterViewModel();

        vm.Apply(Snapshot(new RuntimeHealthResponse(
            "Healthy",
            ApiAvailable: true,
            DatabaseConfigured: true,
            DatabaseReachable: true,
            SettingsLoadedFromApi: true,
            Message: "API is online and PostgreSQL is reachable (DEV).",
            DatabaseIdentity: "DEV",
            ExpectedDatabaseIdentity: "DEV",
            DatabaseIdentityMatches: true)));

        Assert.Equal("ok", vm.DatabaseDot);
        Assert.Equal("DB DEV", vm.DatabaseLabel);
    }

    [Fact]
    public void Apply_MapsDatabaseIdentityMismatchToWarn()
    {
        var vm = new HealthClusterViewModel();

        vm.Apply(Snapshot(new RuntimeHealthResponse(
            "Healthy",
            ApiAvailable: true,
            DatabaseConfigured: true,
            DatabaseReachable: true,
            SettingsLoadedFromApi: true,
            Message: "Database identity mismatch.",
            DatabaseIdentity: "PROD",
            ExpectedDatabaseIdentity: "DEV",
            DatabaseIdentityMatches: false)));

        Assert.Equal("warn", vm.DatabaseDot);
        Assert.Equal("DB PROD", vm.DatabaseLabel);
        Assert.Equal("Database identity mismatch.", vm.DatabaseTooltip);
    }

    [Fact]
    public void Apply_MapsADatabaseThatDoesNotAnswerToErr()
    {
        var vm = new HealthClusterViewModel();

        vm.Apply(Snapshot(new RuntimeHealthResponse(
            "Unhealthy",
            ApiAvailable: true,
            DatabaseConfigured: true,
            DatabaseReachable: false,
            SettingsLoadedFromApi: true,
            Message: "API is online. PostgreSQL must be configured and reachable for readiness.",
            ExpectedDatabaseIdentity: "PROD")));

        Assert.Equal("err", vm.DatabaseDot);
        Assert.Equal("DB ?", vm.DatabaseLabel);
        Assert.Contains("reachable", vm.DatabaseTooltip);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // it does not answer now; the count is the last one found
    public void Apply_MapsADatabaseThatIsBehindToErr_WhetherOrNotItAnswers(bool reachable)
    {
        var vm = new HealthClusterViewModel();

        vm.Apply(Snapshot(new RuntimeHealthResponse(
            "Unhealthy",
            ApiAvailable: true,
            DatabaseConfigured: true,
            DatabaseReachable: reachable,
            SettingsLoadedFromApi: true,
            Message: "whatever the server says",
            DatabaseIdentity: reachable ? "PROD" : null,
            ExpectedDatabaseIdentity: "PROD",
            DatabaseIdentityMatches: reachable ? true : null,
            PendingMigrationCount: 6)));

        Assert.Equal("err", vm.DatabaseDot);
        Assert.Equal("DB BEHIND", vm.DatabaseLabel);
        Assert.Contains("6 schema migration(s)", vm.DatabaseTooltip);
    }

    [Fact]
    public void Apply_TreatsZeroPendingMigrationsAsNothingToReport()
    {
        var vm = new HealthClusterViewModel();

        vm.Apply(Snapshot(new RuntimeHealthResponse(
            "Healthy",
            ApiAvailable: true,
            DatabaseConfigured: true,
            DatabaseReachable: true,
            SettingsLoadedFromApi: true,
            Message: "API is online and PostgreSQL is reachable (PROD).",
            DatabaseIdentity: "PROD",
            ExpectedDatabaseIdentity: "PROD",
            DatabaseIdentityMatches: true,
            PendingMigrationCount: 0)));

        Assert.Equal("ok", vm.DatabaseDot);
        Assert.Equal("DB PROD", vm.DatabaseLabel);
    }

    private static SystemHealthSnapshot Snapshot(TallyCompanyHealthResponse tally) =>
        new(ApiReachable: true, Masters: null, TallyCompany: tally);

    private static SystemHealthSnapshot Snapshot(RuntimeHealthResponse runtime) =>
        new(ApiReachable: true, Masters: null, TallyCompany: null, Runtime: runtime);
}
