using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Masters;

internal static class MasterSnapshotQueryFilters
{
    private const int DefaultPageSize = 500;
    private const int MaxPageSize = 500;

    internal static IQueryable<TallyCompanySnapshotEntity> ApplyNameQuery(
        IQueryable<TallyCompanySnapshotEntity> rows,
        MasterSnapshotQuery? query)
    {
        var search = NormalizeSearch(query?.Search);
        return search is null ? rows : rows.Where(x => EF.Functions.ILike(x.Name, search));
    }

    internal static IQueryable<TallyLedgerSnapshotEntity> ApplyNameQuery(
        IQueryable<TallyLedgerSnapshotEntity> rows,
        MasterSnapshotQuery? query)
    {
        var search = NormalizeSearch(query?.Search);
        return search is null ? rows : rows.Where(x => EF.Functions.ILike(x.Name, search));
    }

    internal static IQueryable<TallyStockItemSnapshotEntity> ApplyNameQuery(
        IQueryable<TallyStockItemSnapshotEntity> rows,
        MasterSnapshotQuery? query)
    {
        var search = NormalizeSearch(query?.Search);
        return search is null ? rows : rows.Where(x => EF.Functions.ILike(x.Name, search));
    }

    internal static IQueryable<TallyVoucherTypeSnapshotEntity> ApplyNameQuery(
        IQueryable<TallyVoucherTypeSnapshotEntity> rows,
        MasterSnapshotQuery? query)
    {
        var search = NormalizeSearch(query?.Search);
        return search is null ? rows : rows.Where(x => EF.Functions.ILike(x.Name, search));
    }

    internal static IQueryable<T> ApplyPaging<T>(IQueryable<T> rows, MasterSnapshotQuery? query)
    {
        if (query?.Skip is int skip && skip > 0)
        {
            rows = rows.Skip(skip);
        }

        var take = Math.Clamp(query?.Take ?? DefaultPageSize, 1, MaxPageSize);
        return rows.Take(take);
    }

    private static string? NormalizeSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var escaped = value.Trim()
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_");
        return $"%{escaped}%";
    }
}
