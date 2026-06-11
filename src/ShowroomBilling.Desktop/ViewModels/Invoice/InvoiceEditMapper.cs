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
            var row = new BillLineViewModel
            {
                ItemName = line.ItemName,
                Unit = string.IsNullOrWhiteSpace(line.Unit) ? ItemUnits.Gram : line.Unit!,
                Karat = line.Karat ?? string.Empty,
                GrossWeight = line.GrossWeight,
                LessWeight = line.LessWeight,
                WastagePercent = line.WastagePercent,
                LabourPerUnit = line.LabourPerUnit,
                DiamondRate = line.DiamondRate,
                Extra = line.Extra,
                StockName = line.StockName
            };

            if (itemMasters is not null)
            {
                row.SetItemMasterFromPayload(itemMasters.FirstOrDefault(m =>
                    string.Equals(m.Name, line.ItemName, StringComparison.OrdinalIgnoreCase)));
            }

            row.ItemCategory = InvoicePayloadMapper.ResolveLineItemCategory(line, row.ItemMaster);
            row.PricingMode = InvoicePayloadMapper.ResolveLinePricingMode(line, row.ItemMaster);

            if (karatMasters is not null && !string.IsNullOrWhiteSpace(line.Karat))
            {
                row.KaratMaster = karatMasters.FirstOrDefault(k =>
                    string.Equals(k.Label, line.Karat, StringComparison.OrdinalIgnoreCase));
            }

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
