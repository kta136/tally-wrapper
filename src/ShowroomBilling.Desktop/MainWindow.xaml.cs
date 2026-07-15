using System.Windows;
using Microsoft.Extensions.Options;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ApiReadyPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ApiReadyTimeout = TimeSpan.FromSeconds(5);

    private readonly MainWindowViewModel _viewModel;
    private readonly IApiEndpointResolver _endpointResolver;
    private readonly IApiReadinessSignal _apiReadiness;

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
        StateChanged += OnWindowStateChanged;
        ApplyMaximizeChromeCompensation();
    }

    // WindowStyle=None + WindowChrome makes Windows position a maximized
    // window so each edge sits SystemParameters.WindowResizeBorder px past the
    // work-area. Without compensation, the bottom status bar gets clipped.
    private void OnWindowStateChanged(object? sender, EventArgs e) => ApplyMaximizeChromeCompensation();

    private void ApplyMaximizeChromeCompensation()
    {
        if (RootChrome is null) return;
        if (WindowState == WindowState.Maximized)
        {
            var border = SystemParameters.WindowResizeBorderThickness;
            // Add the WindowChrome.ResizeBorderThickness (6) on top of the OS
            // resize-border. Together these match how much the maximized window
            // overshoots the work-area on every edge.
            const double chromeResize = 6d;
            RootChrome.Padding = new Thickness(
                border.Left + chromeResize,
                border.Top + chromeResize,
                border.Right + chromeResize,
                border.Bottom + chromeResize);
        }
        else
        {
            RootChrome.Padding = new Thickness(0);
        }
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
}
