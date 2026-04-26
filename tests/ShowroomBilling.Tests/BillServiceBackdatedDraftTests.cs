using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests;

public sealed class BillServiceBackdatedDraftTests
{
    [Fact]
    public async Task CreateBackdatedDraftAsync_UsesOverrideForBillAndAudit()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var backdate = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var response = await service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(null, SamplePayload("Backdated Party", 12_345m)),
            backdate);

        Assert.Equal(IBillService.StatePending, response.State);
        Assert.Equal(backdate, response.CreatedAtUtc);
        Assert.Equal(backdate, response.UpdatedAtUtc);
        Assert.Equal(backdate, response.CurrentRevision!.CreatedAtUtc);

        var audit = await db.AuditEvents
            .Where(a => a.EntityId == response.Id.ToString() && a.EventType == "bill.pending.created")
            .FirstAsync();
        Assert.Equal(backdate, audit.CreatedAtUtc);
    }

    [Fact]
    public async Task CreateDraftAsync_DefaultPathStillUsesUtcNow()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var before = DateTimeOffset.UtcNow;

        var response = await service.CreateDraftAsync(
            new CreateBillDraftRequest(null, SamplePayload("Default Path", 100m)));

        var after = DateTimeOffset.UtcNow;
        Assert.InRange(response.CreatedAtUtc, before, after);
    }

    private static BillPayloadDto SamplePayload(string party, decimal grandTotal) =>
        new(
            PartyName: party,
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Lines:
            [
                new BillLineItemDto("22K Gold Ring", "7113", 10m, "grams", grandTotal / 10m, grandTotal, "22K", null)
            ],
            Totals: new BillTotalsDto(grandTotal, 0m, 0m, 0m, grandTotal),
            Notes: null);

    private static BillService BuildService(ShowroomBillingDbContext db)
    {
        var numbering = new NumberingService(db);
        return new BillService(db, numbering, new FakePoster());
    }

    private static ShowroomBillingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShowroomBillingDbContext(options);
    }

    private sealed class FakePoster : ITallyPoster
    {
        public Task<ShowroomBilling.Contracts.Tally.TallyPostResponse> PostAsync(
            ShowroomBilling.Contracts.Tally.TallyPostRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ShowroomBilling.Contracts.Tally.TallyPostResponse(
                ShowroomBilling.Contracts.Tally.TallyPostOutcome.Posted,
                "VCH",
                null, null, "v1", null, null));
    }
}
