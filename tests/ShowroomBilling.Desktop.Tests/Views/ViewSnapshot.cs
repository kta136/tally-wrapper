using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// Saves a laid-out view as a PNG when the <c>TALLYWRAPPER_VIEW_SNAPSHOTS</c>
/// environment variable names a folder. Off by default: it is a way to look at
/// every view after a XAML change without launching the app, not an assertion.
/// </summary>
/// <example>
/// <code>
/// $env:TALLYWRAPPER_VIEW_SNAPSHOTS = "$PWD\artifacts\view-snapshots"
/// dotnet test --project tests/ShowroomBilling.Desktop.Tests --filter "FullyQualifiedName~ViewSmokeTests"
/// </code>
/// </example>
internal static class ViewSnapshot
{
    public const string EnvironmentVariable = "TALLYWRAPPER_VIEW_SNAPSHOTS";

    private static readonly string? Folder = Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(Folder);

    /// <summary>Renders <paramref name="view"/> at its arranged size over the app background.</summary>
    public static void Save(FrameworkElement view, string name)
    {
        if (!IsEnabled || view.ActualWidth < 1 || view.ActualHeight < 1)
        {
            return;
        }

        var size = new Size(view.ActualWidth, view.ActualHeight);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
        {
            var background = System.Windows.Application.Current.TryFindResource("BgBrush") as Brush ?? Brushes.White;
            context.DrawRectangle(background, null, new Rect(size));
        }

        // The view is drawn in its own layout slot, on top of the backdrop. (A
        // VisualBrush would centre the view's whole drawn extent, shadows and
        // all, and so shift it.)
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(backdrop);
        bitmap.Render(view);

        Directory.CreateDirectory(Folder!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Folder!, name + ".png"));
        encoder.Save(file);
    }
}
