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
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null);
        vm.MaxBillAmount = 250_000; // above ₹1,99,000

        Assert.NotNull(vm.ValidationMessage);
        Assert.Contains("1,99,000", vm.ValidationMessage!);
        Assert.True(vm.HasValidation);
    }

    [Fact]
    public void ValidationMessage_FlagsReversedTimeWindow()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 6000m };
        var now = DateTimeOffset.Now;
        vm.StartAt = now.AddHours(5);
        vm.EndAt = now;

        Assert.NotNull(vm.ValidationMessage);
        Assert.Contains("before", vm.ValidationMessage!);
    }

    [Fact]
    public void A_new_scheduler_has_no_built_in_rate_and_says_so()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null);

        Assert.Equal(0m, vm.Rate24Kt);
        Assert.Equal("24kt rate must be greater than zero.", vm.ValidationMessage);
    }

    [Fact]
    public void KaratOptions_follow_the_saved_karats_and_not_what_is_being_typed_in_Settings()
    {
        var settings = OfflineApi.Settings();
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings);
        Assert.Empty(vm.KaratOptions);

        settings.Publish(karats: [TestMasters.Karat("22K", 91.6m, "Hallmarked 22KT")]);
        Assert.Equal(["22K"], vm.KaratOptions.Select(option => option.Label));

        // A row typed into Settings and not saved is not a karat yet.
        settings.Draft.KaratRows.Add(new ShowroomBilling.Desktop.ViewModels.Settings.KaratMasterRowVm
        {
            Label = "18K",
            PurityPercent = "75",
            TallyItem = "Hallmarked 18KT",
        });
        Assert.Equal(["22K"], vm.KaratOptions.Select(option => option.Label));

        settings.Publish(karats: [TestMasters.Karat("22KT", 91.6m, "Hallmarked 22KT")]);
        Assert.Equal(["22KT"], vm.KaratOptions.Select(option => option.Label));
    }

    [Fact]
    public async Task StartCommand_OpensAdminUnlock_WhenTokenMissing()
    {
        var tokenStore = new AdminTokenStore();
        var api = new FakeBillsApi();
        var promptCalled = false;
        var dialogs = new FakeDialogs
        {
            AdminUnlock = _ =>
            {
                promptCalled = true;
                return Task.CompletedTask;
            }
        };
        var vm = new SyntheticBatchViewModel(api, dialogs, tokenStore, settings: null) { Rate24Kt = 6000m };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        var start = new DateTimeOffset(DateTime.Today.AddHours(9));
        var end = start.AddHours(3);
        vm.StartAt = start;
        vm.EndAt = end;

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
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), tokenStore, settings: null) { Rate24Kt = 6000m };
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
