using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>Settings → Server: the one connection setting a workstation holds.</summary>
public sealed class ServerSettingsViewModelTests
{
    [Fact]
    public void It_opens_on_the_address_in_use()
    {
        var vm = Build(out _, out _, current: "http://192.168.1.50:5107");

        Assert.Equal("http://192.168.1.50:5107", vm.ServerAddress);
        Assert.Equal("http://192.168.1.50:5107", vm.CurrentServerAddress);
        Assert.False(vm.HasStatus);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.50:5107")]
    [InlineData("ftp://192.168.1.50")]
    [InlineData("not an address")]
    public void An_address_that_is_not_an_http_url_cannot_be_saved_or_tested(string address)
    {
        var vm = Build(out _, out _);

        vm.ServerAddress = address;

        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.TestCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_stores_the_address_without_path_or_trailing_slash_and_restarts()
    {
        var vm = Build(out var saved, out var restarts);
        vm.ServerAddress = "  http://192.168.1.60:5107/api/health/  ";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["http://192.168.1.60:5107"], saved);
        Assert.Equal(1, restarts());
    }

    [Fact]
    public async Task With_unsaved_settings_edits_save_asks_first_and_does_nothing_when_declined()
    {
        var asked = new List<ConfirmRequest>();
        var vm = Build(out var saved, out var restarts, unsavedEdits: true);
        vm.ConfirmHandler = request =>
        {
            asked.Add(request);
            return Task.FromResult(false);
        };
        vm.ServerAddress = "http://192.168.1.60:5107";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(asked).IsWarning);
        Assert.Empty(saved);
        Assert.Equal(0, restarts());
        Assert.StartsWith("Not saved", vm.Status);
    }

    [Fact]
    public async Task With_unsaved_settings_edits_and_no_way_to_ask_nothing_is_saved()
    {
        var vm = Build(out var saved, out var restarts, unsavedEdits: true);
        vm.ServerAddress = "http://192.168.1.60:5107";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(saved);
        Assert.Equal(0, restarts());
    }

    [Fact]
    public async Task Test_reports_a_server_that_cannot_be_reached()
    {
        var vm = Build(out _, out _);
        // Port 9 (discard) on loopback: nothing listens there.
        vm.ServerAddress = "http://127.0.0.1:9";

        await vm.TestCommand.ExecuteAsync(null);

        Assert.StartsWith("Cannot reach a server at http://127.0.0.1:9", vm.Status);
        Assert.False(vm.IsTesting);
        Assert.True(vm.TestCommand.CanExecute(null));
    }

    private static ServerSettingsViewModel Build(
        out List<string> saved,
        out Func<int> restarts,
        string current = "http://localhost:5107",
        bool unsavedEdits = false)
    {
        var savedAddresses = new List<string>();
        var restartCount = 0;
        saved = savedAddresses;
        restarts = () => restartCount;
        return new ServerSettingsViewModel(
            new DesktopBootstrapOptions { ApiBaseUrl = current },
            restartApplication: () => restartCount++,
            hasUnsavedSettingsEdits: () => unsavedEdits,
            saveServerAddress: (address, _) =>
            {
                savedAddresses.Add(address);
                return Task.CompletedTask;
            });
    }
}
