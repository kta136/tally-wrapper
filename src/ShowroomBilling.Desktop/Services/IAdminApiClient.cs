using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Desktop.Services;

public interface IAdminApiClient
{
    Task<AdminPasscodeStatusResponse> GetPasscodeStatusAsync(CancellationToken cancellationToken = default);

    Task SetPasscodeAsync(AdminSetPasscodeRequest request, CancellationToken cancellationToken = default);

    Task<AdminUnlockResponse> UnlockAsync(AdminUnlockRequest request, CancellationToken cancellationToken = default);

    Task LogoutAsync(AdminLogoutRequest request, CancellationToken cancellationToken = default);
}

public sealed class AdminUnlockFailedException(string message) : Exception(message);
