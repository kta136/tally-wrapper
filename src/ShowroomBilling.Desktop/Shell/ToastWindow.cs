using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// A card at the bottom right of the screen, over every window, that never takes the focus: the
/// operator keeps typing in the window they are in. It fades after <see cref="VisibleFor"/> (held
/// while the pointer is on it); a click on it runs <see cref="Clicked"/>. Not a dialog: the dialogs
/// are overlays in the main window, and this one has to show while that window is behind another.
/// </summary>
public sealed class ToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _timer;
    private bool _closing;

    /// <param name="card">The card; it carries its own shadow, so the window leaves room round it.</param>
    public ToastWindow(FrameworkElement card)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Off screen until its size is known, so it never shows anywhere but the corner.
        Left = -32000;
        Top = -32000;
        Title = "Tally Wrapper";
        Content = new Border { Padding = new Thickness(24), Child = card, Background = Brushes.Transparent };

        _timer = new DispatcherTimer { Interval = VisibleFor };
        _timer.Tick += (_, _) => FadeOut();
        MouseEnter += (_, _) => Hold();
        MouseLeave += (_, _) => _timer.Start();
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        SourceInitialized += (_, _) => MakeNonActivating();
        Loaded += (_, _) =>
        {
            PlaceAtBottomRight();
            _timer.Start();
        };
    }

    /// <summary>How long the card stays before it fades.</summary>
    public static TimeSpan VisibleFor { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Runs when the card is clicked anywhere but on one of its buttons.</summary>
    public Action? Clicked { get; set; }

    /// <summary>Fades the card out and closes it.</summary>
    public void FadeOut()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _timer.Stop();
        var fade = new DoubleAnimation(0, FadeTime);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void Hold()
    {
        if (_closing)
        {
            return;
        }

        _timer.Stop();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A button on the card has its own command and marks the click handled.
        if (e.Handled)
        {
            return;
        }

        Clicked?.Invoke();
    }

    private void PlaceAtBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }

    private void MakeNonActivating()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);
}
