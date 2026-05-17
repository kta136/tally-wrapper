using System;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Settings;

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

    [Fact]
    public void Constructor_SeedsFiscalYearAndWorkstationFromEnvironment()
    {
        var vm = new StatusBarViewModel();

        Assert.StartsWith("FY ", vm.FiscalYear);
        Assert.Equal($"WS: {Environment.MachineName}", vm.Workstation);
    }

    [Fact]
    public void ApplyRate24Kt_WithNullOrZeroRate_BlanksAllDisplays()
    {
        var vm = new StatusBarViewModel();
        vm.ApplyRate24Kt(7780m, null);

        vm.ApplyRate24Kt(null, null);
        Assert.Equal("—", vm.Rate24Kt);
        Assert.Equal("—", vm.Rate22Kt);
        Assert.Equal("—", vm.Rate18Kt);

        vm.ApplyRate24Kt(0m, null);
        Assert.Equal("—", vm.Rate24Kt);
        Assert.Equal("—", vm.Rate22Kt);
        Assert.Equal("—", vm.Rate18Kt);
    }

    [Fact]
    public void ApplyRate24Kt_WithNoKaratMasters_FallsBackToStandardPurities()
    {
        var vm = new StatusBarViewModel();

        vm.ApplyRate24Kt(7780m, null);

        Assert.Equal("7,780", vm.Rate24Kt);
        // 7780 * 91.6 / 100 = 7126.48 → 7,126
        Assert.Equal("7,126", vm.Rate22Kt);
        // 7780 * 75 / 100 = 5835 → 5,835
        Assert.Equal("5,835", vm.Rate18Kt);
    }

    [Fact]
    public void ApplyRate24Kt_UsesKaratMasterPurityWhenLabelMatches()
    {
        var vm = new StatusBarViewModel();
        var masters = new[]
        {
            new KaratMasterRowVm { Label = "22KT", PurityPercent = "91.7", TallyItem = "x" },
            new KaratMasterRowVm { Label = "18kt", PurityPercent = "75.0", TallyItem = "y" },
            new KaratMasterRowVm { Label = "14KT", PurityPercent = "58.5", TallyItem = "z" },
        };

        vm.ApplyRate24Kt(8000m, masters);

        Assert.Equal("8,000", vm.Rate24Kt);
        // 8000 * 91.7 / 100 = 7336 → 7,336
        Assert.Equal("7,336", vm.Rate22Kt);
        // 8000 * 75 / 100 = 6000 → 6,000
        Assert.Equal("6,000", vm.Rate18Kt);
    }

    [Fact]
    public void ApplyRate24Kt_IgnoresUnparseableOrOutOfRangePurities()
    {
        var vm = new StatusBarViewModel();
        var masters = new[]
        {
            new KaratMasterRowVm { Label = "22KT", PurityPercent = "abc", TallyItem = "x" },
            new KaratMasterRowVm { Label = "18KT", PurityPercent = "150", TallyItem = "y" },
        };

        vm.ApplyRate24Kt(10000m, masters);

        // Both rows rejected → fallback purities used.
        Assert.Equal("10,000", vm.Rate24Kt);
        Assert.Equal("9,160", vm.Rate22Kt);
        Assert.Equal("7,500", vm.Rate18Kt);
    }

    [Fact]
    public void ApplyLineCount_ClampsNegative()
    {
        var vm = new StatusBarViewModel();

        vm.ApplyLineCount(7);
        Assert.Equal(7, vm.LineCount);

        vm.ApplyLineCount(-3);
        Assert.Equal(0, vm.LineCount);
    }

    [Fact]
    public void ApplyLastSaved_NullClearsToDash_ValueFormatsAsLocalTime()
    {
        var vm = new StatusBarViewModel();

        vm.ApplyLastSaved(null);
        Assert.Equal("—", vm.LastSaved);

        var stamp = new DateTimeOffset(2026, 5, 17, 14, 23, 45, TimeSpan.Zero);
        vm.ApplyLastSaved(stamp);
        var expected = stamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, vm.LastSaved);
    }
}
