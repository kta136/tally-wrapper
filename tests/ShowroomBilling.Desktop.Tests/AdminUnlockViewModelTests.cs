using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Leases;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Admin;

namespace ShowroomBilling.Desktop.Tests;

public sealed class AdminUnlockViewModelTests
{
    [Fact]
    public async Task InitialPasscodeSetup_UnlocksAdminSession()
    {
        var tokenStore = new AdminTokenStore();
        var adminApi = new FakeAdminApiClient();
        var vm = new AdminUnlockViewModel(
            adminApi,
            new FakeDraftLeaseApiClient(),
            tokenStore);

        await vm.LoadStatusAsync();
        vm.NewPasscode = "246810";
        vm.ConfirmNewPasscode = "246810";

        await vm.SetOrChangePasscodeCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasscodeConfigured);
        Assert.True(tokenStore.IsUnlocked);
        Assert.Equal("admin-token", tokenStore.Current!.Token);
        Assert.Equal("246810", adminApi.SetRequest!.NewPasscode);
        Assert.Equal("246810", adminApi.UnlockRequest!.Passcode);
    }

    private sealed class FakeAdminApiClient : IAdminApiClient
    {
        public AdminSetPasscodeRequest? SetRequest { get; private set; }

        public AdminUnlockRequest? UnlockRequest { get; private set; }

        public Task<AdminPasscodeStatusResponse> GetPasscodeStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminPasscodeStatusResponse(false));

        public Task SetPasscodeAsync(AdminSetPasscodeRequest request, CancellationToken cancellationToken = default)
        {
            SetRequest = request;
            return Task.CompletedTask;
        }

        public Task<AdminUnlockResponse> UnlockAsync(AdminUnlockRequest request, CancellationToken cancellationToken = default)
        {
            UnlockRequest = request;
            return Task.FromResult(new AdminUnlockResponse(
                "admin-token",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(30),
                request.ActorLabel ?? "admin"));
        }

        public Task LogoutAsync(AdminLogoutRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeDraftLeaseApiClient : IDraftLeaseApiClient
    {
        public Task<DraftLeaseAcquireResult> AcquireAsync(DraftLeaseAcquireRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DraftLeaseResponse> RenewAsync(Guid leaseId, DraftLeaseRenewRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DraftLeaseResponse> ReleaseAsync(Guid leaseId, DraftLeaseReleaseRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DraftLeaseResponse?> GetActiveForBillAsync(Guid billId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DraftLeaseListResponse> ListActiveAsync(string adminToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DraftLeaseListResponse(Array.Empty<DraftLeaseResponse>()));

        public Task<DraftLeaseResponse> ForceReleaseAsync(
            Guid leaseId,
            DraftLeaseForceReleaseRequest request,
            string adminToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
