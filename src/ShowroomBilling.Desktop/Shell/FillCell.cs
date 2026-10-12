using System.Windows;
using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Wraps the content of a cell in a flexible table column: it fills the width the column gives it and
/// asks for none. Without it, a long name in one row asks for more width than the column has and
/// that row's columns end up a few pixels away from the other rows' and from the heading, because a
/// grid's star column takes its smallest size from the widest content it holds.
/// </summary>
public sealed class FillCell : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return new Size(0, 0);
        }

        Child.Measure(constraint);
        return new Size(0, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, arrangeSize.Height));
        return arrangeSize;
    }
}
