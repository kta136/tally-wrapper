using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests.TestSupport;

internal static class BillTestData
{
    internal static BillLifecycleWorkflow Lifecycle(ShowroomBillingDbContext db) =>
        new(db, new NumberingService(db), new BillAuditStore(db));

    internal static BillPayloadDto SamplePayload(string party, decimal grandTotal, DateOnly? billDate = null) =>
        new(
            PartyName: party,
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: billDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Lines:
            [
                new BillLineItemDto("22K Gold Ring", "7113", 10m, "grams", grandTotal / 10m, grandTotal, "22K", null)
            ],
            Totals: new BillTotalsDto(grandTotal, 0m, 0m, 0m, grandTotal),
            Notes: null);
}
