using System.Net;
using System.Net.Http;
using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Desktop.Services;

public sealed class AdminApiClient(HttpClient httpClient) : IAdminApiClient
{
    public Task<AdminPasscodeStatusResponse> GetPasscodeStatusAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<AdminPasscodeStatusResponse>("/api/admin/passcode", cancellationToken);

    public async Task SetPasscodeAsync(
        AdminSetPasscodeRequest request,
        string? adminToken = null,
        CancellationToken cancellationToken = default)
    {
        using var message = ApiHttpClientExtensions.CreateRequest(HttpMethod.Post, "/api/admin/passcode", request, adminToken);
        using var http = await httpClient.SendAsync(message, cancellationToken);
        if (http.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new AdminUnlockFailedException("Current passcode is invalid.");
        }
        if (http.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new AdminUnlockFailedException("Admin session has expired. Unlock admin again, then change the passcode.");
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
        using var message = ApiHttpClientExtensions.CreateRequest(HttpMethod.Post, "/api/admin/unlock", request);
        using var http = await httpClient.SendAsync(message, cancellationToken);
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
        using var message = ApiHttpClientExtensions.CreateRequest(HttpMethod.Post, "/api/admin/logout", request);
        using var http = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
    }
}
