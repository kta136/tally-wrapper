using ShowroomBilling.Contracts.Health;

namespace ShowroomBilling.Application.Health;

public interface ITallyCompanyHealthService
{
    Task<TallyCompanyHealthResponse> CheckAsync(CancellationToken cancellationToken = default);
}
