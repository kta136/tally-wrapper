using ShowroomBilling.Contracts.Tally;

namespace ShowroomBilling.Application.Tally;

/// <summary>
/// Synchronous "post this bill to Tally now" abstraction. Invoked by BillService.PushAsync
/// when the operator clicks Push. Returns once Tally has answered (or the call has failed).
/// There is no retry, queue, or background worker — manual-only, click-and-wait.
/// </summary>
public interface ITallyPoster
{
    Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default);
}
