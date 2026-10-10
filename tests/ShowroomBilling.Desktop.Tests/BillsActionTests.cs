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
    public async Task Void_asks_first_and_does_nothing_when_declined()
    {
        var (vm, api, asked) = await LoadedAsync(answer: false, Summary("SR/25-26/0001"));

        await vm.VoidRowCommand.ExecuteAsync(vm.Items[0]);

        var question = Assert.Single(asked);
        Assert.Equal("Confirm Void", question.Title);
        Assert.Contains("SR/25-26/0001", question.Message);
        Assert.True(question.IsWarning);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Void_confirmed_calls_the_api_reports_it_and_reloads_the_list()
    {
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001"));
        var billId = vm.Items[0].Id;
        var loadsBefore = api.SearchCount;

        await vm.VoidRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal([$"void:{billId}"], api.Calls);
        Assert.Equal(loadsBefore + 1, api.SearchCount);
        Assert.False(vm.IsActing);
    }

    [Fact]
    public async Task A_refused_action_shows_the_servers_reason()
    {
        var (vm, api, _) = await LoadedAsync(answer: true, Summary("SR/25-26/0001"));
        api.FailWith = new ApiException(HttpStatusCode.Conflict, "Conflict", "Bill is already voided.", null);
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BillsViewModel.StatusMessage)) statuses.Add(vm.StatusMessage);
        };

        await vm.VoidRowCommand.ExecuteAsync(vm.Items[0]);

        // The reload that follows every action then writes its own summary.
        Assert.Contains("Void failed: Bill is already voided.", statuses);
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
    public async Task Void_selected_calls_once_per_row_and_stops_at_the_first_failure()
    {
        var (vm, api, _) = await LoadedAsync(
            answer: true,
            Summary("SR/25-26/0001"),
            Summary("SR/25-26/0002"),
            Summary("SR/25-26/0003"));
        vm.ToggleVisibleSelection(true);
        var ids = vm.Items.Select(x => x.Id).ToArray();

        await vm.VoidSelectedCommand.ExecuteAsync(null);

        Assert.Equal(ids.Select(id => $"void:{id}"), api.Calls);

        api.Calls.Clear();
        api.FailWith = new ApiException(HttpStatusCode.Conflict, null, "Bill is already voided.", null);
        vm.ToggleVisibleSelection(true);
        await vm.VoidSelectedCommand.ExecuteAsync(null);

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
        Assert.Contains("will NOT remove it from Tally", Assert.Single(asked).Message);
        Assert.All(api.AdminTokens, token => Assert.Equal("admin-token", token));
    }

    [Fact]
    public async Task Without_a_confirm_overlay_nothing_destructive_runs()
    {
        var api = new FakeBillsApi { Response = new BillListResponse(1, 0, 50, [Summary("SR/25-26/0001")]) };
        var vm = new BillsViewModel(api);
        await vm.LoadAsync();

        await vm.VoidRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Empty(api.Calls);
        Assert.Equal("Confirmation prompt is not wired.", vm.StatusMessage);
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
    public async Task Details_loads_the_bill_its_posting_status_and_its_timeline()
    {
        var billId = Guid.NewGuid();
        var api = new FakeBillsApi { GetResponse = FakeBillsApi.Bill(billId, BillStates.Failed) };
        var details = new BillDetailsViewModel(api);

        await details.LoadAsync(billId);

        Assert.Equal(billId, details.Bill?.Id);
        Assert.Equal(
            new[] { $"audit:{billId}", $"get:{billId}", $"posting-status:{billId}" },
            api.Calls.Order());
        Assert.Equal(string.Empty, details.StatusMessage);
        Assert.True(details.RetryCommand.CanExecute(null));
        Assert.False(details.PushCommand.CanExecute(null));
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
