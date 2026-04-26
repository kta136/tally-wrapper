using System.Collections.ObjectModel;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

internal sealed class InvoiceQuickAddWorkflow(
    Func<ObservableCollection<ItemMasterRowVm>?> itemMasters,
    ObservableCollection<BillLineViewModel> lines,
    ObservableCollection<ItemMasterRowVm> results,
    Func<BillLineViewModel> createBlankRow,
    Func<string> getQuery,
    Action<string> setQuery,
    Func<ItemMasterRowVm?> getSelection,
    Action<ItemMasterRowVm?> setSelection)
{
    private List<(ItemMasterRowVm Item, string SearchName)>? _index;

    public void InvalidateIndex()
    {
        _index = null;
        RefreshResults();
    }

    public void RefreshResults()
    {
        results.Clear();
        if (itemMasters() is null) return;

        var query = (getQuery() ?? string.Empty).Trim();
        var source = GetIndex();
        IEnumerable<ItemMasterRowVm> matches = query.Length > 0
            ? source
                .Where(m => m.SearchName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Item)
            : source.Select(m => m.Item);

        foreach (var match in matches.Take(50))
            results.Add(match);

        setSelection(results.FirstOrDefault());
    }

    public BillLineViewModel? Commit(ItemMasterRowVm? master)
    {
        master ??= getSelection() ?? results.FirstOrDefault();
        if (master is null) return null;

        var target = lines.FirstOrDefault(l => l.IsEmpty);
        if (target is null)
        {
            target = createBlankRow();
            lines.Add(target);
        }

        target.ItemMaster = master;
        setQuery(string.Empty);
        return target;
    }

    private IReadOnlyList<(ItemMasterRowVm Item, string SearchName)> GetIndex()
    {
        if (_index is not null)
        {
            return _index;
        }

        _index = itemMasters()?
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => (m, m.Name.Trim()))
            .ToList()
            ?? [];
        return _index;
    }
}
