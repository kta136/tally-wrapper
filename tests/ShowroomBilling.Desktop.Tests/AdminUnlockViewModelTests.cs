using ShowroomBilling.Contracts.Admin;
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

        public string? SetAdminToken { get; private set; }

        public Task SetPasscodeAsync(AdminSetPasscodeRequest request, string? adminToken = null, CancellationToken cancellationToken = default)
        {
            SetRequest = request;
            SetAdminToken = adminToken;
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
}
