namespace ShowroomBilling.Infrastructure.Bills;

internal static class BillAdminSelection
{
    internal static IReadOnlyList<Guid> NormalizeOrderedIds(IReadOnlyList<Guid>? billIds)
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
