using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Npgsql;
using Polly;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Leases;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Application.Numbering;
using ShowroomBilling.Application.PrintAssets;
using ShowroomBilling.Application.Printing;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Infrastructure.Admin;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Configuration;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Leases;
using ShowroomBilling.Infrastructure.Masters;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.PrintAssets;
using ShowroomBilling.Infrastructure.Printing;
using ShowroomBilling.Infrastructure.Settings;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Infrastructure;

public static class DependencyInjection
{
    private const string DefaultLocalPostgresConnectionString =
        "Host=localhost;Port=5432;Database=tally_wrapper;Username=postgres;Password=postgres";

    public static IServiceCollection AddInfrastructureLayer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        var configuredConnectionString = configuration.GetConnectionString("Postgres");
        var connectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
            ? DefaultLocalPostgresConnectionString
            : configuredConnectionString;

        var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MinPoolSize = 2,
            KeepAlive = 30,
            NoResetOnClose = true
        };
        var dataSource = new NpgsqlDataSourceBuilder(connectionBuilder.ConnectionString).Build();
        services.AddSingleton(dataSource);
        services.AddDbContextPool<ShowroomBillingDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql => npgsql.CommandTimeout(15)));
        services.AddScoped<IMasterSnapshotService, MasterSnapshotService>();
        services.AddScoped<INumberingService, NumberingService>();
        services.AddScoped<IBillService, BillService>();
        services.AddScoped<ILatestBillTimestampProvider, LatestBillTimestampProvider>();
        services.AddScoped<ISyntheticBatchExecutor, SyntheticBatchExecutor>();
        services.AddScoped<IInvoiceDocumentRenderer, QuestPdfFoundationDocumentRenderer>();
        services.AddScoped<ICloudSettingsService, CloudSettingsService>();
        services.AddScoped<IDraftLeaseService, DraftLeaseService>();
        services.AddScoped<IAdminAuthService, AdminAuthService>();
        services.AddSingleton<IAdminUnlockThrottle, AdminUnlockThrottle>();
        services.AddScoped<IPrintAssetService, PrintAssetService>();
        services.AddScoped<ITallyCompanyHealthService, TallyCompanyHealthService>();

        // Tally (sync, operator-initiated only — no background workers).
        // Short retry handles transient XMLHTTP blips; the total timeout still
        // comes from the cloud-settings-configured CTS inside TallyXmlClient.
        services.AddHttpClient<ITallyXmlClient, TallyXmlClient>()
            .AddResilienceHandler("tally", pipeline =>
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .HandleResult(r => (int)r.StatusCode >= 500)
                });
            });
        services.AddScoped<ITallyMasterSource, TallyXmlMasterSource>();
        services.AddScoped<ITallyPoster, TallyPoster>();
        services.AddScoped<ITallyMasterRefresher, TallyMasterRefresher>();

        services.AddSingleton<IStartupStatus, StartupStatus>();
        services.AddHostedService<DatabaseInitializationHostedService>();
        services.AddHostedService<StuckPostingRecoveryHostedService>();
        // One-shot reconcile of InvoiceSequences.NextValue against actual
        // remaining bills, so a sequence that fell out of sync under older
        // code (rename-without-rollback) self-heals on the next boot.
        services.AddHostedService<SequenceSelfHealHostedService>();
        // Warms the connection pool, EF model, and bills index plan after
        // migrations finish — eliminates the ~0.5–2 s cold-path penalty on the
        // operator's first SaveDraft. Fire-and-forget; failure is non-fatal.
        services.AddHostedService<DatabaseWarmupHostedService>();
        services.AddHealthChecks().AddCheck<PostgresReadinessHealthCheck>("postgres");

        return services;
    }
}
