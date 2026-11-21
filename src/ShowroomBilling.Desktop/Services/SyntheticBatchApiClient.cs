using System.Net.Http;
using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.Services;

public sealed class SyntheticBatchApiClient(HttpClient httpClient) : ISyntheticBatchApiClient
{
    /// <summary>
    /// How long making a batch may take. Each bill is a transaction of its own against a database
    /// across the internet, so a large batch runs for minutes; the usual 100 seconds cut one off
    /// part-way. The client is registered with this limit.
    /// </summary>
    public static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The two reads are small and are not left to wait as long as a batch.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    public Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<SyntheticBatchResponse>("/api/bills/synthetic-batch", request, cancellationToken, adminToken);

    public async Task<SyntheticBatchWindowResponse> GetSyntheticBatchWindowAsync(CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ReadTimeout);
        return await httpClient.GetApiAsync<SyntheticBatchWindowResponse>("/api/bills/synthetic-batch/window", limit.Token);
    }

    public async Task<SyntheticBatchOutcomeResponse?> GetSyntheticBatchOutcomeAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ReadTimeout);
        return await httpClient.GetApiOrNullAsync<SyntheticBatchOutcomeResponse>($"/api/bills/synthetic-batch/{batchId}", limit.Token);
    }

    public async Task<BatchHoursResponse> GetBatchHoursAsync(CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ReadTimeout);
        return await httpClient.GetApiAsync<BatchHoursResponse>("/api/settings/batch-hours", limit.Token);
    }

    public async Task<BatchHoursResponse> SaveBatchHoursAsync(UpdateBatchHoursRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ReadTimeout);
        return await httpClient.PutApiAsync<BatchHoursResponse>("/api/settings/batch-hours", request, limit.Token, adminToken);
    }
}
