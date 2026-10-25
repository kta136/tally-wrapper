using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// The shell every dialog shares: the scrim, one rounded panel, a head (an optional icon, the title with
/// its chips, a context line and a close button), the body, and a grey foot. A dialog is a
/// <c>UserControl</c> whose only child is one of these:
/// <code>
/// &lt;shell:DialogFrame Title="Change invoice number" Subtitle="Anand Pillai" PanelWidth="520"
///                   CloseCommand="{Binding CancelCommand}"&gt;
///     &lt;shell:DialogFrame.Footer&gt; … buttons … &lt;/shell:DialogFrame.Footer&gt;
///     … body …
/// &lt;/shell:DialogFrame&gt;
/// </code>
/// Its look is the implicit style in <c>Controls.xaml</c>; a dialog never paints a scrim or a shadow itself.
/// </summary>
public sealed class DialogFrame : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(DialogFrame), new PropertyMetadata(string.Empty));

    /// <summary>The context line under the title: text, or anything a dialog wants to show there.</summary>
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

    /// <summary>Chips that sit after the title, such as "Admin only".</summary>
    public static readonly DependencyProperty HeaderChipsProperty = DependencyProperty.Register(
        nameof(HeaderChips), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

    /// <summary>A glyph of the icon font drawn in a soft indigo tile before the title; empty for none.</summary>
    public static readonly DependencyProperty HeaderGlyphProperty = DependencyProperty.Register(
        nameof(HeaderGlyph), typeof(string), typeof(DialogFrame), new PropertyMetadata(string.Empty));

    /// <summary>"warn" draws the icon tile amber, for a dialog that reports something gone wrong; anything else is the soft indigo tile.</summary>
    public static readonly DependencyProperty HeaderToneProperty = DependencyProperty.Register(
        nameof(HeaderTone), typeof(string), typeof(DialogFrame), new PropertyMetadata(string.Empty));

    /// <summary>The buttons, at the right of the foot.</summary>
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

    /// <summary>A sentence at the left of the foot.</summary>
    public static readonly DependencyProperty FooterNoteProperty = DependencyProperty.Register(
        nameof(FooterNote), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(
        nameof(CloseCommand), typeof(ICommand), typeof(DialogFrame), new PropertyMetadata(null));

    /// <summary>False for a dialog that has no head at all (a question asked of the operator).</summary>
    public static readonly DependencyProperty ShowHeaderProperty = DependencyProperty.Register(
        nameof(ShowHeader), typeof(bool), typeof(DialogFrame), new PropertyMetadata(true));

    public static readonly DependencyProperty PanelWidthProperty = DependencyProperty.Register(
        nameof(PanelWidth), typeof(double), typeof(DialogFrame), new PropertyMetadata(520d));

    /// <summary>Fixed height of the panel; NaN (the default) lets the content decide, up to the window.</summary>
    public static readonly DependencyProperty PanelHeightProperty = DependencyProperty.Register(
        nameof(PanelHeight), typeof(double), typeof(DialogFrame), new PropertyMetadata(double.NaN));

    public static readonly DependencyProperty PanelMinWidthProperty = DependencyProperty.Register(
        nameof(PanelMinWidth), typeof(double), typeof(DialogFrame), new PropertyMetadata(0d));

    public static readonly DependencyProperty PanelMinHeightProperty = DependencyProperty.Register(
        nameof(PanelMinHeight), typeof(double), typeof(DialogFrame), new PropertyMetadata(0d));

    /// <summary>Drawn over the bottom-right corner of the panel: the grip of a dialog that can be resized.</summary>
    public static readonly DependencyProperty OverlayProperty = DependencyProperty.Register(
        nameof(Overlay), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

    public static readonly DependencyProperty BodyPaddingProperty = DependencyProperty.Register(
        nameof(BodyPadding), typeof(Thickness), typeof(DialogFrame), new PropertyMetadata(new Thickness(20)));

    public static readonly DependencyProperty BodyBackgroundProperty = DependencyProperty.Register(
        nameof(BodyBackground), typeof(System.Windows.Media.Brush), typeof(DialogFrame), new PropertyMetadata(null));

    /// <summary>Auto (the default) scrolls a body that is taller than the window; Disabled for a body that scrolls itself.</summary>
    public static readonly DependencyProperty BodyScrollProperty = DependencyProperty.Register(
        nameof(BodyScroll), typeof(ScrollBarVisibility), typeof(DialogFrame), new PropertyMetadata(ScrollBarVisibility.Auto));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? HeaderChips
    {
        get => GetValue(HeaderChipsProperty);
        set => SetValue(HeaderChipsProperty, value);
    }

    public string HeaderGlyph
    {
        get => (string)GetValue(HeaderGlyphProperty);
        set => SetValue(HeaderGlyphProperty, value);
    }

    public string HeaderTone
    {
        get => (string)GetValue(HeaderToneProperty);
        set => SetValue(HeaderToneProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public object? FooterNote
    {
        get => GetValue(FooterNoteProperty);
        set => SetValue(FooterNoteProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public bool ShowHeader
    {
        get => (bool)GetValue(ShowHeaderProperty);
        set => SetValue(ShowHeaderProperty, value);
    }

    public double PanelWidth
    {
        get => (double)GetValue(PanelWidthProperty);
        set => SetValue(PanelWidthProperty, value);
    }

    public double PanelHeight
    {
        get => (double)GetValue(PanelHeightProperty);
        set => SetValue(PanelHeightProperty, value);
    }

    public double PanelMinWidth
    {
        get => (double)GetValue(PanelMinWidthProperty);
        set => SetValue(PanelMinWidthProperty, value);
    }

    public double PanelMinHeight
    {
        get => (double)GetValue(PanelMinHeightProperty);
        set => SetValue(PanelMinHeightProperty, value);
    }

    public object? Overlay
    {
        get => GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    public Thickness BodyPadding
    {
        get => (Thickness)GetValue(BodyPaddingProperty);
        set => SetValue(BodyPaddingProperty, value);
    }

    public System.Windows.Media.Brush? BodyBackground
    {
        get => (System.Windows.Media.Brush?)GetValue(BodyBackgroundProperty);
        set => SetValue(BodyBackgroundProperty, value);
    }

    public ScrollBarVisibility BodyScroll
    {
        get => (ScrollBarVisibility)GetValue(BodyScrollProperty);
        set => SetValue(BodyScrollProperty, value);
    }
}
