using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

internal sealed class BillReadWorkflow(ShowroomBillingDbContext dbContext)
{
    private const string DefaultShowroomCode = "default";
    private const string SortWorkflow = "workflow";
    private const string SortCreatedDesc = "created_desc";

    internal async Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        var bill = await dbContext.Bills
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == billId, cancellationToken);
        if (bill is null)
        {
            return null;
        }

        BillRevisionEntity? currentRevision = null;
        if (bill.CurrentRevisionId is Guid revisionId)
        {
            currentRevision = await dbContext.BillRevisions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == revisionId, cancellationToken);
        }

        return BillSerialization.MapResponse(bill, currentRevision);
    }

    internal async Task<BillBatchGetResponse> GetManyAsync(
        BillBatchGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var orderedIds = NormalizeOrderedIds(request.BillIds);
        if (orderedIds.Count == 0)
        {
            return new BillBatchGetResponse(Array.Empty<BillResponse>());
        }

        var requested = orderedIds.Take(200).ToArray();
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
        var showroomId = ResolveShowroomId(DefaultShowroomCode);

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

        var includeTotal = filter.IncludeTotal ?? true;
        var total = includeTotal ? await query.CountAsync(cancellationToken) : 0;
        var ordered = sort == SortCreatedDesc
            ? query.OrderByDescending(x => x.bill.CreatedAtUtc)
            : query
                .OrderBy(x =>
                    x.bill.State == IBillService.StatePending || x.bill.State == BillStates.Draft
                        ? 0
                        : 1)
                .ThenBy(x =>
                    x.bill.State == IBillService.StatePending || x.bill.State == BillStates.Draft
                        ? x.bill.CreatedAtUtc
                        : DateTimeOffset.MaxValue)
                .ThenByDescending(x => x.bill.FiscalYear)
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

    private static Guid ResolveShowroomId(string showroomCode)
    {
        var bytes = Encoding.UTF8.GetBytes(showroomCode.Trim().ToLowerInvariant());
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
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

    private static IReadOnlyList<Guid> NormalizeOrderedIds(IReadOnlyList<Guid>? billIds)
    {
        if (billIds is null || billIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var seen = new HashSet<Guid>();
        var ordered = new List<Guid>(billIds.Count);
        foreach (var billId in billIds)
        {
            if (billId == Guid.Empty || !seen.Add(billId))
            {
                continue;
            }

            ordered.Add(billId);
        }

        return ordered;
    }
}
