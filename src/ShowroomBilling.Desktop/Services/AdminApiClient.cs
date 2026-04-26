using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Desktop.Services;

public sealed class AdminApiClient(HttpClient httpClient) : IAdminApiClient
{
    public async Task<AdminPasscodeStatusResponse> GetPasscodeStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<AdminPasscodeStatusResponse>(
            "/api/admin/passcode", cancellationToken);
        return response ?? new AdminPasscodeStatusResponse(false);
    }

    public async Task SetPasscodeAsync(AdminSetPasscodeRequest request, CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync("/api/admin/passcode", request, cancellationToken);
        if (http.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new AdminUnlockFailedException("Current passcode is invalid.");
        }
        try
        {
            await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        }
        catch (ApiException ex) when (http.StatusCode is HttpStatusCode.BadRequest)
        {
            throw new AdminUnlockFailedException(ex.Message);
        }
    }

    public async Task<AdminUnlockResponse> UnlockAsync(AdminUnlockRequest request, CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync("/api/admin/unlock", request, cancellationToken);
        if (http.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new AdminUnlockFailedException("Passcode is invalid.");
        }
        if (http.StatusCode is HttpStatusCode.Conflict)
        {
            throw new AdminUnlockFailedException("Admin passcode has not been configured yet.");
        }
        return await ApiResponseReader.ReadOrThrowAsync<AdminUnlockResponse>(http, cancellationToken);
    }

    public async Task LogoutAsync(AdminLogoutRequest request, CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync("/api/admin/logout", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
    }
}
