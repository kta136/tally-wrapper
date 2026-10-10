using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

internal sealed class BillReadWorkflow(ShowroomBillingDbContext dbContext)
{
    private const string SortWorkflow = "workflow";
    private const string SortCreatedDesc = "created_desc";
    private const int MaxBatchGetIds = 200;

    internal async Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default)
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

    internal async Task<BillBatchGetResponse> GetManyAsync(
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

    internal async Task<BillListResponse> SearchAsync(
        BillSearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var skip = Math.Max(0, filter.Skip ?? 0);
        var take = Math.Clamp(filter.Take ?? 50, 1, 200);
        var sort = NormalizeSort(filter.Sort);
        var showroomId = Showrooms.DefaultId;

        var query = from bill in dbContext.Bills.AsNoTracking()
                    join rev in dbContext.BillRevisions.AsNoTracking()
                        on bill.CurrentRevisionId equals rev.Id into revs
                    from rev in revs.DefaultIfEmpty()
                    where bill.ShowroomId == showroomId
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
            query = dbContext.Database.IsNpgsql()
                ? query.Where(x =>
                    (x.bill.InvoiceNumber != null && EF.Functions.ILike(x.bill.InvoiceNumber, pattern, @"\"))
                    || (x.rev != null && x.rev.PartyName != null && EF.Functions.ILike(x.rev.PartyName, pattern, @"\")))
                : query.Where(x =>
                    (x.bill.InvoiceNumber != null && EF.Functions.Like(x.bill.InvoiceNumber, pattern, @"\"))
                    || (x.rev != null && x.rev.PartyName != null && EF.Functions.Like(x.rev.PartyName, pattern, @"\")));
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

        return new BillListResponse(total, skip, take, rows, hasMore);
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
