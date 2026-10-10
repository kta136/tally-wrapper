using System.IO;
using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// The tests must never read or write the settings of whoever runs them.
/// Every store resolves its folder through <see cref="AppPaths"/>, and
/// <see cref="TestAppData"/> points that at a scratch folder.
/// </summary>
public sealed class TestIsolationTests
{
    [Fact]
    public void Every_store_keeps_its_files_under_the_scratch_folder()
    {
        string[] paths =
        [
            AppPaths.DataDirectory,
            AppPaths.LogsDirectory,
            AppPaths.LocalDirectory,
            DesktopLocalDatabaseOverrideStore.ConfigPath,
            DesktopBootstrapLocalOverrideStore.ConfigPath,
            SetupWizardCompletionStore.FilePath,
            SettingsFolderLauncher.AppDataFolderPath,
            SettingsFolderLauncher.LogFolderPath,
            DeviceTokenConstants.ResolveTokenFilePath(),
        ];

        Assert.All(paths, path => Assert.StartsWith(TestAppData.Root, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_settings_screen_built_in_a_test_holds_no_saved_database_connection()
    {
        var settings = new SettingsViewModel();

        Assert.Equal(string.Empty, settings.Database.DatabaseConnectionString);
        Assert.False(File.Exists(DesktopLocalDatabaseOverrideStore.ConfigPath));
    }
}
