using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Tests.Postgres;

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
        var bills = new BillService(db, new NumberingService(db), new UnusedTallyPoster());
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
        // Two, not three: what is left under 50,000 after the last bill is dropped, not
        // billed, so 400,000 can come out as, say, 192,536 + 189,573. Asking for three
        // would fail about one run in 19. SyntheticBillPlannerTests holds the rule itself.
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
        var bills = new BillService(db, new NumberingService(db), new UnusedTallyPoster());
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
    public async Task The_earliest_time_follows_the_bills_of_the_latest_date()
    {
        var day = ADayBack();
        var dayStart = IndiaTime.StartOf(day);
        await using var db = CreateDbContext();
        var bills = new BillService(db, new NumberingService(db), new UnusedTallyPoster());
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

    private sealed class MastersOnlySettings : ICloudSettingsService
    {
        private static readonly string Items = JsonSerializer.Serialize(new[]
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

        private static readonly string Karats = JsonSerializer.Serialize(new[]
        {
            new KaratMasterEntry("22K", 92.0m, "Hallmarked Gold Jewellery 22KT"),
        });

        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveSettingsResponse(
                SettingsSource: "cloud",
                Summary: "test",
                Settings: new EffectiveCloudSettingsDto(
                    new ConnectionSettingsDto("127.0.0.1", 9000, 30, "Acme Jewellers"),
                    new NumberingSettingsDto("DEV-", "", 4),
                    new PrintSettingsDto("Acme", null, null, null, null, null, null, null, null, null, null, true, false, false, 11, 9),
                    new LedgerMappingsDto("Sales", "Cash", "Card", "CGST", "SGST", "Round Off", "Discount", "Sales"),
                    new MasterDataSettingsDto(Items, Karats)),
                CloudOwnedCategories: [],
                LocalOnlyCategories: [],
                UpdatedAtUtc: DateTimeOffset.UtcNow));

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
            UpdateEffectiveSettingsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
            string companyName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
            UpdatePrintLayoutRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>A batch only saves bills; nothing is sent to Tally.</summary>
    private sealed class UnusedTallyPoster : ITallyPoster
    {
        public Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
