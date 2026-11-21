using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// The Batch Data Scheduler's calls. They have a client of their own because making a batch is one
/// request that saves every bill, and takes far longer than any other call is allowed to.
/// </summary>
public interface ISyntheticBatchApiClient
{
    Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default);

    Task<SyntheticBatchWindowResponse> GetSyntheticBatchWindowAsync(CancellationToken cancellationToken = default);

    /// <summary>How far the batch sent with this id got, or null when the server knows of none (it has restarted, or is an older build).</summary>
    Task<SyntheticBatchOutcomeResponse?> GetSyntheticBatchOutcomeAsync(Guid batchId, CancellationToken cancellationToken = default);

    /// <summary>The shop hours a batch keeps to, as saved in Settings → Admin.</summary>
    Task<BatchHoursResponse> GetBatchHoursAsync(CancellationToken cancellationToken = default);

    Task<BatchHoursResponse> SaveBatchHoursAsync(UpdateBatchHoursRequest request, string adminToken, CancellationToken cancellationToken = default);
}
