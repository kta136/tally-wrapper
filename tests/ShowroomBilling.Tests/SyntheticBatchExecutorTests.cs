using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Settings;
using ShowroomBilling.Tests.Postgres;
using ShowroomBilling.Tests.TestSupport;

namespace ShowroomBilling.Tests;

/// <summary>
/// The Batch Data Scheduler end to end: masters from settings, a plan, and
/// each planned bill saved as a backdated draft.
/// </summary>
public sealed class SyntheticBatchExecutorTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task A_batch_is_refused_before_the_newest_bill_and_saved_after_it()
    {
        await using var db = CreateDbContext();
        var bills = BillTestData.Lifecycle(db);
        var windowStart = DateTimeOffset.UtcNow.AddHours(-6);
        var existingAt = windowStart.AddHours(1);
        var existing = await bills.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(new BillPayloadDto(
                PartyName: "Walk-in",
                PartyGstin: null,
                PartyPhone: null,
                PartyAddress: null,
                BillDate: IndiaTime.DateOf(existingAt),
                Lines: [new BillLineItemDto("22K Gold Ring", "7113", 10m, "grams", 100m, 1_000m, "22K", null)],
                Totals: new BillTotalsDto(1_000m, 0m, 0m, 0m, 1_000m),
                Notes: null)),
            existingAt);
        var executor = new SyntheticBatchExecutor(
            bills, new MastersOnlySettings(), db, NullLogger<SyntheticBatchExecutor>.Instance);

        var request = new SyntheticBatchRequest(
            TotalAmount: 400_000,
            MaxBillAmount: 199_000,
            Rate24Kt: 7200m,
            PaymentMode: "Cash",
            MinItemsPerBill: 1,
            MaxItemsPerBill: 2,
            StartAtUtc: windowStart,
            EndAtUtc: windowStart.AddHours(4),
            SelectedKaratLabels: ["22K"]);

        // A window that opens before the newest bill would put bills out of order.
        var refused = await Assert.ThrowsAsync<RequestValidationException>(() => executor.ExecuteAsync(request));
        Assert.StartsWith("Start time cannot be before ", refused.Message);
        Assert.Equal(1, await db.Bills.CountAsync());

        // So would one that ends later than now.
        var future = await Assert.ThrowsAsync<RequestValidationException>(() =>
            executor.ExecuteAsync(request with { StartAtUtc = existingAt.AddMinutes(1), EndAtUtc = DateTimeOffset.UtcNow.AddMinutes(5) }));
        Assert.Equal("End time cannot be later than now.", future.Message);
        Assert.Equal(1, await db.Bills.CountAsync());

        var response = await executor.ExecuteAsync(request with { StartAtUtc = existingAt.AddMinutes(1) });

        await using var verify = CreateDbContext();
        var stored = await verify.Bills.AsNoTracking().Where(b => b.Id != existing.Id).ToListAsync();
        // Two, not three: what is left under 50,000 after the last bill is no bill of its
        // own, so 400,000 can come out as two bills. Asking for three would fail about one
        // run in 19. SyntheticBillPlannerTests holds the rule itself.
        Assert.True(response.BillCount >= 2, $"400,000 in bills of at most 199,000 is at least two; got {response.BillCount}.");
        Assert.Equal(response.BillCount, stored.Count);
        Assert.All(stored, bill =>
        {
            Assert.Equal(BillStates.Pending, bill.State);
            Assert.NotNull(bill.InvoiceCore);
            Assert.True(bill.CreatedAtUtc > existingAt, $"{bill.InvoiceNumber} was dated {bill.CreatedAtUtc:o}, not after {existingAt:o}.");
            Assert.True(bill.CreatedAtUtc <= DateTimeOffset.UtcNow, $"{bill.InvoiceNumber} was dated {bill.CreatedAtUtc:o}, in the future.");
        });
        Assert.Equal(stored.Count, stored.Select(b => b.InvoiceNumber).Distinct().Count());
    }

    [Fact]
    public async Task A_bill_dated_an_earlier_day_than_it_was_saved_lets_a_batch_start_on_the_day_it_was_saved()
    {
        // Owner, 21 November 2026: a bill saved today but dated yesterday lets a batch start today,
        // also before the time that bill was saved.
        var day = ADayBack();
        var dayStart = IndiaTime.StartOf(day);
        await using var db = CreateDbContext();
        var bills = BillTestData.Lifecycle(db);
        var dayBefore = await bills.CreateBackdatedDraftAsync(
            new CreateBillDraftRequest(Payload(day.AddDays(-1))), dayStart.AddHours(13).AddMinutes(53).AddSeconds(26).ToUniversalTime());
        var executor = new SyntheticBatchExecutor(
            bills, new MastersOnlySettings(), db, NullLogger<SyntheticBatchExecutor>.Instance, new FakeTimeProvider(dayStart.AddHours(15).ToUniversalTime()));

        Assert.Equal(dayStart, await executor.EarliestBillTimeAsync());

        var request = new SyntheticBatchRequest(
            TotalAmount: 400_000,
            MaxBillAmount: 199_000,
            Rate24Kt: 7200m,
            PaymentMode: "Cash",
            MinItemsPerBill: 1,
            MaxItemsPerBill: 2,
            StartAtUtc: dayStart.AddMinutes(-2),
            EndAtUtc: dayStart.AddHours(12),
            SelectedKaratLabels: ["22K"]);
        var refused = await Assert.ThrowsAsync<RequestValidationException>(() => executor.ExecuteAsync(request));
        Assert.Equal(
            $"Start time cannot be before {dayStart.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}, 00:00, the first time after the last bill.",
            refused.Message);

        var response = await executor.ExecuteAsync(request with { StartAtUtc = dayStart.AddHours(10) });

        await using var verify = CreateDbContext();
        var stored = await (from bill in verify.Bills.AsNoTracking()
                            join rev in verify.BillRevisions.AsNoTracking() on bill.CurrentRevisionId equals rev.Id
                            where bill.Id != dayBefore.Id
                            select new { bill.CreatedAtUtc, bill.InvoiceCore, rev.BillDate }).ToListAsync();
        var dayBeforeCore = await verify.Bills.Where(b => b.Id == dayBefore.Id).Select(b => b.InvoiceCore).SingleAsync();
        Assert.Equal(response.BillCount, stored.Count);
        Assert.All(stored, bill =>
        {
            Assert.InRange(bill.CreatedAtUtc, dayStart.AddHours(10), dayStart.AddHours(12));
            Assert.Equal(day, bill.BillDate);
            Assert.True(bill.InvoiceCore > dayBeforeCore, "A batch bill is numbered after the bill already stored.");
        });
    }

    [Fact]
    public async Task A_batch_made_whole_gives_its_bill_numbers_and_is_kept_as_finished()
    {
        await using var db = CreateDbContext();
        var bills = BillTestData.Lifecycle(db);
        var outcomes = new SyntheticBatchOutcomes();
        var executor = new SyntheticBatchExecutor(bills, new MastersOnlySettings(), db, NullLogger<SyntheticBatchExecutor>.Instance, AfterTheWindow(), outcomes);
        var request = ThreeBillsOrMore() with { BatchId = Guid.NewGuid() };

        var response = await executor.ExecuteAsync(request);

        await using var verify = CreateDbContext();
        var stored = await verify.Bills.AsNoTracking().OrderBy(b => b.InvoiceCore).ToListAsync();
        Assert.True(response.BillCount >= 3);
        Assert.Equal(response.BillCount, stored.Count);
        Assert.Equal(stored[0].InvoiceNumber, response.FirstInvoiceNumber);
        Assert.Equal(stored[^1].InvoiceNumber, response.LastInvoiceNumber);
        // Numbered in the order they are dated.
        Assert.Equal(stored.Select(b => b.InvoiceNumber), stored.OrderBy(b => b.CreatedAtUtc).Select(b => b.InvoiceNumber));
        Assert.Equal(
            new SyntheticBatchOutcomeResponse(true, response.BillCount, response.BillCount, response.TotalAmount, response.FirstInvoiceNumber, response.LastInvoiceNumber),
            outcomes.Find(request.BatchId!.Value));

        // The same batch sent again is refused, and makes no bill.
        var again = await Assert.ThrowsAsync<BillStateConflictException>(() => executor.ExecuteAsync(request));
        Assert.StartsWith("This batch has already been sent to the server.", again.Message);
        Assert.Equal(stored.Count, await verify.Bills.CountAsync());
    }

    [Fact]
    public async Task A_cancelled_batch_keeps_the_bills_it_saved_and_says_how_many()
    {
        await using var db = CreateDbContext();
        var bills = BillTestData.Lifecycle(db);
        var outcomes = new SyntheticBatchOutcomes();
        var executor = new SyntheticBatchExecutor(bills, new MastersOnlySettings(), db, NullLogger<SyntheticBatchExecutor>.Instance, AfterTheWindow(), outcomes);
        var request = ThreeBillsOrMore() with { BatchId = Guid.NewGuid() };

        // The run is cancelled while the second bill is being saved, before its commit: that
        // bill is still saved whole, and the batch stops before the third.
        using var cancel = new CancellationTokenSource();
        var saves = 0;
        db.SavedChanges += (_, _) =>
        {
            if (++saves == 2) cancel.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(request, cancel.Token));

        await using var verify = CreateDbContext();
        var stored = await verify.Bills.AsNoTracking().OrderBy(b => b.InvoiceCore).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(2, await verify.BillRevisions.CountAsync());
        var outcome = outcomes.Find(request.BatchId!.Value)!;
        Assert.True(outcome.Finished);
        Assert.False(outcome.LastSaveInDoubt);
        Assert.True(outcome.PlannedBillCount >= 3);
        Assert.Equal(2, outcome.BillCount);
        Assert.Equal((stored[0].InvoiceNumber, stored[1].InvoiceNumber), (outcome.FirstInvoiceNumber, outcome.LastInvoiceNumber));

        // The numbering goes on from the bills that were saved. The next batch has the two hours
        // after the first one's window to itself: room for every bill it can make.
        var next = await new SyntheticBatchExecutor(bills, new MastersOnlySettings(), db, NullLogger<SyntheticBatchExecutor>.Instance, AfterTheWindow())
            .ExecuteAsync(request with { BatchId = null, StartAtUtc = request.EndAtUtc.AddMinutes(1), EndAtUtc = request.EndAtUtc.AddHours(2) });
        var after = await verify.Bills.AsNoTracking().OrderBy(b => b.InvoiceCore).ToListAsync();
        Assert.Equal(2 + next.BillCount, after.Count);
        Assert.Equal(Enumerable.Range(0, after.Count).Select(i => stored[0].InvoiceCore + i), after.Select(b => b.InvoiceCore));
    }

    [Fact]
    public async Task A_batch_keeps_to_the_shop_hours_that_were_saved()
    {
        // The hours are saved as Settings → Admin saves them, and the batch reads them from there:
        // 11:00 to 12:00, shut on the weekday before the day in question, bills 5 minutes apart.
        var day = ADayBack();
        var dayStart = IndiaTime.StartOf(day);
        await using var db = CreateDbContext();
        var cloud = new CloudSettingsService(db);
        var seeded = await cloud.GetEffectiveSettingsAsync();
        await cloud.SaveEffectiveSettingsAsync(new UpdateEffectiveSettingsRequest(
            seeded.Settings with { Masters = new MasterDataSettingsDto(MastersOnlySettings.Items, MastersOnlySettings.Karats) }));
        var hours = new BatchHoursSettings(new TimeOnly(11, 0), new TimeOnly(12, 0), day.AddDays(-1).DayOfWeek, GapMinutes: 5);
        await cloud.UpdateBatchHoursAsync(new UpdateBatchHoursRequest(hours));
        var bills = BillTestData.Lifecycle(db);
        var executor = new SyntheticBatchExecutor(
            bills, cloud, db, NullLogger<SyntheticBatchExecutor>.Instance, new FakeTimeProvider(dayStart.AddDays(1).ToUniversalTime()));

        // The form is told the hours with the earliest time.
        Assert.Equal(new SyntheticBatchWindowResponse(null, hours), await executor.GetWindowAsync());

        // Two whole days: the first is the closed day, so every bill is of the second, in its one open hour.
        var request = new SyntheticBatchRequest(
            TotalAmount: 600_000,
            MaxBillAmount: 199_000,
            Rate24Kt: 7200m,
            PaymentMode: "Cash",
            MinItemsPerBill: 1,
            MaxItemsPerBill: 2,
            StartAtUtc: dayStart.AddDays(-1).ToUniversalTime(),
            EndAtUtc: dayStart.AddHours(23).ToUniversalTime(),
            SelectedKaratLabels: ["22K"]);

        var response = await executor.ExecuteAsync(request);

        await using var verify = CreateDbContext();
        var stored = await (from bill in verify.Bills.AsNoTracking()
                            join rev in verify.BillRevisions.AsNoTracking() on bill.CurrentRevisionId equals rev.Id
                            orderby bill.CreatedAtUtc
                            select new { bill.CreatedAtUtc, rev.BillDate }).ToListAsync();
        Assert.True(response.BillCount >= 3);
        Assert.Equal(response.BillCount, stored.Count);
        Assert.All(stored, bill =>
        {
            Assert.InRange(bill.CreatedAtUtc, dayStart.AddHours(11), dayStart.AddHours(12));
            Assert.Equal(day, bill.BillDate);
        });
        // 61 open minutes hold thirteen bills 5 minutes apart, and 6,00,000 is twelve bills at most.
        for (var i = 1; i < stored.Count; i++)
            Assert.True(stored[i].CreatedAtUtc - stored[i - 1].CreatedAtUtc >= TimeSpan.FromMinutes(5));

        // A window with no open minute is refused, and makes no bill.
        var refused = await Assert.ThrowsAsync<RequestValidationException>(() => executor.ExecuteAsync(
            request with { StartAtUtc = dayStart.AddHours(13).ToUniversalTime(), EndAtUtc = dayStart.AddHours(20).ToUniversalTime() }));
        Assert.StartsWith("The shop is not open at any minute of the selected times (shop hours: 11:00 to 12:00, closed on ", refused.Message);
        Assert.Equal(stored.Count, await verify.Bills.CountAsync());
    }

    [Fact]
    public async Task A_batch_approved_by_other_shop_hours_than_the_saved_ones_is_refused()
    {
        await using var db = CreateDbContext();
        var bills = BillTestData.Lifecycle(db);
        // These settings hold no shop hours: any time of day.
        var executor = new SyntheticBatchExecutor(bills, new MastersOnlySettings(), db, NullLogger<SyntheticBatchExecutor>.Instance, AfterTheWindow());

        // The form showed hours that another counter has since taken away.
        var shown = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 0), DayOfWeek.Sunday, 5);
        var refused = await Assert.ThrowsAsync<RequestValidationException>(() =>
            executor.ExecuteAsync(ThreeBillsOrMore() with { ExpectedHours = shown }));

        Assert.Equal("The shop hours were changed while this batch was being set up. Look at them again, then start the batch again.", refused.Message);
        Assert.Equal(0, await db.Bills.CountAsync());

        // Approved by the hours that are saved, it is made.
        var made = await executor.ExecuteAsync(ThreeBillsOrMore() with { ExpectedHours = new BatchHoursSettings() });
        Assert.True(made.BillCount >= 3);
    }

    [Fact]
    public async Task A_save_that_fails_without_saying_whether_the_bill_was_stored_leaves_the_count_in_doubt()
    {
        await using var db = CreateDbContext();
        var bills = BillTestData.Lifecycle(db);
        var outcomes = new SyntheticBatchOutcomes();
        var executor = new SyntheticBatchExecutor(bills, new MastersOnlySettings(), db, NullLogger<SyntheticBatchExecutor>.Instance, AfterTheWindow(), outcomes);
        var request = ThreeBillsOrMore() with { BatchId = Guid.NewGuid() };

        // The second bill's save fails after its rows were sent and before its commit is answered,
        // as a line that drops at that moment does. Here the bill is in fact not stored, but the
        // batch cannot tell, and says so instead of giving a count as if it were sure.
        var saves = 0;
        db.SavedChanges += (_, _) =>
        {
            if (++saves == 2) throw new TimeoutException("The database stopped answering.");
        };

        await Assert.ThrowsAsync<TimeoutException>(() => executor.ExecuteAsync(request));

        var outcome = outcomes.Find(request.BatchId!.Value)!;
        Assert.True(outcome.Finished);
        Assert.True(outcome.LastSaveInDoubt);
        Assert.Equal(1, outcome.BillCount);
        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.Bills.CountAsync());
    }

    /// <summary>
    /// A batch of three bills at least: 6,00,000 in bills of at most 1,99,000, dated from 09:00 to
    /// 13:00 of one day in India, so every bill is of one date and one fiscal year and the bill
    /// numbers run on without a break.
    /// </summary>
    private static SyntheticBatchRequest ThreeBillsOrMore() => new(
        TotalAmount: 600_000,
        MaxBillAmount: 199_000,
        Rate24Kt: 7200m,
        PaymentMode: "Cash",
        MinItemsPerBill: 1,
        MaxItemsPerBill: 2,
        StartAtUtc: IndiaTime.StartOf(ADayBack()).AddHours(9).ToUniversalTime(),
        EndAtUtc: IndiaTime.StartOf(ADayBack()).AddHours(13).ToUniversalTime(),
        SelectedKaratLabels: ["22K"]);

    /// <summary>The time the server takes for now: 16:00 that day, after that window and the two hours following it.</summary>
    private static FakeTimeProvider AfterTheWindow() => new(IndiaTime.StartOf(ADayBack()).AddHours(16).ToUniversalTime());

    [Fact]
    public async Task The_earliest_time_follows_the_bills_of_the_latest_date()
    {
        var day = ADayBack();
        var dayStart = IndiaTime.StartOf(day);
        await using var db = CreateDbContext();
        var bills = BillTestData.Lifecycle(db);
        var executor = new SyntheticBatchExecutor(bills, new MastersOnlySettings(), db, NullLogger<SyntheticBatchExecutor>.Instance);

        // No bill: no limit but now.
        Assert.Null(await executor.EarliestBillTimeAsync());

        // A bill dated ahead of the day it was saved counts from the start of its date.
        await bills.CreateBackdatedDraftAsync(new CreateBillDraftRequest(Payload(day.AddDays(2))), dayStart.AddHours(9).ToUniversalTime());
        Assert.Equal(IndiaTime.StartOf(day.AddDays(2)), await executor.EarliestBillTimeAsync());

        // A bill of that date saved on it counts from the minute after it was saved.
        var sameDay = dayStart.AddDays(2).AddHours(11).AddSeconds(40).ToUniversalTime();
        await bills.CreateBackdatedDraftAsync(new CreateBillDraftRequest(Payload(day.AddDays(2))), sameDay);
        Assert.Equal(dayStart.AddDays(2).AddHours(11).AddMinutes(1), await executor.EarliestBillTimeAsync());
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

    private static BillPayloadDto Payload(DateOnly billDate) => new(
        PartyName: "Walk-in",
        PartyGstin: null,
        PartyPhone: null,
        PartyAddress: null,
        BillDate: billDate,
        Lines: [new BillLineItemDto("22K Gold Ring", "7113", 10m, "grams", 100m, 1_000m, "22K", null)],
        Totals: new BillTotalsDto(1_000m, 0m, 0m, 0m, 1_000m),
        Notes: null);

    /// <summary>The masters a batch bills, and no shop hours.</summary>
    private sealed class MastersOnlySettings : FakeCloudSettingsService
    {
        public static readonly string Items = JsonSerializer.Serialize(new[]
        {
            new ItemMasterEntry(
                Name: "22K Gold Chain",
                Unit: ItemUnits.Gram,
                WastagePercent: 8m,
                ItemCategory: ItemCategories.GoldBased,
                PricingMode: PricingModes.Wastage,
                DefaultLabourPerGram: 0m,
                DefaultStockMappingLabel: "22K"),
        });

        public static readonly string Karats = JsonSerializer.Serialize(new[]
        {
            new KaratMasterEntry("22K", 92.0m, "Hallmarked Gold Jewellery 22KT"),
        });

        public MastersOnlySettings()
        {
            Masters = new MasterDataSettingsDto(Items, Karats);
        }

        public override Task<BatchHoursResponse> GetBatchHoursAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatchHoursResponse(new BatchHoursSettings(), DateTimeOffset.UtcNow));
    }
}
