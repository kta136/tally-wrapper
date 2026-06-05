using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Logging;
using ShowroomBilling.Desktop.Configuration;
using System.Collections.Generic;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Phase timings are emitted at Information level. Run a `dotnet-trace`
        // alongside or just `Get-Content` the rolling log to see exactly where
        // pre-window time goes. Total = "ready" timestamp; per-phase tells us
        // which slice dominates and is worth optimising next.
        var totalTimer = Stopwatch.StartNew();
        var phaseTimer = Stopwatch.StartNew();

        // If the publish-prod build embedded the API as a zipped resource inside
        // this exe, extract it now (before the host binds ChildProcessOptions) and
        // override the configured API path so ChildProcessSupervisor spawns the
        // extracted copy instead of looking for a sibling folder.
        var embeddedApi = EmbeddedApiExtractor.ExtractIfPresent();
        var embeddedExtractMs = phaseTimer.ElapsedMilliseconds;
        phaseTimer.Restart();

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

        if (embeddedApi is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChildProcesses:Api:ExecutablePath"] = embeddedApi.ApiExePath,
                ["ChildProcesses:Api:WorkingDirectory"] = embeddedApi.WorkingDirectory,
            });
        }

        builder.Logging.AddRollingFile(builder.Configuration, filePrefix: "desktop");

        builder.Services.AddApplicationLayer();
        builder.Services.Configure<DesktopBootstrapOptions>(builder.Configuration.GetSection(DesktopBootstrapOptions.SectionName));
        builder.Services.Configure<DesktopLocalPreferencesOptions>(builder.Configuration.GetSection(DesktopLocalPreferencesOptions.SectionName));
        builder.Services.Configure<ChildProcessOptions>(builder.Configuration.GetSection(ChildProcessOptions.SectionName));
        builder.Services.AddSingleton<IApiEndpointResolver, ApiEndpointResolver>();
        builder.Services.AddSingleton<DesktopDeviceIdentityStore>();
        builder.Services.AddSingleton<ChildProcessSupervisor>();
        builder.Services.AddSingleton<IChildProcessSupervisor>(sp => sp.GetRequiredService<ChildProcessSupervisor>());
        builder.Services.AddSingleton<ISetupWizardCompletionStore, SetupWizardCompletionStore>();
        builder.Services.AddSingleton<DeviceTokenProvider>();
        builder.Services.AddTransient<CorrelationIdHandler>();
        builder.Services.AddTransient<DeviceTokenHandler>();
        builder.Services.AddHttpClient<IRuntimeApiClient, RuntimeApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHttpClient<ISettingsApiClient, SettingsApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHttpClient<IBillsApiClient, BillsApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHttpClient<INumberingApiClient, NumberingApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHttpClient<IMastersApiClient, MastersApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHttpClient<IHealthApiClient, HealthApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHttpClient<IAdminApiClient, AdminApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHttpClient<IDraftLeaseApiClient, DraftLeaseApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHttpClient<IPrintAssetApiClient, PrintAssetApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHttpClient<IClientPresenceApiClient, ClientPresenceApiClient>((services, client) =>
        {
            var endpoint = services.GetRequiredService<IApiEndpointResolver>();
            client.BaseAddress = new Uri(endpoint.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        }).AddHttpMessageHandler<CorrelationIdHandler>()
          .AddHttpMessageHandler<DeviceTokenHandler>();
        builder.Services.AddHostedService<ClientPresenceHeartbeatService>();
        builder.Services.AddSingleton<AdminTokenStore>();
        // Set by MainWindow.OnLoaded after the API child is reachable; awaited
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
            sp.GetService<IApiReadinessSignal>()));
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
            sp.GetService<IRuntimeApiClient>(),
            sp.GetService<IHealthApiClient>(),
            sp.GetService<AdminTokenStore>(),
            sp.GetService<IChildProcessSupervisor>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DesktopBootstrapOptions>>().Value));
        builder.Services.AddSingleton<AdminUnlockViewModel>();
        builder.Services.AddSingleton(sp => new ViewModels.SyntheticBatch.SyntheticBatchViewModel(
            sp.GetService<IBillsApiClient>(),
            sp.GetService<AdminTokenStore>(),
            sp.GetService<SettingsViewModel>()));
        builder.Services.AddSingleton(sp => new SetupWizardViewModel(
            sp.GetRequiredService<IRuntimeApiClient>(),
            sp.GetRequiredService<IHealthApiClient>(),
            sp.GetRequiredService<ISettingsApiClient>(),
            sp.GetService<IChildProcessSupervisor>(),
            sp.GetRequiredService<ISetupWizardCompletionStore>()));
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        var hostBuildMs = phaseTimer.ElapsedMilliseconds;
        phaseTimer.Restart();

        // Starts lightweight host services such as the non-blocking client
        // presence heartbeat.
        await _host.StartAsync();
        var hostStartMs = phaseTimer.ElapsedMilliseconds;
        phaseTimer.Restart();

        // Materialize the device token and spawn the API child while WPF resolves
        // MainWindow. The order inside the worker is still important: the API
        // child reads the token file during its own startup.
        var apiChildStartupTask = StartApiChildAsync(_host.Services);

        var window = _host.Services.GetRequiredService<MainWindow>();
        var resolveWindowMs = phaseTimer.ElapsedMilliseconds;
        phaseTimer.Restart();
        window.Show();
        var showWindowMs = phaseTimer.ElapsedMilliseconds;
        var windowVisibleMs = totalTimer.ElapsedMilliseconds;

        var apiChildStartup = await apiChildStartupTask;

        var startupLogger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        startupLogger.LogInformation(
            "[startup-timing] embeddedExtract={EmbeddedExtractMs}ms hostBuild={HostBuildMs}ms hostStart={HostStartMs}ms resolveWindow={ResolveWindowMs}ms showWindow={ShowWindowMs}ms deviceToken={DeviceTokenMs}ms supervisor={SupervisorMs}ms apiChildStarted={ApiChildStarted} windowVisibleAt={WindowVisibleMs}ms",
            embeddedExtractMs,
            hostBuildMs,
            hostStartMs,
            resolveWindowMs,
            showWindowMs,
            apiChildStartup.DeviceTokenMs,
            apiChildStartup.SupervisorMs,
            apiChildStartup.Succeeded,
            windowVisibleMs);
    }

    private static Task<ApiChildStartupTiming> StartApiChildAsync(IServiceProvider services)
        => Task.Run(() =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
            var spawnTimer = Stopwatch.StartNew();
            long deviceTokenMs = 0;

            try
            {
                var endpoint = services.GetRequiredService<IApiEndpointResolver>();
                if (endpoint.IsServerMode)
                {
                    return new ApiChildStartupTiming(deviceTokenMs, spawnTimer.ElapsedMilliseconds, Succeeded: true);
                }

                services.GetRequiredService<DeviceTokenProvider>().GetOrCreateToken();
                deviceTokenMs = spawnTimer.ElapsedMilliseconds;
                spawnTimer.Restart();

                // Spawn the API as a child inside a Windows Job Object.
                // KillOnJobClose guarantees it dies when this process exits (even on crash).
                services.GetRequiredService<ChildProcessSupervisor>().Start();
                return new ApiChildStartupTiming(deviceTokenMs, spawnTimer.ElapsedMilliseconds, Succeeded: true);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed while preparing the device token or starting the API child process.");
                return new ApiChildStartupTiming(deviceTokenMs, spawnTimer.ElapsedMilliseconds, Succeeded: false);
            }
        });

    private readonly record struct ApiChildStartupTiming(long DeviceTokenMs, long SupervisorMs, bool Succeeded);

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Disposing the supervisor first closes the Job Object handle,
            // which terminates every spawned API child via KillOnJobClose.
            // Must run synchronously so WPF doesn't tear down the process
            // before the API and hosted services have stopped.
            try { _host.Services.GetRequiredService<ChildProcessSupervisor>().Dispose(); } catch { }

            try { _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } catch { }
            try { _host.Dispose(); } catch { }
        }

        base.OnExit(e);
    }
}
