using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Bills;

/// <summary>
/// Runs a synthetic batch: loads the masters, builds a plan with
/// <see cref="SyntheticBillPlanner"/>, and saves each planned bill through
/// <see cref="BillService.CreateBackdatedDraftAsync"/>, one at a time.
///
/// A number is reserved under a <c>FOR UPDATE</c> row lock, so the bills are
/// saved one after another, each in its own transaction.
/// </summary>
public sealed class SyntheticBatchExecutor(
    BillService billService,
    ICloudSettingsService settings,
    ShowroomBillingDbContext dbContext,
    ILogger<SyntheticBatchExecutor> logger,
    TimeProvider? timeProvider = null)
{
    public async Task<SyntheticBatchResponse> ExecuteAsync(
        SyntheticBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await settings.GetEffectiveSettingsAsync(cancellationToken);
        var itemEntries = ReadMasters(
            MasterDataJson.ParseItems, effective.Settings.Masters.ItemMasterDataJson, "item masters");
        var karatEntries = ReadMasters(
            MasterDataJson.ParseKarats, effective.Settings.Masters.KaratMappingDataJson, "karat masters");

        if (itemEntries.Count == 0)
            throw new RequestValidationException("No item masters are configured.");
        if (karatEntries.Count == 0)
            throw new RequestValidationException("No karat masters are configured.");

        // The planner keeps the batch between the last bill already stored and now.
        var earliestUtc = await EarliestBillTimeAsync(cancellationToken);
        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();

        var rng = new Random();
        var plan = new SyntheticBillPlanner().BuildPlan(request, itemEntries, karatEntries, rng, earliestUtc, nowUtc);

        logger.LogInformation(
            "Synthetic batch planned: {BillCount} bill(s), grand ₹{Grand:N0}, window [{Start:o}, {End:o}]",
            plan.Bills.Count, plan.TotalAmount, request.StartAtUtc, request.EndAtUtc);

        var saved = 0;
        foreach (var planned in plan.Bills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await billService.CreateBackdatedDraftAsync(
                    new CreateBillDraftRequest(Payload: planned.Payload),
                    planned.ScheduledAtUtc,
                    cancellationToken);
            }
            catch (BillStateConflictException ex)
            {
                // A counter saved a bill while the batch was being saved: the rest would be
                // numbered after it and dated before it.
                throw new BillStateConflictException(
                    $"A bill was saved at a counter while the batch was being made, so the batch stopped after {saved} of {plan.Bills.Count} bill(s). {ex.Message}");
            }
            saved++;
        }

        return new SyntheticBatchResponse(
            BillCount: plan.Bills.Count,
            TotalAmount: plan.TotalAmount);
    }

    /// <summary>
    /// The first minute a batch may date a bill, or null when there is no bill. Batch bills take
    /// the next numbers, so they are dated after every bill already stored (owner, 20 November
    /// 2026). A bill counts as made when it was saved, except that one saved on a later day than
    /// its date counts as the end of that date, so a bill dated yesterday lets a batch start today
    /// (owner, 21 November 2026), and one dated ahead of the day it was saved counts from the start
    /// of its date. <see cref="BillLifecycleWorkflow"/> refuses a backdated bill by the same rule.
    /// </summary>
    public async Task<DateTimeOffset?> EarliestBillTimeAsync(CancellationToken cancellationToken = default)
    {
        var bills = from bill in dbContext.Bills.AsNoTracking()
                    join rev in dbContext.BillRevisions.AsNoTracking()
                        on bill.CurrentRevisionId equals rev.Id into revs
                    from rev in revs.DefaultIfEmpty()
                    select new { bill.CreatedAtUtc, BillDate = rev == null ? null : rev.BillDate };

        // Only the bills of the latest date matter: a bill of an earlier date counts at most as
        // the end of its date, which is not after the start of the latest one. Its date and its
        // last save are read together, so a bill deleted meanwhile cannot leave one without the other.
        DateTimeOffset? earliest = null;
        var last = await bills
            .Where(x => x.BillDate != null)
            .OrderByDescending(x => x.BillDate)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (last is { BillDate: { } date })
        {
            var dayStart = IndiaTime.StartOf(date);
            var nextDayStart = IndiaTime.StartOf(date.AddDays(1));
            var after = MinuteAfter(last.CreatedAtUtc);
            earliest = after < dayStart ? dayStart : after > nextDayStart ? nextDayStart : after;
        }

        // A bill with no date (no revision) counts as made when it was saved.
        var undated = await bills
            .Where(x => x.BillDate == null)
            .MaxAsync(x => (DateTimeOffset?)x.CreatedAtUtc, cancellationToken);
        if (undated is { } u && (earliest is null || MinuteAfter(u) > earliest))
            earliest = MinuteAfter(u);

        return earliest?.ToUniversalTime();
    }

    private static DateTimeOffset MinuteAfter(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero).AddMinutes(1);
    }

    private static IReadOnlyList<T> ReadMasters<T>(Func<string?, IReadOnlyList<T>> parse, string? json, string label)
    {
        try
        {
            return parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse {label} JSON: {ex.Message}", ex);
        }
    }
}
