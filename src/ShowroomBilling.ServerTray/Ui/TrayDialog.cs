namespace ShowroomBilling.ServerTray.Ui;

/// <summary>
/// The one dialog of the server: a glyph tile, the title (a question, or what
/// happened), a sentence of detail, whatever the case needs under that, and a
/// grey foot with the buttons, the action last. It tells, asks or takes one
/// line of text; see the three static methods.
/// </summary>
internal sealed class TrayDialog : Form
{
    private readonly TableLayoutPanel _content;
    private readonly FootBand _foot = new();
    private readonly FlowLayoutPanel _buttons;
    private Control? _firstFocus;

    // Only a prompt has these: its field, the line under it, and what makes the text unusable.
    private FieldBox? _field;
    private TextLabel? _under;
    private Func<string, string?>? _problemWith;

    // Row 0 is the title; the tile beside it spans this one too.
    private int _nextRow = 1;

    private TrayDialog(int width, GlyphTile tile, string title, string? message)
    {
        // Sizes are written for 100 % scaling; Finish declares the scale once
        // everything is in, so that the first layout scales all of it together.
        SuspendLayout();
        Text = ServerTrayOptions.ProductName;
        Icon = AppIconProvider.CreateIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Panel;
        ClientSize = new Size(width, 220);

        _content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(20, 20, 20, 20),
            BackColor = Theme.Panel,
        };
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _content.Controls.Add(tile, 0, 0);
        _content.SetRowSpan(tile, 2);
        // The title sits level with the middle of the tile.
        _content.Controls.Add(
            new TextLabel(TextRole.DialogTitle, Theme.Ink, title, wrap: true) { Margin = new Padding(0, 6, 0, 0) },
            1,
            0);
        if (!string.IsNullOrWhiteSpace(message))
        {
            AddRow(new TextLabel(TextRole.Body, Theme.InkMuted, message, wrap: true), top: 6);
        }

        _buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Sunken,
            Padding = new Padding(0, 13, 0, 0),
        };
        _foot.Controls.Add(_buttons);

        Controls.Add(_content);
        Controls.Add(_foot);
    }

    /// <summary>Says what happened. <paramref name="details"/> can be long: it scrolls, and Copy takes all of it.</summary>
    public static void ShowMessage(IWin32Window? owner, Tone tone, string title, string? details)
    {
        using var dialog = Message(tone, title, details);
        dialog.ShowFor(owner);
    }

    /// <summary>Asks before something that takes the counters offline. Cancel has the focus; Esc and closing the window mean no.</summary>
    public static bool Confirm(IWin32Window? owner, string title, string message, string note, string confirmText)
    {
        using var dialog = Confirmation(title, message, note, confirmText);
        return dialog.ShowFor(owner) == DialogResult.Yes;
    }

    /// <summary>
    /// Takes one line of text. <paramref name="problemWith"/> says what is
    /// wrong with what was typed, or null when it will do; the dialog stays
    /// open, with that sentence under the field, until it does.
    /// </summary>
    /// <returns>The text, or null when the operator cancelled.</returns>
    public static string? Prompt(
        IWin32Window? owner,
        string title,
        string message,
        string value,
        string hint,
        string actionText,
        Func<string, string?> problemWith)
    {
        using var dialog = TextPrompt(title, message, value, hint, actionText, problemWith);
        return dialog.ShowFor(owner) == DialogResult.OK ? dialog._field!.Input.Text : null;
    }

    /// <summary>The dialog <see cref="ShowMessage"/> shows, not yet shown.</summary>
    internal static TrayDialog Message(Tone tone, string title, string? details)
    {
        var dialog = new TrayDialog(560, GlyphTile.For(tone), title, message: null);
        var close = dialog.AddButton("Close", ButtonKind.Primary, DialogResult.OK);
        close.Hint = "Esc";
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog._firstFocus = close;

        if (!string.IsNullOrWhiteSpace(details))
        {
            dialog.AddDetails(details);
            var copy = dialog.AddButton("Copy", ButtonKind.Secondary, DialogResult.None);
            copy.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText($"{title}{Environment.NewLine}{Environment.NewLine}{details}");
                    copy.Text = "Copied";
                }
                catch (Exception)
                {
                    // Another program holds the clipboard; the text is still on screen to select.
                    copy.Text = "Could not copy";
                }
            };
        }

        return dialog.Finish();
    }

    /// <summary>The dialog <see cref="Confirm"/> shows, not yet shown.</summary>
    internal static TrayDialog Confirmation(string title, string message, string note, string confirmText)
    {
        var dialog = new TrayDialog(480, new GlyphTile(GlyphTile.Power, Theme.Soft(Tone.Err), Theme.ErrText), title, message);
        dialog.AddRow(new NoteBanner(Tone.Err, note), top: 12);
        dialog.AddButton(confirmText, ButtonKind.DangerFilled, DialogResult.Yes);
        var cancel = dialog.AddButton("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
        cancel.Hint = "Esc";
        dialog.CancelButton = cancel;
        dialog._firstFocus = cancel;
        return dialog.Finish();
    }

    /// <summary>The dialog <see cref="Prompt"/> shows, not yet shown.</summary>
    internal static TrayDialog TextPrompt(
        string title,
        string message,
        string value,
        string hint,
        string actionText,
        Func<string, string?> problemWith)
    {
        var dialog = new TrayDialog(480, GlyphTile.For(Tone.Neutral), title, message);
        var field = new FieldBox(TextRole.Mono);
        field.Input.Text = value;
        var under = new TextLabel(TextRole.Small, Theme.InkSoft, hint, wrap: true);
        dialog._field = field;
        dialog._under = under;
        dialog._problemWith = problemWith;
        dialog.AddRow(field, top: 14);
        dialog.AddRow(under, top: 6);

        var action = dialog.AddButton(actionText, ButtonKind.Primary, DialogResult.None);
        var cancel = dialog.AddButton("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
        cancel.Hint = "Esc";
        dialog.AcceptButton = action;
        dialog.CancelButton = cancel;
        dialog._firstFocus = field.Input;

        action.Click += (_, _) => dialog.Accept();
        return dialog.Finish();
    }

    /// <summary>A prompt's action: closes on text that will do; otherwise says under the field what is wrong with it.</summary>
    internal void Accept()
    {
        if (_field is null || _under is null || _problemWith is null)
        {
            return;
        }

        var problem = _problemWith(_field.Input.Text);
        if (problem is null)
        {
            DialogResult = DialogResult.OK;
            return;
        }

        _field.Invalid = true;
        _under.ForeColor = Theme.ErrText;
        _under.Text = problem;
        FitHeight();
        _field.Input.Focus();
        _field.Input.SelectAll();
    }

    /// <summary>Everything is in: declare the scale the sizes were written at, and lay out.</summary>
    private TrayDialog Finish()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(performLayout: false);
        PerformLayout();
        return this;
    }

    private DialogResult ShowFor(IWin32Window? owner)
    {
        if (owner is not null)
        {
            return ShowDialog(owner);
        }

        // Nothing to sit on: the first run, before the tray has a window.
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        return ShowDialog();
    }

    private void AddRow(Control control, int top)
    {
        control.Margin = new Padding(0, top, 0, 0);
        _content.Controls.Add(control, 1, _nextRow++);
    }

    /// <summary>The long part of a message: a grey box that scrolls and can be selected from.</summary>
    private void AddDetails(string details)
    {
        var text = details.ReplaceLineEndings();

        // How tall the words are at the width the box will have: what is left
        // of the dialog after its padding, the tile and the box's own padding.
        var font = Theme.Font(TextRole.Body);
        var scale = DeviceDpi / 96f;
        var width = (int)((ClientSize.Width - 40 - 50 - 18) * scale);
        var needed = TextRenderer.MeasureText(
            text,
            font,
            new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height / scale;

        // As tall as the words, up to about fourteen lines; beyond that it scrolls.
        const int tallest = 266;
        var box = new RoundedPanel(Theme.Sunken)
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 6, 10),
            Height = (int)Math.Ceiling(Math.Min(needed, tallest)) + 22,
        };
        box.Controls.Add(new TextBox
        {
            Text = text,
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
            ScrollBars = needed > tallest ? ScrollBars.Vertical : ScrollBars.None,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Sunken,
            ForeColor = Theme.Ink,
            Font = Theme.Font(TextRole.Body),
            Dock = DockStyle.Fill,
            TabStop = false,
        });
        AddRow(box, top: 12);
    }

    private FlatButton AddButton(string text, ButtonKind kind, DialogResult result)
    {
        var button = new FlatButton(text, kind) { DialogResult = result };
        _buttons.Controls.Add(button);
        return button;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitHeight();
        if (Owner is null)
        {
            CenterToScreen();
        }
        else
        {
            CenterToParent();
        }

        _firstFocus?.Select();
    }

    /// <summary>
    /// As tall as what it holds: the content decides, at the width the dialog
    /// has. Done when the dialog loads, and again when a line under a field changes.
    /// </summary>
    internal void FitHeight()
    {
        _content.PerformLayout();
        ClientSize = new Size(ClientSize.Width, _content.Height + _foot.Height);
    }
}
