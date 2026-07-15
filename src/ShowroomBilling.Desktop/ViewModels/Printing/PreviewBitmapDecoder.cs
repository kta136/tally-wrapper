using System.IO;
using System.Windows.Media.Imaging;

namespace ShowroomBilling.Desktop.ViewModels.Printing;

internal static class PreviewBitmapDecoder
{
    internal static BitmapSource Decode(byte[] encodedImage)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);

        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(encodedImage, writable: false);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
