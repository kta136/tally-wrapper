using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using ShowroomBilling.Application.Logging;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Handover;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.ViewModels.Setup;
using ShowroomBilling.Desktop.Views.Handover;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private ILogger<App>? _logger;
    private HandoverPipe.Server? _handoverPipe;
    private ToastWindow? _handoverToast;
    private readonly UnhandledErrorPolicy _unhandledErrors = UnhandledErrorPolicy.Default();
    private bool _startedUp;
    private bool _reportingError;

    public App()
    {
        UiCulture.Apply();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// The hand-over link this start was given, when no running copy took it:
    /// <see cref="Program.Main"/> has already offered it, so it is not offered again here.
    /// </summary>
    public string? HandoverLink { get; init; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        UiDensityManager.ApplyStoredDensity();
        try
        {
            await StartAsync(HandoverLink);
        }
        catch (Exception ex)
        {
            ReportFatal("Billing could not start.", ex);
        }
    }

    private static string EnvironmentName => Program.EnvironmentName;

    private async Task StartAsync(string? handoverLink)
    {

        // Phase timings are emitted at Information level. Run a `dotnet-trace`
        // alongside or just `Get-Content` the rolling log to see exactly where
        // pre-window time goes. Total = "ready" timestamp; per-phase tells us
        // which slice dominates and is worth optimising next.
        var totalTimer = Stopwatch.StartNew();
        var phaseTimer = Stopwatch.StartNew();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = typeof(App).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = EnvironmentName
        });

        // appsettings.json is embedded into the Desktop assembly (see csproj
        // <EmbeddedResource Include="appsettings.json"/>) so the prod folder contains
        // only TallyWrapper.exe + the two .NET runtime artifacts. AddJsonStream binds the
        // baked-in baseline; appsettings.{Environment}.json still loads from disk so
        // dev can override without rebuilding, and SHOWROOM_DESKTOP_* env vars win
        // last as before.
        var baseSettingsStream = typeof(App).Assembly.GetManifestResourceStream("appsettings.json")
            ?? throw new InvalidOperationException(
                "Embedded resource 'appsettings.json' not found on the Desktop assembly. " +
                "Check the LogicalName in ShowroomBilling.Desktop.csproj.");

        builder.Configuration.Sources.Clear();
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonStream(baseSettingsStream)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
            .AddInMemoryCollection(DesktopBootstrapLocalOverrideStore.LoadConfigurationPairs())
            .AddEnvironmentVariables(prefix: "SHOWROOM_DESKTOP_");

        builder.Logging.AddRollingFile(builder.Configuration, filePrefix: "desktop");

        builder.Services.Configure<DesktopBootstrapOptions>(builder.Configuration.GetSection(DesktopBootstrapOptions.SectionName));
        builder.Services.AddSingleton<IApiEndpointResolver, ApiEndpointResolver>();
        builder.Services.AddSingleton<DesktopDeviceIdentityStore>();
        builder.Services.AddSingleton<ISetupWizardCompletionStore, SetupWizardCompletionStore>();
        builder.Services.AddSingleton<DeviceTokenProvider>();
        builder.Services.AddTransient<CorrelationIdHandler>();
        builder.Services.AddTransient<DeviceTokenHandler>();
        builder.Services.AddApiClient<ISettingsApiClient, SettingsApiClient>();
        builder.Services.AddApiClient<IBillsApiClient, BillsApiClient>();
        builder.Services.AddApiClient<INumberingApiClient, NumberingApiClient>();
        builder.Services.AddApiClient<IMastersApiClient, MastersApiClient>();
        builder.Services.AddApiClient<IHealthApiClient, HealthApiClient>(TimeSpan.FromSeconds(5));
        builder.Services.AddApiClient<IAdminApiClient, AdminApiClient>();
        builder.Services.AddApiClient<IPrintAssetApiClient, PrintAssetApiClient>();
        builder.Services.AddApiClient<IClientPresenceApiClient, ClientPresenceApiClient>(TimeSpan.FromSeconds(5));
        builder.Services.AddHostedService<ClientPresenceHeartbeatService>();
        builder.Services.AddSingleton<AdminTokenStore>();

        builder.Services.AddSingleton<BillPrintRenderer>();
        builder.Services.AddSingleton<IPrintDispatcher, PrintDispatcher>();
        builder.Services.AddSingleton<IPrintPreferencesStore, PrintPreferencesStore>();
        builder.Services.AddSingleton<ICompanyProfileProvider, CompanyProfileProvider>();
        builder.Services.AddSingleton<IPrintLayoutOptionsProvider, PrintLayoutOptionsProvider>();

        // One dialog service: the shell shows what it holds, and every view model asks through it.
        builder.Services.AddSingleton<DialogService>();
        builder.Services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());

        builder.Services.AddSingleton(sp => new InvoiceViewModel(
            sp.GetRequiredService<IBillsApiClient>(),
            sp.GetRequiredService<INumberingApiClient>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<AdminTokenStore>()));
        builder.Services.AddSingleton(sp => new BillsViewModel(
            sp.GetRequiredService<IBillsApiClient>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<AdminTokenStore>(),
            sp.GetRequiredService<SettingsViewModel>()));
        builder.Services.AddSingleton(sp => new BillDetailsViewModel(
            sp.GetRequiredService<IBillsApiClient>(),
            sp.GetRequiredService<IDialogService>()));
        builder.Services.AddSingleton<PrintPreviewViewModel>();
        builder.Services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<ISettingsApiClient>(),
            sp.GetRequiredService<IMastersApiClient>(),
            sp.GetRequiredService<IPrintAssetApiClient>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IPrintDispatcher>(),
            sp.GetRequiredService<IPrintPreferencesStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DesktopBootstrapOptions>>().Value));
        builder.Services.AddSingleton<AdminUnlockViewModel>();
        builder.Services.AddSingleton(sp => new ViewModels.SyntheticBatch.SyntheticBatchViewModel(
            sp.GetRequiredService<IBillsApiClient>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<AdminTokenStore>(),
            sp.GetRequiredService<SettingsViewModel>()));
        builder.Services.AddSingleton(sp => new SetupWizardViewModel(
            sp.GetRequiredService<ISettingsApiClient>(),
            sp.GetRequiredService<ISetupWizardCompletionStore>()));
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        var hostBuildMs = phaseTimer.ElapsedMilliseconds;
        phaseTimer.Restart();

        // Starts lightweight host services such as the non-blocking client
        // presence heartbeat.
        await _host.StartAsync();
        var hostStartMs = phaseTimer.ElapsedMilliseconds;
        phaseTimer.Restart();

        var window = _host.Services.GetRequiredService<MainWindow>();
        var resolveWindowMs = phaseTimer.ElapsedMilliseconds;
        phaseTimer.Restart();
        StartHandover(_host.Services.GetRequiredService<MainWindowViewModel>(), handoverLink);
        window.Show();
        // From here an unexpected error is reported and survived; before it, there was no app to keep.
        _startedUp = true;
        var showWindowMs = phaseTimer.ElapsedMilliseconds;
        var windowVisibleMs = totalTimer.ElapsedMilliseconds;

        var startupLogger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        startupLogger.LogInformation(
            "[startup-timing] hostBuild={HostBuildMs}ms hostStart={HostStartMs}ms resolveWindow={ResolveWindowMs}ms showWindow={ShowWindowMs}ms windowVisibleAt={WindowVisibleMs}ms",
            hostBuildMs,
            hostStartMs,
            resolveWindowMs,
            showWindowMs,
            windowVisibleMs);
    }

    /// <summary>
    /// Bill hand-over (docs/18_bill_handover.md): registers the link scheme when this is the
    /// published app, listens for links from later starts, and queues the link this start was given.
    /// </summary>
    private void StartHandover(MainWindowViewModel shell, string? handoverLink)
    {
        var loggerFactory = _host!.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Handover");
        shell.Handover.Logger = logger;
        shell.Handover.ShowToast = ShowHandoverToast;

        if (HandoverUrlScheme.ShouldRegisterThisProcess(EnvironmentName) && Environment.ProcessPath is { } exe)
        {
            try
            {
                using var classes = Registry.CurrentUser.CreateSubKey(HandoverUrlScheme.ClassesKey);
                if (HandoverUrlScheme.Register(classes, exe))
                {
                    logger.LogInformation("Registered the hand-over link scheme for this Windows user.");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The hand-over link scheme could not be registered.");
            }
        }

        _handoverPipe = new HandoverPipe.Server(
            HandoverPipe.NameFor(EnvironmentName),
            link => Dispatcher.BeginInvoke(() => shell.Handover.Receive(link)),
            logger);
        _handoverPipe.Start();

        if (handoverLink is not null)
        {
            shell.Handover.Receive(handoverLink);
        }
    }

    private void ShowHandoverToast(HandoverToastViewModel toast)
    {
        _handoverToast?.Close();
        var window = new ToastWindow(new HandoverToastView { DataContext = toast });
        window.Clicked = () => toast.OpenBillsCommand.Execute(null);
        toast.OpenBillsRequested += (_, _) => window.Close();
        toast.CloseRequested += (_, _) => window.FadeOut();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_handoverToast, window))
            {
                _handoverToast = null;
            }
        };
        _handoverToast = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (!_unhandledErrors.ShouldKeepRunning(_startedUp, DateTimeOffset.UtcNow))
        {
            ReportFatal("Billing encountered an unexpected error and must close.", e.Exception);
            return;
        }

        _logger?.LogError(e.Exception, "Unhandled error on the UI thread; the app stays open.");
        if (_reportingError)
        {
            // Raised while the message below is already on screen: it is in the log, and one box is enough.
            return;
        }

        // A message box and not an overlay: whatever failed may be the shell
        // itself, and the box does not depend on it.
        _reportingError = true;
        try
        {
            MessageBox.Show(
                "Something went wrong, and what you were doing may not have finished.\n\n"
                + $"{e.Exception.Message}\n\n"
                + "Billing is still open and your bill is still on screen. Check it before you save. "
                + "Details were written to the application log.",
                "Tally Wrapper",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _reportingError = false;
        }
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _logger?.LogCritical(exception, "Unhandled AppDomain exception. Terminating={IsTerminating}.", e.IsTerminating);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved background task exception.");
        e.SetObserved();
    }

    private void ReportFatal(string message, Exception exception)
    {
        _logger?.LogCritical(exception, "{Message}", message);
        MessageBox.Show(
            $"{message}\n\n{exception.Message}\n\nDetails were written to the application log.",
            "Tally Wrapper",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(-1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _handoverPipe?.Dispose();
        if (_host is not null)
        {
            // Synchronously, so WPF doesn't tear down the process before the
            // hosted services have stopped.
            try { _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } catch { }
            try { _host.Dispose(); } catch { }
        }

        base.OnExit(e);
    }
}
