using System.Net;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// The Bills tab actions end to end against a fake API: what the operator is
/// asked, what is sent, and what the status line says afterwards.
/// </summary>
public sealed class BillsActionTests
{
    [Fact]
    public async Task A_row_action_calls_the_api_and_reloads_the_list()
    {
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001", BillStates.Failed));
        var billId = vm.Items[0].Id;
        var loadsBefore = api.SearchCount;

        await vm.RetryRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal([$"retry:{billId}"], api.Calls);
        Assert.Equal(loadsBefore + 1, api.SearchCount);
        Assert.False(vm.IsActing);
    }

    [Fact]
    public async Task A_refused_action_shows_the_servers_reason()
    {
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001", BillStates.Failed));
        api.FailWith = new ApiException(HttpStatusCode.Conflict, "Conflict", "Bill is already posted.", null);
        await vm.RetryRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal("Retry failed: Bill is already posted.", vm.FooterNotice);
        Assert.False(vm.IsActing);
    }

    [Fact]
    public async Task Reposting_a_posted_bill_warns_about_a_duplicate_voucher()
    {
        var (vm, api, asked) = await LoadedAsync(answer: false, Summary("SR/25-26/0001", BillStates.Posted));

        await vm.RepostRowCommand.ExecuteAsync(vm.Items[0]);

        var question = Assert.Single(asked);
        Assert.True(question.IsWarning);
        Assert.Contains("duplicate sale", question.Message);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Reposting_a_failed_bill_is_a_plain_question()
    {
        var (vm, api, asked) = await LoadedAsync(answer: true, Summary("SR/25-26/0001", BillStates.Failed));
        var billId = vm.Items[0].Id;

        await vm.RepostRowCommand.ExecuteAsync(vm.Items[0]);

        var question = Assert.Single(asked);
        Assert.False(question.IsWarning);
        Assert.DoesNotContain("duplicate", question.Message);
        Assert.Equal([$"repost:{billId}"], api.Calls);
    }

    [Fact]
    public async Task The_batch_and_the_row_repost_ask_the_same_question()
    {
        var (vm, _, asked) = await LoadedAsync(
            answer: false,
            Summary("SR/25-26/0001", BillStates.Posted),
            Summary("SR/25-26/0002", BillStates.Posted));

        await vm.RepostRowCommand.ExecuteAsync(vm.Items[0]);
        vm.ToggleVisibleSelection(true);
        await vm.RepostSelectedCommand.ExecuteAsync(null);

        Assert.Equal(2, asked.Count);
        Assert.Equal(
            asked[0].Message.Replace("SR/25-26/0001", "X"),
            asked[1].Message.Replace("2 bill(s)", "X"));
    }

    [Fact]
    public async Task A_selection_action_calls_once_per_row_and_stops_at_the_first_failure()
    {
        var (vm, api, _) = await LoadedAsync(
            answer: true,
            Summary("SR/25-26/0001", BillStates.Failed),
            Summary("SR/25-26/0002", BillStates.Failed),
            Summary("SR/25-26/0003", BillStates.Failed));
        vm.ToggleVisibleSelection(true);
        var ids = vm.Items.Select(x => x.Id).ToArray();

        await vm.RetrySelectedCommand.ExecuteAsync(null);

        Assert.Equal(ids.Select(id => $"retry:{id}"), api.Calls);

        api.Calls.Clear();
        api.FailWith = new ApiException(HttpStatusCode.Conflict, null, "Bill is already posted.", null);
        vm.ToggleVisibleSelection(true);
        await vm.RetrySelectedCommand.ExecuteAsync(null);

        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task Delete_checks_first_and_warns_when_tally_holds_the_voucher()
    {
        var (vm, api, asked) = await LoadedAsync(answer: true, Summary("SR/25-26/0001", BillStates.Posted));
        api.DeleteDryRunSaysTallyDiverges = true;
        var billId = vm.Items[0].Id;

        await vm.DeleteRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal([$"delete-check:{billId}", $"delete:{billId}"], api.Calls);
        var question = Assert.Single(asked);
        Assert.Contains("does not remove it from Tally", question.Message);
        Assert.True(question.IsDestructive);
        Assert.All(api.AdminTokens, token => Assert.Equal("admin-token", token));
    }

    [Theory]
    [InlineData(3, 3, 0, false, null, "Pushed 3 bill(s).")]
    [InlineData(3, 0, 0, false, null, "No bills were pushed.")]
    [InlineData(3, 1, 1, false, "Ledger missing", "Pushed 1 bill(s); stopped on failure: Ledger missing")]
    [InlineData(3, 0, 1, false, "Ledger missing", "Push stopped immediately: Ledger missing")]
    [InlineData(3, 1, 0, true, null, "Pushed 1 of 3 bill(s); the run was stopped before the rest.")]
    public void A_batch_push_is_summed_up_in_one_line(
        int matched, int succeeded, int failed, bool cancelled, string? failure, string expected)
    {
        var response = new BillBatchPushResponse(
            matched, succeeded, failed, failed > 0, failed > 0 ? Guid.NewGuid() : null, failure, [], cancelled);

        Assert.Equal(expected, BillsStatusFormatter.FormatBatchPushStatus(response));
    }

    [Fact]
    public async Task The_toolbars_push_says_of_a_bill_what_the_row_menus_push_says()
    {
        var (vm, api, _) = await LoadedAsync(
            answer: true,
            Summary("SR/25-26/0001"),
            Summary("SR/25-26/0002"),
            Summary("SR/25-26/0003"));
        var ids = vm.Items.Select(row => row.Id).ToArray();

        // One bill ticked, and it went through: named, not counted.
        api.PushSelectedResponse = new BillBatchPushResponse(
            1, 1, 0, false, null, null, [new BillBatchPushItemResult(ids[0], true, BillStates.Posted, "SR/25-26/0001", null)]);
        vm.Items[0].IsSelected = true;
        await vm.PushSelectedCommand.ExecuteAsync(null);
        Assert.Equal(("Pushed SR/25-26/0001.", "ok"), (vm.FooterNotice, vm.FooterNoticeTone));

        // Tally refused the only one: its number and the reason.
        api.PushSelectedResponse = new BillBatchPushResponse(
            1, 0, 1, true, ids[0], "Ledger 'Sales' does not exist.",
            [new BillBatchPushItemResult(ids[0], false, BillStates.Failed, "SR/25-26/0001", "Ledger 'Sales' does not exist.")]);
        await vm.PushSelectedCommand.ExecuteAsync(null);
        Assert.Equal(
            ("Tally did not accept SR/25-26/0001: Ledger 'Sales' does not exist.", "err"),
            (vm.FooterNotice, vm.FooterNoticeTone));

        // Two went through and Tally never answered for the third: what the server made of the
        // transport is not the operator's concern, what to do next is.
        api.PushSelectedResponse = new BillBatchPushResponse(
            3, 2, 1, true, ids[2], "Tally may have processed the voucher before the transport failed: 500",
            [
                new BillBatchPushItemResult(ids[0], true, BillStates.Posted, "SR/25-26/0001", null),
                new BillBatchPushItemResult(ids[1], true, BillStates.Posted, "SR/25-26/0002", null),
                new BillBatchPushItemResult(ids[2], false, BillStates.ReconciliationRequired, "SR/25-26/0003", "Tally may have processed the voucher before the transport failed: 500"),
            ]);
        vm.ToggleVisibleSelection(true);
        await vm.PushSelectedCommand.ExecuteAsync(null);
        Assert.Equal(
            "Pushed 2 bill(s), then stopped. Tally did not answer for SR/25-26/0003. Check its voucher in Tally before anything else.",
            vm.FooterNotice);
    }

    /// <summary>
    /// 10 October 2026: the server answered every bill request with an error
    /// and the Bills tab showed an empty list, with nothing to say why.
    /// </summary>
    [Fact]
    public async Task A_failed_load_says_so_and_a_successful_one_takes_it_back()
    {
        var api = new FakeBillsApi
        {
            // What the client makes of the server's 503 problem answer.
            SearchFailure = new ApiException(
                System.Net.HttpStatusCode.ServiceUnavailable,
                "Database upgrade needed",
                "The database has not had the schema migrations this version needs.",
                rawBody: null)
        };
        var vm = new BillsViewModel(api, new FakeDialogs());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.LoadAsync();

        Assert.True(vm.HasLoadError);
        Assert.Contains("schema migrations", vm.LoadError);
        Assert.Empty(vm.Items);
        Assert.False(vm.IsLoading);
        Assert.Contains(nameof(vm.HasLoadError), raised);

        api.SearchFailure = null;
        api.Response = new BillListResponse(1, 0, 50, [Summary("SR/25-26/0001")]);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.HasLoadError);
        Assert.Null(vm.LoadError);
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task When_the_answer_is_no_nothing_destructive_runs()
    {
        var api = new FakeBillsApi { Response = new BillListResponse(1, 0, 50, [Summary("SR/25-26/0001", BillStates.Posted)]) };
        var vm = new BillsViewModel(api, new FakeDialogs());
        await vm.LoadAsync();

        await vm.RepostRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Empty(api.Calls);

        var billId = Guid.NewGuid();
        api.GetResponse = FakeBillsApi.Bill(billId);
        var details = new BillDetailsViewModel(api, new FakeDialogs());
        await details.LoadAsync(billId);
        api.Calls.Clear();

        await details.VoidCommand.ExecuteAsync(null);

        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Editing_a_posted_bill_asks_for_admin_first_and_stops_if_it_is_declined()
    {
        var api = new FakeBillsApi { Response = new BillListResponse(1, 0, 50, [Summary("SR/25-26/0001", BillStates.Posted)]) };
        var tokens = new AdminTokenStore();
        var opened = new List<Guid>();
        var prompts = 0;
        var dialogs = new FakeDialogs
        {
            AdminUnlock = _ =>
            {
                prompts++;
                return Task.CompletedTask;
            }
        };
        var vm = new BillsViewModel(api, dialogs, tokens)
        {
            EditBillHandler = (id, _) =>
            {
                opened.Add(id);
                return Task.CompletedTask;
            }
        };
        await vm.LoadAsync();

        await vm.EditRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(1, prompts);
        Assert.Empty(opened);
        Assert.Equal("SR/25-26/0001 is posted; editing it needs admin unlock.", vm.FooterNotice);

        dialogs.AdminUnlock = _ =>
        {
            tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "test"));
            return Task.CompletedTask;
        };
        await vm.EditRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal([vm.Items[0].Id], opened);
    }

    [Fact]
    public async Task Editing_a_pending_bill_does_not_ask_for_admin()
    {
        var api = new FakeBillsApi { Response = new BillListResponse(1, 0, 50, [Summary("SR/25-26/0001")]) };
        var opened = new List<Guid>();
        var prompts = 0;
        var dialogs = new FakeDialogs
        {
            AdminUnlock = _ =>
            {
                prompts++;
                return Task.CompletedTask;
            }
        };
        var vm = new BillsViewModel(api, dialogs, new AdminTokenStore())
        {
            EditBillHandler = (id, _) =>
            {
                opened.Add(id);
                return Task.CompletedTask;
            }
        };
        await vm.LoadAsync();

        await vm.EditRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(0, prompts);
        Assert.Equal([vm.Items[0].Id], opened);
    }

    [Fact]
    public async Task Ticking_every_row_refreshes_the_selection_once_not_once_per_row()
    {
        var rows = Enumerable.Range(1, 50).Select(n => Summary($"SR/25-26/{n:0000}")).ToArray();
        var (vm, _, _) = await LoadedAsync(answer: true, rows);
        var selectedCountRaised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BillsViewModel.SelectedCount)) selectedCountRaised++;
        };

        vm.ToggleVisibleSelection(true);

        Assert.Equal(50, vm.SelectedCount);
        Assert.True(selectedCountRaised <= 2, $"SelectedCount was raised {selectedCountRaised} times");

        // A single tick by the operator still updates straight away.
        selectedCountRaised = 0;
        vm.Items[3].IsSelected = false;
        Assert.Equal(49, vm.SelectedCount);
        Assert.Equal(1, selectedCountRaised);
    }

    [Fact]
    public async Task Details_repost_goes_through_the_same_confirmation()
    {
        var billId = Guid.NewGuid();
        var api = new FakeBillsApi { GetResponse = FakeBillsApi.Bill(billId, BillStates.Posted) };
        var asked = new List<ConfirmRequest>();
        var details = new BillDetailsViewModel(api, new FakeDialogs
        {
            Confirm = request =>
            {
                asked.Add(request);
                return Task.FromResult(false);
            }
        });
        await details.LoadAsync(billId);
        api.Calls.Clear();

        await details.RepostCommand.ExecuteAsync(null);

        Assert.Contains("duplicate sale", Assert.Single(asked).Message);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Details_void_asks_first_and_does_nothing_when_declined()
    {
        var (details, api, asked) = await DetailsLoadedAsync(answer: false);

        await details.VoidCommand.ExecuteAsync(null);

        var question = Assert.Single(asked);
        Assert.Equal("Void invoice SR/25-26/0001?", question.Title);
        Assert.True(question.IsDestructive);
        Assert.Equal("This cannot be undone.", question.Note);
        Assert.Empty(api.Calls);
        Assert.Equal(BillStates.Pending, details.Bill?.State);
    }

    [Fact]
    public async Task Details_void_confirmed_voids_the_bill()
    {
        var (details, api, _) = await DetailsLoadedAsync(answer: true);
        var billId = details.Bill!.Id;

        await details.VoidCommand.ExecuteAsync(null);

        Assert.Contains($"void:{billId}", api.Calls);
        Assert.Equal(BillStates.Voided, details.Bill?.State);
        Assert.Equal("Bill voided.", details.StatusMessage);
    }

    [Fact]
    public async Task Details_loads_the_bill_and_its_timeline()
    {
        var billId = Guid.NewGuid();
        var api = new FakeBillsApi { GetResponse = FakeBillsApi.Bill(billId, BillStates.Failed) };
        var details = new BillDetailsViewModel(api, new FakeDialogs());

        await details.LoadAsync(billId);

        Assert.Equal(billId, details.Bill?.Id);
        Assert.Equal(
            new[] { $"audit:{billId}", $"get:{billId}" },
            api.Calls.Order());
        Assert.Equal(string.Empty, details.StatusMessage);
        Assert.True(details.RetryCommand.CanExecute(null));
        Assert.False(details.PushCommand.CanExecute(null));
    }

    [Fact]
    public async Task What_an_action_came_to_stays_on_the_status_line_after_the_list_reloads()
    {
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001", BillStates.Failed));
        Assert.False(vm.HasFooterNotice);

        await vm.RetryRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(2, api.SearchCount);
        Assert.Equal(("Retried SR/25-26/0001.", "ok"), (vm.FooterNotice, vm.FooterNoticeTone));

        api.FailWith = new ApiException(HttpStatusCode.Conflict, "Conflict", "Bill is already posted.", null);
        await vm.RetryRowCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal(("Retry failed: Bill is already posted.", "err"), (vm.FooterNotice, vm.FooterNoticeTone));

        // A number already in use: the check refuses it, and the operator is told why.
        api.FailWith = new ApiException(HttpStatusCode.Conflict, "Conflict", "Invoice number SR/25-26/0009 is already in use.", null);
        var dialogs = new FakeDialogs { ChangeNumber = _ => Task.FromResult((true, "9", (string?)null)) };
        var tokens = new AdminTokenStore();
        tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "test"));
        var numbered = new BillsViewModel(api, dialogs, tokens);
        await numbered.LoadAsync();
        await numbered.ChangeNumberRowCommand.ExecuteAsync(numbered.Items[0]);
        Assert.Equal(
            ("Change number check failed: Invoice number SR/25-26/0009 is already in use.", "err"),
            (numbered.FooterNotice, numbered.FooterNoticeTone));
    }

    [Fact]
    public async Task While_tally_cannot_be_reached_the_status_line_says_why_push_is_greyed_out()
    {
        var api = new FakeBillsApi { Response = new BillListResponse(1, 0, 50, [Summary("SR/25-26/0001")]) };
        var vm = new BillsViewModel(api, new FakeDialogs());
        await vm.LoadAsync();

        Assert.Equal(("Tally connection has not been checked yet.", "warn"), (vm.FooterNotice, vm.FooterNoticeTone));

        vm.ApplyTallyHealthSnapshot(new SystemHealthSnapshot(
            ApiReachable: true,
            Masters: null,
            TallyCompany: new TallyCompanyHealthResponse(
                TallyHealthStatuses.Unhealthy, true, "Test Company", false, 1, DateTimeOffset.UtcNow, "Company 'Test Company' is not open in Tally."),
            Runtime: null));
        Assert.Equal("Tally push blocked: Company 'Test Company' is not open in Tally.", vm.FooterNotice);
        Assert.False(vm.PushRowCommand.CanExecute(vm.Items[0]));

        vm.ApplyTallyHealthSnapshot(new SystemHealthSnapshot(
            ApiReachable: true,
            Masters: null,
            TallyCompany: new TallyCompanyHealthResponse(
                TallyHealthStatuses.Healthy, true, "Test Company", true, 1, DateTimeOffset.UtcNow, "ok"),
            Runtime: null));
        Assert.False(vm.HasFooterNotice);
    }

    [Fact]
    public async Task Ticking_a_row_announces_the_row_the_toolbars_edit_and_change_number_act_on()
    {
        var (vm, _, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001"));
        var announced = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BillsViewModel.SelectedRow)) announced++;
        };

        vm.Items[0].IsSelected = true;

        Assert.True(announced > 0);
        Assert.Same(vm.Items[0], vm.SelectedRow);
        Assert.True(vm.EditRowCommand.CanExecute(vm.SelectedRow));
    }

    [Fact]
    public async Task One_bill_is_named_on_the_status_line_and_several_are_counted()
    {
        var (vm, api, _) = await LoadedAsync(
            answer: true,
            Summary("SR/25-26/0001", BillStates.Failed),
            Summary("SR/25-26/0002", BillStates.Failed));
        var statuses = Statuses(vm);

        // The toolbar with one bill ticked says what the row menu says.
        vm.Items.Single(row => row.InvoiceNumber == "SR/25-26/0001").IsSelected = true;
        await vm.RetrySelectedCommand.ExecuteAsync(null);

        Assert.Contains("Retrying SR/25-26/0001…", statuses);
        Assert.Contains("Retried SR/25-26/0001.", statuses);

        statuses.Clear();
        vm.ToggleVisibleSelection(true);
        await vm.RetrySelectedCommand.ExecuteAsync(null);

        Assert.Contains("Retrying 2 of 2…", statuses);
        Assert.Contains("Retried 2 bill(s).", statuses);

        statuses.Clear();
        api.FailWith = new ApiException(HttpStatusCode.Conflict, null, "Bill is already posted.", null);
        vm.ToggleVisibleSelection(true);
        await vm.RetrySelectedCommand.ExecuteAsync(null);

        Assert.Contains("Retry stopped: Bill is already posted.", statuses);
    }

    [Fact]
    public async Task An_action_stopped_by_a_check_says_why_and_leaves_the_list_as_it_is()
    {
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001", BillStates.Posted));
        var loads = api.SearchCount;

        // A posted bill cannot be marked as pushed.
        vm.ToggleVisibleSelection(true);
        await vm.MarkPostedSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Mark pushed blocked: every selected bill must be pending or failed.", vm.FooterNotice);
        Assert.Empty(api.Calls);

        // The dry run before a delete could not be made.
        api.FailWith = new ApiException(HttpStatusCode.ServiceUnavailable, null, "The server is not answering.", null);
        vm.ClearSelection();
        await vm.DeleteRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal("Delete check failed: The server is not answering.", vm.FooterNotice);
        Assert.Equal(loads, api.SearchCount);
        Assert.False(vm.IsActing);
    }

    [Fact]
    public async Task A_push_past_an_earlier_bill_not_in_Tally_asks_first_and_no_sends_nothing()
    {
        var (vm, api, asked) = await LoadedAsync(answer: false, Summary("SR/25-26/0002"));
        api.EarlierUnpushed.Add(new UnpushedEarlierBill(Guid.NewGuid(), "SR/25-26/0001", new DateOnly(2026, 4, 25), BillStates.Pending));

        await vm.PushRowCommand.ExecuteAsync(vm.Items[0]);

        var question = Assert.Single(asked);
        Assert.Equal("Push before SR/25-26/0001?", question.Title);
        Assert.Equal("Push anyway", question.ConfirmText);
        Assert.True(question.IsWarning);
        Assert.Contains("Tally numbers a day's vouchers in the order they arrive", question.Message);
        Assert.Equal(("SR/25-26/0001", "25 Apr 2026 · Pending"), (question.Facts![0].Label, question.Facts[0].Value));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Push_anyway_pushes()
    {
        var (vm, api, asked) = await LoadedAsync(answer: true, Summary("SR/25-26/0002"));
        api.EarlierUnpushed.Add(new UnpushedEarlierBill(Guid.NewGuid(), "SR/25-26/0001", new DateOnly(2026, 4, 25), BillStates.Pending));

        await vm.PushRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Single(asked);
        Assert.Equal([$"push:{vm.Items[0].Id}"], api.Calls);
    }

    [Fact]
    public async Task With_nothing_earlier_waiting_the_push_asks_nothing()
    {
        var (vm, api, asked) = await LoadedAsync(answer: false, Summary("SR/25-26/0001"));

        await vm.PushRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Empty(asked);
        Assert.Equal([$"push:{vm.Items[0].Id}"], api.Calls);
        Assert.Equal([vm.Items[0].Id], Assert.Single(api.PushOrderChecks));
    }

    [Fact]
    public async Task A_server_that_cannot_answer_the_order_check_does_not_stop_the_push()
    {
        var (vm, api, asked) = await LoadedAsync(answer: false, Summary("SR/25-26/0001"));
        api.PushOrderFailure = new ApiException(HttpStatusCode.NotFound, "Not Found", null, null);

        await vm.PushRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Empty(asked);
        Assert.Equal([$"push:{vm.Items[0].Id}"], api.Calls);
    }

    [Fact]
    public async Task Ticked_bills_go_to_Tally_by_date_then_number_whatever_the_tick_order()
    {
        var (vm, api, _) = await LoadedAsync(
            answer: true,
            Summary("SR/25-26/0003") with { BillDate = new DateOnly(2026, 4, 25) },
            Summary("SR/25-26/0001") with { BillDate = new DateOnly(2026, 4, 26) },
            Summary("SR/25-26/0002") with { BillDate = new DateOnly(2026, 4, 25) });
        foreach (var row in vm.Items.Reverse()) row.IsSelected = true;
        var byNumber = vm.Items.ToDictionary(row => row.InvoiceNumber!, row => row.Id);

        await vm.PushSelectedCommand.ExecuteAsync(null);

        Assert.Equal(
            [byNumber["SR/25-26/0002"], byNumber["SR/25-26/0003"], byNumber["SR/25-26/0001"]],
            Assert.Single(api.PushOrderChecks));
    }

    [Fact]
    public async Task A_retry_past_an_earlier_bill_not_in_Tally_asks_too()
    {
        var (vm, api, asked) = await LoadedAsync(answer: false, Summary("SR/25-26/0002", BillStates.Failed));
        api.EarlierUnpushed.Add(new UnpushedEarlierBill(Guid.NewGuid(), "SR/25-26/0001", new DateOnly(2026, 4, 25), BillStates.Failed));
        api.EarlierUnpushed.Add(new UnpushedEarlierBill(Guid.NewGuid(), "SR/25-26/0000", new DateOnly(2026, 4, 24), BillStates.Pending));

        await vm.RetryRowCommand.ExecuteAsync(vm.Items[0]);

        var question = Assert.Single(asked);
        Assert.Equal("Push before 2 earlier bills?", question.Title);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Ticked_bills_go_in_the_order_the_server_gives()
    {
        // The server reads the bills as they are now; another counter may have renumbered one.
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001"), Summary("SR/25-26/0002"));
        api.PushOrderOf = ids => ids.Reverse().ToArray();
        vm.ToggleVisibleSelection(true);

        await vm.PushSelectedCommand.ExecuteAsync(null);

        Assert.Equal(Assert.Single(api.PushOrderChecks).Reverse(), Assert.Single(api.PushedSelections));
    }

    [Fact]
    public async Task An_order_check_answer_that_does_not_fit_neither_stops_nor_reorders_the_push()
    {
        var (vm, api, asked) = await LoadedAsync(answer: false, Summary("SR/25-26/0001"), Summary("SR/25-26/0002"));
        api.PushOrderAnswer = new BillPushOrderResponse(null!, [Guid.NewGuid()]);
        vm.ToggleVisibleSelection(true);

        await vm.PushSelectedCommand.ExecuteAsync(null);

        Assert.Empty(asked);
        Assert.Equal(Assert.Single(api.PushOrderChecks), Assert.Single(api.PushedSelections));
    }

    [Fact]
    public async Task Retry_selected_stops_at_the_first_bill_that_does_not_reach_Tally()
    {
        // The next bill would take the number of the one Tally refused.
        var (vm, api, _) = await LoadedAsync(
            answer: true,
            Summary("SR/25-26/0001", BillStates.Failed),
            Summary("SR/25-26/0002", BillStates.Failed),
            Summary("SR/25-26/0003", BillStates.Failed));
        api.PostingLeaves = BillStates.Failed;
        vm.ToggleVisibleSelection(true);

        await vm.RetrySelectedCommand.ExecuteAsync(null);

        Assert.Equal([$"retry:{vm.Items.Single(row => row.InvoiceNumber == "SR/25-26/0001").Id}"], api.Calls);
        Assert.Equal(
            ("Retried 1 of 3 bill(s), then stopped: SR/25-26/0001 did not go to Tally. The list shows why.", "err"),
            (vm.FooterNotice, vm.FooterNoticeTone));
    }

    [Fact]
    public async Task A_push_Tally_filed_under_another_number_says_so_and_where_to_look()
    {
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001"));
        api.PushFiledAs = "SR/25-26/0002";
        api.PushFiscalYear = InvoiceNumberFormatter.ComputeFiscalYear(DateTimeOffset.UtcNow);

        await vm.PushRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(
            ("Pushed SR/25-26/0001. Tally filed it as SR/25-26/0002: see Settings → Tally check.", "warn"),
            (vm.FooterNotice, vm.FooterNoticeTone));
    }

    [Fact]
    public async Task A_bill_of_an_earlier_year_Tally_filed_under_another_number_is_not_sent_to_Tally_check()
    {
        // Tally check compares the current year only.
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001"));
        api.PushFiledAs = "SR/25-26/0002";
        api.PushFiscalYear = "2001-02";

        await vm.PushRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(("Pushed SR/25-26/0001. Tally filed it as SR/25-26/0002.", "warn"), (vm.FooterNotice, vm.FooterNoticeTone));
    }

    [Fact]
    public async Task A_push_Tally_filed_under_the_bills_own_number_is_a_plain_push()
    {
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001"));
        api.PushFiledAs = "SR/25-26/0001";

        await vm.PushRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(("Pushed SR/25-26/0001.", "ok"), (vm.FooterNotice, vm.FooterNoticeTone));
    }

    [Fact]
    public async Task Pushing_ticked_bills_counts_those_Tally_filed_under_another_number()
    {
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001"), Summary("SR/25-26/0002"));
        var ids = vm.Items.Select(row => row.Id).ToArray();

        // One ticked bill: named, with Tally's number.
        api.PushSelectedResponse = new BillBatchPushResponse(
            1, 1, 0, false, null, null,
            [new BillBatchPushItemResult(ids[0], true, BillStates.Posted, "SR/25-26/0001", null, "SR/25-26/0003")]);
        vm.Items[0].IsSelected = true;
        await vm.PushSelectedCommand.ExecuteAsync(null);
        // A batch answer does not say the bill's year, and Tally check shows the current one only.
        Assert.Equal(("Pushed SR/25-26/0001. Tally filed it as SR/25-26/0003.", "warn"), (vm.FooterNotice, vm.FooterNoticeTone));

        // Two: counted.
        api.PushSelectedResponse = new BillBatchPushResponse(
            2, 2, 0, false, null, null,
            [
                new BillBatchPushItemResult(ids[0], true, BillStates.Posted, "SR/25-26/0001", null, "SR/25-26/0001"),
                new BillBatchPushItemResult(ids[1], true, BillStates.Posted, "SR/25-26/0002", null, "SR/25-26/0003"),
            ]);
        foreach (var row in vm.Items) row.IsSelected = true;
        await vm.PushSelectedCommand.ExecuteAsync(null);
        Assert.Equal(
            ("Pushed 2 bill(s). Tally filed 1 under another number: their rows say which.", "warn"),
            (vm.FooterNotice, vm.FooterNoticeTone));
    }

    [Theory]
    [InlineData(BillStates.Posted, false, "SR/25-26/0002", "In Tally as SR/25-26/0002", "warn")]
    [InlineData(BillStates.Posted, true, "SR/25-26/0002", "Edited after push · in Tally as SR/25-26/0002", "warn")]
    [InlineData(BillStates.Posted, false, "SR/25-26/0001", "—", "neutral")]
    [InlineData(BillStates.Posted, false, null, "—", "neutral")]
    [InlineData(BillStates.Pending, true, "SR/25-26/0002", "Awaiting push · in Tally as SR/25-26/0002", "warn")]
    [InlineData(BillStates.Pending, false, "SR/25-26/0002", "Awaiting Tally push", "neutral")]
    public void A_rows_note_names_the_number_Tally_holds_the_bill_under_when_it_is_another(
        string state, bool editedAfterPush, string? inTally, string note, string tone)
    {
        var row = new BillListRowViewModel(Summary("SR/25-26/0001", state) with
        {
            EditedAfterPush = editedAfterPush,
            TallyVoucherNumber = inTally,
        });

        Assert.Equal((note, tone), (row.RowNote, row.NoteTone));
    }

    [Fact]
    public async Task Details_push_names_the_bill_and_shows_it_as_the_server_returned_it()
    {
        var (details, api, _) = await DetailsLoadedAsync(answer: true);
        var billId = details.Bill!.Id;
        var statuses = Statuses(details);
        var toldTheList = 0;
        details.BillMutated = () => toldTheList++;

        await details.PushCommand.ExecuteAsync(null);

        // The push returned the bill, so it is not read a second time; its timeline is.
        Assert.Equal([$"push:{billId}", $"audit:{billId}"], api.Calls);
        Assert.Contains("Pushing invoice SR/25-26/0001…", statuses);
        Assert.Equal("Pushed invoice SR/25-26/0001.", details.StatusMessage);
        Assert.Equal(BillStates.Posted, details.Bill?.State);
        Assert.Equal(1, toldTheList);
        Assert.False(details.IsActing);
    }

    [Fact]
    public async Task Details_reads_the_bill_again_after_a_push_the_server_could_not_finish()
    {
        var (details, api, _) = await DetailsLoadedAsync(answer: true);
        var billId = details.Bill!.Id;
        var toldTheList = 0;
        details.BillMutated = () => toldTheList++;
        // Tally did not answer: the push is refused, and the server has moved the bill on.
        api.GetResponse = FakeBillsApi.Bill(billId, BillStates.ReconciliationRequired);
        api.FailWith = new ApiException(HttpStatusCode.BadGateway, null, "Tally did not answer.", null);

        await details.PushCommand.ExecuteAsync(null);

        Assert.Equal("Push failed: Tally did not answer.", details.StatusMessage);
        Assert.Equal(BillStates.ReconciliationRequired, details.Bill?.State);
        Assert.False(details.PushCommand.CanExecute(null));
        Assert.Equal(1, toldTheList);
        Assert.False(details.IsActing);
    }

    [Fact]
    public async Task A_push_tally_refused_or_did_not_answer_is_not_reported_as_pushed()
    {
        // The server answers a push with the bill whatever Tally did with the voucher.
        var (details, api, _) = await DetailsLoadedAsync(answer: true);
        api.PostingLeaves = BillStates.ReconciliationRequired;

        await details.PushCommand.ExecuteAsync(null);

        Assert.Equal(BillStates.ReconciliationRequired, details.Bill?.State);
        Assert.StartsWith("Tally did not answer for invoice SR/25-26/0001.", details.StatusMessage);
        Assert.False(details.PushCommand.CanExecute(null));

        api.PostingLeaves = BillStates.Failed;
        api.PostingError = "Ledger 'Sales' does not exist.";
        api.GetResponse = FakeBillsApi.Bill(details.Bill!.Id, BillStates.Failed);
        await details.RetryCommand.ExecuteAsync(null);

        Assert.Equal("Tally did not accept invoice SR/25-26/0001: Ledger 'Sales' does not exist.", details.StatusMessage);

        // The Bills tab says the same of a row, and counts how many of several went through.
        var (vm, rowsApi, _) = await LoadedAsync(
            answer: true,
            Summary("SR/25-26/0001", BillStates.Failed),
            Summary("SR/25-26/0002", BillStates.Failed));
        rowsApi.PostingLeaves = BillStates.Failed;
        var statuses = Statuses(vm);

        await vm.RetryRowCommand.ExecuteAsync(vm.Items.Single(row => row.InvoiceNumber == "SR/25-26/0001"));
        Assert.Contains("Tally did not accept SR/25-26/0001. Its timeline has the reason.", statuses);

        vm.ToggleVisibleSelection(true);
        await vm.RetrySelectedCommand.ExecuteAsync(null);
        Assert.Contains("Retried 1 of 2 bill(s), then stopped: SR/25-26/0001 did not go to Tally. The list shows why.", statuses);
    }

    [Fact]
    public async Task Details_retry_and_repost_are_the_row_menus_actions_on_the_bill_shown()
    {
        var (details, api, asked) = await DetailsLoadedAsync(answer: true);
        var billId = details.Bill!.Id;

        await details.RetryCommand.ExecuteAsync(null);

        // The retry, then the bill and its timeline read again (those two side by side).
        Assert.Equal($"retry:{billId}", api.Calls[0]);
        Assert.Equal([$"audit:{billId}", $"get:{billId}"], api.Calls.Skip(1).Order());
        Assert.Equal("Retried invoice SR/25-26/0001.", details.StatusMessage);

        api.Calls.Clear();
        await details.RepostCommand.ExecuteAsync(null);

        Assert.Equal("Repost invoice SR/25-26/0001?", Assert.Single(asked).Title);
        Assert.Contains($"repost:{billId}", api.Calls);
        Assert.Equal("Reposted invoice SR/25-26/0001.", details.StatusMessage);
    }

    [Fact]
    public async Task Details_follows_the_new_bill_a_revision_makes()
    {
        var (details, api, _) = await DetailsLoadedAsync(answer: true);
        var billId = details.Bill!.Id;

        await details.ReviseCommand.ExecuteAsync(null);

        Assert.NotEqual(billId, details.Bill!.Id);
        Assert.Equal("SR/25-26/0002", details.Bill.InvoiceNumber);
        Assert.Equal([$"revise:{billId}", $"audit:{details.Bill.Id}"], api.Calls);
        Assert.Equal("Revision created.", details.StatusMessage);
    }

    private static List<string> Statuses(BillsViewModel vm)
    {
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BillsViewModel.FooterNotice)) statuses.Add(vm.FooterNotice);
        };
        return statuses;
    }

    private static List<string> Statuses(BillDetailsViewModel details)
    {
        var statuses = new List<string>();
        details.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BillDetailsViewModel.StatusMessage)) statuses.Add(details.StatusMessage);
        };
        return statuses;
    }

    private static async Task<(BillDetailsViewModel Details, FakeBillsApi Api, List<ConfirmRequest> Asked)> DetailsLoadedAsync(bool answer)
    {
        var billId = Guid.NewGuid();
        var api = new FakeBillsApi { GetResponse = FakeBillsApi.Bill(billId) };
        var asked = new List<ConfirmRequest>();
        var details = new BillDetailsViewModel(api, new FakeDialogs
        {
            Confirm = request =>
            {
                asked.Add(request);
                return Task.FromResult(answer);
            }
        });
        await details.LoadAsync(billId);
        api.Calls.Clear();
        return (details, api, asked);
    }

    private static async Task<(BillsViewModel Vm, FakeBillsApi Api, List<ConfirmRequest> Asked)> LoadedAsync(
        bool answer,
        params BillSummaryItem[] rows)
    {
        var api = new FakeBillsApi { Response = new BillListResponse(rows.Length, 0, 50, rows) };
        var tokens = new AdminTokenStore();
        tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "test"));
        var asked = new List<ConfirmRequest>();
        var vm = new BillsViewModel(api, new FakeDialogs
        {
            Confirm = request =>
            {
                asked.Add(request);
                return Task.FromResult(answer);
            }
        }, tokens);
        vm.ApplyTallyHealthSnapshot(new SystemHealthSnapshot(
            ApiReachable: true,
            Masters: null,
            TallyCompany: new TallyCompanyHealthResponse(
                TallyHealthStatuses.Healthy, true, "Test Company", true, 1, DateTimeOffset.UtcNow, "ok"),
            Runtime: null));
        await vm.LoadAsync();
        return (vm, api, asked);
    }

    private static BillSummaryItem Summary(string invoiceNumber, string state = BillStates.Pending) => new(
        Id: Guid.NewGuid(),
        State: state,
        InvoiceNumber: invoiceNumber,
        PartyName: "Walk-in",
        BillDate: new DateOnly(2026, 4, 25),
        GrandTotal: 100m,
        CreatedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: DateTimeOffset.UtcNow);
}
