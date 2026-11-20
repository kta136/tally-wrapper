using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace ShowroomBilling.Printing;

public sealed class BillPrintRenderer
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

    /// <summary>
    /// Each page as an SVG drawing: text as text with a position for every
    /// letter, lines as lines, the logo and signature as the images they are.
    /// This is what a direct print is built from, so the printer gets shapes
    /// to draw at its own resolution instead of a picture of the page.
    /// (QuestPDF's own XPS output was removed in 2026.8.0.) An SVG page leaves
    /// out anything drawn half-transparent, so a watermark has to be given
    /// here already faded, at full opacity.
    /// </summary>
    public IReadOnlyList<string> GeneratePageSvgs(PrintDocumentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var document = new BillDocument(options);
        return document.GenerateSvg().ToList();
    }
}
