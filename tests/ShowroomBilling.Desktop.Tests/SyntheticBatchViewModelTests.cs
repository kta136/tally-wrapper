using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

namespace ShowroomBilling.Desktop.Tests;

public sealed class SyntheticBatchViewModelTests
{
    [Fact]
    public void ValidationMessage_FlagsInvalidMaxBillAmount()
    {
        var vm = new SyntheticBatchViewModel();
        vm.MaxBillAmount = 250_000; // above ₹1,99,000

        Assert.NotNull(vm.ValidationMessage);
        Assert.Contains("1,99,000", vm.ValidationMessage!);
        Assert.True(vm.HasValidation);
    }

    [Fact]
    public void ValidationMessage_FlagsReversedTimeWindow()
    {
        var vm = new SyntheticBatchViewModel { Rate24Kt = 6000m };
        var now = DateTimeOffset.Now;
        vm.StartAt = now.AddHours(5);
        vm.EndAt = now;

        Assert.NotNull(vm.ValidationMessage);
        Assert.Contains("before", vm.ValidationMessage!);
    }

    [Fact]
    public void A_new_scheduler_has_no_built_in_rate_and_says_so()
    {
        var vm = new SyntheticBatchViewModel();

        Assert.Equal(0m, vm.Rate24Kt);
        Assert.Equal("24kt rate must be greater than zero.", vm.ValidationMessage);
    }

    [Fact]
    public void KaratOptions_FollowKaratRowsAddedAndEditedInSettings()
    {
        var settings = new ShowroomBilling.Desktop.ViewModels.Settings.SettingsViewModel();
        var vm = new SyntheticBatchViewModel(billsApi: null, adminTokens: null, settings);
        Assert.Empty(vm.KaratOptions);

        var row = new ShowroomBilling.Desktop.ViewModels.Settings.KaratMasterRowVm
        {
            Label = "22K",
            PurityPercent = "91.6",
            TallyItem = "Hallmarked 22KT",
        };
        settings.Draft.KaratRows.Add(row);
        Assert.Equal(["22K"], vm.KaratOptions.Select(option => option.Label));

        // An edit to an existing row was announced under the item-master
        // collection's name, so the list here kept the old label.
        row.Label = "22KT";
        Assert.Equal(["22KT"], vm.KaratOptions.Select(option => option.Label));
    }

    [Fact]
    public async Task StartCommand_OpensAdminUnlock_WhenTokenMissing()
    {
        var tokenStore = new AdminTokenStore();
        var api = new FakeBillsApi();
        var vm = new SyntheticBatchViewModel(api, tokenStore, settings: null) { Rate24Kt = 6000m };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        var start = new DateTimeOffset(DateTime.Today.AddHours(9));
        var end = start.AddHours(3);
        vm.StartAt = start;
        vm.EndAt = end;

        var promptCalled = false;
        vm.AdminUnlockHandler = _ =>
        {
            promptCalled = true;
            return Task.CompletedTask;
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(promptCalled, "handler should be invoked when token is absent");
        Assert.Contains("cancelled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartCommand_SubmitsToApi_WhenTokenIsPresent()
    {
        var tokenStore = new AdminTokenStore();
        tokenStore.Set(new AdminUnlockResponse(
            Token: "admin-token",
            ActorLabel: "unit-test",
            IssuedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(30)));
        var api = new FakeBillsApi();
        var vm = new SyntheticBatchViewModel(api, tokenStore, settings: null) { Rate24Kt = 6000m };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        var start = new DateTimeOffset(DateTime.Today.AddHours(9));
        vm.StartAt = start;
        vm.EndAt = start.AddHours(3);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(api.LastRequest);
        Assert.Equal("admin-token", api.LastAdminToken);
        Assert.Equal(1, api.CallCount);
    }
}
