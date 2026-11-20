using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// Turns the SVG pages of <see cref="BillPrintRenderer.GeneratePageSvgs"/> into
/// what a printer is sent: text as glyphs of the installed font, lines as
/// paths, the logo and signature as the images they are. The printer draws
/// those at its own resolution, where a picture of the page had to be made,
/// packed and unpacked at 600 DPI first.
/// <para>
/// It reads only what QuestPDF writes for a bill and refuses everything else
/// with a <see cref="NotSupportedException"/>: an element or attribute it does
/// not know, a font it cannot find or may not embed, a letter the font has no
/// glyph for. A refused bill is printed as pictures, as every bill used to be,
/// so nothing is ever drawn nearly right.
/// </para>
/// <para>
/// One thing cannot be seen in the pages themselves. The SVG names each glyph
/// by the letter it stands for, and a glyph that stands for no single letter
/// (two Devanagari letters joined into one shape, a ligature) is left out
/// without a trace. So <see cref="RefuseTextBeyondPlainLetters"/> looks at the
/// bill's text first, and only Latin text, digits, punctuation and signs are
/// drawn this way.
/// </para>
/// </summary>
internal sealed partial class VectorPrintPages
{
    private const double PointsToDips = 96.0 / 72.0;

    // A4 in points, as QuestPDF lays the page out. Its SVG gives whole numbers.
    private const double A4WidthPoints = 595.28;
    private const double A4HeightPoints = 841.89;

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    private static readonly JsonSerializerOptions UnescapedJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] PaintAttributes =
    [
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-miterlimit", "stroke-opacity", "transform",
    ];

    // The sixteen names Skia writes instead of a hex value.
    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.Ordinal)
    {
        ["black"] = Colors.Black,
        ["silver"] = Colors.Silver,
        ["gray"] = Colors.Gray,
        ["white"] = Colors.White,
        ["maroon"] = Colors.Maroon,
        ["red"] = Colors.Red,
        ["purple"] = Colors.Purple,
        ["fuchsia"] = Colors.Fuchsia,
        ["green"] = Colors.Green,
        ["lime"] = Colors.Lime,
        ["olive"] = Colors.Olive,
        ["yellow"] = Colors.Yellow,
        ["navy"] = Colors.Navy,
        ["blue"] = Colors.Blue,
        ["teal"] = Colors.Teal,
        ["aqua"] = Colors.Aqua,
    };

    private readonly Dictionary<(string Families, string? Weight, string? Style), GlyphTypeface> _fonts = [];
    private readonly Dictionary<string, BitmapSource> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<Color, Brush> _brushes = [];

    private VectorPrintPages()
    {
    }

    /// <summary>
    /// One element per page, laid out on A4 at 96 units an inch. Call it on
    /// the thread that prints: the elements belong to the thread that made them.
    /// </summary>
    public static IReadOnlyList<UIElement> Build(IReadOnlyList<string> svgPages)
    {
        ArgumentNullException.ThrowIfNull(svgPages);

        var builder = new VectorPrintPages();
        var pages = new List<UIElement>(svgPages.Count);
        foreach (var svg in svgPages)
        {
            pages.Add(builder.BuildPage(svg));
        }

        return pages;
    }

    /// <summary>
    /// Refuses a bill that holds a letter of a script whose letters join or
    /// change shape next to each other. Every text of the bill is looked at,
    /// whatever field it is in.
    /// </summary>
    public static void RefuseTextBeyondPlainLetters(BillPrintContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        // As JSON every text of the bill is in one string, also a field added later.
        foreach (var character in JsonSerializer.Serialize(content, UnescapedJson))
        {
            if (!IsPlainLetter(character))
            {
                throw Refused($"the bill has the letter U+{(int)character:X4}, which may be drawn joined to its neighbours");
            }
        }
    }

    // Basic Latin to Latin Extended-B, then general punctuation, currency
    // signs (the rupee), letter-like signs, number forms, arrows and maths
    // signs (the minus). Accents that are typed apart from their letter, and
    // every other script, are left out.
    private static bool IsPlainLetter(char character) => character is
        '\t' or '\n' or '\r'
        or (>= '\u0020' and <= '\u007E')
        or (>= '\u00A0' and <= '\u024F')
        or (>= '\u2000' and <= '\u206F')
        or (>= '\u20A0' and <= '\u20CF')
        or (>= '\u2100' and <= '\u22FF');

    /// <summary>
    /// The image as it looks at this opacity on white paper, with nothing
    /// see-through left in it. An SVG page leaves out whatever is drawn
    /// half-transparent, and a printer driver need not blend it well either;
    /// the watermark is the lowest thing on the page, so on paper the two are
    /// the same.
    /// </summary>
    public static byte[] FadeOntoWhite(byte[] image, double opacity)
    {
        ArgumentNullException.ThrowIfNull(image);
        opacity = Math.Clamp(opacity, 0, 1);

        var source = new FormatConvertedBitmap(Decode(image), PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var strength = pixels[i + 3] / 255.0 * opacity;
            pixels[i] = Fade(pixels[i], strength);
            pixels[i + 1] = Fade(pixels[i + 1], strength);
            pixels[i + 2] = Fade(pixels[i + 2], strength);
            pixels[i + 3] = 255;
        }

        var faded = BitmapSource.Create(
            source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(faded));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();

        static byte Fade(byte channel, double strength) => (byte)Math.Round(255 + (channel - 255) * strength);
    }

    private Canvas BuildPage(string svg)
    {
        var root = XDocument.Parse(svg, LoadOptions.PreserveWhitespace).Root;
        if (root is null || root.Name != Svg + "svg")
        {
            throw Refused("the page is not an SVG drawing");
        }

        var size = Attributes(root, "width", "height");
        if (Math.Abs(Number(Required(size, "width")) - A4WidthPoints) > 1.5
            || Math.Abs(Number(Required(size, "height")) - A4HeightPoints) > 1.5)
        {
            throw Refused("the page is not A4");
        }

        // The drawing is in points; the page is in WPF units.
        var canvas = new Canvas { RenderTransform = Frozen(new ScaleTransform(PointsToDips, PointsToDips)) };
        var definitions = new Dictionary<string, (BitmapSource Image, double Width, double Height)>(StringComparer.Ordinal);

        foreach (var element in root.Elements())
        {
            if (element.Name.Namespace != Svg)
            {
                throw Refused($"element {element.Name}");
            }

            switch (element.Name.LocalName)
            {
                case "text":
                    AddText(canvas, element);
                    break;
                case "path":
                    AddPath(canvas, element);
                    break;
                case "rect":
                    AddRectangle(canvas, element);
                    break;
                case "defs":
                    ReadDefinitions(element, definitions);
                    break;
                case "use":
                    AddImage(canvas, element, definitions);
                    break;
                default:
                    throw Refused($"element <{element.Name.LocalName}>");
            }
        }

        return canvas;
    }

    private void AddText(Canvas canvas, XElement element)
    {
        var attributes = Attributes(
            element, "transform", "font-size", "font-weight", "font-style", "font-family", "x", "y", "fill", "fill-opacity");
        if (element.HasElements)
        {
            throw Refused("text in parts");
        }

        // Skia indents the text and its closing tag; it never starts a run with a space of the text's own.
        var text = element.Value.TrimStart().TrimEnd('\n', '\r', '\t');
        var xs = Numbers(attributes.GetValueOrDefault("x"));
        var ys = Numbers(attributes.GetValueOrDefault("y"));
        if (text.Length == 0 && xs.Count == 0)
        {
            return;
        }

        // One position for each letter, and one line height for all or one each.
        if (xs.Count != text.Length || (ys.Count != 1 && ys.Count != text.Length))
        {
            throw Refused("text whose letters and positions do not match");
        }

        var font = ReadFont(
            Required(attributes, "font-family"),
            attributes.GetValueOrDefault("font-weight"),
            attributes.GetValueOrDefault("font-style"));
        var size = Number(Required(attributes, "font-size"));
        if (size <= 0)
        {
            throw Refused("text with no size");
        }

        var fill = ReadPaint(attributes.GetValueOrDefault("fill"), attributes.GetValueOrDefault("fill-opacity"), Colors.Black)
            ?? throw Refused("text with no fill");
        var transform = ReadTransform(attributes.GetValueOrDefault("transform"));

        var glyphs = new ushort[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            // A letter Skia could not name (a joined form, say) or one outside
            // the font would come out as another shape than the PDF shows.
            if (char.IsSurrogate(text[i]) || !font.CharacterToGlyphMap.TryGetValue(text[i], out glyphs[i]))
            {
                throw Refused($"a letter the font {attributes["font-family"]} cannot draw as written");
            }
        }

        // A run of glyphs goes left to right on one line. Where the next
        // letter sits further left or on another line, a new run starts.
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i < text.Length && xs[i] >= xs[i - 1] && Y(i) == Y(i - 1))
            {
                continue;
            }

            var indices = new StringBuilder();
            for (var j = start; j < i; j++)
            {
                if (j > start)
                {
                    indices.Append(';');
                }

                indices.Append(glyphs[j].ToString(CultureInfo.InvariantCulture));
                if (j + 1 < i)
                {
                    // The advance to the next letter, in hundredths of the font size.
                    indices.Append(',').Append(
                        ((xs[j + 1] - xs[j]) * 100 / size).ToString("0.####", CultureInfo.InvariantCulture));
                }
            }

            canvas.Children.Add(new Glyphs
            {
                FontUri = font.FontUri,
                FontRenderingEmSize = size,
                UnicodeString = text[start..i],
                Indices = indices.ToString(),
                OriginX = xs[start],
                OriginY = Y(start),
                Fill = fill,
                RenderTransform = transform,
            });
            start = i;
        }

        double Y(int index) => ys.Count == 1 ? ys[0] : ys[index];
    }

    private void AddPath(Canvas canvas, XElement element)
    {
        var attributes = Attributes(element, [.. PaintAttributes, "d"]);
        var rule = attributes.GetValueOrDefault("fill-rule") switch
        {
            null or "nonzero" => "F1 ",
            "evenodd" => "F0 ",
            var other => throw Refused($"fill rule {other}"),
        };

        AddShape(canvas, attributes, Geometry.Parse(rule + Required(attributes, "d")));
    }

    private void AddRectangle(Canvas canvas, XElement element)
    {
        var attributes = Attributes(element, [.. PaintAttributes, "x", "y", "width", "height"]);
        var bounds = new Rect(
            Number(attributes.GetValueOrDefault("x") ?? "0"),
            Number(attributes.GetValueOrDefault("y") ?? "0"),
            Number(Required(attributes, "width")),
            Number(Required(attributes, "height")));

        AddShape(canvas, attributes, Frozen(new RectangleGeometry(bounds)));
    }

    private void AddShape(Canvas canvas, Dictionary<string, string> attributes, Geometry geometry)
    {
        var fill = ReadPaint(attributes.GetValueOrDefault("fill"), attributes.GetValueOrDefault("fill-opacity"), Colors.Black);
        var stroke = ReadPaint(attributes.GetValueOrDefault("stroke"), attributes.GetValueOrDefault("stroke-opacity"), null);
        if (fill is null && stroke is null)
        {
            return;
        }

        var shape = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = fill,
            Stroke = stroke,
            RenderTransform = ReadTransform(attributes.GetValueOrDefault("transform")),
        };
        if (stroke is not null)
        {
            shape.StrokeThickness = Number(attributes.GetValueOrDefault("stroke-width") ?? "1");
            shape.StrokeMiterLimit = Number(attributes.GetValueOrDefault("stroke-miterlimit") ?? "4");
        }

        canvas.Children.Add(shape);
    }

    private void ReadDefinitions(
        XElement definitions,
        Dictionary<string, (BitmapSource Image, double Width, double Height)> images)
    {
        foreach (var element in definitions.Elements())
        {
            if (element.Name != Svg + "image")
            {
                throw Refused($"definition <{element.Name.LocalName}>");
            }

            var attributes = Attributes(element, "id", "width", "height", "href");
            images[Required(attributes, "id")] = (
                ReadImage(Required(attributes, "href")),
                Number(Required(attributes, "width")),
                Number(Required(attributes, "height")));
        }
    }

    private static void AddImage(
        Canvas canvas,
        XElement element,
        Dictionary<string, (BitmapSource Image, double Width, double Height)> images)
    {
        var attributes = Attributes(element, "transform", "href");
        var reference = Required(attributes, "href");
        if (!reference.StartsWith('#') || !images.TryGetValue(reference[1..], out var image))
        {
            throw Refused("an image that is not on the page");
        }

        canvas.Children.Add(new Image
        {
            Source = image.Image,
            Width = image.Width,
            Height = image.Height,
            Stretch = Stretch.Fill,
            RenderTransform = ReadTransform(attributes.GetValueOrDefault("transform")),
        });
    }

    private BitmapSource ReadImage(string dataUri)
    {
        if (_images.TryGetValue(dataUri, out var known))
        {
            return known;
        }

        const string marker = ";base64,";
        var at = dataUri.IndexOf(marker, StringComparison.Ordinal);
        if (!dataUri.StartsWith("data:image/", StringComparison.Ordinal) || at < 0)
        {
            throw Refused("an image that is not part of the page");
        }

        var image = Decode(Convert.FromBase64String(dataUri[(at + marker.Length)..]));
        _images[dataUri] = image;
        return image;
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        // A BitmapImage, not a decoder's frame: the XPS writer tells frames
        // read from a stream apart by an address they do not have, and wrote
        // the logo a second time where the signature belonged.
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private GlyphTypeface ReadFont(string families, string? weight, string? style)
    {
        if (_fonts.TryGetValue((families, weight, style), out var known))
        {
            return known;
        }

        // Skia lists a face's own name first and its family last ("Segoe UI Semibold, Segoe UI").
        var names = families.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            throw Refused("text with no font");
        }

        var typeface = new Typeface(
            new FontFamily(names[^1]),
            style switch
            {
                null or "normal" => FontStyles.Normal,
                "italic" => FontStyles.Italic,
                "oblique" => FontStyles.Oblique,
                _ => throw Refused($"font style {style}"),
            },
            FontWeight.FromOpenTypeWeight(Weight(weight)),
            FontStretches.Normal);

        // WPF answers an unknown family with another font, and a missing bold
        // or italic with a faked one. Either would not be the PDF's letters.
        if (!typeface.TryGetGlyphTypeface(out var font)
            || font.StyleSimulations != StyleSimulations.None
            || !font.FamilyNames.Values.Concat(font.Win32FamilyNames.Values)
                .Any(name => names.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            throw Refused($"the font {families} is not installed in that weight and style");
        }

        if (font.EmbeddingRights is FontEmbeddingRight.RestrictedLicense)
        {
            throw Refused($"the font {families} may not be sent to a printer");
        }

        _fonts[(families, weight, style)] = font;
        return font;
    }

    /// <summary>
    /// The weight a face has, from what Skia's SVG writer calls it. Its table
    /// has "normal" where 400 belongs and the numbers after it one place on,
    /// so bold (700) is written "600" and semi-bold (600) "500".
    /// </summary>
    private static int Weight(string? written) => written switch
    {
        null or "normal" => 400,
        "100" => 100,
        "200" => 200,
        "300" => 300,
        "400" => 500,
        "500" => 600,
        "600" => 700,
        "bold" => 800,
        "800" => 900,
        "900" => 999,
        _ => throw Refused($"font weight {written}"),
    };

    private Brush? ReadPaint(string? value, string? opacity, Color? unset)
    {
        Color color;
        if (value is null)
        {
            if (unset is null)
            {
                return null;
            }

            color = unset.Value;
        }
        else if (value == "none")
        {
            return null;
        }
        else if (NamedColors.TryGetValue(value, out var named))
        {
            color = named;
        }
        else if (HexColor().Match(value) is { Success: true } hex)
        {
            var digits = hex.Groups[1].Value;
            if (digits.Length == 3)
            {
                digits = string.Concat(digits.Select(digit => new string(digit, 2)));
            }

            color = Color.FromRgb(
                Convert.ToByte(digits[..2], 16),
                Convert.ToByte(digits[2..4], 16),
                Convert.ToByte(digits[4..], 16));
        }
        else if (RgbColor().Match(value) is { Success: true } rgb)
        {
            color = Color.FromRgb(
                byte.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture),
                byte.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture),
                byte.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        else
        {
            throw Refused($"colour {value}");
        }

        if (opacity is not null)
        {
            color.A = (byte)Math.Round(Math.Clamp(Number(opacity), 0, 1) * 255);
        }

        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = Frozen(new SolidColorBrush(color));
            _brushes[color] = brush;
        }

        return brush;
    }

    private static Transform ReadTransform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return System.Windows.Media.Transform.Identity;
        }

        // A list is applied right to left: the last one written moves the shape first.
        var matrix = Matrix.Identity;
        var read = 0;
        foreach (Match step in TransformStep().Matches(value))
        {
            read += step.Length;
            var numbers = Numbers(step.Groups[2].Value);
            matrix.Prepend((step.Groups[1].Value, numbers.Count) switch
            {
                ("translate", 1) => new Matrix(1, 0, 0, 1, numbers[0], 0),
                ("translate", 2) => new Matrix(1, 0, 0, 1, numbers[0], numbers[1]),
                ("scale", 1) => new Matrix(numbers[0], 0, 0, numbers[0], 0, 0),
                ("scale", 2) => new Matrix(numbers[0], 0, 0, numbers[1], 0, 0),
                ("matrix", 6) => new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]),
                _ => throw Refused($"transform {step.Value}"),
            });
        }

        if (read != value.Length)
        {
            throw Refused($"transform {value}");
        }

        return Frozen(new MatrixTransform(matrix));
    }

    /// <summary>The element's attributes by name; one that is not expected refuses the page.</summary>
    private static Dictionary<string, string> Attributes(XElement element, params string[] expected)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            var name = attribute.Name.LocalName;
            var known = attribute.Name.Namespace == XNamespace.None || (attribute.Name.Namespace == XLink && name == "href");
            if (!known || !expected.Contains(name, StringComparer.Ordinal))
            {
                throw Refused($"{attribute.Name.LocalName} on <{element.Name.LocalName}>");
            }

            attributes[name] = attribute.Value;
        }

        return attributes;
    }

    private static string Required(Dictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) ? value : throw Refused($"a missing {name}");

    private static double Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw Refused($"number {value}");

    private static List<double> Numbers(string? values) =>
        (values ?? string.Empty)
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Number)
            .ToList();

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static NotSupportedException Refused(string what) =>
        new($"The page cannot be printed as text and lines: {what}.");

    [GeneratedRegex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^rgb\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$")]
    private static partial Regex RgbColor();

    [GeneratedRegex(@"\s*([a-zA-Z]+)\s*\(([^)]*)\)\s*,?")]
    private static partial Regex TransformStep();
}
