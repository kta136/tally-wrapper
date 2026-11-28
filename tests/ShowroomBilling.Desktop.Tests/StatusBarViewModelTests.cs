using System;
using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop.Tests;

public sealed class StatusBarViewModelTests
{
    [Fact]
    public void Constructor_SeedsFiscalYearWorkstationAndTime()
    {
        var vm = new StatusBarViewModel();

        Assert.StartsWith("FY ", vm.FiscalYear);
        Assert.Equal($"Workstation {Environment.MachineName}", vm.Workstation);
        Assert.Matches(@"^\d{2}:\d{2}$", vm.Time);
    }
}
