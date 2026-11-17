using System.IO;
using System.IO.Packaging;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// The document a print is made of when its pages are pictures: written as XPS here, as a print
/// queue would be sent it, and read back. No printer takes part.
/// </summary>
public sealed class PrintDispatcherPictureDocumentTests
{
    [Fact]
    public void Each_page_picture_is_written_as_it_is_on_an_A4_page_of_its_own_in_order()
    {
        var pictures = new[] { Png(40, 60, Colors.Navy), Png(30, 50, Colors.DarkRed), Png(20, 30, Colors.DarkGreen) };

        var written = OnSta(() => WrittenAsXps(PrintDispatcher.BuildFixedDocument(pictures)));

        Assert.Equal(3, written.Pages);
        Assert.All(written.PageSizes, size => Assert.Equal(("793.7", "1122.5"), size));

        // The same pictures, pixel for pixel: reading them only while they are written
        // (which is what keeps a job of many pages from holding them all) loses nothing.
        Assert.Equal(
            [(20, 30, Colors.DarkGreen), (30, 50, Colors.DarkRed), (40, 60, Colors.Navy)],
            written.Images.OrderBy(image => image.Width));
    }

    [Fact]
    public void No_page_picture_is_unpacked_before_its_page_is_written()
    {
        // A page at 600 DPI is about 140 MB unpacked. Unpacked when the document was built,
        // a job of six pages held 1.7 GB at once.
        var caches = OnSta(() => PrintDispatcher.BuildFixedDocument([Png(40, 60, Colors.Navy), Png(30, 50, Colors.DarkRed)])
            .Pages
            .Select(page => ((BitmapImage)((Image)page.Child.Children[0]).Source).CacheOption)
            .ToList());

        Assert.Equal([BitmapCacheOption.None, BitmapCacheOption.None], caches);
    }

    private sealed record Written(int Pages, IReadOnlyList<(string? Width, string? Height)> PageSizes, IReadOnlyList<(int Width, int Height, Color Colour)> Images);

    private static Written WrittenAsXps(FixedDocument document)
    {
        using var buffer = new MemoryStream();
        using (var package = Package.Open(buffer, FileMode.Create, FileAccess.ReadWrite))
        {
            var address = new Uri($"pack://print-test-{Guid.NewGuid():N}.xps");
            PackageStore.AddPackage(address, package);
            try
            {
                using var xps = new XpsDocument(package, CompressionOption.Fast, address.AbsoluteUri);
                XpsDocument.CreateXpsDocumentWriter(xps).Write(document);
            }
            finally
            {
                PackageStore.RemovePackage(address);
            }
        }

        buffer.Position = 0;
        var sizes = new List<(string?, string?)>();
        var images = new List<(int, int, Color)>();
        using var read = Package.Open(buffer, FileMode.Open, FileAccess.Read);
        foreach (var part in read.GetParts())
        {
            using var stream = part.GetStream();
            if (part.Uri.ToString().EndsWith(".fpage", StringComparison.OrdinalIgnoreCase))
            {
                var page = System.Xml.Linq.XDocument.Load(stream).Root!;
                sizes.Add(((string?)page.Attribute("Width"), (string?)page.Attribute("Height")));
            }
            else if (part.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = copy;
                image.EndInit();

                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, image.PixelWidth * 4, 0);
                var first = Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
                Assert.All(pixels.Chunk(4), pixel => Assert.Equal(first, Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0])));
                images.Add((image.PixelWidth, image.PixelHeight, first));
            }
        }

        return new Written(sizes.Count, sizes, images);
    }

    private static byte[] Png(int width, int height, Color colour)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (colour.B, colour.G, colour.R, colour.A);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(
            BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // WPF elements belong to the thread that made them, and want an STA one.
    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
