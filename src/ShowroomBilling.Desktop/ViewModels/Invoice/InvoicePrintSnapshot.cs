using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

public sealed record InvoicePrintSnapshot(
    string InvoiceNumber, BillPayloadDto Payload, IReadOnlyList<KaratMasterEntry>? KaratMappings)
{
    public BillPrintContent ToPrintContent(CompanyProfile company) => new(
        InvoiceNumber: InvoiceNumber,
        BillDate: Payload.BillDate,
        PartyName: Payload.PartyName,
        PartyGstin: Payload.PartyGstin,
        PartyPhone: Payload.PartyPhone,
        PartyAddress: Payload.PartyAddress,
        Payment: Payload.Payment,
        Lines: PrintLineSubstitution.WithTallyMappedItemNames(Payload.Lines, KaratMappings),
        Totals: Payload.Totals,
        Notes: Payload.Notes,
        Company: company);
}
