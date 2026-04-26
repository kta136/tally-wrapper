using System.Windows;
using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Views;

public partial class TitleBarView : UserControl
{
    public TitleBarView()
    {
        InitializeComponent();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w)
            w.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Window.GetWindow(this)?.Close();
    }
}
