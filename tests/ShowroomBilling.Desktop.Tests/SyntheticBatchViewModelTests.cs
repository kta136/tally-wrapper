using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

namespace ShowroomBilling.Desktop.Tests;

public sealed class SyntheticBatchViewModelTests
{
    // ── What stops Start ────────────────────────────────────────────────────

    [Fact]
    public void A_new_scheduler_has_no_built_in_rate_and_says_so()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null);

        Assert.Equal(0m, vm.Rate24Kt);
        Assert.Equal(string.Empty, vm.RateText);
        Assert.True(vm.IsRateMissing);
        Assert.Equal("Type today's 24K rate to start.", vm.ValidationMessage);
        Assert.True(vm.ShowValidation);
    }

    [Fact]
    public void The_rate_box_keeps_what_is_typed_and_shows_a_rate_set_from_elsewhere()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null);

        // Typed, with a decimal point on its way: the box is not rewritten under the cursor.
        vm.RateText = "12,000.";
        Assert.Equal(12_000m, vm.Rate24Kt);
        Assert.Equal("12,000.", vm.RateText);

        // Not a number: no rate.
        vm.RateText = "abc";
        Assert.Equal(0m, vm.Rate24Kt);

        // The Invoice tab's rate, put in when the dialog opens.
        vm.Rate24Kt = 12_345.5m;
        Assert.Equal("12,345.5", vm.RateText);
    }

    [Fact]
    public void A_largest_bill_outside_the_limits_stops_Start()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 6000m };

        vm.MaxBillAmount = 250_000; // above ₹1,99,000
        Assert.Equal("The largest single bill must be between ₹50,000 and ₹1,99,000.", vm.ValidationMessage);

        vm.MaxBillAmount = 49_999;
        Assert.Equal("The largest single bill must be between ₹50,000 and ₹1,99,000.", vm.ValidationMessage);
        Assert.Equal("—", vm.BillCountText);
    }

    [Fact]
    public void A_total_under_the_smallest_bill_stops_Start()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 6000m };
        vm.TotalAmount = 49_999;

        Assert.Equal("The total must be at least ₹50,000, the smallest bill a batch makes.", vm.ValidationMessage);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(4, 3)]
    [InlineData(1, 26)]
    public void Items_per_bill_outside_1_to_25_stop_Start(int fewest, int most)
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 6000m };
        vm.MaxItemsPerBill = most;
        vm.MinItemsPerBill = fewest;

        Assert.Equal("Items per bill must be from 1 to 25, the smaller number first.", vm.ValidationMessage);
    }

    [Fact]
    public void Times_the_wrong_way_round_stop_Start()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 6000m };
        var now = DateTimeOffset.Now;
        vm.EndAt = now.AddHours(-5);
        vm.StartAt = now.AddHours(-1);

        Assert.Equal("The From time must be before the To time.", vm.ValidationMessage);
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

    // ── The two times ───────────────────────────────────────────────────────

    [Fact]
    public void A_time_is_typed_as_hours_and_minutes_and_a_day_is_picked_beside_it()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null);
        var day = DateTime.Today.AddDays(-2);
        vm.StartAt = new DateTimeOffset(day.AddHours(10).AddMinutes(43));

        Assert.Equal(day, vm.FromDate);
        Assert.Equal("10:43", vm.FromTime);

        vm.FromTime = "9:05";
        Assert.Equal(day.AddHours(9).AddMinutes(5), vm.StartAt.LocalDateTime);

        // Text that is no time changes nothing, and the box is told to show the time again.
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.FromTime = "later";
        Assert.Equal("09:05", vm.FromTime);
        Assert.Contains(nameof(vm.FromTime), raised);

        // Another day keeps the time of day.
        vm.FromDate = day.AddDays(1);
        Assert.Equal(day.AddDays(1).AddHours(9).AddMinutes(5), vm.StartAt.LocalDateTime);

        vm.EndAt = new DateTimeOffset(day.AddHours(16));
        vm.ToTime = "1645";
        vm.ToDate = day.AddDays(1);
        Assert.Equal(day.AddDays(1).AddHours(16).AddMinutes(45), vm.EndAt.LocalDateTime);
    }

    [Theory]
    [InlineData("10:43", 10, 43)]
    [InlineData("9:05", 9, 5)]
    [InlineData("1043", 10, 43)]
    [InlineData("943", 9, 43)]
    [InlineData("10.43", 10, 43)]
    [InlineData("10", 10, 0)]
    [InlineData(" 7 ", 7, 0)]
    [InlineData("23:59", 23, 59)]
    public void A_typed_time_is_read(string typed, int hour, int minute)
    {
        Assert.True(BatchClock.TryParse(typed, out var time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("10:60")]
    [InlineData("ten")]
    [InlineData("10:43:10")]
    [InlineData("-1:00")]
    public void Text_that_is_no_time_is_not_read(string typed)
    {
        Assert.False(BatchClock.TryParse(typed, out _));
    }

    [Fact]
    public async Task The_times_run_from_the_first_minute_after_the_last_bill_to_now()
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
        Assert.Equal($"The earliest is {earliest.ToLocalTime():dd MMM, HH:mm}, just after the last bill. The latest is now.", vm.WindowNote);
    }

    [Fact]
    public async Task With_no_bill_yet_the_times_start_at_the_start_of_today()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null);

        await vm.PrepareWindowAsync();

        Assert.Null(vm.EarliestAt);
        Assert.Equal(DateTime.Today, vm.StartAt.LocalDateTime);
        Assert.Equal("There is no bill yet, so any time up to now can be used.", vm.WindowNote);
    }

    [Fact]
    public async Task Times_outside_the_earliest_time_and_now_stop_Start()
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
        Assert.Equal("The To time cannot be later than now.", vm.ValidationMessage);

        vm.EndAt = end;
        vm.StartAt = earliest.AddMinutes(-1);
        Assert.Equal($"The From time cannot be before {earliest.ToLocalTime():dd MMM, HH:mm}, just after the last bill.", vm.ValidationMessage);

        vm.StartAt = earliest;
        Assert.Null(vm.ValidationMessage);
        Assert.False(vm.ShowValidation);
    }

    [Fact]
    public async Task An_earliest_time_later_than_now_leaves_no_time_for_a_batch()
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
    public async Task When_the_last_bills_time_cannot_be_read_the_form_says_so_and_the_server_checks()
    {
        var vm = new SyntheticBatchViewModel(
            new FakeBillsApi { WindowFailure = new System.Net.Http.HttpRequestException("No connection") }, new FakeDialogs(), adminTokens: null, settings: null);

        await vm.PrepareWindowAsync();

        Assert.Null(vm.EarliestAt);
        Assert.Equal(DateTime.Today, vm.StartAt.LocalDateTime);
        Assert.Equal("Could not read when the last bill was made. The server checks the times when the batch starts.", vm.WindowNote);
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
    public async Task A_time_typed_while_the_last_bills_time_is_read_is_kept_and_Last_bill_to_now_puts_it_back()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-3));
        var slow = new TaskCompletionSource<SyntheticBatchWindowResponse>();
        var api = new FakeBillsApi { Window = new(earliest) };
        api.PendingWindows.Enqueue(slow);
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), adminTokens: null, settings: null);

        var opening = vm.PrepareWindowAsync();
        var typed = earliest.AddHours(1);
        vm.StartAt = typed;
        slow.SetResult(new(earliest));
        await opening;

        Assert.Equal(typed, vm.StartAt);
        Assert.Equal(earliest, vm.EarliestAt);

        await vm.LastBillToNowCommand.ExecuteAsync(null);

        Assert.Equal(earliest, vm.StartAt);
    }

    // ── What the batch makes ────────────────────────────────────────────────

    [Fact]
    public async Task The_summary_says_how_many_bills_and_the_shop_hours_a_batch_keeps_to()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-2));
        var api = new FakeBillsApi { Window = new(earliest, new BatchHoursSettings(new TimeOnly(0, 0), new TimeOnly(23, 59), DayOfWeek.Monday, 5)) };
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 12_000m };

        // Before the hours are read, and from a server that has none.
        Assert.Equal("5 to 20 bills", vm.BillCountText);
        // Five bills of ₹1,99,000 are the fewest ₹10,00,000 can come out as, and hold ₹9,95,000.
        Assert.Equal("About ₹9,95,000 to ₹10,00,000", vm.AddsUpToText);
        Assert.Equal("Any time of day", vm.ShopHoursText);
        Assert.Equal("Open every day", vm.ClosedOnText);
        Assert.Equal("No gap set", vm.GapText);

        await vm.PrepareWindowAsync();

        Assert.Equal("00:00 to 23:59", vm.ShopHoursText);
        Assert.Equal("Monday", vm.ClosedOnText);
        Assert.Equal("5 minutes apart", vm.GapText);
        Assert.Equal("Each between ₹50,000 and ₹1,99,000. ₹1,99,000 is the GST limit and cannot be raised.", vm.BillRangeNote);

        vm.TotalAmount = 60_000;
        Assert.Equal("1 bill", vm.BillCountText);
        Assert.Equal("About ₹60,000", vm.AddsUpToText);

        // A total the limits cannot reach: two bills would need ₹1,00,000, and one holds ₹60,000.
        vm.MaxBillAmount = 60_000;
        vm.TotalAmount = 99_999;
        Assert.Equal("1 bill", vm.BillCountText);
        Assert.Equal("About ₹60,000", vm.AddsUpToText);
        vm.TotalAmount = 60_000;
        vm.MaxBillAmount = 199_000;
        vm.MaxBillAmount = 60_000;
        Assert.Equal("Each between ₹50,000 and ₹60,000. ₹1,99,000 is the GST limit and cannot be raised.", vm.BillRangeNote);
    }

    [Fact]
    public void Times_too_short_for_the_gap_are_said_quietly_and_do_not_stop_Start()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 12_000m };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        var end = Minute(DateTimeOffset.UtcNow.AddHours(-1));
        vm.EndAt = end;
        vm.StartAt = end.AddMinutes(-59);
        vm.Hours = new BatchHoursSettings(GapMinutes: 5);

        // Sixty minutes hold twelve bills five minutes apart; ₹10,00,000 can make up to twenty.
        var room = BatchOpenMinutes.RoomAtGap(BatchOpenMinutes.Count(vm.StartAt, vm.EndAt, null, vm.Hours), 5);
        Assert.Equal(12, room);
        Assert.Null(vm.ValidationMessage);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal("These times have room for 12 bills 5 minutes apart. If the batch makes more, some will be closer together.", vm.CloserNote);
        Assert.True(vm.HasCloserNote);

        // With room for every bill the batch can make, nothing is said.
        vm.StartAt = end.AddHours(-3);
        Assert.Equal(string.Empty, vm.CloserNote);
    }

    [Fact]
    public void Times_with_too_few_minutes_or_none_inside_shop_hours_stop_Start()
    {
        var vm = new SyntheticBatchViewModel(new FakeBillsApi(), new FakeDialogs(), adminTokens: null, settings: null) { Rate24Kt = 12_000m };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        var end = Minute(DateTimeOffset.UtcNow.AddHours(-1));
        vm.EndAt = end;
        vm.StartAt = end.AddMinutes(-9);

        Assert.Equal("These times have room for 10 bill(s), and this batch can make up to 20. Lower the total, or pick a longer time.", vm.ValidationMessage);

        // Closed on every day of these times: no minute a bill could be dated at.
        vm.Hours = new BatchHoursSettings(ClosedOn: ShowroomBilling.Contracts.IndiaTime.DateOf(end).DayOfWeek);
        if (ShowroomBilling.Contracts.IndiaTime.DateOf(vm.StartAt) == ShowroomBilling.Contracts.IndiaTime.DateOf(end))
        {
            Assert.Equal("These times have no minute inside shop hours. Pick other times, or change the hours in Settings, Admin.", vm.ValidationMessage);
        }
    }

    // ── The question before a batch ─────────────────────────────────────────

    [Fact]
    public async Task Start_asks_first_and_a_no_sends_nothing()
    {
        var api = new FakeBillsApi();
        var dialogs = new FakeDialogs();
        var vm = ReadyToStart(api, dialogs);

        await vm.StartCommand.ExecuteAsync(null);

        var question = Assert.Single(dialogs.Questions);
        Assert.Equal("Make 5 to 20 pending bills?", question.Title);
        Assert.Equal("They are saved as soon as you say yes, and wait in the Bills tab. Nothing is sent to Tally.", question.Message);
        Assert.Equal("Make the bills", question.ConfirmText);
        Assert.False(question.IsWarning);
        Assert.Null(question.Note);
        Assert.Equal(
            ["Adds up to", "24K rate", "Dated", "Shop hours", "Payment", "Items per bill", "Karats"],
            question.Facts!.Select(fact => fact.Label));
        Assert.Equal("About ₹9,95,000 to ₹10,00,000", question.Facts![0].Value);
        Assert.Equal("₹6,000 per gram", question.Facts[1].Value);
        Assert.Equal("Any time of day", question.Facts[3].Value);
        Assert.Equal("1 to 3", question.Facts[5].Value);
        Assert.Equal("22K", question.Facts[6].Value);

        Assert.Equal(0, api.CallCount);
        Assert.False(vm.IsRunning);
        Assert.Null(vm.Result);
        Assert.Equal("Not started", vm.RunState);
    }

    [Fact]
    public async Task A_rate_far_from_the_Invoice_tabs_turns_the_question_into_a_warning()
    {
        var dialogs = new FakeDialogs();
        var vm = ReadyToStart(new FakeBillsApi(), dialogs);
        vm.Hours = new BatchHoursSettings(new TimeOnly(0, 0), new TimeOnly(23, 59), DayOfWeek.Sunday, 0);
        vm.Rate24Kt = 9_500m;

        // Within a tenth: said plainly.
        vm.InvoiceRate24Kt = 10_000m;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.False(dialogs.Questions[^1].IsWarning);
        Assert.Equal("00:00 to 23:59, not on Sunday", dialogs.Questions[^1].Facts![3].Value);

        // More than a tenth away: most likely a slip of a digit.
        vm.InvoiceRate24Kt = 12_000m;
        await vm.StartCommand.ExecuteAsync(null);
        var warned = dialogs.Questions[^1];
        Assert.True(warned.IsWarning);
        Assert.Equal("The 24K rate here, ₹9,500, is 21% below the Invoice tab's ₹12,000. Check it before you go on.", warned.Note);
        Assert.True(warned.Facts![1].Marked);

        vm.Rate24Kt = 120_000m;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("The 24K rate here, ₹1,20,000, is 900% above the Invoice tab's ₹12,000. Check it before you go on.", dialogs.Questions[^1].Note);

        // A rate typed to the paisa is shown to the paisa: it is what is sent.
        vm.RateText = "12,345.67";
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("₹12,345.67 per gram", dialogs.Questions[^1].Facts![1].Value);
    }

    [Fact]
    public async Task The_batch_sent_is_the_one_the_operator_said_yes_to()
    {
        // The form can change while the question is open: the read of the last bill's time may
        // still be on its way and put other times in. What was approved is what is sent.
        var api = new FakeBillsApi();
        var dialogs = new FakeDialogs();
        var vm = ReadyToStart(api, dialogs);
        var approvedFrom = vm.StartAt;
        var approvedTo = vm.EndAt;
        dialogs.Confirm = _ =>
        {
            vm.StartAt = approvedFrom.AddMinutes(30);
            vm.EndAt = approvedTo.AddMinutes(-30);
            vm.TotalAmount = 500_000;
            return Task.FromResult(true);
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(approvedFrom.ToUniversalTime(), api.LastRequest!.StartAtUtc);
        Assert.Equal(approvedTo.ToUniversalTime(), api.LastRequest.EndAtUtc);
        Assert.Equal(1_000_000, api.LastRequest.TotalAmount);
        Assert.Equal("₹10,00,000", vm.Asked[0].Value);
        // The shop hours the operator was shown go with it; the server refuses when they have changed.
        Assert.Null(api.LastRequest.ExpectedHours);
        vm.ShowFormCommand.Execute(null);
        dialogs.Confirm = _ => Task.FromResult(true);
        var shown = new BatchHoursSettings(new TimeOnly(0, 0), new TimeOnly(23, 59), null, 0);
        vm.Hours = shown;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(shown, api.LastRequest.ExpectedHours);
    }

    [Fact]
    public async Task Start_opens_admin_unlock_when_admin_is_locked()
    {
        var api = new FakeBillsApi();
        var promptCalled = false;
        var dialogs = Yes();
        dialogs.AdminUnlock = _ =>
        {
            promptCalled = true;
            return Task.CompletedTask;
        };
        var vm = new SyntheticBatchViewModel(api, dialogs, new AdminTokenStore(), settings: null) { Rate24Kt = 6000m };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        var start = DateTimeOffset.Now.AddHours(-4);
        vm.StartAt = start;
        vm.EndAt = start.AddHours(3);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(promptCalled, "the unlock dialog is asked for when there is no admin session");
        // The unlock dialog took the scheduler's place; the scheduler is put back.
        Assert.Equal([ShowroomBilling.Desktop.Shell.ShellDialogs.SyntheticBatch], dialogs.Shown);
        // Still locked afterwards: nothing was sent, and the form is as it was.
        Assert.Equal(0, api.CallCount);
        Assert.Equal("Not started", vm.RunState);
        Assert.Null(vm.Result);
    }

    // ── What a run comes to ─────────────────────────────────────────────────

    [Fact]
    public async Task A_batch_made_whole_says_its_total_and_bill_numbers_and_loads_the_bills_again()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-5));
        var api = new FakeBillsApi { Window = new(earliest) };
        var vm = ReadyToStart(api);
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;
        var afterTheNewBills = Minute(DateTimeOffset.UtcNow.AddMinutes(-30));
        api.Window = new(afterTheNewBills);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("admin-token", api.LastAdminToken);
        Assert.Equal(1, api.CallCount);
        var result = vm.Result!;
        Assert.Equal(("ok", "3 bills made", string.Empty), (result.Tone, result.Headline, result.Reason));
        Assert.Equal("They come to **₹3,00,000** and are in the Bills tab as **pending**. Nothing was sent to Tally.", result.Where);
        Assert.Equal(("SR/25-26/0101", "SR/25-26/0103"), (result.FirstNumber, result.LastNumber));
        Assert.True(result.BillsMayBeSaved);
        Assert.Equal("Done", vm.RunState);
        Assert.True(vm.IsResultShown);
        Assert.False(vm.IsFormShown);
        Assert.Equal(1, reloads);
        // The next batch goes after these bills.
        Assert.Equal(afterTheNewBills, vm.StartAt);
        Assert.Equal($"A next batch can be dated from {afterTheNewBills.ToLocalTime():dd MMM, HH:mm}.", vm.NextBatchNote);
        // The batch goes with an id of its own, so the server can be asked about it afterwards.
        Assert.NotNull(api.LastRequest!.BatchId);
        Assert.Empty(api.OutcomeReads);
        // What was asked stays beside the result.
        Assert.Equal("Total", vm.Asked[0].Label);
        Assert.Equal("₹10,00,000", vm.Asked[0].Value);
        Assert.Equal(10, vm.Asked.Count);
    }

    [Fact]
    public async Task After_a_result_the_form_comes_back_and_the_Bills_tab_can_be_asked_for()
    {
        var vm = ReadyToStart(new FakeBillsApi());
        await vm.StartCommand.ExecuteAsync(null);
        Assert.False(vm.StartCommand.CanExecute(null));

        var asked = 0;
        vm.OpenBillsRequested += (_, _) => asked++;
        vm.OpenBillsCommand.Execute(null);
        Assert.Equal(1, asked);
        // Going to the bills deals with the result: the scheduler opens on its form next time.
        Assert.Null(vm.Result);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Result);
        vm.ShowFormCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.True(vm.IsFormShown);
        Assert.True(vm.IsReadyShown);
        Assert.Equal("Not started", vm.RunState);
    }

    [Fact]
    public async Task After_a_batch_a_failed_read_of_the_last_bill_keeps_the_times_and_says_so()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-3));
        var api = new FakeBillsApi { Window = new(earliest) };
        var vm = ReadyToStart(api);
        await vm.PrepareWindowAsync();

        api.WindowFailure = new System.Net.Http.HttpRequestException("No connection");
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(earliest, vm.StartAt);
        Assert.Equal(earliest, vm.EarliestAt);
        Assert.Equal("3 bills made", vm.Result!.Headline);
        Assert.StartsWith("Could not read when the last bill was made", vm.WindowNote);
        Assert.Equal(string.Empty, vm.NextBatchNote);
    }

    [Fact]
    public async Task A_cancelled_batch_says_how_many_bills_were_saved_and_follows_them()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-3));
        var api = new FakeBillsApi { Window = new(earliest), BatchWaitsToBeCancelled = true };
        var vm = ReadyToStart(api);
        await vm.PrepareWindowAsync();
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        Assert.True(vm.IsFormShown);
        Assert.False(vm.IsReadyShown);
        // The server stops after the bill in hand; three of twelve are saved, the last one an hour ago.
        api.Outcome = new SyntheticBatchOutcomeResponse(true, 12, 3, 300_000m, "SR/25-26/0101", "SR/25-26/0103");
        var afterTheSavedBills = Minute(DateTimeOffset.UtcNow.AddHours(-1));
        api.Window = new(afterTheSavedBills);
        vm.CancelCommand.Execute(null);
        await run;

        var result = vm.Result!;
        Assert.Equal(("warn", "Stopped after 3 of 12 bills", "You cancelled the run."), (result.Tone, result.Headline, result.Reason));
        Assert.Equal("The 3 saved bills are in the Bills tab as **pending**. Nothing was sent to Tally.", result.Where);
        Assert.Equal(("SR/25-26/0101", "SR/25-26/0103"), (result.FirstNumber, result.LastNumber));
        Assert.Equal("Stopped", vm.RunState);
        Assert.False(vm.IsRunning);
        // The Bills tab shows them, and the next batch starts after them.
        Assert.Equal(1, reloads);
        Assert.Equal(afterTheSavedBills, vm.StartAt);
        Assert.Equal([api.LastRequest!.BatchId!.Value], api.OutcomeReads);
    }

    [Fact]
    public async Task A_batch_cancelled_before_its_first_bill_changes_nothing()
    {
        var api = new FakeBillsApi
        {
            BatchWaitsToBeCancelled = true,
            Outcome = new SyntheticBatchOutcomeResponse(true, 12, 0, 0m, null, null),
        };
        var vm = ReadyToStart(api);
        var typed = vm.StartAt;
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        var run = vm.StartCommand.ExecuteAsync(null);
        vm.CancelCommand.Execute(null);
        await run;

        var result = vm.Result!;
        Assert.Equal(("Stopped before any bill was saved", "You cancelled the run."), (result.Headline, result.Reason));
        Assert.Equal("Nothing was saved, so you can change the form and start again.", result.Where);
        Assert.False(result.BillsMayBeSaved);
        Assert.Equal(0, reloads);
        Assert.Equal(typed, vm.StartAt);
    }

    [Fact]
    public async Task A_batch_the_server_stops_part_way_gives_its_reason_and_how_many_were_saved()
    {
        var api = new FakeBillsApi
        {
            BatchFailure = new ApiException(System.Net.HttpStatusCode.Conflict, "Bill state conflict", "A bill was saved at a counter while the batch was being made.", null),
            Outcome = new SyntheticBatchOutcomeResponse(true, 12, 3, 300_000m, "SR/25-26/0101", "SR/25-26/0103"),
        };
        var vm = ReadyToStart(api);
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Stopped after 3 of 12 bills", vm.Result!.Headline);
        Assert.Equal("A bill was saved at a counter while the batch was being made.", vm.Result.Reason);
        Assert.Equal("Failed", vm.RunState);
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task A_batch_refused_before_any_bill_changes_nothing()
    {
        var api = new FakeBillsApi
        {
            BatchFailure = new ApiException(System.Net.HttpStatusCode.BadRequest, "Bad request", "End time cannot be later than now.", null),
        };
        var vm = ReadyToStart(api);
        var typed = vm.StartAt;
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        await vm.StartCommand.ExecuteAsync(null);

        var result = vm.Result!;
        Assert.Equal(("err", "The batch was not made", "End time cannot be later than now."), (result.Tone, result.Headline, result.Reason));
        Assert.Equal("Nothing was saved.", result.Where);
        Assert.False(result.BillsMayBeSaved);
        Assert.Equal("Failed", vm.RunState);
        // No bill was saved: the Bills tab and the times the operator typed are left alone. The
        // server was asked, and holds nothing under the id of a batch it never planned.
        Assert.Equal(0, reloads);
        Assert.Equal(typed, vm.StartAt);
        Assert.Single(api.OutcomeReads);
    }

    [Fact]
    public async Task A_refusal_that_comes_after_bills_were_saved_is_not_taken_for_nothing_saved()
    {
        // A bill that fails its checks part-way is refused like a bad form (400), with bills
        // already saved before it.
        var api = new FakeBillsApi
        {
            BatchFailure = new ApiException(System.Net.HttpStatusCode.BadRequest, "Bill payload invalid", "A line's item name is too long.", null),
            Outcome = new SyntheticBatchOutcomeResponse(true, 12, 4, 400_000m, "SR/25-26/0101", "SR/25-26/0104"),
        };
        var vm = ReadyToStart(api);
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Stopped after 4 of 12 bills", vm.Result!.Headline);
        Assert.Equal("A line's item name is too long.", vm.Result.Reason);
        Assert.True(vm.Result.BillsMayBeSaved);
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task A_refusal_whose_outcome_cannot_be_read_is_not_taken_for_nothing_saved()
    {
        // The refusal may have come after bills were saved, and the server could not be asked.
        var api = new FakeBillsApi
        {
            BatchFailure = new ApiException(System.Net.HttpStatusCode.BadRequest, "Bill payload invalid", "A line's item name is too long.", null),
            OutcomeFailure = new TaskCanceledException("The outcome was not answered in time"),
        };
        var vm = ReadyToStart(api);
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("How many bills were saved is not known", vm.Result!.Headline);
        Assert.True(vm.Result.BillsMayBeSaved);
        Assert.Equal(1, reloads);
    }

    [Theory]
    [InlineData(0, "The batch stopped at its first bill")]
    [InlineData(3, "Stopped after 3 of 12 bills")]
    public async Task A_save_that_did_not_say_whether_its_bill_was_stored_is_said_as_in_doubt(int counted, string headline)
    {
        var api = new FakeBillsApi
        {
            BatchFailure = new ApiException(System.Net.HttpStatusCode.InternalServerError, "Server error", "The database stopped answering.", null),
            Outcome = new SyntheticBatchOutcomeResponse(true, 12, counted, counted * 100_000m, null, null, LastSaveInDoubt: true),
        };
        var vm = ReadyToStart(api);
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        await vm.StartCommand.ExecuteAsync(null);

        var result = vm.Result!;
        Assert.Equal(headline, result.Headline);
        Assert.Equal("The database stopped answering.", result.Reason);
        Assert.Equal("One more bill may have been saved than the server counted. Look in the Bills tab before you start another batch.", result.Where);
        // Also at the first bill: there may be a bill to look at, so the Bills tab is offered and loaded.
        Assert.True(result.BillsMayBeSaved);
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task A_batch_whose_answer_was_lost_but_was_made_whole_is_done()
    {
        var api = new FakeBillsApi
        {
            BatchFailure = new System.Net.Http.HttpRequestException("The connection was closed"),
            Outcome = new SyntheticBatchOutcomeResponse(true, 3, 3, 300_000m, "SR/25-26/0101", "SR/25-26/0103"),
        };
        var vm = ReadyToStart(api);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(("ok", "3 bills made"), (vm.Result!.Tone, vm.Result.Headline));
        Assert.Equal("Done", vm.RunState);
    }

    [Fact]
    public async Task An_error_answer_to_a_batch_that_was_made_whole_is_still_done()
    {
        var api = new FakeBillsApi
        {
            BatchFailure = new ApiException(System.Net.HttpStatusCode.BadGateway, "Bad gateway", "The answer was lost on the way.", null),
            Outcome = new SyntheticBatchOutcomeResponse(true, 3, 3, 300_000m, "SR/25-26/0101", "SR/25-26/0103"),
        };
        var vm = ReadyToStart(api);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Done", vm.RunState);
        Assert.Equal(("ok", "3 bills made", string.Empty), (vm.Result!.Tone, vm.Result.Headline, vm.Result.Reason));
    }

    [Fact]
    public async Task A_batch_that_took_too_long_says_so_and_how_far_the_server_got()
    {
        // The request gave up, not the operator. Giving up stops the server after the bill in hand.
        var api = new FakeBillsApi
        {
            BatchFailure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            Outcome = new SyntheticBatchOutcomeResponse(true, 12, 1, 100_000m, "SR/25-26/0101", "SR/25-26/0101"),
        };
        var vm = ReadyToStart(api);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Stopped after 1 of 12 bills", vm.Result!.Headline);
        Assert.Equal("The server took too long to answer.", vm.Result.Reason);
        Assert.Equal("The 1 saved bill is in the Bills tab as **pending**. Nothing was sent to Tally.", vm.Result.Where);
        Assert.Equal("Stopped", vm.RunState);
    }

    [Fact]
    public async Task When_the_server_cannot_say_how_far_a_batch_got_the_form_says_it_is_not_known()
    {
        var api = new FakeBillsApi
        {
            BatchFailure = new System.Net.Http.HttpRequestException("No connection"),
            OutcomeFailure = new System.Net.Http.HttpRequestException("No connection"),
        };
        var vm = ReadyToStart(api);
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        await vm.StartCommand.ExecuteAsync(null);

        var result = vm.Result!;
        Assert.Equal("How many bills were saved is not known", result.Headline);
        Assert.Equal("The batch did not finish. No connection.", result.Reason);
        Assert.Equal("Look in the Bills tab before you start another batch.", result.Where);
        Assert.True(result.BillsMayBeSaved);
        Assert.Equal("Stopped", vm.RunState);
        // Bills may have been saved, so the Bills tab is loaded again all the same.
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task A_server_failure_whose_outcome_cannot_be_read_is_not_taken_for_nothing_saved()
    {
        // A batch can save bills before the server answers with an error. If how far it got then
        // cannot be read (or the server is an older build that cannot say), the count is not known.
        var api = new FakeBillsApi
        {
            BatchFailure = new ApiException(System.Net.HttpStatusCode.InternalServerError, "Server error", "The database did not answer.", null),
            OutcomeFailure = new System.Net.Http.HttpRequestException("No connection"),
        };
        var vm = ReadyToStart(api);
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("How many bills were saved is not known", vm.Result!.Headline);
        Assert.Equal("The batch did not finish. The database did not answer.", vm.Result.Reason);
        Assert.Equal(1, reloads);

        // The same when the server knows of no such batch.
        api.OutcomeFailure = null;
        vm.ShowFormCommand.Execute(null);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("How many bills were saved is not known", vm.Result!.Headline);
        Assert.Equal(2, reloads);
    }

    [Fact]
    public async Task While_the_server_is_still_saving_the_form_stays_running_and_reports_the_final_count()
    {
        // The request was cancelled while a bill was being saved: the server says "not finished"
        // until that bill is in. Start must not come back before then, or the count and the next
        // start time would both be stale.
        var api = new FakeBillsApi { BatchWaitsToBeCancelled = true };
        api.EarlierOutcomes.Enqueue(new SyntheticBatchOutcomeResponse(false, 12, 2, 200_000m, "SR/25-26/0101", "SR/25-26/0102"));
        api.EarlierOutcomes.Enqueue(new SyntheticBatchOutcomeResponse(false, 12, 2, 200_000m, "SR/25-26/0101", "SR/25-26/0102"));
        api.Outcome = new SyntheticBatchOutcomeResponse(true, 12, 3, 300_000m, "SR/25-26/0101", "SR/25-26/0103");
        var vm = ReadyToStart(api);
        var runningWhileAsked = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusMessage) && vm.StatusMessage.StartsWith("The server is finishing", StringComparison.Ordinal))
                runningWhileAsked.Add(vm.IsRunning && !vm.StartCommand.CanExecute(null) && vm.Result is null);
        };

        var run = vm.StartCommand.ExecuteAsync(null);
        vm.CancelCommand.Execute(null);
        await run;

        Assert.NotEmpty(runningWhileAsked);
        Assert.All(runningWhileAsked, Assert.True);
        Assert.Equal(3, api.OutcomeReads.Count);
        Assert.Equal("Stopped after 3 of 12 bills", vm.Result!.Headline);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task A_batch_stopped_by_a_counter_before_its_first_bill_still_follows_the_counters_bill()
    {
        var earliest = Minute(DateTimeOffset.UtcNow.AddHours(-3));
        var api = new FakeBillsApi
        {
            Window = new(earliest),
            BatchFailure = new ApiException(System.Net.HttpStatusCode.Conflict, "Bill state conflict", "A bill was saved at a counter while the batch was being made.", null),
            Outcome = new SyntheticBatchOutcomeResponse(true, 12, 0, 0m, null, null),
        };
        var vm = ReadyToStart(api);
        await vm.PrepareWindowAsync();
        var afterTheCountersBill = Minute(DateTimeOffset.UtcNow);
        api.Window = new(afterTheCountersBill);
        var reloads = 0;
        vm.BillsSaved += (_, _) => reloads++;

        await vm.StartCommand.ExecuteAsync(null);

        // The batch saved nothing, but a bill is stored that was not before: the Bills tab shows
        // it, and the times start after it, so the same form is not refused when sent again.
        var result = vm.Result!;
        Assert.Equal("Stopped before any bill was saved", result.Headline);
        Assert.Equal("A bill was saved at a counter while the batch was being made.", result.Reason);
        Assert.Equal("This batch saved nothing. The times now start after the last bill.", result.Where);
        Assert.False(result.BillsMayBeSaved);
        Assert.Equal("Failed", vm.RunState);
        Assert.Equal(1, reloads);
        Assert.Equal(afterTheCountersBill, vm.StartAt);
    }

    // ── Shop hours in Settings, Admin ───────────────────────────────────────

    [Fact]
    public async Task The_shop_hours_are_read_into_the_card_and_followed_by_the_scheduler()
    {
        var api = new FakeBillsApi { Hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 30), DayOfWeek.Sunday, 5) };
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), adminTokens: null, settings: null);
        var card = vm.HoursEditor;

        await card.LoadAsync();

        Assert.Equal(("10:00", "20:30", "Sunday", "5"), (card.OpensText, card.ClosesText, card.ClosedDay.Label, card.GapText));
        Assert.Equal(DayOfWeek.Sunday, card.ClosedDay.Day);
        Assert.False(card.HasNotice);
        Assert.Equal(api.Hours, vm.Hours);
        Assert.Equal(
            ["None, open every day", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"],
            card.ClosedDayOptions.Select(option => option.Label));
    }

    [Fact]
    public async Task Unlocking_admin_fills_the_shop_hours_card()
    {
        var api = new FakeBillsApi { Hours = new BatchHoursSettings(new TimeOnly(9, 30), new TimeOnly(19, 0)) };
        var tokens = new AdminTokenStore();
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), tokens, settings: null);

        tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        await Task.Yield();

        Assert.Equal(("09:30", "19:00", "None, open every day", ""), (vm.HoursEditor.OpensText, vm.HoursEditor.ClosesText, vm.HoursEditor.ClosedDay.Label, vm.HoursEditor.GapText));
    }

    [Fact]
    public async Task Saving_the_shop_hours_sends_them_with_the_admin_token_and_the_scheduler_follows()
    {
        var api = new FakeBillsApi();
        var tokens = new AdminTokenStore();
        tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        var vm = new SyntheticBatchViewModel(api, new FakeDialogs(), tokens, settings: null);
        var card = vm.HoursEditor;
        await card.LoadAsync();
        card.OpensText = "10";
        card.ClosesText = "2030";
        card.ClosedDay = card.ClosedDayOptions.Single(option => option.Day == DayOfWeek.Tuesday);
        card.GapText = "7";

        await card.SaveCommand.ExecuteAsync(null);

        var (saved, token) = Assert.Single(api.SavedHours);
        Assert.Equal(new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 30), DayOfWeek.Tuesday, 7), saved);
        Assert.Equal("admin-token", token);
        Assert.Equal(("Shop hours saved.", "ok"), (card.Notice, card.NoticeTone));
        Assert.Equal(("10:00", "20:30"), (card.OpensText, card.ClosesText));
        Assert.Equal(saved, vm.Hours);
        Assert.Equal("10:00 to 20:30", vm.ShopHoursText);

        // Emptied again: any time of day, every day, no gap.
        card.OpensText = card.ClosesText = card.GapText = string.Empty;
        card.ClosedDay = card.ClosedDayOptions[0];
        await card.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new BatchHoursSettings(), api.SavedHours[^1].Hours);
    }

    [Theory]
    [InlineData("10:00", "", "", "Type both times, or leave both empty to date bills at any time of day.")]
    [InlineData("10:00", "soon", "", "Type the times as hours and minutes on the 24-hour clock, such as 10:00.")]
    [InlineData("20:00", "10:00", "", "The closing time must be later than the opening time.")]
    [InlineData("10:00", "10:00", "", "The closing time must be later than the opening time.")]
    [InlineData("", "", "121", "The minutes between bills must be a whole number from 0 to 120.")]
    [InlineData("", "", "five", "The minutes between bills must be a whole number from 0 to 120.")]
    public async Task Shop_hours_that_make_no_sense_are_said_on_the_card_and_not_sent(string opens, string closes, string gap, string problem)
    {
        var api = new FakeBillsApi();
        var tokens = new AdminTokenStore();
        tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        var card = new BatchHoursViewModel(api, new FakeDialogs(), tokens);
        await card.LoadAsync();
        (card.OpensText, card.ClosesText, card.GapText) = (opens, closes, gap);

        await card.SaveCommand.ExecuteAsync(null);

        Assert.Equal((problem, "err"), (card.Notice, card.NoticeTone));
        Assert.Empty(api.SavedHours);
    }

    [Fact]
    public async Task Shop_hours_are_not_saved_while_admin_is_locked_and_a_refusal_is_said_on_the_card()
    {
        var api = new FakeBillsApi();
        var asked = 0;
        var dialogs = new FakeDialogs
        {
            AdminUnlock = _ =>
            {
                asked++;
                return Task.CompletedTask;
            },
        };
        var tokens = new AdminTokenStore();
        var card = new BatchHoursViewModel(api, dialogs, tokens);
        await card.LoadAsync();

        await card.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.Equal("Unlock admin to save the shop hours.", card.Notice);
        Assert.Empty(api.SavedHours);

        tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        api.HoursFailure = new ApiException(System.Net.HttpStatusCode.BadRequest, "Bad request", "The closing time must be later than the opening time.", null);
        await card.SaveCommand.ExecuteAsync(null);

        Assert.Equal(("The shop hours were not saved. The closing time must be later than the opening time.", "err"), (card.Notice, card.NoticeTone));
        Assert.False(card.IsBusy);
    }

    [Fact]
    public async Task Shop_hours_that_could_not_be_read_cannot_be_saved_over()
    {
        // The boxes are empty because the read failed, not because no hours are saved. Saving them
        // would clear the hours for every counter.
        var api = new FakeBillsApi
        {
            Hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 0), DayOfWeek.Sunday, 5),
            HoursFailure = new System.Net.Http.HttpRequestException("No connection"),
        };
        var tokens = new AdminTokenStore();
        tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        var card = new BatchHoursViewModel(api, new FakeDialogs(), tokens);

        await card.LoadAsync();

        Assert.False(card.IsLoaded);
        Assert.True(card.NeedsReading);
        Assert.False(card.SaveCommand.CanExecute(null));
        Assert.Equal(("The shop hours could not be read, so they cannot be changed yet. No connection", "err"), (card.Notice, card.NoticeTone));
        card.GapText = "7";
        await card.SaveCommand.ExecuteAsync(null);
        Assert.Empty(api.SavedHours);

        // Read again, once the server answers: the saved hours are in the boxes and can be changed.
        api.HoursFailure = null;
        await card.ReadAgainCommand.ExecuteAsync(null);

        Assert.True(card.IsLoaded);
        Assert.False(card.NeedsReading);
        Assert.Equal(("10:00", "20:00", "5"), (card.OpensText, card.ClosesText, card.GapText));
        card.GapText = "7";
        await card.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 0), DayOfWeek.Sunday, 7), Assert.Single(api.SavedHours).Hours);
    }

    [Fact]
    public async Task Shop_hours_cannot_be_saved_over_after_a_later_read_of_them_fails()
    {
        // Read once, then read again (admin unlocked again) and that read fails: another counter
        // may have changed the hours since, so the boxes are no longer known to hold what is saved.
        var api = new FakeBillsApi { Hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 0), DayOfWeek.Sunday, 5) };
        var tokens = new AdminTokenStore();
        tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        var card = new BatchHoursViewModel(api, new FakeDialogs(), tokens);
        await card.LoadAsync();
        Assert.True(card.SaveCommand.CanExecute(null));

        api.HoursFailure = new System.Net.Http.HttpRequestException("No connection");
        await card.LoadAsync();

        Assert.False(card.IsLoaded);
        Assert.True(card.NeedsReading);
        Assert.False(card.SaveCommand.CanExecute(null));
        card.GapText = "9";
        await card.SaveCommand.ExecuteAsync(null);
        Assert.Empty(api.SavedHours);
    }

    [Fact]
    public async Task A_save_of_shop_hours_that_gets_no_answer_is_read_back_and_not_called_a_failure()
    {
        // The save may have gone through with its answer lost. The card shows what the server
        // holds, whichever that is, and does not say "not saved".
        var api = new LostAnswerApi { Hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 0), DayOfWeek.Sunday, 5) };
        var tokens = new AdminTokenStore();
        tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        var card = new BatchHoursViewModel(api, new FakeDialogs(), tokens);
        await card.LoadAsync();
        card.GapText = "9";

        await card.SaveCommand.ExecuteAsync(null);

        // It did go through: the boxes show the nine minutes the server now holds.
        Assert.Equal("9", card.GapText);
        Assert.True(card.IsLoaded);
        Assert.Equal(("The answer to the save was lost. These are the shop hours the server holds now.", "err"), (card.Notice, card.NoticeTone));
        Assert.False(card.IsBusy);
    }

    /// <summary>A server that saves the shop hours and whose answer to the save never arrives.</summary>
    private sealed class LostAnswerApi : ISyntheticBatchApiClient
    {
        public BatchHoursSettings Hours { get; set; } = new();

        public Task<BatchHoursResponse> GetBatchHoursAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchHoursResponse(Hours, DateTimeOffset.UtcNow));

        public Task<BatchHoursResponse> SaveBatchHoursAsync(UpdateBatchHoursRequest request, string adminToken, CancellationToken cancellationToken = default)
        {
            Hours = request.Hours;
            return Task.FromException<BatchHoursResponse>(new TaskCanceledException("No answer in time"));
        }

        public Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SyntheticBatchWindowResponse> GetSyntheticBatchWindowAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SyntheticBatchOutcomeResponse?> GetSyntheticBatchOutcomeAsync(Guid batchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>Dialogs that say yes to the question before a batch.</summary>
    private static FakeDialogs Yes() => new() { Confirm = _ => Task.FromResult(true) };

    /// <summary>
    /// A scheduler with admin unlocked, a rate, a karat and times in the past, and an operator who
    /// says yes to the question (unless other dialogs are given): Start goes straight to the server.
    /// </summary>
    private static SyntheticBatchViewModel ReadyToStart(FakeBillsApi api, FakeDialogs? dialogs = null)
    {
        var tokenStore = new AdminTokenStore();
        tokenStore.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "unit-test"));
        var vm = new SyntheticBatchViewModel(api, dialogs ?? Yes(), tokenStore, settings: null) { Rate24Kt = 6000m };
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        var start = DateTimeOffset.Now.AddHours(-4);
        vm.StartAt = start;
        vm.EndAt = start.AddHours(3);
        return vm;
    }

    /// <summary>A whole minute, as the server gives the earliest time.</summary>
    private static DateTimeOffset Minute(DateTimeOffset utc)
        => new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
}
