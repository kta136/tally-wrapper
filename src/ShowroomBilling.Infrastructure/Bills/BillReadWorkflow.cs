using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

public sealed class BillReadWorkflow(ShowroomBillingDbContext dbContext)
{
    private const string SortWorkflow = BillSearchSorts.Workflow;
    private const string SortCreatedDesc = BillSearchSorts.CreatedDesc;
    private const int MaxBatchGetIds = 200;

    public async Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        // One round trip for the bill and its current revision; the database
        // is across the network from the showroom.
        var row = await (
            from bill in dbContext.Bills.AsNoTracking()
            join rev in dbContext.BillRevisions.AsNoTracking()
                on bill.CurrentRevisionId equals rev.Id into revs
            from rev in revs.DefaultIfEmpty()
            where bill.Id == billId
            select new { bill, rev })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : BillSerialization.MapResponse(row.bill, row.rev);
    }

    /// <summary>
    /// The bill whose create carried <paramref name="operationId"/> as its
    /// <c>ClientOperationId</c>, with its current revision, or null.
    /// </summary>
    public async Task<BillResponse?> GetByOperationIdAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var row = await (
            from bill in dbContext.Bills.AsNoTracking()
            join rev in dbContext.BillRevisions.AsNoTracking()
                on bill.CurrentRevisionId equals rev.Id into revs
            from rev in revs.DefaultIfEmpty()
            where bill.CreateOperationId == operationId
            select new { bill, rev })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : BillSerialization.MapResponse(row.bill, row.rev);
    }

    /// <summary>
    /// The bills that should reach Tally before the ones named: see <see cref="BillPushOrderResponse"/>.
    /// Only a bill whose push makes a voucher counts on either side; an edited bill's push alters the
    /// voucher it already has, which keeps its place.
    /// </summary>
    public async Task<BillPushOrderResponse> FindEarlierUnpushedAsync(
        BillPushOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = BillAdminSelection.NormalizeOrderedIds(request.BillIds).ToArray();
        if (ids.Length > MaxBatchGetIds)
        {
            throw new RequestValidationException(
                $"At most {MaxBatchGetIds} bills can be checked at once (got {ids.Length}).");
        }

        string[] notInTally = [BillStates.Pending, BillStates.Draft, BillStates.Failed];
        var requested = ids.Length == 0
            ? []
            : await dbContext.Bills.AsNoTracking()
                .Where(bill => ids.Contains(bill.Id))
                .Select(bill => new
                {
                    bill.Id,
                    bill.FiscalYear,
                    bill.InvoiceCore,
                    bill.State,
                    bill.EditedAfterPush,
                    BillDate = dbContext.BillRevisions.Where(r => r.Id == bill.CurrentRevisionId).Select(r => r.BillDate).FirstOrDefault(),
                })
                .ToListAsync(cancellationToken);

        // The order to push in: by date, then number, from what the database holds now.
        var known = requested.ToDictionary(r => r.Id);
        var pushOrder = ids
            .Select((id, index) => (Id: id, Index: index, Bill: known.GetValueOrDefault(id)))
            .OrderBy(x => x.Bill is null)
            .ThenBy(x => x.Bill?.BillDate ?? DateOnly.MaxValue)
            .ThenBy(x => x.Bill?.InvoiceCore ?? long.MaxValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Id)
            .ToList();

        // The bills about to make a voucher; an edited bill's push alters the one it has.
        var targets = requested
            .Where(r => r.InvoiceCore is not null && r.FiscalYear is not null && !r.EditedAfterPush && notInTally.Contains(r.State))
            .Select(r => new { r.FiscalYear, Core = r.InvoiceCore!.Value, r.BillDate })
            .ToList();
        if (targets.Count == 0)
        {
            return new BillPushOrderResponse([], pushOrder);
        }

        var years = targets.Select(t => t.FiscalYear).Distinct().ToArray();
        var highestCore = targets.Max(t => t.Core);
        var candidates = await dbContext.Bills.AsNoTracking()
            .Where(bill => years.Contains(bill.FiscalYear)
                && !ids.Contains(bill.Id)
                && bill.InvoiceCore != null
                && bill.InvoiceCore < highestCore
                && !bill.EditedAfterPush
                && notInTally.Contains(bill.State))
            .Select(bill => new
            {
                bill.Id,
                bill.InvoiceNumber,
                bill.State,
                bill.FiscalYear,
                Core = bill.InvoiceCore!.Value,
                BillDate = dbContext.BillRevisions.Where(r => r.Id == bill.CurrentRevisionId).Select(r => r.BillDate).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // A bill without a date on either side is held to its number alone, so it is not missed.
        var earlier = candidates
            .Where(c => targets.Any(t => t.FiscalYear == c.FiscalYear
                && c.Core < t.Core
                && (c.BillDate is null || t.BillDate is null || c.BillDate <= t.BillDate)))
            .OrderBy(c => c.BillDate ?? DateOnly.MinValue)
            .ThenBy(c => c.Core)
            .Select(c => new UnpushedEarlierBill(c.Id, c.InvoiceNumber ?? string.Empty, c.BillDate, c.State))
            .ToList();
        return new BillPushOrderResponse(earlier, pushOrder);
    }

    public async Task<BillBatchGetResponse> GetManyAsync(
        BillBatchGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var orderedIds = BillAdminSelection.NormalizeOrderedIds(request.BillIds);
        if (orderedIds.Count == 0)
        {
            return new BillBatchGetResponse(Array.Empty<BillResponse>());
        }

        if (orderedIds.Count > MaxBatchGetIds)
        {
            throw new RequestValidationException(
                $"At most {MaxBatchGetIds} bills can be fetched at once (got {orderedIds.Count}).");
        }

        var requested = orderedIds.ToArray();
        var bills = await dbContext.Bills
            .AsNoTracking()
            .Where(x => requested.Contains(x.Id))
            .ToListAsync(cancellationToken);

        var revisionIds = bills
            .Select(x => x.CurrentRevisionId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var revisions = revisionIds.Length == 0
            ? new Dictionary<Guid, BillRevisionEntity>()
            : await dbContext.BillRevisions
                .AsNoTracking()
                .Where(x => revisionIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);

        var byId = bills.ToDictionary(x => x.Id);
        var responses = new List<BillResponse>(bills.Count);
        foreach (var id in requested)
        {
            if (!byId.TryGetValue(id, out var bill))
            {
                continue;
            }

            var revision = bill.CurrentRevisionId is Guid revisionId
                && revisions.TryGetValue(revisionId, out var found)
                    ? found
                    : null;
            responses.Add(BillSerialization.MapResponse(bill, revision));
        }

        return new BillBatchGetResponse(responses);
    }

    public async Task<BillListResponse> SearchAsync(
        BillSearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var skip = Math.Max(0, filter.Skip ?? 0);
        var take = Math.Clamp(filter.Take ?? 50, 1, 200);
        var sort = NormalizeSort(filter.Sort);

        var query = from bill in dbContext.Bills.AsNoTracking()
                    join rev in dbContext.BillRevisions.AsNoTracking()
                        on bill.CurrentRevisionId equals rev.Id into revs
                    from rev in revs.DefaultIfEmpty()
                    select new { bill, rev };

        if (!string.IsNullOrWhiteSpace(filter.State))
        {
            var state = filter.State.Trim();
            query = query.Where(x => x.bill.State == state);
        }
        if (filter.FromDate is DateOnly from)
        {
            query = query.Where(x => x.rev != null && x.rev.BillDate >= from);
        }
        if (filter.ToDate is DateOnly to)
        {
            query = query.Where(x => x.rev != null && x.rev.BillDate <= to);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim()
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            var pattern = $"%{search}%";
            // Operators type party names in any case. PostgreSQL LIKE is
            // case-sensitive; ILIKE is, and the trigram GIN indexes serve it.
            query = query.Where(x =>
                (x.bill.InvoiceNumber != null && EF.Functions.ILike(x.bill.InvoiceNumber, pattern, @"\"))
                || (x.rev != null && x.rev.PartyName != null && EF.Functions.ILike(x.rev.PartyName, pattern, @"\")));
        }

        var includeTotal = filter.IncludeTotal ?? true;
        var total = includeTotal ? await query.CountAsync(cancellationToken) : 0;
        // Workflow sort:
        //   1. Pending/Draft above everything else (operator's work queue).
        //   2. Within each state group: highest invoice number first, read
        //      from the stored core so padding and suffixes do not matter.
        //      Legacy numbers without a core come after, longest text first
        //      (natural order for unpadded numbers), then by text.
        //   3. CreatedAtUtc desc is only a last-resort tiebreak for bills
        //      that share fiscal year + invoice number (effectively only
        //      bills with no InvoiceNumber yet).
        // The old "ASC CreatedAtUtc within pending" rule was removed because
        // it produced surprising orderings after change-number — a bill
        // renamed down (e.g. 94 -> 92) would sit above a higher-numbered
        // peer simply because it was created earlier.
        var ordered = sort == SortCreatedDesc
            ? query.OrderByDescending(x => x.bill.CreatedAtUtc)
            : query
                .OrderBy(x =>
                    x.bill.State == BillStates.Pending || x.bill.State == BillStates.Draft
                        ? 0
                        : 1)
                .ThenByDescending(x => x.bill.FiscalYear)
                .ThenByDescending(x => x.bill.InvoiceCore ?? -1L)
                .ThenByDescending(x => x.bill.InvoiceNumber == null ? 0 : x.bill.InvoiceNumber.Length)
                .ThenByDescending(x => x.bill.InvoiceNumber)
                .ThenByDescending(x => x.bill.CreatedAtUtc);

        var rows = await ordered
            .Skip(skip)
            .Take(includeTotal ? take : take + 1)
            .Select(x => new BillSummaryItem(
                x.bill.Id,
                x.bill.State,
                x.bill.InvoiceNumber,
                x.rev == null ? null : x.rev.PartyName,
                x.rev == null ? null : x.rev.BillDate,
                x.rev == null ? 0m : x.rev.GrandTotal,
                x.bill.CreatedAtUtc,
                x.bill.UpdatedAtUtc,
                x.bill.EditedAfterPush))
            .ToListAsync(cancellationToken);
        var hasMore = !includeTotal && rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        if (!includeTotal)
        {
            total = skip + rows.Count + (hasMore ? 1 : 0);
        }

        return new BillListResponse(total, skip, take, await WithTallyNumbersAsync(rows, cancellationToken), hasMore);
    }

    /// <summary>
    /// Adds the number Tally filed each bill's voucher under, from the read-back
    /// after its last push (<c>tally.voucher.filed</c>), while that push is still
    /// the latest word on what Tally holds: a later push, Mark as Pushed or Mark
    /// as Pending makes it stale, so it is left out then. A read-back counts only
    /// for the voucher its push recorded, since it is written after the push has
    /// settled and another counter may have pushed or marked the bill meanwhile.
    /// </summary>
    private async Task<IReadOnlyList<BillSummaryItem>> WithTallyNumbersAsync(
        List<BillSummaryItem> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var ids = rows.Select(r => r.Id.ToString()).ToList();
        var events = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill"
                && ids.Contains(e.EntityId)
                && (e.EventType == BillAuditEvents.TallyPosted
                    || e.EventType == BillAuditEvents.MarkPosted
                    || e.EventType == BillAuditEvents.MarkPending
                    || e.EventType == BillAuditEvents.TallyVoucherFiled))
            .Select(e => new
            {
                e.EntityId,
                e.EventType,
                e.CreatedAtUtc,
                e.Id,
                PayloadJson = e.EventType == BillAuditEvents.TallyVoucherFiled ? e.PayloadJson : null,
            })
            .ToListAsync(cancellationToken);

        var filedAs = new Dictionary<string, string?>();
        foreach (var trail in events.GroupBy(e => e.EntityId))
        {
            var ordered = trail.OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).ToList();
            var lastTruth = ordered.LastOrDefault(e => e.EventType != BillAuditEvents.TallyVoucherFiled);
            if (lastTruth?.EventType != BillAuditEvents.TallyPosted)
            {
                continue;
            }

            // Only the read made for that very push: a read of an earlier push of the same
            // voucher can be written late, after a newer push has settled.
            var pushAuditId = lastTruth.Id.ToString();
            var filed = ordered.LastOrDefault(e => e.EventType == BillAuditEvents.TallyVoucherFiled
                && string.Equals(BillAuditStore.ReadDetailsString(e.PayloadJson, "pushAuditId"), pushAuditId, StringComparison.OrdinalIgnoreCase));
            if (filed is not null)
            {
                filedAs[trail.Key] = BillAuditStore.ReadDetailsString(filed.PayloadJson, "voucherNumberInTally");
            }
        }

        return rows
            .Select(r => filedAs.TryGetValue(r.Id.ToString(), out var number) && number is not null
                ? r with { TallyVoucherNumber = number }
                : r)
            .ToList();
    }

    private static string NormalizeSort(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return SortWorkflow;
        }

        return sort.Trim().ToLowerInvariant() switch
        {
            SortCreatedDesc => SortCreatedDesc,
            _ => SortWorkflow
        };
    }

}
