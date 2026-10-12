using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ShowroomBilling.ServerTray.Ui;

/// <summary>What a status means, whatever colour it is drawn in.</summary>
internal enum Tone
{
    Neutral,
    Ok,
    Warn,
    Err,
}

/// <summary>The text sizes of the design, as the Desktop names them.</summary>
internal enum TextRole
{
    /// <summary>13.5 px: everything that is not one of the others.</summary>
    Body,

    /// <summary>13.5 px semibold: row titles, card headings, buttons.</summary>
    BodyStrong,

    /// <summary>12.5 px: context lines under a heading.</summary>
    Small,

    /// <summary>12.5 px semibold: chips and table headings.</summary>
    SmallStrong,

    /// <summary>17 px semibold: the title of a dialog.</summary>
    DialogTitle,

    /// <summary>22 px semibold: the one sentence the dashboard opens on.</summary>
    Title,

    /// <summary>12.5 px fixed width: an address or a path, never prose.</summary>
    Mono,

    /// <summary>11 px fixed width: the key chip on a button.</summary>
    Key,

    /// <summary>16 px glyphs of the Windows icon font.</summary>
    Icon,
}

/// <summary>
/// The "Ledger Clear" tokens the server dashboard draws with. They are a copy
/// of the Desktop's <c>Resources/DesignTokens.xaml</c>: this project is
/// WinForms and cannot read that file. A value changed there is changed here.
/// </summary>
internal static class Theme
{
    public static readonly Color Page = Rgb(0xF4F5F8);
    public static readonly Color Panel = Color.White;
    public static readonly Color Sunken = Rgb(0xF3F5F8);
    public static readonly Color Hover = Rgb(0xEAEDF4);

    public static readonly Color Border = Rgb(0xD5D9E2);
    public static readonly Color BorderInput = Rgb(0x8C93A4);
    public static readonly Color Divider = Rgb(0xE6E9EF);

    public static readonly Color Ink = Rgb(0x1B1F2A);
    public static readonly Color InkMuted = Rgb(0x464D5C);
    public static readonly Color InkSoft = Rgb(0x626A7A);
    public static readonly Color InkDisabled = Rgb(0x8A91A0);
    public static readonly Color DisabledFill = Rgb(0xE4E6EC);

    public static readonly Color Accent = Rgb(0x4F46E5);
    public static readonly Color AccentHover = Rgb(0x4338CA);
    public static readonly Color AccentPressed = Rgb(0x3730A3);
    public static readonly Color AccentInk = Rgb(0x3730A3);
    public static readonly Color AccentSoft = Rgb(0xEEF2FF);
    public static readonly Color AccentSoftPressed = Rgb(0xE0E7FF);

    public static readonly Color Err = Rgb(0xC53333);
    public static readonly Color ErrText = Rgb(0xB42323);
    public static readonly Color ErrWash = Rgb(0xFDF1EF);
    public static readonly Color ErrSoft = Rgb(0xFBE3DF);
    public static readonly Color ErrPressed = Rgb(0x7A1616);

    /// <summary>The outline of a banner of that tone.</summary>
    public static Color Edge(Tone tone) => tone switch
    {
        Tone.Ok => Rgb(0xA9D3B5),
        Tone.Warn => Rgb(0xE7C48F),
        Tone.Err => Rgb(0xE5A9A1),
        _ => Border,
    };

    /// <summary>Corner radii at 96 DPI: chips, buttons and inputs, cards.</summary>
    public const float RadiusChip = 4;
    public const float RadiusButton = 6;
    public const float RadiusCard = 8;

    /// <summary>The dot that goes with a tone.</summary>
    public static Color Dot(Tone tone) => tone switch
    {
        Tone.Ok => Rgb(0x3E8E54),
        Tone.Warn => Rgb(0xC07D2B),
        Tone.Err => Err,
        _ => Rgb(0x9AA1B0),
    };

    /// <summary>The fill of a chip of that tone.</summary>
    public static Color Soft(Tone tone) => tone switch
    {
        Tone.Ok => Rgb(0xE3F1E7),
        Tone.Warn => Rgb(0xFBEFDC),
        Tone.Err => ErrSoft,
        _ => Rgb(0xE4E6EC),
    };

    /// <summary>The text on a chip of that tone.</summary>
    public static Color InkOf(Tone tone) => tone switch
    {
        Tone.Ok => Rgb(0x1F5130),
        Tone.Warn => Rgb(0x6E4213),
        Tone.Err => Rgb(0x7A1616),
        _ => InkMuted,
    };

    // Windows-native, as on the Desktop. GDI lists each weight of the variable
    // font as a family of its own, with the name cut at 31 characters.
    private static readonly string UiFamily =
        FirstInstalled("Segoe UI Variable Text", "Segoe UI") ?? FontFamily.GenericSansSerif.Name;

    private static readonly string? StrongFamily =
        FirstInstalled("Segoe UI Variable Text Semibold", "Segoe UI Semibold");

    private static readonly string MonoFamily =
        FirstInstalled("Cascadia Mono", "Consolas") ?? FontFamily.GenericMonospace.Name;

    // Windows 11 has the first, Windows 10 the second; the glyphs used here
    // sit at the same code points in both.
    private static readonly string? IconFamily = FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets");

    /// <summary>Whether <see cref="TextRole.Icon"/> draws glyphs on this PC.</summary>
    public static bool HasIcons => IconFamily is not null;

    // A point size turns into pixels at the DPI the process started with,
    // whatever monitor a window is on now.
    private static readonly int SystemDpi = ReadSystemDpi();

    private static readonly ConcurrentDictionary<(TextRole Role, int Dpi), Font> Fonts = new();

    /// <summary>
    /// The font to set on a label or a text box. WinForms rescales a control's
    /// own font when the window moves to a monitor with another scaling.
    /// </summary>
    public static Font Font(TextRole role) => Font(role, SystemDpi);

    /// <summary>The font to paint with on a control whose monitor runs at <paramref name="deviceDpi"/>.</summary>
    public static Font Font(TextRole role, int deviceDpi) =>
        Fonts.GetOrAdd((role, deviceDpi), static key => Create(key.Role, key.Dpi / (float)SystemDpi));

    /// <summary>A length of the design, given at 96 DPI, in this control's pixels.</summary>
    public static int Px(Control control, float designPixels) =>
        (int)Math.Round(designPixels * control.DeviceDpi / 96f, MidpointRounding.AwayFromZero);

    /// <summary>The same, unrounded, for outlines and corner radii.</summary>
    public static float PxF(Control control, float designPixels) => designPixels * control.DeviceDpi / 96f;

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A filled, outlined rounded box, as every card, button, chip and input is.</summary>
    public static void FillRounded(
        Graphics graphics,
        RectangleF bounds,
        float radius,
        Color fill,
        Color? outline = null,
        float outlineWidth = 1)
    {
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            // Half a pen in from the edge, so the outline is whole pixels.
            var inset = outline is null ? 0 : outlineWidth / 2;
            var box = RectangleF.Inflate(bounds, -inset, -inset);
            using var path = RoundedRectangle(box, Math.Max(0, radius - inset));
            using var brush = new SolidBrush(fill);
            graphics.FillPath(brush, path);
            if (outline is { } colour)
            {
                using var pen = new Pen(colour, outlineWidth);
                graphics.DrawPath(pen, path);
            }
        }
        finally
        {
            graphics.SmoothingMode = smoothing;
        }
    }

    private static Font Create(TextRole role, float scale)
    {
        if (role == TextRole.Icon)
        {
            return new Font(IconFamily ?? UiFamily, 12f * scale, FontStyle.Regular, GraphicsUnit.Point);
        }

        var (points, strong, mono) = role switch
        {
            TextRole.BodyStrong => (10f, true, false),
            TextRole.Small => (9.5f, false, false),
            TextRole.SmallStrong => (9.5f, true, false),
            TextRole.DialogTitle => (12.75f, true, false),
            TextRole.Title => (16.5f, true, false),
            TextRole.Mono => (9.5f, false, true),
            TextRole.Key => (8.25f, false, true),
            _ => (10f, false, false),
        };

        var size = points * scale;
        if (mono)
        {
            return new Font(MonoFamily, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        if (!strong)
        {
            return new Font(UiFamily, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        return StrongFamily is not null
            ? new Font(StrongFamily, size, FontStyle.Regular, GraphicsUnit.Point)
            : new Font(UiFamily, size, FontStyle.Bold, GraphicsUnit.Point);
    }

    private static string? FirstInstalled(params string[] families)
    {
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return families.FirstOrDefault(names.Contains);
    }

    private static int ReadSystemDpi()
    {
        using var screen = Graphics.FromHwnd(IntPtr.Zero);
        return (int)Math.Round(screen.DpiX);
    }

    private static Color Rgb(int value) => Color.FromArgb(0xFF, (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
}
