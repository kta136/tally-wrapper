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
/// saved one after another, each in its own transaction. A batch that stops
/// part-way keeps the bills it has saved; <see cref="SyntheticBatchOutcomes"/>
/// holds how many that was, for a Desktop that got no answer.
/// </summary>
public sealed class SyntheticBatchExecutor(
    BillService billService,
    ICloudSettingsService settings,
    ShowroomBillingDbContext dbContext,
    ILogger<SyntheticBatchExecutor> logger,
    TimeProvider? timeProvider = null,
    SyntheticBatchOutcomes? outcomes = null)
{
    public async Task<SyntheticBatchResponse> ExecuteAsync(
        SyntheticBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The same batch sent twice (a request sent again after its answer was lost) makes no
        // second batch. Said before anything else, or the times, which are now too early, would
        // be what is refused.
        const string alreadySent = "This batch has already been sent to the server. Look at the Bills tab before starting another.";
        if (request.BatchId is { } sentId && outcomes?.Find(sentId) is not null)
            throw new BillStateConflictException(alreadySent);

        var effective = await settings.GetEffectiveSettingsAsync(cancellationToken);
        var itemEntries = ReadMasters(
            MasterDataJson.ParseItems, effective.Settings.Masters.ItemMasterDataJson, "item masters");
        var karatEntries = ReadMasters(
            MasterDataJson.ParseKarats, effective.Settings.Masters.KaratMappingDataJson, "karat masters");

        if (itemEntries.Count == 0)
            throw new RequestValidationException("No item masters are configured.");
        if (karatEntries.Count == 0)
            throw new RequestValidationException("No karat masters are configured.");

        // The planner keeps the batch between the last bill already stored and now, and inside
        // the shop hours saved in Settings → Admin.
        var earliestUtc = await EarliestBillTimeAsync(cancellationToken);
        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var hours = (await settings.GetBatchHoursAsync(cancellationToken)).Hours;

        // The operator said yes to a batch that keeps to the hours the form showed. If they were
        // changed meanwhile (at another counter), no bill is dated by hours nobody approved.
        if (request.ExpectedHours is { } shown && shown != hours)
            throw new RequestValidationException(
                "The shop hours were changed while this batch was being set up. Look at them again, then start the batch again.");

        var rng = new Random();
        var plan = new SyntheticBillPlanner().BuildPlan(request, itemEntries, karatEntries, rng, earliestUtc, nowUtc, hours);

        logger.LogInformation(
            "Synthetic batch planned: {BillCount} bill(s), grand ₹{Grand:N0}, window [{Start:o}, {End:o}]",
            plan.Bills.Count, plan.TotalAmount, request.StartAtUtc, request.EndAtUtc);

        // Only a batch that came with an id is kept track of: nobody could ask about another.
        var batchId = outcomes is null ? null : request.BatchId;
        if (batchId is { } id && !outcomes!.TryStart(id, plan.Bills.Count))
            throw new BillStateConflictException(alreadySent);

        var saved = 0;
        var lastSaveInDoubt = false;
        string? firstNumber = null;
        string? lastNumber = null;
        try
        {
            foreach (var planned in plan.Bills)
            {
                // A cancelled run stops between two bills. The bill in hand is saved or it is
                // not, never cut off at its commit, so the count of saved bills is what is stored.
                cancellationToken.ThrowIfCancellationRequested();
                BillResponse bill;
                try
                {
                    bill = await billService.CreateBackdatedDraftAsync(
                        new CreateBillDraftRequest(Payload: planned.Payload),
                        planned.ScheduledAtUtc,
                        CancellationToken.None);
                }
                catch (BillStateConflictException ex)
                {
                    // A counter saved a bill while the batch was being saved: the rest would be
                    // numbered after it and dated before it.
                    throw new BillStateConflictException(
                        $"A bill was saved at a counter while the batch was being made, so the batch stopped after {saved} of {plan.Bills.Count} bill(s). {ex.Message}");
                }
                catch (Exception ex) when (ex is not BillValidationException and not RequestValidationException)
                {
                    // Refused before anything was written (the two above, and a bill that fails its
                    // checks) is a bill not saved. Anything else may have failed at the commit, with
                    // the bill stored and the answer lost, so the count is one short or it is not.
                    lastSaveInDoubt = true;
                    throw;
                }

                saved++;
                firstNumber ??= bill.InvoiceNumber;
                lastNumber = bill.InvoiceNumber;
                if (batchId is { } known) outcomes!.Saved(known, bill.InvoiceNumber, planned.Payload.Totals.GrandTotal);

                // Each bill starts from what the database holds. One context carries the whole
                // batch, and without this it would keep every bill it has saved, and its own copy
                // of the year's numbering row, which a delete or a renumbering at a counter changes.
                dbContext.ChangeTracker.Clear();
            }
        }
        finally
        {
            if (batchId is { } known) outcomes!.Finish(known, lastSaveInDoubt);
            if (saved < plan.Bills.Count)
                logger.LogWarning(
                    "Synthetic batch stopped after {Saved} of {Planned} bill(s){InDoubt}",
                    saved, plan.Bills.Count, lastSaveInDoubt ? "; the next bill may or may not have been saved" : string.Empty);
        }

        return new SyntheticBatchResponse(
            BillCount: plan.Bills.Count,
            TotalAmount: plan.TotalAmount,
            FirstInvoiceNumber: firstNumber,
            LastInvoiceNumber: lastNumber);
    }

    /// <summary>
    /// What the Batch Data Scheduler's form needs before a batch is sent: when a batch may be dated
    /// from (<see cref="EarliestBillTimeAsync"/>) and the shop hours it keeps to.
    /// </summary>
    public async Task<SyntheticBatchWindowResponse> GetWindowAsync(CancellationToken cancellationToken = default)
        => new(
            await EarliestBillTimeAsync(cancellationToken),
            (await settings.GetBatchHoursAsync(cancellationToken)).Hours);

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
