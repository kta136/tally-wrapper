using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Brings a window in front of the others for something the operator must answer. Windows lets a
/// program do that only while it may take the foreground (it was just started, the user just used
/// it, or the program that had the foreground allowed it); when it may not, the window's taskbar
/// button flashes instead.
/// </summary>
public static class WindowActivation
{
    private const int ShowRestore = 9;
    private const uint FlashAll = 0x00000003;
    private const uint FlashUntilForeground = 0x0000000C;

    public static void BringToFront(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsVisible)
        {
            window.Show();
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Minimized)
        {
            _ = ShowWindow(handle, ShowRestore);
        }

        window.Activate();
        // A topmost flip puts the window over the others even where the activation is refused.
        window.Topmost = true;
        window.Topmost = false;
        _ = SetForegroundWindow(handle);

        if (GetForegroundWindow() != handle)
        {
            var flash = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = handle,
                Flags = FlashAll | FlashUntilForeground,
                Count = uint.MaxValue,
                Timeout = 0,
            };
            _ = FlashWindowEx(ref flash);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
