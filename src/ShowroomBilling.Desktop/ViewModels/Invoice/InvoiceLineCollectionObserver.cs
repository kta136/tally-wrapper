using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

/// <summary>
/// Owns the mechanical collection plumbing for invoice rows: mutation subscriptions,
/// reset-safe detachment, and one-based row numbering. Billing behavior remains in the
/// view model through the supplied callbacks.
/// </summary>
internal sealed class InvoiceLineCollectionObserver : IDisposable
{
    private readonly ObservableCollection<BillLineViewModel> _lines;
    private readonly EventHandler _lineMutated;
    private readonly Action _collectionChanged;
    private readonly HashSet<BillLineViewModel> _observed = [];

    internal InvoiceLineCollectionObserver(
        ObservableCollection<BillLineViewModel> lines,
        EventHandler lineMutated,
        Action collectionChanged)
    {
        _lines = lines;
        _lineMutated = lineMutated;
        _collectionChanged = collectionChanged;
        _lines.CollectionChanged += OnCollectionChanged;
        ReconcileSubscriptions();
        RefreshRowNumbers();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReconcileSubscriptions();
        RefreshRowNumbers();
        _collectionChanged();
    }

    private void ReconcileSubscriptions()
    {
        var current = _lines.ToHashSet();
        foreach (var removed in _observed.Where(line => !current.Contains(line)).ToArray())
        {
            removed.MutationOccurred -= _lineMutated;
            _observed.Remove(removed);
        }

        foreach (var added in current.Where(line => !_observed.Contains(line)))
        {
            added.MutationOccurred += _lineMutated;
            _observed.Add(added);
        }
    }

    private void RefreshRowNumbers()
    {
        for (var index = 0; index < _lines.Count; index++)
        {
            _lines[index].RowNumber = index + 1;
        }
    }

    public void Dispose()
    {
        _lines.CollectionChanged -= OnCollectionChanged;
        foreach (var line in _observed)
        {
            line.MutationOccurred -= _lineMutated;
        }
        _observed.Clear();
    }
}
