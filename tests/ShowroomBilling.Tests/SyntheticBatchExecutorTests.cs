using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Application.Tally;
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
            new CreateBillDraftRequest(null, new BillPayloadDto(
                PartyName: "Walk-in",
                PartyGstin: null,
                PartyPhone: null,
                PartyAddress: null,
                BillDate: DateOnly.FromDateTime(existingAt.UtcDateTime),
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
        Assert.Equal("Start time must be later than the latest existing bill's timestamp.", refused.Message);
        Assert.Equal(1, await db.Bills.CountAsync());

        var response = await executor.ExecuteAsync(request with { StartAtUtc = existingAt.AddMinutes(1) });

        await using var verify = CreateDbContext();
        var stored = await verify.Bills.AsNoTracking().Where(b => b.Id != existing.Id).ToListAsync();
        Assert.True(response.BillCount >= 3, $"400,000 in bills of at most 199,000 is at least three; got {response.BillCount}.");
        Assert.Equal(response.BillCount, stored.Count);
        Assert.All(stored, bill =>
        {
            Assert.Equal(BillStates.Pending, bill.State);
            Assert.NotNull(bill.InvoiceCore);
            Assert.True(bill.CreatedAtUtc > existingAt, $"{bill.InvoiceNumber} was dated {bill.CreatedAtUtc:o}, not after {existingAt:o}.");
        });
        Assert.Equal(stored.Count, stored.Select(b => b.InvoiceNumber).Distinct().Count());
    }

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
