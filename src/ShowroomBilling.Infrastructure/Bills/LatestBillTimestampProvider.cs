using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Bills;

public sealed class LatestBillTimestampProvider(ShowroomBillingDbContext dbContext)
    : ILatestBillTimestampProvider
{
    public async Task<DateTimeOffset?> GetLatestCreatedAtUtcAsync(CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Bills.AnyAsync(cancellationToken))
            return null;
        return await dbContext.Bills
            .AsNoTracking()
            .OrderByDescending(b => b.CreatedAtUtc)
            .Select(b => b.CreatedAtUtc)
            .FirstAsync(cancellationToken);
    }
}
