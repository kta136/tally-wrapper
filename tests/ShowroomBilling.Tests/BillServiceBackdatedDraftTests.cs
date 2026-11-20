using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

public sealed class BillServiceBackdatedDraftTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task CreateBackdatedDraftAsync_UsesOverrideForBillAndAudit()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var backdate = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var response = await service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Backdated Party", 12_345m)),
            backdate);

        Assert.Equal(BillStates.Pending, response.State);
        Assert.Equal(backdate, response.CreatedAtUtc);
        Assert.Equal(backdate, response.UpdatedAtUtc);
        Assert.Equal(backdate, response.CurrentRevision!.CreatedAtUtc);

        var audit = await db.AuditEvents
            .Where(a => a.EntityId == response.Id.ToString() && a.EventType == "bill.pending.created")
            .FirstAsync();
        Assert.Equal(backdate, audit.CreatedAtUtc);
    }

    [Fact]
    public async Task CreateBackdatedDraftAsync_IsRefusedWhenABillMadeLaterIsAlreadySaved()
    {
        // A counter saved a bill while a batch was being saved: a batch bill dated before it would
        // take the next number and so be numbered after it.
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var counterBill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Counter Party", 100m)));

        var refused = await Assert.ThrowsAsync<BillStateConflictException>(() => service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Batch Party", 60_000m)),
            counterBill.CreatedAtUtc.AddMinutes(-5)));

        Assert.StartsWith("A bill made after", refused.Message);
        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.Bills.CountAsync());

        // Nothing was used up: the next bill takes the number after the counter's bill.
        db.ChangeTracker.Clear();
        var next = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Next Party", 100m)));
        Assert.NotEqual(counterBill.InvoiceNumber, next.InvoiceNumber);
        var cores = await verify.Bills.AsNoTracking().OrderBy(b => b.InvoiceCore).Select(b => b.InvoiceCore).ToListAsync();
        Assert.Equal(cores[0] + 1, cores[1]);
    }

    [Fact]
    public async Task CreateBackdatedDraftAsync_GoesAfterTheNewestBill()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var earlier = DateTimeOffset.UtcNow.AddHours(-2);
        await service.CreateBackdatedDraftAsync(new CreateBillDraftRequest(SamplePayload("First Party", 60_000m)), earlier);

        var later = await service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Second Party", 60_000m)), earlier.AddMinutes(10));

        Assert.Equal(earlier.AddMinutes(10), later.CreatedAtUtc);
    }

    [Fact]
    public async Task CreateDraftAsync_DefaultPathStillUsesUtcNow()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var before = DateTimeOffset.UtcNow;

        var response = await service.CreateDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Default Path", 100m)));

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
