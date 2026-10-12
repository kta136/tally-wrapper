using System.ComponentModel;

namespace ShowroomBilling.ServerTray.Ui;

/// <summary>
/// A white panel with a hairline and 8 px corners. It is a table, so what it
/// holds is laid out in it directly, one row under the next.
/// </summary>
internal sealed class Card : TableLayoutPanel
{
    public Card()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 0, 12);
        Padding = new Padding(16);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The corners show what the card lies on. Nothing inside may reach
        // them: keep at least the radius of padding above and below.
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Page);
        Theme.FillRounded(
            e.Graphics,
            new RectangleF(0, 0, Width, Height),
            Theme.PxF(this, Theme.RadiusCard),
            BackColor,
            Theme.Border);
    }
}

/// <summary>
/// Text, drawn from the left edge of its box with no padding, so a heading
/// lines up with the table and the field under it. A wrapping one takes the
/// width its container gives it and grows downwards.
/// </summary>
internal sealed class TextLabel : Control
{
    private readonly TextRole _role;
    private readonly bool _wrap;

    public TextLabel(TextRole role, Color colour, string text = "", bool wrap = false)
    {
        _role = role;
        _wrap = wrap;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        ForeColor = colour;
        AutoSize = true;
        TabStop = false;
        Margin = Padding.Empty;
        if (wrap)
        {
            Dock = DockStyle.Fill;
        }

        Text = text;
    }

    private TextFormatFlags Flags =>
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
        | (_wrap
            ? TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl
            : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var font = Theme.Font(_role, DeviceDpi);
        var text = Text.Length == 0 ? " " : Text;
        // Measured without the ellipsis flag: with it, and no width to fit in,
        // the answer is the width of one letter and three dots.
        var natural = TextRenderer.MeasureText(
            text,
            font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        if (!_wrap || proposedSize.Width <= 1 || proposedSize.Width >= natural.Width)
        {
            return natural;
        }

        var wrapped = TextRenderer.MeasureText(text, font, new Size(proposedSize.Width, int.MaxValue), Flags);
        return new Size(proposedSize.Width, wrapped.Height);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (!_wrap)
        {
            Size = GetPreferredSize(Size.Empty);
        }

        // A wrapping label may need another line: its container decides.
        Parent?.PerformLayout(this, nameof(Text));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        TextRenderer.DrawText(e.Graphics, Text, Theme.Font(_role, DeviceDpi), ClientRectangle, ForeColor, Flags);
    }
}

/// <summary>The grey band at the bottom of a window or dialog, where its buttons are.</summary>
internal sealed class FootBand : Panel
{
    public FootBand()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Sunken;
        Dock = DockStyle.Bottom;
        Height = 60;
        // One pixel at the top is the hairline's: what the band holds starts under it.
        Padding = new Padding(20, 1, 20, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}

/// <summary>The 12 px dot in front of a health line. The chip beside it says the same in a word.</summary>
internal sealed class StatusDot : Control
{
    private Tone _tone;

    public StatusDot()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        Size = new Size(12, 12);
        TabStop = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Tone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Theme.Dot(_tone));
        e.Graphics.FillEllipse(brush, 0.5f, 0.5f, Width - 1f, Height - 1f);
    }
}

/// <summary>A status in a word: 24 px tall, 4 px corners, the tone's soft fill and dark text.</summary>
internal sealed class Chip : Control
{
    private Tone _tone;

    public Chip()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        AutoSize = true;
        TabStop = false;
        Margin = Padding.Empty;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Tone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(
            Text,
            Theme.Font(TextRole.SmallStrong, DeviceDpi),
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(text.Width + 2 * Theme.Px(this, 8), Theme.Px(this, 24));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        Theme.FillRounded(
            e.Graphics,
            new RectangleF(0, 0, Width, Height),
            Theme.PxF(this, Theme.RadiusChip),
            Theme.Soft(_tone));
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Theme.Font(TextRole.SmallStrong, DeviceDpi),
            ClientRectangle,
            Theme.InkOf(_tone),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPadding);
    }
}

internal enum ButtonKind
{
    /// <summary>White with a hairline: most buttons.</summary>
    Secondary,

    /// <summary>Filled indigo: the one action a form is for.</summary>
    Primary,

    /// <summary>Red outline: stops or removes something.</summary>
    Danger,

    /// <summary>Filled red: the button that confirms such an action in its dialog.</summary>
    DangerFilled,

    /// <summary>Words only: maintenance that should not draw the eye.</summary>
    Text,
}

/// <summary>
/// The one button of the design. The kind picks its colours; hover, pressed,
/// focus and disabled are drawn here for all four.
/// </summary>
internal sealed class FlatButton : Button
{
    private readonly bool _small;
    private ButtonKind _kind;
    private string? _hint;
    private bool _hover;
    private bool _pressed;

    public FlatButton(string text, ButtonKind kind = ButtonKind.Secondary, bool small = false)
    {
        _kind = kind;
        _small = small;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.Font(TextRole.BodyStrong);
        Text = text;
        UseMnemonic = false;
        AutoSize = true;
        Margin = new Padding(8, 0, 0, 0);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Invalidate();
        }
    }

    /// <summary>The key that does the same, drawn as a chip after the words: "Esc".</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Hint
    {
        get => _hint;
        set
        {
            _hint = value;
            Size = GetPreferredSize(Size.Empty);
            Invalidate();
        }
    }

    private bool IsFilled => _kind is ButtonKind.Primary or ButtonKind.DangerFilled;

    private int SidePadding => Theme.Px(this, _kind == ButtonKind.Text ? 10 : 14);

    private Size TextSize => TextRenderer.MeasureText(
        Text,
        Theme.Font(TextRole.BodyStrong, DeviceDpi),
        Size.Empty,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

    /// <summary>The chip's size, and nothing when there is no key to show.</summary>
    private Size HintSize => string.IsNullOrEmpty(_hint)
        ? Size.Empty
        : new Size(
            TextRenderer.MeasureText(
                _hint,
                Theme.Font(TextRole.Key, DeviceDpi),
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 2 * Theme.Px(this, 5),
            Theme.Px(this, 18));

    public override Size GetPreferredSize(Size proposedSize)
    {
        var hint = HintSize;
        var content = TextSize.Width + (hint.IsEmpty ? 0 : Theme.Px(this, 8) + hint.Width);
        return new Size(
            Math.Max(content + 2 * SidePadding, Theme.Px(this, _kind == ButtonKind.Text ? 0 : 72)),
            Theme.Px(this, _small ? 30 : 34));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        base.OnMouseDown(mevent);
        _pressed = mevent.Button == MouseButtons.Left;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        base.OnMouseUp(mevent);
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Page);

        var (fill, outline, ink) = Colours();
        var bounds = new RectangleF(0, 0, Width, Height);
        var radius = Theme.PxF(this, Theme.RadiusButton);
        Theme.FillRounded(g, bounds, radius, fill, outline);

        if (Focused && ShowFocusCues)
        {
            // A filled button has no outline to thicken: its ring is drawn
            // inside the fill, in white.
            var ring = Theme.PxF(this, 2);
            if (IsFilled)
            {
                var inner = RectangleF.Inflate(bounds, -ring, -ring);
                Theme.FillRounded(g, inner, radius - ring, fill, Color.White, Theme.PxF(this, 1));
            }
            else
            {
                Theme.FillRounded(g, bounds, radius, fill, Theme.Accent, ring);
            }
        }

        const TextFormatFlags centred =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPadding;
        var hint = HintSize;
        if (hint.IsEmpty)
        {
            TextRenderer.DrawText(g, Text, Theme.Font(TextRole.BodyStrong, DeviceDpi), ClientRectangle, ink, centred);
            return;
        }

        // Words, a gap, then the key chip, centred as one.
        var text = TextSize;
        var left = (Width - text.Width - Theme.Px(this, 8) - hint.Width) / 2;
        TextRenderer.DrawText(
            g,
            Text,
            Theme.Font(TextRole.BodyStrong, DeviceDpi),
            new Rectangle(left, 0, text.Width, Height),
            ink,
            centred);

        var chip = new Rectangle(left + text.Width + Theme.Px(this, 8), (Height - hint.Height) / 2, hint.Width, hint.Height);
        var onFill = IsFilled && Enabled;
        Theme.FillRounded(
            g,
            chip,
            Theme.PxF(this, Theme.RadiusChip),
            onFill ? Color.FromArgb(0x29, Color.White) : Theme.Sunken,
            onFill ? Color.FromArgb(0x73, Color.White) : Theme.Border);
        TextRenderer.DrawText(
            g,
            _hint,
            Theme.Font(TextRole.Key, DeviceDpi),
            chip,
            onFill ? Color.White : Enabled ? Theme.InkMuted : Theme.InkDisabled,
            centred);
    }

    private (Color Fill, Color? Outline, Color Ink) Colours()
    {
        var parent = Parent?.BackColor ?? Theme.Page;
        if (!Enabled)
        {
            return _kind == ButtonKind.Text
                ? (parent, null, Theme.InkDisabled)
                : (Theme.DisabledFill, null, Theme.InkDisabled);
        }

        return _kind switch
        {
            ButtonKind.Primary => (
                _pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent,
                null,
                Color.White),
            ButtonKind.Danger => (
                _pressed ? Theme.ErrSoft : _hover ? Theme.ErrWash : Theme.Panel,
                Theme.Err,
                Theme.ErrText),
            ButtonKind.DangerFilled => (
                _pressed ? Theme.ErrPressed : _hover ? Theme.Err : Theme.ErrText,
                null,
                Color.White),
            ButtonKind.Text => (
                _pressed ? Theme.AccentSoftPressed : _hover ? Theme.AccentSoft : parent,
                null,
                Theme.AccentInk),
            _ => (
                _pressed ? Theme.Hover : _hover ? Theme.Sunken : Theme.Panel,
                _hover || _pressed ? Theme.BorderInput : Theme.Border,
                Theme.Ink),
        };
    }
}

/// <summary>
/// A 34 px text input: a borderless text box inside a drawn outline, so the
/// corners and the focus ring are the design's and not the system's. A
/// read-only one is a grey box without an outline, as on the Desktop.
/// </summary>
internal sealed class FieldBox : Panel
{
    private bool _invalid;

    public FieldBox(TextRole role = TextRole.Body, bool readOnly = false)
    {
        // First: setting a size lays the box out, and that places the input.
        Input = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = Theme.Font(role),
            ForeColor = Theme.Ink,
            ReadOnly = readOnly,
            BackColor = readOnly ? Theme.Sunken : Theme.Panel,
        };
        Input.GotFocus += (_, _) => Invalidate();
        Input.LostFocus += (_, _) => Invalidate();

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 34;
        Margin = Padding.Empty;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Cursor = Cursors.IBeam;
        Controls.Add(Input);
    }

    public TextBox Input { get; }

    /// <summary>Red outline and wash: what was typed cannot be used.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Invalid
    {
        get => _invalid;
        set
        {
            _invalid = value;
            Input.BackColor = Fill;
            Invalidate();
        }
    }

    private Color Fill => Input.ReadOnly ? Theme.Sunken : _invalid ? Theme.ErrWash : Theme.Panel;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var side = Theme.Px(this, 10);
        Input.SetBounds(side, (Height - Input.Height) / 2, Math.Max(0, Width - 2 * side), Input.Height);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Input.Focus();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        var bounds = new RectangleF(0, 0, Width, Height);
        var radius = Theme.PxF(this, Theme.RadiusButton);
        if (Input.Focused && !Input.ReadOnly)
        {
            Theme.FillRounded(e.Graphics, bounds, radius, Fill, _invalid ? Theme.Err : Theme.Accent, Theme.PxF(this, 2));
        }
        else if (Input.ReadOnly)
        {
            Theme.FillRounded(e.Graphics, bounds, radius, Fill);
        }
        else
        {
            Theme.FillRounded(e.Graphics, bounds, radius, Fill, _invalid ? Theme.Err : Theme.BorderInput);
        }
    }
}

/// <summary>One counter in the table of connected counters.</summary>
internal sealed record CounterRow(string Counter, string PcAndUser, string Address, string LastSeen);

/// <summary>
/// The counters the server has heard from: a grey heading band, then one row
/// each, divided by hairlines. It is drawn, not built from controls, so a
/// refresh every few seconds does not flicker.
/// </summary>
internal sealed class CounterTable : Control
{
    private static readonly string[] Headings = ["Counter", "PC and user", "Address", "Last seen"];

    private IReadOnlyList<CounterRow> _rows = [];
    private string _emptyText = string.Empty;

    public CounterTable()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        AutoSize = true;
        TabStop = false;
        Margin = Padding.Empty;
        Dock = DockStyle.Fill;
    }

    /// <param name="rows">The counters to list.</param>
    /// <param name="emptyText">What to say in place of rows when there are none.</param>
    public void SetRows(IReadOnlyList<CounterRow> rows, string emptyText)
    {
        _rows = rows;
        _emptyText = emptyText;
        Height = GetPreferredSize(Size.Empty).Height;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(
            proposedSize.Width > 0 ? proposedSize.Width : Width,
            Theme.Px(this, 34) + Math.Max(1, _rows.Count) * Theme.Px(this, 38));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);

        var headHeight = Theme.Px(this, 34);
        var rowHeight = Theme.Px(this, 38);
        var side = Theme.Px(this, 16);
        var columns = Columns(side);

        using (var band = new SolidBrush(Theme.Sunken))
        {
            g.FillRectangle(band, 0, 0, Width, headHeight);
        }

        using var hairline = new Pen(Theme.Divider);
        using var bandEdge = new Pen(Theme.Border);
        g.DrawLine(bandEdge, 0, 0, Width, 0);
        g.DrawLine(bandEdge, 0, headHeight - 1, Width, headHeight - 1);

        var headFont = Theme.Font(TextRole.SmallStrong, DeviceDpi);
        for (var i = 0; i < Headings.Length; i++)
        {
            Draw(g, Headings[i], headFont, Theme.InkMuted, columns[i], 0, headHeight, rightAligned: i == 3);
        }

        if (_rows.Count == 0)
        {
            Draw(
                g,
                _emptyText,
                Theme.Font(TextRole.Body, DeviceDpi),
                Theme.InkSoft,
                Rectangle.FromLTRB(side, 0, Width - side, 0),
                headHeight,
                rowHeight,
                rightAligned: false);
            return;
        }

        var body = Theme.Font(TextRole.Body, DeviceDpi);
        var strong = Theme.Font(TextRole.BodyStrong, DeviceDpi);
        for (var r = 0; r < _rows.Count; r++)
        {
            var top = headHeight + r * rowHeight;
            var row = _rows[r];
            Draw(g, row.Counter, strong, Theme.Ink, columns[0], top, rowHeight, rightAligned: false);
            Draw(g, row.PcAndUser, body, Theme.Ink, columns[1], top, rowHeight, rightAligned: false);
            Draw(g, row.Address, body, Theme.Ink, columns[2], top, rowHeight, rightAligned: false);
            Draw(g, row.LastSeen, body, Theme.Ink, columns[3], top, rowHeight, rightAligned: true);
            if (r < _rows.Count - 1)
            {
                g.DrawLine(hairline, 0, top + rowHeight - 1, Width, top + rowHeight - 1);
            }
        }
    }

    /// <summary>Three columns share the width; the time has a width of its own at the right.</summary>
    private Rectangle[] Columns(int side)
    {
        var gap = Theme.Px(this, 12);
        var timeWidth = Theme.Px(this, 84);
        var shared = Math.Max(0, Width - 2 * side - timeWidth - 3 * gap);
        var first = (int)(shared * 0.30);
        var second = (int)(shared * 0.40);
        var third = shared - first - second;

        var x = side;
        var counter = new Rectangle(x, 0, first, 0);
        x += first + gap;
        var pc = new Rectangle(x, 0, second, 0);
        x += second + gap;
        var address = new Rectangle(x, 0, third, 0);
        x += third + gap;
        return [counter, pc, address, new Rectangle(x, 0, timeWidth, 0)];
    }

    private static void Draw(
        Graphics g,
        string text,
        Font font,
        Color colour,
        Rectangle column,
        int top,
        int height,
        bool rightAligned)
    {
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | (rightAligned ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(g, text, font, new Rectangle(column.X, top, column.Width, height), colour, flags);
    }
}

/// <summary>A filled box with rounded corners and, if given, an outline: a banner, or a box of details.</summary>
internal class RoundedPanel : Panel
{
    public RoundedPanel(Color fill, Color? outline = null)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = fill;
        Outline = outline;
        Margin = Padding.Empty;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Outline { get; set; }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        Theme.FillRounded(
            e.Graphics,
            new RectangleF(0, 0, Width, Height),
            Theme.PxF(this, Theme.RadiusButton),
            BackColor,
            Outline);
    }
}

/// <summary>The line to read twice: one bold sentence on its tone's wash.</summary>
internal sealed class NoteBanner : RoundedPanel
{
    private readonly TextLabel _text;

    public NoteBanner(Tone tone, string text)
        : base(Theme.Soft(tone), Theme.Edge(tone))
    {
        Padding = new Padding(12, 10, 12, 10);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        _text = new TextLabel(TextRole.BodyStrong, Theme.InkOf(tone), text, wrap: true) { Dock = DockStyle.Top };
        Controls.Add(_text);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var inner = Math.Max(1, proposedSize.Width - Padding.Horizontal);
        var text = _text.GetPreferredSize(new Size(inner, 0));
        return new Size(
            proposedSize.Width > 1 ? proposedSize.Width : text.Width + Padding.Horizontal,
            text.Height + Padding.Vertical);
    }
}

/// <summary>The round tile at the top left of a dialog: one glyph on its tone's wash.</summary>
internal sealed class GlyphTile : Control
{
    /// <summary>Glyphs of the Windows icon font that the dialogs use.</summary>
    public const string Warning = "\uE7BA";
    public const string Error = "\uE783";
    public const string Done = "\uE73E";
    public const string Power = "\uE7E8";
    public const string Server = "\uEDA2";

    private readonly string _glyph;
    private readonly Color _fill;
    private readonly Color _ink;

    public GlyphTile(string glyph, Color fill, Color ink)
    {
        _glyph = glyph;
        _fill = fill;
        _ink = ink;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        Size = new Size(36, 36);
        TabStop = false;
        Margin = new Padding(0, 0, 14, 0);
    }

    public static GlyphTile For(Tone tone) => tone switch
    {
        Tone.Ok => new GlyphTile(Done, Theme.Soft(Tone.Ok), Theme.InkOf(Tone.Ok)),
        Tone.Warn => new GlyphTile(Warning, Theme.Soft(Tone.Warn), Theme.InkOf(Tone.Warn)),
        Tone.Err => new GlyphTile(Error, Theme.Soft(Tone.Err), Theme.ErrText),
        _ => new GlyphTile(Server, Theme.AccentSoft, Theme.Accent),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(_fill))
        {
            e.Graphics.FillEllipse(brush, 0.5f, 0.5f, Width - 1f, Height - 1f);
        }

        if (!Theme.HasIcons)
        {
            return;
        }

        TextRenderer.DrawText(
            e.Graphics,
            _glyph,
            Theme.Font(TextRole.Icon, DeviceDpi),
            ClientRectangle,
            _ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPadding);
    }
}
