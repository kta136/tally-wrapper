using System.Windows;
using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Wraps the content of a cell in a flexible table column: it fills the width the column gives it and
/// asks for none. Without it, a long name in one row asks for more width than the column has and
/// that row's columns end up a few pixels away from the other rows' and from the heading, because a
/// grid's star column takes its smallest size from the widest content it holds.
/// <para>
/// The content is also held to the column's width. A row can be measured with no limit on its width,
/// and content that then asks for more than the column has is laid out that wide and cut off at the
/// cell's edge: a long item name pushed the end of its own text box, and the chevron beside it, out
/// of sight under the next column. So the width the cell was last given is remembered, and content
/// that asked for more is measured again within it.
/// </para>
/// </summary>
public sealed class FillCell : Decorator
{
    private double _givenWidth = double.PositiveInfinity;
    private double _remeasuredFor = double.NaN;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return new Size(0, 0);
        }

        Child.Measure(new Size(Math.Min(constraint.Width, _givenWidth), constraint.Height));
        return new Size(0, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _givenWidth = arrangeSize.Width;
        if (Child is not null && Child.DesiredSize.Width > arrangeSize.Width && _remeasuredFor != arrangeSize.Width)
        {
            // Once more, now that the width is known: the next measure holds the content to it.
            // Once for each width only, or content that cannot be narrower than it asks to be (a
            // minimum width) would have the layout go round for ever.
            _remeasuredFor = arrangeSize.Width;
            InvalidateMeasure();
        }

        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, arrangeSize.Height));
        return arrangeSize;
    }
}
