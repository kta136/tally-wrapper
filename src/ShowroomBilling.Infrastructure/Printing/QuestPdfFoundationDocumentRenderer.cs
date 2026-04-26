using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ShowroomBilling.Application.Printing;

namespace ShowroomBilling.Infrastructure.Printing;

public sealed class QuestPdfFoundationDocumentRenderer : IInvoiceDocumentRenderer
{
    public byte[] RenderFoundationDocument(string title, string detail)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(24);
                page.DefaultTextStyle(TextStyle.Default.FontSize(12));
                page.Content().Column(column =>
                {
                    column.Spacing(12);
                    column.Item().Text(title).FontSize(20).SemiBold();
                    column.Item().Text(detail);
                    column.Item().Text("Foundation scaffold only. Canonical invoice composition arrives in later phases.");
                });
            });
        }).GeneratePdf();
    }
}
