using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Views.Settings;

public partial class PrintLayoutSettingsSectionView : UserControl
{
    private Point _dragStart;
    private PrintLayoutSectionRowViewModel? _draggedRow;

    public PrintLayoutSettingsSectionView()
    {
        InitializeComponent();
    }

    private void SectionList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(SectionList);
        _draggedRow = FindRow(e.OriginalSource as DependencyObject);
    }

    private void SectionList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedRow is null) return;

        var current = e.GetPosition(SectionList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(SectionList, _draggedRow, DragDropEffects.Move);
        _draggedRow = null;
    }

    private void SectionList_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PrintLayoutSectionRowViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SectionList_OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not PrintLayoutViewModel viewModel
            || e.Data.GetData(typeof(PrintLayoutSectionRowViewModel)) is not PrintLayoutSectionRowViewModel source)
        {
            return;
        }

        var target = FindRow(e.OriginalSource as DependencyObject);
        if (target is null)
        {
            viewModel.MoveSectionToEnd(source.SectionKey);
        }
        else
        {
            viewModel.MoveSection(source.SectionKey, target.SectionKey);
        }

        e.Handled = true;
    }

    private static PrintLayoutSectionRowViewModel? FindRow(DependencyObject? origin)
    {
        var current = origin;
        while (current is not null)
        {
            if (current is ListBoxItem item)
            {
                return item.DataContext as PrintLayoutSectionRowViewModel;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
