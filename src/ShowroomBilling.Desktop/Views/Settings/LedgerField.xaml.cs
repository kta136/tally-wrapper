using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Views.Settings;

/// <summary>
/// A ledger or voucher-type field of the Ledgers page: its label, a list of what Tally has, and a tick
/// at the right when the name is one Tally has, or a warning, with what to do, when it is not. It keeps the
/// data context of the page it sits in, so the combo box reads the page's edit mode.
/// </summary>
public partial class LedgerField : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(LedgerField), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LedgerField),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnMatchInputChanged));

    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(IEnumerable), typeof(LedgerField), new PropertyMetadata(null, OnOptionsChanged));

    public static readonly DependencyProperty MissingHintProperty = DependencyProperty.Register(
        nameof(MissingHint), typeof(string), typeof(LedgerField),
        new PropertyMetadata("Not in the Tally ledger list. Refresh from Tally, or check the spelling."));

    private static readonly DependencyPropertyKey MatchStatePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(MatchState), typeof(LedgerMatchState), typeof(LedgerField), new PropertyMetadata(LedgerMatchState.Unknown));

    public static readonly DependencyProperty MatchStateProperty = MatchStatePropertyKey.DependencyProperty;

    public LedgerField()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IEnumerable? Options
    {
        get => (IEnumerable?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public string MissingHint
    {
        get => (string)GetValue(MissingHintProperty);
        set => SetValue(MissingHintProperty, value);
    }

    public LedgerMatchState MatchState => (LedgerMatchState)GetValue(MatchStateProperty);

    private static void OnMatchInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LedgerField)d).Recheck();

    private static void OnOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (LedgerField)d;
        if (e.OldValue is INotifyCollectionChanged oldList)
        {
            oldList.CollectionChanged -= field.OnOptionsCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newList)
        {
            newList.CollectionChanged += field.OnOptionsCollectionChanged;
        }

        field.Recheck();
    }

    private void OnOptionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Recheck();

    private void Recheck()
    {
        var names = new List<string>();
        if (Options is not null)
        {
            foreach (var option in Options)
            {
                if (option?.GetType().GetProperty("Name")?.GetValue(option) is string name)
                {
                    names.Add(name);
                }
            }
        }

        SetValue(MatchStatePropertyKey, LedgerMatch.Check(Text, names));
    }
}
