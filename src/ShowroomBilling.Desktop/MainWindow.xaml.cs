using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly MainWindowViewModel _viewModel;
    private HwndSource? _windowSource;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.Handover.BringToFront = () => WindowActivation.BringToFront(this);
        Loaded += OnLoaded;

        // The cursor goes back to the screen when the last dialog closes.
        var focusReturn = new DialogFocusReturn(this, ScreenRegion, () => _viewModel.IsAnyDialogOpen);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.ActiveDialog) && !_viewModel.IsAnyDialogOpen)
            {
                focusReturn.Restore();
            }
        };
        _viewModel.ConfirmDialog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConfirmDialogViewModel.IsOpen) && !_viewModel.IsAnyDialogOpen)
            {
                focusReturn.Restore();
            }
        };
    }

    /// <summary>
    /// <c>?</c> and the backtick are shortcuts only where they are not text, so they are not
    /// key bindings (see <see cref="BareKeyShortcuts"/>): the key is taken only when its shortcut ran.
    /// The same goes for Ctrl+Alt+G, which only acts while admin is unlocked.
    /// </summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // With Alt held, the key arrives as Key.System and the real key is SystemKey.
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (!e.Handled && BareKeyShortcuts.TryRun(
                key,
                System.Windows.Input.Keyboard.Modifiers,
                _viewModel.OpenShortcutsKeyCommand,
                _viewModel.OpenAdminUnlockKeyCommand,
                _viewModel.OpenSyntheticBatchKeyCommand))
        {
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowProc);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowProc);
        _windowSource = null;
        base.OnClosed(e);
    }

    private static nint WindowProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmGetMinMaxInfo && TryApplyMonitorWorkArea(hwnd, lParam))
        {
            handled = true;
        }

        return nint.Zero;
    }

    private static bool TryApplyMonitorWorkArea(nint hwnd, nint minMaxInfoPointer)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
        minMaxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMaxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMaxInfo.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMaxInfo.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;
        Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, fDeleteOld: false);
        return true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _viewModel.SystemState = SystemState.Limited;
            _viewModel.BannerText = $"Startup initialization failed: {ex.Message}";
            MessageBox.Show(
                $"Billing opened in limited mode because startup initialization failed.\n\n{ex.Message}",
                "Startup warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            // A hand-over link that started the app is taken as soon as the settings are in
            // (InitializeAsync). This is for what that leaves: a start that failed before then,
            // and a PC whose first setup is due, where the link waits until the form is open.
            _viewModel.Handover.MarkStarted();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);
}
