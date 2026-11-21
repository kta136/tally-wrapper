using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Infrastructure.Bills;

/// <summary>
/// How far the last few batches got, by the id their Desktop gave them. A batch is one request, so
/// a Desktop whose request was cancelled, timed out or lost its connection has no answer to read
/// how many bills were saved from; it asks here instead.
///
/// In memory only, and gone when the API restarts: a Desktop that finds nothing says it does not
/// know and shows the Bills tab. Nothing is stored with the bills, which carry no mark of a batch.
/// </summary>
public sealed class SyntheticBatchOutcomes
{
    /// <summary>Batches remembered. A Desktop asks within seconds of its request ending.</summary>
    private const int Kept = 20;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, SyntheticBatchOutcomeResponse> _byId = [];
    private readonly List<Guid> _oldestFirst = [];

    /// <summary>Starts the record of a batch. False when that id has been used: the same batch sent twice.</summary>
    public bool TryStart(Guid batchId, int plannedBillCount)
    {
        lock (_gate)
        {
            if (_byId.ContainsKey(batchId)) return false;

            // Only batches that have ended make room, the oldest first: one still running is the
            // one a Desktop will ask about.
            while (_oldestFirst.Count >= Kept)
            {
                var ended = _oldestFirst.FindIndex(id => _byId[id].Finished);
                if (ended < 0) break;
                _byId.Remove(_oldestFirst[ended]);
                _oldestFirst.RemoveAt(ended);
            }

            _byId[batchId] = new SyntheticBatchOutcomeResponse(
                Finished: false, plannedBillCount, BillCount: 0, TotalAmount: 0m, FirstInvoiceNumber: null, LastInvoiceNumber: null);
            _oldestFirst.Add(batchId);
            return true;
        }
    }

    /// <summary>One more bill of the batch is saved.</summary>
    public void Saved(Guid batchId, string? invoiceNumber, decimal grandTotal)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(batchId, out var soFar)) return;
            _byId[batchId] = soFar with
            {
                BillCount = soFar.BillCount + 1,
                TotalAmount = soFar.TotalAmount + grandTotal,
                FirstInvoiceNumber = soFar.FirstInvoiceNumber ?? invoiceNumber,
                LastInvoiceNumber = invoiceNumber,
            };
        }
    }

    /// <summary>
    /// The batch saves no more bills, whether it was made whole or stopped.
    /// <paramref name="lastSaveInDoubt"/>: it stopped at a save that may or may not have stored its bill.
    /// </summary>
    public void Finish(Guid batchId, bool lastSaveInDoubt = false)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(batchId, out var soFar))
                _byId[batchId] = soFar with { Finished = true, LastSaveInDoubt = lastSaveInDoubt };
        }
    }

    public SyntheticBatchOutcomeResponse? Find(Guid batchId)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(batchId);
        }
    }
}
