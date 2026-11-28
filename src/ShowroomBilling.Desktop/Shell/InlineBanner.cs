using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>A notice with a tone-specific glyph and either a message or custom content.</summary>
public sealed class InlineBanner : ContentControl
{
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(InlineBanner));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(InlineBanner));

    public static readonly DependencyProperty GlyphStyleProperty =
        DependencyProperty.Register(nameof(GlyphStyle), typeof(Style), typeof(InlineBanner));

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Style? GlyphStyle
    {
        get => (Style?)GetValue(GlyphStyleProperty);
        set => SetValue(GlyphStyleProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
