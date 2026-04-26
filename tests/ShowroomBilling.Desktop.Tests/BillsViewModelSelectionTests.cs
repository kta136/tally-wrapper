using ShowroomBilling.Contracts.Bills;
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

    private static BillSummaryItem Summary(Guid id, string invoiceNumber) => new(
        Id: id,
        State: BillStates.Pending,
        InvoiceNumber: invoiceNumber,
        PartyName: "Walk-in",
        BillDate: new DateOnly(2026, 4, 25),
        GrandTotal: 100m,
        CreatedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: DateTimeOffset.UtcNow);

    private sealed class FakeBillsApi : IBillsApiClient
    {
        public BillListResponse Response { get; set; } = new(0, 0, 50, []);

        public Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response);

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
        public Task<BillResponse> MarkPostedAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> MarkPendingAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DeleteBillResponse> DeleteAsync(Guid billId, DeleteBillRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(DeleteSelectedBillsRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
