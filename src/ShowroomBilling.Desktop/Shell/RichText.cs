using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Puts a sentence with a few bold words into a <c>TextBlock</c> from one bound string:
/// <c>shell:RichText.Markup="{Binding WarningText}"</c>, where the text is
/// <c>"This marks SR/1 as posted **without contacting Tally**."</c>. Only <c>**bold**</c> is read;
/// anything else is shown as it is.
/// </summary>
public static class RichText
{
    public static readonly DependencyProperty MarkupProperty = DependencyProperty.RegisterAttached(
        "Markup", typeof(string), typeof(RichText), new PropertyMetadata(null, OnMarkupChanged));

    public static string? GetMarkup(DependencyObject element) => (string?)element.GetValue(MarkupProperty);

    public static void SetMarkup(DependencyObject element, string? value) => element.SetValue(MarkupProperty, value);

    private static void OnMarkupChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (e.NewValue is not string text || text.Length == 0)
        {
            return;
        }

        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
            {
                continue;
            }

            var run = new Run(parts[i]);
            if (i % 2 == 1)
            {
                run.FontWeight = FontWeights.Bold;
            }

            block.Inlines.Add(run);
        }
    }
}
