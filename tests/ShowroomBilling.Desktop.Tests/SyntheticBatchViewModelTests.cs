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
    public void ValidationMessage_FlagsAMaxBillAmountUnderTheSmallestBill()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 6000m };
        vm.MaxBillAmount = 49_999;

        Assert.Equal("Max bill amount must be between ₹50,000 and ₹1,99,000.", vm.ValidationMessage);
    }

    [Fact]
    public void ValidationMessage_FlagsATotalUnderTheSmallestBill()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 6000m };
        vm.TotalAmount = 49_999;

        Assert.Equal("Total target amount must be at least ₹50,000, the smallest bill a batch makes.", vm.ValidationMessage);
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
        var start = DateTimeOffset.Now.AddHours(-4);
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
        var start = DateTimeOffset.Now.AddHours(-4);
        vm.StartAt = start;
        vm.EndAt = start.AddHours(3);
        Assert.Null(vm.ValidationMessage);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(api.LastRequest);
        Assert.Equal("admin-token", api.LastAdminToken);
        Assert.Equal(1, api.CallCount);
    }

    [Fact]
    public async Task The_window_runs_from_the_last_bill_to_now()
    {
        var lastBill = LastBillAt(DateTimeOffset.UtcNow.AddHours(-2));
        var api = new FakeBillsApi { Response = OneBill(lastBill) };
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), adminTokens: null, settings: null);

        var before = DateTimeOffset.Now;
        await vm.PrepareWindowAsync();

        // The newest bill made, whatever its state or number.
        Assert.Equal((0, 1, BillSearchSorts.CreatedDesc), (api.LastFilter!.Skip, api.LastFilter.Take, api.LastFilter.Sort));
        Assert.Equal(lastBill, vm.StartAt);
        Assert.Equal(lastBill, vm.LastBillAt);
        // Now, to the minute the box shows.
        Assert.Equal(0, vm.EndAt.Second);
        Assert.InRange(vm.EndAt, before.AddMinutes(-1), DateTimeOffset.Now);
        Assert.Equal($"Bills can be dated from the last bill, {lastBill.ToLocalTime():dd MMM, HH:mm}, up to now.", vm.WindowNote);
    }

    [Fact]
    public async Task With_no_bill_yet_the_window_starts_at_the_start_of_today()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null);

        await vm.PrepareWindowAsync();

        Assert.Null(vm.LastBillAt);
        Assert.Equal(DateTime.Today, vm.StartAt.LocalDateTime);
        Assert.Equal("Bills can be dated up to now.", vm.WindowNote);
    }

    [Fact]
    public async Task A_window_outside_the_last_bill_and_now_is_refused()
    {
        var lastBill = LastBillAt(DateTimeOffset.UtcNow.AddHours(-2));
        var vm = new SyntheticBatchViewModel(new FakeBillsApi { Response = OneBill(lastBill) }, new FakeDialogs(), adminTokens: null, settings: null)
        {
            Rate24Kt = 6000m,
        };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        await vm.PrepareWindowAsync();
        var end = vm.EndAt;

        vm.EndAt = DateTimeOffset.Now.AddMinutes(30);
        Assert.Equal("End time cannot be later than now.", vm.ValidationMessage);

        vm.EndAt = end;
        vm.StartAt = lastBill.AddMinutes(-1);
        Assert.Equal($"Start time cannot be before the last bill, {lastBill.ToLocalTime():dd MMM, HH:mm}.", vm.ValidationMessage);

        // The boxes hold whole minutes, so the last bill's own minute is allowed; no bill goes in it.
        vm.StartAt = new DateTimeOffset(lastBill.Year, lastBill.Month, lastBill.Day, lastBill.Hour, lastBill.Minute, 0, lastBill.Offset);
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public async Task A_last_bill_dated_later_than_now_leaves_no_window()
    {
        var lastBill = LastBillAt(DateTimeOffset.UtcNow.AddHours(3));
        var vm = new SyntheticBatchViewModel(new FakeBillsApi { Response = OneBill(lastBill) }, new FakeDialogs(), adminTokens: null, settings: null)
        {
            Rate24Kt = 6000m,
        };

        await vm.PrepareWindowAsync();

        Assert.Equal(
            $"The last bill is dated {lastBill.ToLocalTime():dd MMM, HH:mm}, later than now, so there is no time left to date a batch in.",
            vm.ValidationMessage);
    }

    [Fact]
    public async Task When_the_last_bill_cannot_be_read_the_form_says_so_and_the_server_checks()
    {
        var vm = new SyntheticBatchViewModel(
            new FakeBillsApi { SearchFailure = new System.Net.Http.HttpRequestException("No connection") }, new FakeDialogs(), adminTokens: null, settings: null);

        await vm.PrepareWindowAsync();

        Assert.Null(vm.LastBillAt);
        Assert.Equal(DateTime.Today, vm.StartAt.LocalDateTime);
        Assert.StartsWith("Could not read when the last bill was made", vm.StatusMessage);
    }

    [Fact]
    public async Task The_answer_to_an_earlier_opening_is_dropped()
    {
        var older = LastBillAt(DateTimeOffset.UtcNow.AddHours(-3));
        var newer = LastBillAt(DateTimeOffset.UtcNow.AddHours(-1));
        var slow = new TaskCompletionSource<BillListResponse>();
        var api = new FakeBillsApi { Response = OneBill(newer) };
        api.PendingSearches.Enqueue(slow);
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), adminTokens: null, settings: null);

        var first = vm.PrepareWindowAsync();
        await vm.PrepareWindowAsync();
        slow.SetResult(OneBill(older));
        await first;

        Assert.Equal(newer, vm.LastBillAt);
        Assert.Equal(newer, vm.StartAt);
    }

    [Fact]
    public async Task A_time_typed_while_the_last_bill_is_read_is_kept()
    {
        var lastBill = LastBillAt(DateTimeOffset.UtcNow.AddHours(-3));
        var slow = new TaskCompletionSource<BillListResponse>();
        var api = new FakeBillsApi();
        api.PendingSearches.Enqueue(slow);
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), adminTokens: null, settings: null);

        var opening = vm.PrepareWindowAsync();
        var typed = lastBill.AddHours(1);
        vm.StartAt = typed;
        slow.SetResult(OneBill(lastBill));
        await opening;

        Assert.Equal(typed, vm.StartAt);
        Assert.Equal(lastBill, vm.LastBillAt);
    }

    [Fact]
    public async Task After_a_batch_a_failed_read_keeps_the_times_and_says_so_with_the_result()
    {
        var lastBill = LastBillAt(DateTimeOffset.UtcNow.AddHours(-3));
        var tokenStore = new AdminTokenStore();
        tokenStore.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        var api = new FakeBillsApi { Response = OneBill(lastBill) };
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), tokenStore, settings: null) { Rate24Kt = 6000m };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        await vm.PrepareWindowAsync();

        api.SearchFailure = new System.Net.Http.HttpRequestException("No connection");
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(lastBill, vm.StartAt);
        Assert.Equal(lastBill, vm.LastBillAt);
        Assert.StartsWith("Created 3 bill(s)", vm.StatusMessage);
        Assert.Contains("Could not read when the last bill was made", vm.StatusMessage);
    }

    /// <summary>A time to the second in local time, as the dialog holds it.</summary>
    private static DateTimeOffset LastBillAt(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        return new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, local.Minute, 37, local.Offset);
    }

    private static BillListResponse OneBill(DateTimeOffset createdAt) => new(1, 0, 1,
    [
        new BillSummaryItem(Guid.NewGuid(), BillStates.Pending, "SR/1", "Party A", DateOnly.FromDateTime(createdAt.Date), 60_000m, createdAt, createdAt),
    ]);
}
