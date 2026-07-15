using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests;

public sealed class BillsViewModelSelectionTests
{
    [Fact]
    public async Task LoadAsync_PreservesSelectedBill_WhenBillStillVisible()
    {
        var selectedId = Guid.NewGuid();
        var api = new FakeBillsApi
        {
            Response = new BillListResponse(
                Total: 2,
                Skip: 0,
                Take: 50,
                Items:
                [
                    Summary(Guid.NewGuid(), "SR/25-26/0001"),
                    Summary(selectedId, "SR/25-26/0002")
                ])
        };
        var vm = new BillsViewModel(api);

        await vm.LoadAsync();
        vm.SelectOnly(vm.Items[1]);

        api.Response = new BillListResponse(
            Total: 2,
            Skip: 0,
            Take: 50,
            Items:
            [
                Summary(selectedId, "SR/25-26/0002"),
                Summary(Guid.NewGuid(), "SR/25-26/0003")
            ]);

        await vm.LoadAsync();

        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal(selectedId, vm.SelectedBill?.Id);
        Assert.True(vm.Items.Single(x => x.Id == selectedId).IsSelected);
    }

    [Fact]
    public async Task EnsureContextSelection_RightClickInsideSelection_KeepsMultiSelection()
    {
        var api = new FakeBillsApi
        {
            Response = new BillListResponse(
                Total: 3,
                Skip: 0,
                Take: 50,
                Items:
                [
                    Summary(Guid.NewGuid(), "SR/25-26/0001"),
                    Summary(Guid.NewGuid(), "SR/25-26/0002"),
                    Summary(Guid.NewGuid(), "SR/25-26/0003")
                ])
        };
        var vm = new BillsViewModel(api);
        await vm.LoadAsync();

        vm.Items[0].IsSelected = true;
        vm.Items[1].IsSelected = true;
        vm.EnsureContextSelection(vm.Items[1]);

        Assert.Equal(2, vm.SelectedCount);
        Assert.True(vm.Items[0].IsSelected);
        Assert.True(vm.Items[1].IsSelected);
        Assert.False(vm.Items[2].IsSelected);
    }

    [Fact]
    public async Task EnsureContextSelection_RightClickOutsideSelection_SelectsOnlyThatRow()
    {
        var api = new FakeBillsApi
        {
            Response = new BillListResponse(
                Total: 3,
                Skip: 0,
                Take: 50,
                Items:
                [
                    Summary(Guid.NewGuid(), "SR/25-26/0001"),
                    Summary(Guid.NewGuid(), "SR/25-26/0002"),
                    Summary(Guid.NewGuid(), "SR/25-26/0003")
                ])
        };
        var vm = new BillsViewModel(api);
        await vm.LoadAsync();

        vm.Items[0].IsSelected = true;
        vm.Items[1].IsSelected = true;
        vm.EnsureContextSelection(vm.Items[2]);

        Assert.Equal(1, vm.SelectedCount);
        Assert.False(vm.Items[0].IsSelected);
        Assert.False(vm.Items[1].IsSelected);
        Assert.True(vm.Items[2].IsSelected);
    }

    [Fact]
    public async Task PushCommands_DisabledWhenTallyHealthIsUnhealthy()
    {
        var api = new FakeBillsApi
        {
            Response = new BillListResponse(
                Total: 1,
                Skip: 0,
                Take: 50,
                Items: [Summary(Guid.NewGuid(), "SR/25-26/0001")])
        };
        var vm = new BillsViewModel(api);
        await vm.LoadAsync();
        vm.Items[0].IsSelected = true;

        vm.ApplyTallyHealthSnapshot(Snapshot("unhealthy", reachable: false, companyOpen: false));
        var allowed = await vm.EnsureTallyPushAllowedAsync(CancellationToken.None);

        Assert.False(allowed);
        Assert.False(vm.IsTallyPushAllowed);
        Assert.False(vm.TallyPushSelectedCommand.CanExecute(null));
        Assert.Contains("Tally push blocked", vm.StatusMessage);
    }

    [Fact]
    public async Task MarkPostedRowCommand_RightClickedSelectedRow_MarksAllSelectedRowsWithOneReason()
    {
        var tokenStore = UnlockedTokenStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var api = new FakeBillsApi
        {
            Response = new BillListResponse(
                Total: 3,
                Skip: 0,
                Take: 50,
                Items:
                [
                    Summary(first, "SR/25-26/0001"),
                    Summary(second, "SR/25-26/0002"),
                    Summary(third, "SR/25-26/0003")
                ])
        };
        var promptCount = 0;
        var vm = new BillsViewModel(api, tokenStore)
        {
            ReasonPromptHandler = (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult<string?>("operator correction");
            }
        };
        await vm.LoadAsync();
        vm.Items[0].IsSelected = true;
        vm.Items[1].IsSelected = true;

        Assert.True(vm.MarkPostedRowCommand.CanExecute(vm.Items[1]));
        await vm.MarkPostedRowCommand.ExecuteAsync(vm.Items[1]);

        Assert.Equal(1, promptCount);
        Assert.Equal(new[] { first, second }, api.MarkPostedIds);
        Assert.All(api.MarkPostedReasons, reason => Assert.Equal("operator correction", reason));
    }

    [Fact]
    public async Task MarkPendingRowCommand_RightClickedSelectedRow_MarksAllSelectedRowsWithOneReason()
    {
        var tokenStore = UnlockedTokenStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var api = new FakeBillsApi
        {
            Response = new BillListResponse(
                Total: 2,
                Skip: 0,
                Take: 50,
                Items:
                [
                    Summary(first, "SR/25-26/0001", BillStates.Posted),
                    Summary(second, "SR/25-26/0002", BillStates.Failed)
                ])
        };
        var vm = new BillsViewModel(api, tokenStore)
        {
            ReasonPromptHandler = (_, _, _) => Task.FromResult<string?>("needs retry")
        };
        await vm.LoadAsync();
        vm.Items[0].IsSelected = true;
        vm.Items[1].IsSelected = true;

        Assert.True(vm.MarkPendingRowCommand.CanExecute(vm.Items[1]));
        await vm.MarkPendingRowCommand.ExecuteAsync(vm.Items[1]);

        Assert.Equal(new[] { first, second }, api.MarkPendingIds);
        Assert.All(api.MarkPendingReasons, reason => Assert.Equal("needs retry", reason));
    }

    [Fact]
    public async Task MarkPostedRowCommand_DisabledForMixedInvalidSelection()
    {
        var tokenStore = UnlockedTokenStore();
        var api = new FakeBillsApi
        {
            Response = new BillListResponse(
                Total: 2,
                Skip: 0,
                Take: 50,
                Items:
                [
                    Summary(Guid.NewGuid(), "SR/25-26/0001", BillStates.Pending),
                    Summary(Guid.NewGuid(), "SR/25-26/0002", BillStates.Posted)
                ])
        };
        var vm = new BillsViewModel(api, tokenStore);
        await vm.LoadAsync();
        vm.Items[0].IsSelected = true;
        vm.Items[1].IsSelected = true;

        Assert.False(vm.MarkPostedRowCommand.CanExecute(vm.Items[0]));
    }

    [Fact]
    public async Task LoadAsync_ForwardsSearchQueryToApi()
    {
        var api = new FakeBillsApi
        {
            Response = new BillListResponse(
                Total: 0,
                Skip: 0,
                Take: 50,
                Items: [])
        };
        var vm = new BillsViewModel(api)
        {
            SearchQuery = "Walk-in"
        };

        await vm.LoadAsync();

        Assert.Equal("Walk-in", api.LastFilter?.Search);
    }

    [Fact]
    public async Task HideFullyPostedDays_ReportsVisibleAndHiddenCounts()
    {
        var api = new FakeBillsApi
        {
            Response = new BillListResponse(
                Total: 2,
                Skip: 0,
                Take: 50,
                Items:
                [
                    Summary(Guid.NewGuid(), "SR/25-26/0001", BillStates.Posted),
                    Summary(Guid.NewGuid(), "SR/25-26/0002", BillStates.Posted)
                ])
        };
        var vm = new BillsViewModel(api);

        await vm.LoadAsync();

        Assert.Equal(0, vm.Showing);
        Assert.Equal(2, vm.HiddenByPostedDaysCount);
        Assert.True(vm.IsFilteredEmpty);

        vm.ShowPostedDaysCommand.Execute(null);

        Assert.Equal(2, vm.Showing);
        Assert.Equal(0, vm.HiddenByPostedDaysCount);
        Assert.False(vm.IsFilteredEmpty);
    }

    private static BillSummaryItem Summary(
        Guid id,
        string invoiceNumber,
        string state = BillStates.Pending) => new(
        Id: id,
        State: state,
        InvoiceNumber: invoiceNumber,
        PartyName: "Walk-in",
        BillDate: new DateOnly(2026, 4, 25),
        GrandTotal: 100m,
        CreatedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static SystemHealthSnapshot Snapshot(string status, bool reachable, bool companyOpen) =>
        new(
            ApiReachable: true,
            Masters: null,
            TallyCompany: new TallyCompanyHealthResponse(
                Status: status,
                TallyReachable: reachable,
                ActiveCompanyName: "Test Company",
                ActiveCompanyOpen: companyOpen,
                CompanyCount: reachable ? 1 : 0,
                CheckedAtUtc: DateTimeOffset.UtcNow,
                Message: reachable ? "Wrong company open." : "Tally is unreachable."),
            Runtime: null);

    private static AdminTokenStore UnlockedTokenStore()
    {
        var store = new AdminTokenStore();
        store.Set(new AdminUnlockResponse(
            Token: "admin-token",
            IssuedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(30),
            ActorLabel: "test"));
        return store;
    }

    private sealed class FakeBillsApi : IBillsApiClient
    {
        public BillListResponse Response { get; set; } = new(0, 0, 50, []);
        public BillSearchFilter? LastFilter { get; private set; }
        public List<Guid> MarkPostedIds { get; } = [];
        public List<string?> MarkPostedReasons { get; } = [];
        public List<Guid> MarkPendingIds { get; } = [];
        public List<string?> MarkPendingReasons { get; } = [];

        public Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default)
        {
            LastFilter = filter;
            return Task.FromResult(Response);
        }

        public Task<BillResponse> CreateDraftAsync(CreateBillDraftRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> UpdateDraftAsync(Guid billId, UpdateBillDraftRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> PushAsync(Guid billId, PushBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillBatchPushResponse> PushSelectedAsync(PushSelectedBillsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillBatchPushResponse> PushPendingAsync(PushPendingBillsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillBatchGetResponse> GetManyAsync(BillBatchGetRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillAuditResponse?> GetAuditAsync(Guid billId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillPostingStatusResponse?> GetPostingStatusAsync(Guid billId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillPostingStatusResponse> RetryAsync(Guid billId, RetryBillPostingRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillPostingStatusResponse> RepostAsync(Guid billId, RepostBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> VoidAsync(Guid billId, VoidBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> ReviseAsync(Guid billId, ReviseBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(Guid billId, ChangeBillNumberRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> MarkPostedAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default)
        {
            MarkPostedIds.Add(billId);
            MarkPostedReasons.Add(request.Reason);
            return Task.FromResult(ResponseFor(billId, BillStates.Posted));
        }

        public Task<BillResponse> MarkPendingAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default)
        {
            MarkPendingIds.Add(billId);
            MarkPendingReasons.Add(request.Reason);
            return Task.FromResult(ResponseFor(billId, BillStates.Pending));
        }
        public Task<DeleteBillResponse> DeleteAsync(Guid billId, DeleteBillRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(DeleteSelectedBillsRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        private static BillResponse ResponseFor(Guid billId, string state) => new(
            Id: billId,
            ShowroomId: Guid.Empty,
            CounterId: null,
            BillType: "sales",
            State: state,
            InvoiceNumber: null,
            FiscalYear: null,
            SupersededByBillId: null,
            CurrentRevisionId: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            VoidedAtUtc: null,
            CurrentRevision: null);
    }
}
