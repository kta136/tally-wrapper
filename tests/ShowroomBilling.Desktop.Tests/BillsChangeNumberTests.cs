using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// Change bill number, from the row menu: admin first, then the new number,
/// then a dry run whose warning the operator must accept, then the change.
/// </summary>
public sealed class BillsChangeNumberTests
{
    private static readonly DateOnly BillDate = new(2026, 4, 25);

    private readonly FakeBillsApi _api = new();
    private readonly AdminTokenStore _tokens = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly List<ChangeNumberPrompt> _prompts = [];

    public BillsChangeNumberTests()
    {
        _api.Response = new BillListResponse(1, 0, 50,
        [
            new BillSummaryItem(Guid.NewGuid(), BillStates.Pending, "SR/26-27/0007", "Walk-in", BillDate, 100m, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        ]);
        _dialogs.ChangeNumber = prompt =>
        {
            _prompts.Add(prompt);
            return Task.FromResult((true, "42", (string?)"typing mistake"));
        };
    }

    [Fact]
    public async Task The_number_is_asked_for_in_the_bills_own_fiscal_year_and_then_changed()
    {
        Unlock();
        var vm = await LoadedAsync();

        await vm.ChangeNumberRowCommand.ExecuteAsync(vm.Items[0]);

        var prompt = Assert.Single(_prompts);
        Assert.Equal("SR/26-27/0007", prompt.CurrentNumber);
        Assert.Equal(InvoiceNumberFormatter.ComputeFiscalYear(BillDate), prompt.FiscalYear);
        Assert.Equal(InvoiceNumberFormatter.DefaultPadding, prompt.Padding);
        Assert.Contains("Walk-in", prompt.Context);

        // A dry run, then the change itself, both with the admin token.
        Assert.Equal(2, ChangeNumberCalls().Count);
        Assert.All(ChangeNumberCalls(), call => Assert.EndsWith(":42", call));
        Assert.Equal(["admin-token", "admin-token"], _api.AdminTokens);
        Assert.Empty(_dialogs.Questions);
    }

    [Fact]
    public async Task A_warning_from_the_dry_run_is_put_to_the_operator_and_no_means_nothing_changes()
    {
        Unlock();
        _api.ChangeNumberWarning = "This leaves a gap in the series.";
        var vm = await LoadedAsync();

        await vm.ChangeNumberRowCommand.ExecuteAsync(vm.Items[0]);

        var question = Assert.Single(_dialogs.Questions);
        Assert.Equal("Change the number anyway?", question.Title);
        Assert.Equal("This leaves a gap in the series.", question.Message);
        Assert.True(question.IsWarning);
        Assert.Single(ChangeNumberCalls());
    }

    [Fact]
    public async Task A_warning_that_is_accepted_goes_ahead()
    {
        Unlock();
        _api.ChangeNumberWarning = "This leaves a gap in the series.";
        _dialogs.Confirm = _ => Task.FromResult(true);
        var vm = await LoadedAsync();

        await vm.ChangeNumberRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(2, ChangeNumberCalls().Count);
    }

    [Fact]
    public async Task Cancelling_the_number_dialog_sends_nothing()
    {
        Unlock();
        _dialogs.ChangeNumber = _ => Task.FromResult((false, string.Empty, (string?)null));
        var vm = await LoadedAsync();

        await vm.ChangeNumberRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Empty(ChangeNumberCalls());
    }

    [Fact]
    public async Task Without_admin_the_number_is_not_even_asked_for()
    {
        var unlockAsked = 0;
        _dialogs.AdminUnlock = _ =>
        {
            unlockAsked++;
            return Task.CompletedTask;
        };
        var vm = await LoadedAsync();

        await vm.ChangeNumberRowCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(1, unlockAsked);
        Assert.Empty(_prompts);
        Assert.Empty(ChangeNumberCalls());
    }

    private async Task<BillsViewModel> LoadedAsync()
    {
        var vm = new BillsViewModel(_api, _dialogs, _tokens);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        return vm;
    }

    private void Unlock() =>
        _tokens.Set(new AdminUnlockResponse("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "test"));

    private List<string> ChangeNumberCalls() => _api.Calls.Where(call => call.StartsWith("change-number:", StringComparison.Ordinal)).ToList();
}
