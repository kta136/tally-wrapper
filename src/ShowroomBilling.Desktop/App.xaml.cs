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
using ShowroomBilling.Application.Logging;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.ViewModels.Setup;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiDensityManager.ApplyStoredDensity();
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            ReportFatal("Billing could not start.", ex);
        }
    }

    private async Task StartAsync()
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
            EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Production
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
        // Set by MainWindow.OnLoaded once the API has been given a moment to answer; awaited
        // by InvoiceViewModel.RefreshNextNumberAsync(waitForApi: true) so the
        // first numbering preview lands on a hot connection.
        builder.Services.AddSingleton<IApiReadinessSignal, ApiReadinessSignal>();

        builder.Services.AddSingleton<IBillPrintRenderer, BillPrintRenderer>();
        builder.Services.AddSingleton<IPrintDispatcher, PrintDispatcher>();
        builder.Services.AddSingleton<IPrintPreferencesStore, PrintPreferencesStore>();
        builder.Services.AddSingleton<ICompanyProfileProvider, CompanyProfileProvider>();
        builder.Services.AddSingleton<IPrintLayoutOptionsProvider, PrintLayoutOptionsProvider>();

        builder.Services.AddSingleton(sp => new InvoiceViewModel(
            sp.GetService<IBillsApiClient>(),
            sp.GetService<INumberingApiClient>(),
            sp.GetService<SettingsViewModel>(),
            sp.GetService<IApiReadinessSignal>(),
            sp.GetService<AdminTokenStore>()));
        builder.Services.AddSingleton(sp => new BillsViewModel(
            sp.GetService<IBillsApiClient>(),
            sp.GetService<AdminTokenStore>()));
        builder.Services.AddSingleton(sp => new BillDetailsViewModel(
            sp.GetService<IBillsApiClient>()));
        builder.Services.AddSingleton<PrintPreviewViewModel>();
        builder.Services.AddSingleton(sp => new SettingsViewModel(
            sp.GetService<ISettingsApiClient>(),
            sp.GetService<IMastersApiClient>(),
            sp.GetService<IPrintAssetApiClient>(),
            sp.GetService<IPrintDispatcher>(),
            sp.GetService<IPrintPreferencesStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DesktopBootstrapOptions>>().Value));
        builder.Services.AddSingleton<AdminUnlockViewModel>();
        builder.Services.AddSingleton(sp => new ViewModels.SyntheticBatch.SyntheticBatchViewModel(
            sp.GetService<IBillsApiClient>(),
            sp.GetService<AdminTokenStore>(),
            sp.GetService<SettingsViewModel>()));
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
        window.Show();
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportFatal("Billing encountered an unexpected error and must close.", e.Exception);
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
