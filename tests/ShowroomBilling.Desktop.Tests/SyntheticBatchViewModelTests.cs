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
    public async Task The_window_runs_from_the_first_minute_after_the_last_bill_to_now()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-2));
        var api = new FakeBillsApi { Window = new(earliest) };
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), adminTokens: null, settings: null);

        var before = DateTimeOffset.Now;
        await vm.PrepareWindowAsync();

        // The server works the time out: a bill dated an earlier day than it was saved counts as the end of that day.
        Assert.Equal(earliest, vm.StartAt);
        Assert.Equal(earliest, vm.EarliestAt);
        // Now, to the minute the box shows.
        Assert.Equal(0, vm.EndAt.Second);
        Assert.InRange(vm.EndAt, before.AddMinutes(-1), DateTimeOffset.Now);
        Assert.Equal($"Bills can be dated from {earliest.ToLocalTime():dd MMM, HH:mm}, after the last bill, up to now.", vm.WindowNote);
    }

    [Fact]
    public async Task With_no_bill_yet_the_window_starts_at_the_start_of_today()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null);

        await vm.PrepareWindowAsync();

        Assert.Null(vm.EarliestAt);
        Assert.Equal(DateTime.Today, vm.StartAt.LocalDateTime);
        Assert.Equal("Bills can be dated up to now.", vm.WindowNote);
    }

    [Fact]
    public async Task A_window_outside_the_earliest_time_and_now_is_refused()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-2));
        var vm = new SyntheticBatchViewModel(new FakeBillsApi { Window = new(earliest) }, new FakeDialogs(), adminTokens: null, settings: null)
        {
            Rate24Kt = 6000m,
        };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        await vm.PrepareWindowAsync();
        var end = vm.EndAt;

        vm.EndAt = DateTimeOffset.Now.AddMinutes(30);
        Assert.Equal("End time cannot be later than now.", vm.ValidationMessage);

        vm.EndAt = end;
        vm.StartAt = earliest.AddMinutes(-1);
        Assert.Equal($"Start time cannot be before {earliest.ToLocalTime():dd MMM, HH:mm}, the first time after the last bill.", vm.ValidationMessage);

        vm.StartAt = earliest;
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public async Task An_earliest_time_later_than_now_leaves_no_window()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(3));
        var vm = new SyntheticBatchViewModel(new FakeBillsApi { Window = new(earliest) }, new FakeDialogs(), adminTokens: null, settings: null)
        {
            Rate24Kt = 6000m,
        };

        await vm.PrepareWindowAsync();

        Assert.Equal(
            $"The last bill leaves no time to date a batch in: a batch can be dated from {earliest.ToLocalTime():dd MMM, HH:mm}, which is later than now.",
            vm.ValidationMessage);
    }

    [Fact]
    public async Task When_the_window_cannot_be_read_the_form_says_so_and_the_server_checks()
    {
        var vm = new SyntheticBatchViewModel(
            new FakeBillsApi { WindowFailure = new System.Net.Http.HttpRequestException("No connection") }, new FakeDialogs(), adminTokens: null, settings: null);

        await vm.PrepareWindowAsync();

        Assert.Null(vm.EarliestAt);
        Assert.Equal(DateTime.Today, vm.StartAt.LocalDateTime);
        Assert.StartsWith("Could not read when the last bill was made", vm.StatusMessage);
    }

    [Fact]
    public async Task The_answer_to_an_earlier_opening_is_dropped()
    {
        var older = Minute(DateTimeOffset.UtcNow.AddHours(-3));
        var newer = Minute(DateTimeOffset.UtcNow.AddHours(-1));
        var slow = new TaskCompletionSource<SyntheticBatchWindowResponse>();
        var api = new FakeBillsApi { Window = new(newer) };
        api.PendingWindows.Enqueue(slow);
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), adminTokens: null, settings: null);

        var first = vm.PrepareWindowAsync();
        await vm.PrepareWindowAsync();
        slow.SetResult(new(older));
        await first;

        Assert.Equal(newer, vm.EarliestAt);
        Assert.Equal(newer, vm.StartAt);
    }

    [Fact]
    public async Task A_time_typed_while_the_window_is_read_is_kept()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-3));
        var slow = new TaskCompletionSource<SyntheticBatchWindowResponse>();
        var api = new FakeBillsApi();
        api.PendingWindows.Enqueue(slow);
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), adminTokens: null, settings: null);

        var opening = vm.PrepareWindowAsync();
        var typed = earliest.AddHours(1);
        vm.StartAt = typed;
        slow.SetResult(new(earliest));
        await opening;

        Assert.Equal(typed, vm.StartAt);
        Assert.Equal(earliest, vm.EarliestAt);
    }

    [Fact]
    public async Task After_a_batch_a_failed_read_keeps_the_times_and_says_so_with_the_result()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-3));
        var tokenStore = new AdminTokenStore();
        tokenStore.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        var api = new FakeBillsApi { Window = new(earliest) };
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), tokenStore, settings: null) { Rate24Kt = 6000m };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        await vm.PrepareWindowAsync();

        api.WindowFailure = new System.Net.Http.HttpRequestException("No connection");
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(earliest, vm.StartAt);
        Assert.Equal(earliest, vm.EarliestAt);
        Assert.StartsWith("Created 3 bill(s)", vm.StatusMessage);
        Assert.Contains("Could not read when the last bill was made", vm.StatusMessage);
    }

    /// <summary>A whole minute, as the server gives the earliest time.</summary>
    private static DateTimeOffset Minute(DateTimeOffset utc)
        => new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
}
