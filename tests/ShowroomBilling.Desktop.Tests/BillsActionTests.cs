using System.Net;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
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
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BillsViewModel.StatusMessage)) statuses.Add(vm.StatusMessage);
        };

        await vm.RetryRowCommand.ExecuteAsync(vm.Items[0]);

        // The reload that follows every action then writes its own summary.
        Assert.Contains("Retry failed: Bill is already posted.", statuses);
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
        var vm = new BillsViewModel(api);
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
    public async Task Without_a_confirm_overlay_nothing_destructive_runs()
    {
        var api = new FakeBillsApi { Response = new BillListResponse(1, 0, 50, [Summary("SR/25-26/0001", BillStates.Posted)]) };
        var vm = new BillsViewModel(api);
        await vm.LoadAsync();

        await vm.RepostRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Empty(api.Calls);
        Assert.Equal("Confirmation prompt is not wired.", vm.StatusMessage);

        var billId = Guid.NewGuid();
        api.GetResponse = FakeBillsApi.Bill(billId);
        var details = new BillDetailsViewModel(api);
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
        var vm = new BillsViewModel(api, tokens)
        {
            EditBillHandler = (id, _) =>
            {
                opened.Add(id);
                return Task.CompletedTask;
            },
            AdminUnlockHandler = _ =>
            {
                prompts++;
                return Task.CompletedTask;
            }
        };
        await vm.LoadAsync();

        await vm.EditRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(1, prompts);
        Assert.Empty(opened);
        Assert.Equal("SR/25-26/0001 is posted; editing it needs admin unlock.", vm.StatusMessage);

        vm.AdminUnlockHandler = _ =>
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
        var vm = new BillsViewModel(api, new AdminTokenStore())
        {
            EditBillHandler = (id, _) =>
            {
                opened.Add(id);
                return Task.CompletedTask;
            },
            AdminUnlockHandler = _ =>
            {
                prompts++;
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
        var details = new BillDetailsViewModel(api)
        {
            ConfirmHandler = request =>
            {
                asked.Add(request);
                return Task.FromResult(false);
            }
        };
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
        var details = new BillDetailsViewModel(api);

        await details.LoadAsync(billId);

        Assert.Equal(billId, details.Bill?.Id);
        Assert.Equal(
            new[] { $"audit:{billId}", $"get:{billId}" },
            api.Calls.Order());
        Assert.Equal(string.Empty, details.StatusMessage);
        Assert.True(details.RetryCommand.CanExecute(null));
        Assert.False(details.PushCommand.CanExecute(null));
    }

    private static async Task<(BillDetailsViewModel Details, FakeBillsApi Api, List<ConfirmRequest> Asked)> DetailsLoadedAsync(bool answer)
    {
        var billId = Guid.NewGuid();
        var api = new FakeBillsApi { GetResponse = FakeBillsApi.Bill(billId) };
        var asked = new List<ConfirmRequest>();
        var details = new BillDetailsViewModel(api)
        {
            ConfirmHandler = request =>
            {
                asked.Add(request);
                return Task.FromResult(answer);
            }
        };
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
        var vm = new BillsViewModel(api, tokens)
        {
            ConfirmHandler = request =>
            {
                asked.Add(request);
                return Task.FromResult(answer);
            }
        };
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
