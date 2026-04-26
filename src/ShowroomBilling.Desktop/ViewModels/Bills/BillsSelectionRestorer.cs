using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal static class BillsSelectionRestorer
{
    public static BillSummaryItem? Restore(
        IReadOnlyCollection<BillListRowViewModel> rows,
        IReadOnlyCollection<Guid> priorSelectedIds,
        Guid? priorFocusedId)
    {
        if (priorSelectedIds.Count > 0)
        {
            var selectedSet = priorSelectedIds.ToHashSet();
            foreach (var item in rows)
            {
                item.IsSelected = selectedSet.Contains(item.Id);
            }
        }

        var focused = priorFocusedId is Guid focusedId
            ? rows.FirstOrDefault(x => x.Id == focusedId)
            : null;
        return focused?.Item ?? rows.FirstOrDefault(x => x.IsSelected)?.Item;
    }
}
