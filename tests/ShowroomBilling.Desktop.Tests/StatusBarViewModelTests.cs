using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop.Tests;

public sealed class StatusBarViewModelTests
{
    [Theory]
    [InlineData("DEV", "Production", "DB DEV")]
    [InlineData("PROD", "Development", "DB PROD")]
    [InlineData("UNSET", "Development", "DB UNSET")]
    [InlineData(null, "Production", "DB ?")]
    [InlineData(null, null, "DB ?")]
    public void ApplyDatabaseIdentity_PrefersDatabaseOwnedMarker_WhenSet(
        string? databaseIdentity,
        string? environmentName,
        string expected)
    {
        var vm = new StatusBarViewModel();

        vm.ApplyDatabaseIdentity(databaseIdentity, environmentName);

        Assert.Equal(expected, vm.DatabaseEnvironment);
    }
}
