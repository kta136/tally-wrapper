using System;
using System.Windows;
using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// A column of a page that is as wide as the page allows up to <see cref="MaxContentWidth"/>, and sits
/// at the left. A left-aligned <c>StackPanel</c> with a <c>MaxWidth</c> does the same on paper, but it
/// takes its width from its widest child, so a table inside it shrinks to its longest cell instead of
/// filling the column.
/// </summary>
public sealed class ReadingColumn : Decorator
{
    public static readonly DependencyProperty MaxContentWidthProperty = DependencyProperty.Register(
        nameof(MaxContentWidth), typeof(double), typeof(ReadingColumn),
        new FrameworkPropertyMetadata(860d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    static ReadingColumn()
    {
        // A stretched element that is arranged narrower than its slot is centred, so the column asks for the left.
        HorizontalAlignmentProperty.OverrideMetadata(typeof(ReadingColumn), new FrameworkPropertyMetadata(HorizontalAlignment.Left));
    }

    public double MaxContentWidth
    {
        get => (double)GetValue(MaxContentWidthProperty);
        set => SetValue(MaxContentWidthProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsInfinity(constraint.Width) ? MaxContentWidth : Math.Min(constraint.Width, MaxContentWidth);
        if (Child is null)
        {
            return new Size(width, 0);
        }

        Child.Measure(new Size(width, constraint.Height));
        return new Size(width, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var width = Math.Min(arrangeSize.Width, MaxContentWidth);
        Child?.Arrange(new Rect(0, 0, width, arrangeSize.Height));
        return new Size(width, arrangeSize.Height);
    }
}
