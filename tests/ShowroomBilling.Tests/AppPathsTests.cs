using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Application.Logging;
using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Tests.TestSupport;

namespace ShowroomBilling.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void The_tests_run_against_a_scratch_folder_not_the_developers_settings()
    {
        string[] paths =
        [
            AppPaths.DataDirectory,
            AppPaths.LocalDirectory,
            DatabaseConfigurationStore.DirectoryPath,
            RollingFileLoggerOptions.DefaultDirectory(),
            DeviceTokenConstants.ResolveTokenFilePath(),
        ];

        Assert.All(paths, path => Assert.StartsWith(TestAppData.Root, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Logs_live_under_the_data_folder_and_the_device_token_under_the_local_one()
    {
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "logs"), AppPaths.LogsDirectory);
        Assert.Equal(Path.Combine(AppPaths.LocalDirectory, "device_token.txt"), DeviceTokenConstants.ResolveTokenFilePath());
    }

    [Fact]
    public void The_device_token_is_created_once_and_then_read_back()
    {
        var path = Path.Combine(TestAppData.Root, "token-test", Guid.NewGuid().ToString("N"), "device_token.txt");

        var created = DeviceTokenFile.ReadOrCreate(path);
        var readBack = DeviceTokenFile.ReadOrCreate(path);

        Assert.Equal(32, Convert.FromBase64String(created).Length);
        Assert.Equal(created, readBack);
    }
}
