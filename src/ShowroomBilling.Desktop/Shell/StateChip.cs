using System.Windows;
using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// A bill's state as a chip: a shape, then the word, on a soft fill of the state's colour. The
/// shape and the word carry the meaning, so the chip still reads for someone who cannot tell the
/// colours apart. Used by the Bills list and the Bill Details dialog; its look is the implicit
/// style in <c>Controls.xaml</c>.
/// <code>
/// &lt;shell:StateChip Label="{Binding StateChipLabel}" Tone="{Binding StateTone}" Kind="{Binding StateKind}" /&gt;
/// </code>
/// </summary>
public sealed class StateChip : Control
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(StateChip), new PropertyMetadata(string.Empty));

    /// <summary>"ok", "warn", "err", "info"; anything else is neutral.</summary>
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(string), typeof(StateChip), new PropertyMetadata("neutral"));

    /// <summary>"ring", "half", "dot", "x", "bang", "redo" or "dash"; anything else draws no shape.</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(StateChip), new PropertyMetadata("none"));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Tone
    {
        get => (string)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
