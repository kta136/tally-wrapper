using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal static class BillDetailsPrintMapper
{
    public static BillPrintContent? CreatePrintContent(
        BillResponse bill,
        CompanyProfile company,
        IReadOnlyList<KaratMasterEntry>? karatMappings = null)
    {
        var payload = bill.CurrentRevision?.Payload;
        var totals = payload?.Totals;
        if (payload is null || totals is null)
        {
            return null;
        }

        return new BillPrintContent(
            InvoiceNumber: bill.InvoiceNumber ?? bill.Id.ToString("N")[..8],
            BillDate: payload.BillDate,
            PartyName: payload.PartyName,
            PartyGstin: payload.PartyGstin,
            PartyPhone: payload.PartyPhone,
            PartyAddress: payload.PartyAddress,
            Payment: payload.Payment,
            Rate24Kt: payload.Rate24Kt,
            Lines: PrintLineSubstitution.WithTallyMappedItemNames(payload.Lines, karatMappings),
            Totals: totals,
            Notes: payload.Notes,
            Company: company);
    }
}
