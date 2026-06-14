using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

internal static class InvoiceEditMapper
{
    public static IReadOnlyList<BillLineViewModel> CreateRows(
        BillPayloadDto payload,
        IEnumerable<ItemMasterRowVm>? itemMasters,
        IEnumerable<KaratMasterRowVm>? karatMasters)
    {
        var rows = new List<BillLineViewModel>(payload.Lines.Count);
        foreach (var line in payload.Lines)
        {
            var row = new BillLineViewModel();
            ItemMasterRowVm? itemMaster = null;
            if (itemMasters is not null)
            {
                itemMaster = itemMasters.FirstOrDefault(m =>
                    string.Equals(m.Name, line.ItemName, StringComparison.OrdinalIgnoreCase));
            }

            KaratMasterRowVm? karatMaster = null;
            if (karatMasters is not null && !string.IsNullOrWhiteSpace(line.Karat))
            {
                karatMaster = karatMasters.FirstOrDefault(k =>
                    string.Equals(k.Label, line.Karat, StringComparison.OrdinalIgnoreCase));
            }

            row.ApplyPayloadValues(
                line,
                InvoicePayloadMapper.ResolveLineItemCategory(line, itemMaster),
                InvoicePayloadMapper.ResolveLinePricingMode(line, itemMaster),
                itemMaster,
                karatMaster);

            rows.Add(row);
        }

        return rows;
    }

    public static string ResolvePayment(string? payment, IReadOnlyList<string> paymentOptions, string currentPayment)
    {
        if (!string.IsNullOrWhiteSpace(payment)
            && paymentOptions.Any(o => string.Equals(o, payment, StringComparison.OrdinalIgnoreCase)))
        {
            return paymentOptions.First(o => string.Equals(o, payment, StringComparison.OrdinalIgnoreCase));
        }

        return currentPayment;
    }
}
