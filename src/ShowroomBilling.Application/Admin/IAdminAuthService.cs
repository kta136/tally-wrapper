using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Application.Admin;

public interface IAdminAuthService
{
    Task<AdminPasscodeStatusResponse> GetPasscodeStatusAsync(CancellationToken cancellationToken = default);

    Task SetPasscodeAsync(AdminSetPasscodeRequest request, CancellationToken cancellationToken = default);

    Task<AdminUnlockResponse> UnlockAsync(AdminUnlockRequest request, CancellationToken cancellationToken = default);

    Task<AdminSessionInfoResponse?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);

    Task LogoutAsync(AdminLogoutRequest request, CancellationToken cancellationToken = default);
}

public sealed class AdminPasscodeNotConfiguredException() : Exception("Admin passcode has not been configured.");

public sealed class AdminPasscodeInvalidException() : Exception("Admin passcode is invalid.");
