using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Options;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private static readonly TimeSpan ApiReadyPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ApiReadyTimeout = TimeSpan.FromSeconds(5);

    private readonly MainWindowViewModel _viewModel;
    private readonly IApiEndpointResolver _endpointResolver;
    private readonly IApiReadinessSignal _apiReadiness;
    private HwndSource? _windowSource;

    public MainWindow(
        MainWindowViewModel viewModel,
        IApiEndpointResolver endpointResolver,
        IApiReadinessSignal apiReadiness)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _endpointResolver = endpointResolver;
        _apiReadiness = apiReadiness;
        DataContext = _viewModel;
        Loaded += OnLoaded;
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
            await WaitForApiReadinessAsync();
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
            // Unblocks consumers waiting to attempt their own bounded API call.
            _apiReadiness.MarkReady();
        }
    }

    private async Task WaitForApiReadinessAsync()
    {
        if (!Uri.TryCreate(_endpointResolver.BaseUrl, UriKind.Absolute, out var uri) || uri.IsDefaultPort)
        {
            return;
        }

        await TcpPortProbe.WaitUntilOpenAsync(uri.Host, uri.Port, ApiReadyPollInterval, ApiReadyTimeout);
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
