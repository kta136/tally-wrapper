using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

/// <summary>Bill lookups shared by the bill workflows.</summary>
internal static class BillQueries
{
    /// <summary>Loads a bill for update, or throws <see cref="BillNotFoundException"/>.</summary>
    internal static async Task<BillEntity> LoadTrackedBillAsync(
        this ShowroomBillingDbContext dbContext,
        Guid billId,
        CancellationToken cancellationToken)
    {
        var bill = await dbContext.Bills.FirstOrDefaultAsync(x => x.Id == billId, cancellationToken);
        return bill ?? throw new BillNotFoundException(billId);
    }
}
