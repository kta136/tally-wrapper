using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Views.Invoice;

public partial class InvoiceView : UserControl
{
    private static readonly int[] EditableLineColumns = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    public InvoiceView()
    {
        InitializeComponent();
        LinesItems.PreviewKeyDown += OnLinesPreviewKeyDown;
        LinesItems.GotFocus += OnLinesGotFocus;
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is InvoiceViewModel oldVm)
            oldVm.QuickAddCommitted -= OnQuickAddCommitted;
        if (e.NewValue is InvoiceViewModel newVm)
            newVm.QuickAddCommitted += OnQuickAddCommitted;
    }

    private void OnQuickAddCommitted(object? sender, BillLineViewModel target)
        => FocusGrossWeightForLine(target);

    private void FocusGrossWeightForLine(BillLineViewModel target)
    {
        if (DataContext is not InvoiceViewModel vm) return;
        var index = vm.Lines.IndexOf(target);
        if (index < 0) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var container = GetLineContainer(index);
            if (container is null) return;
            var grossBox = FindGrossWeightTextBox(container);
            if (grossBox is null) return;
            grossBox.Focus();
            grossBox.SelectAll();
        }));
    }

    private static TextBox? FindGrossWeightTextBox(DependencyObject root)
    {
        // Body cell at Grid.Column=2 in the row template.
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border b && Grid.GetColumn(b) == 2)
                return FindDescendant<TextBox>(b);
            var nested = FindGrossWeightTextBox(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            CommitFocusedEdit();
            return;
        }
        Dispatcher.BeginInvoke(new Action(FocusRate24Kt));
    }

    private static void CommitFocusedEdit()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return;

        // Editable ComboBox: focus is on the inner TextBox; flush the ComboBox.Text binding
        // (LostFocus doesn't fire reliably when an ancestor goes Visibility=Collapsed).
        if (FindAncestor<ComboBox>(focused) is { } combo)
        {
            combo.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            return;
        }

        if (focused is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void FocusRate24Kt()
    {
        Rate24KtBox.Focus();
        Rate24KtBox.SelectAll();
    }

    private void OnRate24KtPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PartyBox.Focus();
        PartyBox.SelectAll();
        e.Handled = true;
    }

    private void OnPartyBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (FocusFirstRowItemCell()) e.Handled = true;
    }

    private bool FocusFirstRowItemCell()
    {
        if (LinesItems.Items.Count == 0) return false;
        var container = GetLineContainer(0);
        if (container is null) return false;
        var combo = FindItemCellComboBox(container);
        return combo?.Focus() == true;
    }

    private DependencyObject? GetLineContainer(int index)
    {
        if (index < 0 || index >= LinesItems.Items.Count) return null;

        var container = LinesItems.ItemContainerGenerator.ContainerFromIndex(index) as DependencyObject;
        if (container is not null) return container;

        LinesItems.ScrollIntoView(LinesItems.Items[index]);
        LinesItems.UpdateLayout();
        return LinesItems.ItemContainerGenerator.ContainerFromIndex(index) as DependencyObject;
    }

    private static ComboBox? FindItemCellComboBox(DependencyObject root)
    {
        // Item cell is the Border at Grid.Column=1 in the row template.
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border b && Grid.GetColumn(b) == 1)
            {
                return FindDescendant<ComboBox>(b);
            }
            var nested = FindItemCellComboBox(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnQuickAddBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not InvoiceViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                vm.QuickAddCommitCommand.Execute(null);
                QuickAddBox.Focus();
                e.Handled = true;
                break;
            case Key.Down:
                if (QuickAddList.Items.Count > 0)
                {
                    QuickAddList.Focus();
                    if (QuickAddList.SelectedIndex < 0) QuickAddList.SelectedIndex = 0;
                    (QuickAddList.ItemContainerGenerator.ContainerFromIndex(QuickAddList.SelectedIndex)
                        as ListBoxItem)?.Focus();
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                vm.QuickAddQuery = string.Empty;
                e.Handled = true;
                break;
        }
    }

    private void OnQuickAddListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not InvoiceViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                vm.QuickAddCommitCommand.Execute(QuickAddList.SelectedItem);
                QuickAddBox.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.QuickAddQuery = string.Empty;
                QuickAddBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnQuickAddListClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not InvoiceViewModel vm) return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ItemMasterRowVm master) return;

        vm.QuickAddCommitCommand.Execute(master);
        e.Handled = true;
    }

    private void OnNumericBoxDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        tb.Focus();
        tb.SelectAll();
        e.Handled = true;
    }

    private void OnLinesPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.OriginalSource is not UIElement source) return;

        // Let an open ComboBox consume Enter for selection first.
        var combo = FindAncestor<ComboBox>(source as DependencyObject);
        if (combo is { IsDropDownOpen: true }) return;

        var reverse = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (TryMoveLineFocus(source, reverse))
            e.Handled = true;
    }

    private bool TryMoveLineFocus(UIElement source, bool reverse)
    {
        var sourceNode = source as DependencyObject;
        var cellBorder = FindLineCellBorder(sourceNode);
        if (cellBorder is null) return false;

        var combo = FindAncestor<ComboBox>(sourceNode);
        if (combo is not null)
            combo.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();

        // If Enter lands on the Item cell of an empty row, jump to Save.
        if (!reverse && Grid.GetColumn(cellBorder) == 1)
        {
            if (cellBorder.DataContext is BillLineViewModel row && row.IsEmpty)
            {
                SaveButton.Focus();
                return true;
            }
        }

        var rowContainer = FindAncestor<ListBoxItem>(sourceNode);
        if (rowContainer is null) return false;

        var rowIndex = LinesItems.ItemContainerGenerator.IndexFromContainer(rowContainer);
        if (rowIndex < 0) return false;

        var currentColumn = Grid.GetColumn(cellBorder);
        if (FocusLineCellWithinRow(rowContainer, currentColumn, reverse))
            return true;

        var targetRowIndex = reverse ? rowIndex - 1 : rowIndex + 1;
        if (FocusLineRowEndpoint(targetRowIndex, reverse))
            return true;

        if (!reverse)
        {
            SaveButton.Focus();
            return true;
        }

        return source.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
    }

    private bool FocusLineCellWithinRow(DependencyObject rowContainer, int currentColumn, bool reverse)
    {
        if (reverse)
        {
            for (var i = EditableLineColumns.Length - 1; i >= 0; i--)
            {
                var column = EditableLineColumns[i];
                if (column >= currentColumn) continue;
                if (FocusLineCell(rowContainer, column)) return true;
            }

            return false;
        }

        foreach (var column in EditableLineColumns)
        {
            if (column <= currentColumn) continue;
            if (FocusLineCell(rowContainer, column)) return true;
        }

        return false;
    }

    private bool FocusLineRowEndpoint(int rowIndex, bool reverse)
    {
        var container = GetLineContainer(rowIndex);
        if (container is null) return false;

        if (reverse)
        {
            for (var i = EditableLineColumns.Length - 1; i >= 0; i--)
            {
                if (FocusLineCell(container, EditableLineColumns[i])) return true;
            }

            return false;
        }

        foreach (var column in EditableLineColumns)
        {
            if (FocusLineCell(container, column)) return true;
        }

        return false;
    }

    private static bool FocusLineCell(DependencyObject rowContainer, int column)
    {
        var cell = FindLineCellBorderByColumn(rowContainer, column);
        if (cell is null) return false;

        var combo = FindDescendant<ComboBox>(cell);
        if (combo is not null && combo.IsVisible && combo.IsEnabled && combo.Focusable)
        {
            combo.Focus();
            if (combo.IsEditable)
            {
                combo.ApplyTemplate();
                if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editableTextBox)
                {
                    editableTextBox.Focus();
                    editableTextBox.SelectAll();
                }
            }
            return true;
        }

        var textBox = FindDescendant<TextBox>(cell);
        if (textBox is not null && textBox.IsVisible && textBox.IsEnabled && textBox.Focusable)
        {
            textBox.Focus();
            textBox.SelectAll();
            return true;
        }

        return false;
    }

    private static Border? FindLineCellBorder(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Border border && IsLineCellBorder(border))
                return border;
            if (node is ListBoxItem)
                return null;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private static Border? FindLineCellBorderByColumn(DependencyObject root, int column)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border border
                && Grid.GetColumn(border) == column
                && IsLineCellBorder(border))
            {
                return border;
            }

            var nested = FindLineCellBorderByColumn(child, column);
            if (nested is not null) return nested;
        }

        return null;
    }

    private static bool IsLineCellBorder(Border border)
        => border.DataContext is BillLineViewModel
           && VisualTreeHelper.GetParent(border) is Grid grid
           && grid.ColumnDefinitions.Count == 13
           && ReferenceEquals(border.DataContext, grid.DataContext);

    private void OnLinesGotFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InvoiceViewModel vm) return;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container is null) return;
        var index = LinesItems.ItemContainerGenerator.IndexFromContainer(container);
        if (index >= 0) vm.FocusedRowIndex = index;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = VisualTreeHelper.GetParent(node);
        return node as T;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F2:
                Rate24KtBox.Focus();
                Rate24KtBox.SelectAll();
                e.Handled = true;
                break;
            case Key.F3:
                QuickAddBox.Focus();
                e.Handled = true;
                break;
            case Key.F4:
                PartyBox.Focus();
                PartyBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Delete when (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                // Without this preview-time interception the focused TextBox swallows
                // Ctrl+Delete (built-in word-delete-forward), so the MainWindow KeyBinding
                // never fires and the operator can't remove the focused line.
                if (DataContext is InvoiceViewModel vm
                    && vm.RemoveFocusedRowCommand.CanExecute(null))
                {
                    vm.RemoveFocusedRowCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }

        base.OnPreviewKeyDown(e);
    }
}
