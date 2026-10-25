using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Handover;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests.Handover;

public sealed class HandoverCoordinatorTests
{
    private readonly FakeBillsApi _api = new();
    private readonly Host _host;
    private readonly HandoverCoordinator _coordinator;
    private readonly List<HandoverToastViewModel> _toasts = [];
    private int _broughtToFront;

    public HandoverCoordinatorTests()
    {
        _host = new Host(HandoverTestMasters.Settings());
        _coordinator = new HandoverCoordinator(_api, _host)
        {
            ShowToast = _toasts.Add,
            BringToFront = () => _broughtToFront++,
        };
    }

    private void SetKey() => Assert.Null(_host.Settings.Handover.Store.Save(HandoverTestLinks.VectorKey));

    private static string FreshLink(params Guid[] saveIds)
        => HandoverTestLinks.Signed(HandoverTestLinks.Document(
            DateTimeOffset.UtcNow,
            saveIds.Select(id => HandoverTestLinks.Bill(id, "Buyer A", HandoverTestLinks.Line()))));

    [Fact]
    public async Task A_link_is_not_handled_before_the_app_has_started()
    {
        SetKey();
        _coordinator.Receive(FreshLink(Guid.NewGuid()));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(_api.Calls);

        _coordinator.MarkStarted();
        await _coordinator.Idle;
        Assert.Single(_api.Creates);
    }

    [Fact]
    public async Task A_hand_over_that_went_well_shows_the_toast_and_no_dialog()
    {
        SetKey();
        _coordinator.MarkStarted();

        _coordinator.Receive(FreshLink(Guid.NewGuid(), Guid.NewGuid()));
        await _coordinator.Idle;

        var toast = Assert.Single(_toasts);
        Assert.Equal("2 bills saved and printed", toast.Title);
        Assert.Null(_host.ActiveDialog);
        Assert.Equal(0, _broughtToFront);
        Assert.Equal(2, _host.Printed.Count);

        toast.OpenBillsCommand.Execute(null);
        Assert.Equal(NavTab.Bills, _host.ActiveTab);
        Assert.Equal(1, _broughtToFront);
    }

    [Fact]
    public async Task With_no_key_the_result_dialog_says_this_PC_has_none()
    {
        _coordinator.MarkStarted();

        _coordinator.Receive(FreshLink(Guid.NewGuid()));
        await WaitForDialogAsync();

        Assert.Equal(ShellDialogs.HandoverResult, _host.ActiveDialog);
        Assert.Equal(HandoverLinkReader.NoKey, _coordinator.Result.Banner);
        Assert.Equal(1, _broughtToFront);
        Assert.Empty(_api.Calls);

        _coordinator.Result.CloseCommand.Execute(null);
        await _coordinator.Idle;
        Assert.Null(_host.ActiveDialog);
    }

    [Fact]
    public async Task Esc_closes_the_result_dialog_and_lets_the_next_link_in()
    {
        _coordinator.MarkStarted();
        _coordinator.Receive("tallywrapper:something-else");
        await WaitForDialogAsync();
        Assert.Equal(HandoverLinkReader.NotAHandover, _coordinator.Result.Banner);
        SetKey();
        _coordinator.Receive(FreshLink(Guid.NewGuid()));

        // What the shell does on Esc.
        _host.ActiveDialog = null;
        _coordinator.OnActiveDialogChanged(null);
        await _coordinator.Idle;

        Assert.Single(_api.Creates);
        Assert.Single(_toasts);
    }

    [Fact]
    public async Task Try_again_runs_the_same_hand_over_once_more()
    {
        SetKey();
        _coordinator.MarkStarted();
        _api.ByOperationFailure = new System.Net.Http.HttpRequestException("No connection could be made");

        _coordinator.Receive(FreshLink(Guid.NewGuid()));
        await WaitForDialogAsync();
        Assert.Equal("Bill not saved", _coordinator.Result.Title);
        Assert.True(_coordinator.Result.TryAgainCommand.CanExecute(null));

        _api.ByOperationFailure = null;
        _coordinator.Result.TryAgainCommand.Execute(null);
        await _coordinator.Idle;

        Assert.Single(_api.Creates);
        Assert.Single(_toasts);
        Assert.Null(_host.ActiveDialog);
    }

    [Fact]
    public async Task A_posted_bill_opens_the_admin_unlock_with_the_bills_context_and_clears_it_after()
    {
        SetKey();
        _coordinator.MarkStarted();
        var saveId = Guid.NewGuid();
        var link = FreshLink(saveId);
        var payload = HandoverBillMapper.Map(
            HandoverTestLinks.Read(HandoverTestLinks.Document(DateTimeOffset.UtcNow, [HandoverTestLinks.Bill(saveId, "Buyer A", HandoverTestLinks.Line())])).Bills[0],
            HandoverTestMasters.Masters).Payload!;
        _api.ByOperation[saveId] = FakeBillsApi.Bill(Guid.NewGuid(), BillStates.Posted, payload with { PartyName = "Buyer Z" }, "SR/2026-27/4");

        _coordinator.Receive(link);
        await _host.AdminAsked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("SR/2026-27/4 · Buyer A is in Tally. Updating it needs the admin passcode.", _host.Admin.HandoverContext);
        Assert.Equal("Unlock and update", _host.Admin.UnlockButtonText);
        Assert.Equal("Skip this bill", _host.Admin.CloseButtonText);

        // Skip this bill (Esc).
        _host.AdminClosed.SetResult();
        await WaitForDialogAsync();

        Assert.Null(_host.Admin.HandoverContext);
        Assert.Equal("Bill not updated", _coordinator.Result.Title);
        Assert.Empty(_api.Updates);
        _coordinator.Result.CloseCommand.Execute(null);
        await _coordinator.Idle;
    }

    private async Task WaitForDialogAsync()
    {
        for (var attempt = 0; attempt < 200 && _host.ActiveDialog != ShellDialogs.HandoverResult; attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(ShellDialogs.HandoverResult, _host.ActiveDialog);
    }

    private sealed class Host : IHandoverHost
    {
        public Host(SettingsViewModel settings)
        {
            Settings = settings;
            Admin = new AdminUnlockViewModel(null!, AdminTokens);
        }

        public string? ActiveDialog { get; set; }

        public NavTab ActiveTab { get; set; } = NavTab.Invoice;

        public BillsViewModel Bills { get; } = new();

        public SettingsViewModel Settings { get; }

        public AdminUnlockViewModel Admin { get; }

        public AdminTokenStore AdminTokens { get; } = new();

        public List<BillResponse> Printed { get; } = [];

        public TaskCompletionSource AdminAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AdminClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RequestAdminUnlockAsync(CancellationToken cancellationToken)
        {
            AdminAsked.TrySetResult();
            await AdminClosed.Task;
        }

        public Task<bool> PrintSavedBillAsync(BillResponse bill)
        {
            Printed.Add(bill);
            return Task.FromResult(true);
        }

        public void OpenSavedBillsPreview(IReadOnlyList<BillResponse> bills)
        {
        }
    }
}
