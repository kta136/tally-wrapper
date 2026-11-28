using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Tests.Postgres;
using static ShowroomBilling.Tests.TestSupport.BillTestData;

namespace ShowroomBilling.Tests;

public sealed class BillServiceBackdatedDraftTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task CreateBackdatedDraftAsync_UsesOverrideForBillAndAudit()
    {
        await using var db = CreateDbContext();
        var service = Service(db, new FakePoster());

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
        var service = Service(db, new FakePoster());
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
        var service = Service(db, new FakePoster());
        var earlier = DateTimeOffset.UtcNow.AddHours(-2);
        await service.CreateBackdatedDraftAsync(new CreateBillDraftRequest(SamplePayload("First Party", 60_000m)), earlier);

        var later = await service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Second Party", 60_000m)), earlier.AddMinutes(10));

        Assert.Equal(earlier.AddMinutes(10), later.CreatedAtUtc);
    }

    [Fact]
    public async Task CreateBackdatedDraftAsync_IsNotStoppedByABillOfAnEarlierDateSavedLater()
    {
        // A bill saved today but dated yesterday counts as the end of yesterday: a bill dated today
        // may go before the time it was saved, and is numbered after it.
        await using var db = CreateDbContext();
        var service = Service(db, new FakePoster());
        var day = ADayBack();
        var dayStart = ShowroomBilling.Contracts.IndiaTime.StartOf(day);
        var dayBefore = await service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Counter Party", 100m, day.AddDays(-1))), dayStart.AddHours(13).ToUniversalTime());

        var batch = await service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Batch Party", 60_000m, day)), dayStart.AddHours(10).ToUniversalTime());

        await using var verify = CreateDbContext();
        var cores = await verify.Bills.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.InvoiceCore);
        Assert.Equal(cores[dayBefore.Id] + 1, cores[batch.Id]);
    }

    [Fact]
    public async Task CreateBackdatedDraftAsync_IsRefusedByABillOfALaterDate_OrOfItsDateInItsMinute()
    {
        await using var db = CreateDbContext();
        var service = Service(db, new FakePoster());
        var day = ADayBack();
        var at = ShowroomBilling.Contracts.IndiaTime.StartOf(day).AddHours(10).ToUniversalTime();
        await service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Ahead Party", 100m, day.AddDays(1))), at.AddHours(-1));

        // Dated a day ahead, though saved an hour earlier.
        var later = await Assert.ThrowsAsync<BillStateConflictException>(() => service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Batch Party", 60_000m, day)), at));
        Assert.StartsWith("A bill made after", later.Message);

        // Of the same date, in the same minute.
        db.ChangeTracker.Clear();
        await service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Same Minute", 100m, day.AddDays(1))), at.AddDays(1).AddSeconds(10));
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<BillStateConflictException>(() => service.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Batch Party", 60_000m, day.AddDays(1))), at.AddDays(1).AddSeconds(40)));
    }

    [Fact]
    public async Task CreateDraftAsync_DefaultPathStillUsesUtcNow()
    {
        await using var db = CreateDbContext();
        var service = Service(db, new FakePoster());
        var before = DateTimeOffset.UtcNow;

        var response = await service.CreateDraftAsync(
            new CreateBillDraftRequest(SamplePayload("Default Path", 100m)));

        var after = DateTimeOffset.UtcNow;
        Assert.InRange(response.CreatedAtUtc, before, after);
    }

    /// <summary>
    /// Ten days back, inside the dates a bill may carry, and never 1 April, so the day before is in
    /// the same fiscal year and its bill shares the numbering.
    /// </summary>
    private static DateOnly ADayBack()
    {
        var day = ShowroomBilling.Contracts.IndiaTime.Today().AddDays(-10);
        return day is { Month: 4, Day: 1 } ? day.AddDays(-1) : day;
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
