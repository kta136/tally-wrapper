namespace ShowroomBilling.Application.Printing;

public interface IInvoiceDocumentRenderer
{
    byte[] RenderFoundationDocument(string title, string detail);
}
