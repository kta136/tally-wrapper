using System;
using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop.Tests;

public sealed class StatusBarViewModelTests
{
    [Theory]
    [InlineData("DEV", "DB DEV")]
    [InlineData("PROD", "DB PROD")]
    [InlineData("UNSET", "DB UNSET")]
    [InlineData(null, "DB ?")]
    public void ApplyDatabaseIdentity_ShowsTheDatabaseOwnedMarker(
        string? databaseIdentity,
        string expected)
    {
        var vm = new StatusBarViewModel();

        vm.ApplyDatabaseIdentity(databaseIdentity);

        Assert.Equal(expected, vm.DatabaseEnvironment);
    }

    [Fact]
    public void Constructor_SeedsFiscalYearWorkstationAndTime()
    {
        var vm = new StatusBarViewModel();

        Assert.StartsWith("FY ", vm.FiscalYear);
        Assert.Equal($"Workstation {Environment.MachineName}", vm.Workstation);
        Assert.Matches(@"^\d{2}:\d{2}$", vm.Time);
    }
}
