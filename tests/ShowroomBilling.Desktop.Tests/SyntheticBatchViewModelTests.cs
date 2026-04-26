using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

namespace ShowroomBilling.Desktop.Tests;

public sealed class SyntheticBatchViewModelTests
{
    [Fact]
    public void ValidationMessage_FlagsInvalidMaxBillAmount()
    {
        var vm = new SyntheticBatchViewModel();
        vm.MaxBillAmount = 250_000; // above ₹1,99,000

        Assert.NotNull(vm.ValidationMessage);
        Assert.Contains("1,99,000", vm.ValidationMessage!);
        Assert.True(vm.HasValidation);
    }

    [Fact]
    public void ValidationMessage_FlagsReversedTimeWindow()
    {
        var vm = new SyntheticBatchViewModel();
        var now = DateTimeOffset.Now;
        vm.StartAt = now.AddHours(5);
        vm.EndAt = now;

        Assert.NotNull(vm.ValidationMessage);
        Assert.Contains("before", vm.ValidationMessage!);
    }

    [Fact]
    public async Task StartCommand_OpensAdminUnlock_WhenTokenMissing()
    {
        var tokenStore = new AdminTokenStore();
        var api = new FakeBillsApi();
        var vm = new SyntheticBatchViewModel(api, tokenStore, settings: null);
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        var start = new DateTimeOffset(DateTime.Today.AddHours(9));
        var end = start.AddHours(3);
        vm.StartAt = start;
        vm.EndAt = end;

        var promptCalled = false;
        vm.AdminUnlockHandler = _ =>
        {
            promptCalled = true;
            return Task.CompletedTask;
        };

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
        var vm = new SyntheticBatchViewModel(api, tokenStore, settings: null);
        vm.KaratOptions.Add(new SelectableKarat { Label = "22K", TallyItem = "Hallmarked 22KT", IsSelected = true });
        var start = new DateTimeOffset(DateTime.Today.AddHours(9));
        vm.StartAt = start;
        vm.EndAt = start.AddHours(3);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(api.LastRequest);
        Assert.Equal("admin-token", api.LastAdminToken);
        Assert.Equal(1, api.CallCount);
    }

    private sealed class FakeBillsApi : IBillsApiClient
    {
        public SyntheticBatchRequest? LastRequest { get; private set; }
        public string? LastAdminToken { get; private set; }
        public int CallCount { get; private set; }

        public Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            LastAdminToken = adminToken;
            return Task.FromResult(new SyntheticBatchResponse(
                BillCount: 3,
                TotalAmount: 300_000m,
                CreatedBills: Array.Empty<SyntheticBatchCreatedBill>()));
        }

        public Task<BillResponse> CreateDraftAsync(CreateBillDraftRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> UpdateDraftAsync(Guid billId, UpdateBillDraftRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> PushAsync(Guid billId, PushBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillBatchPushResponse> PushSelectedAsync(PushSelectedBillsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillBatchPushResponse> PushPendingAsync(PushPendingBillsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default) => throw new NotImplementedException();
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
    }
}
