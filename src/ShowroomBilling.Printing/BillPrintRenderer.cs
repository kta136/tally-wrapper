using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace ShowroomBilling.Printing;

public interface IBillPrintRenderer
{
    byte[] GeneratePdf(PrintDocumentOptions options);
    IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int imageDpi = 150);
}

public sealed class BillPrintRenderer : IBillPrintRenderer
{
    static BillPrintRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] GeneratePdf(PrintDocumentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var document = new BillDocument(options);
        return document.GeneratePdf();
    }

    public IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int imageDpi = 150)
    {
        ArgumentNullException.ThrowIfNull(options);
        var document = new BillDocument(options);
        var images = document.GenerateImages(new ImageGenerationSettings
        {
            ImageFormat = ImageFormat.Png,
            RasterDpi = imageDpi,
        });
        return images.ToList();
    }
}
