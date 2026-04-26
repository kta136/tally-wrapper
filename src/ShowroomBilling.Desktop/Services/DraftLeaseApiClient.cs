using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Leases;

namespace ShowroomBilling.Desktop.Services;

public sealed class DraftLeaseApiClient(HttpClient httpClient) : IDraftLeaseApiClient
{
    public async Task<DraftLeaseAcquireResult> AcquireAsync(
        DraftLeaseAcquireRequest request,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync("/api/leases/drafts/acquire", request, cancellationToken);
        if (http.StatusCode is HttpStatusCode.Conflict)
        {
            var conflict = await http.Content.ReadFromJsonAsync<DraftLeaseConflictResponse>(cancellationToken: cancellationToken);
            throw new DraftLeaseConflictClientException(
                conflict?.Error ?? "Draft is locked by another counter.",
                conflict?.ExistingLease ?? throw new InvalidOperationException("Conflict response missing existing lease."));
        }
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<DraftLeaseAcquireResult>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("Empty acquire response.");
    }

    public async Task<DraftLeaseResponse> RenewAsync(
        Guid leaseId,
        DraftLeaseRenewRequest request,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync($"/api/leases/drafts/{leaseId}/renew", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<DraftLeaseResponse>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("Empty renew response.");
    }

    public async Task<DraftLeaseResponse> ReleaseAsync(
        Guid leaseId,
        DraftLeaseReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync($"/api/leases/drafts/{leaseId}/release", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<DraftLeaseResponse>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("Empty release response.");
    }

    public async Task<DraftLeaseResponse?> GetActiveForBillAsync(
        Guid billId,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.GetAsync($"/api/leases/drafts/bill/{billId}", cancellationToken);
        if (http.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return null;
        }
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        return await http.Content.ReadFromJsonAsync<DraftLeaseResponse>(cancellationToken: cancellationToken);
    }

    public async Task<DraftLeaseListResponse> ListActiveAsync(
        string adminToken,
        CancellationToken cancellationToken = default)
    {
        using var http = new HttpRequestMessage(HttpMethod.Get, "/api/leases/drafts/active");
        http.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        var response = await httpClient.SendAsync(http, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<DraftLeaseListResponse>(cancellationToken: cancellationToken);
        return body ?? new DraftLeaseListResponse(Array.Empty<DraftLeaseResponse>());
    }

    public async Task<DraftLeaseResponse> ForceReleaseAsync(
        Guid leaseId,
        DraftLeaseForceReleaseRequest request,
        string adminToken,
        CancellationToken cancellationToken = default)
    {
        using var http = new HttpRequestMessage(HttpMethod.Post, $"/api/leases/drafts/{leaseId}/force-release")
        {
            Content = JsonContent.Create(request)
        };
        http.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        var response = await httpClient.SendAsync(http, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<DraftLeaseResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException("Empty force-release response.");
    }
}
