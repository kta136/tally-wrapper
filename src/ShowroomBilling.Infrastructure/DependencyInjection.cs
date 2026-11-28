using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Npgsql;
using Polly;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Infrastructure.Admin;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Configuration;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Masters;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.PrintAssets;
using ShowroomBilling.Infrastructure.Settings;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Infrastructure;

public static class DependencyInjection
{
    internal const string DefaultLocalPostgresConnectionString =
        "Host=localhost;Port=5432;Database=tally_wrapper;Username=postgres;Password=postgres";

    /// <summary>
    /// Applies the WAN-friendly defaults (keep 2 warm connections, 30 s TCP
    /// keepalive) only where the connection string does not set them, preserving
    /// explicit pool and keepalive settings supplied by the operator.
    /// </summary>
    public static string ApplyPoolDefaults(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ShouldSerialize("Minimum Pool Size"))
        {
            builder.MinPoolSize = 2;
        }
        if (!builder.ShouldSerialize("Keepalive"))
        {
            builder.KeepAlive = 30;
        }
        return builder.ConnectionString;
    }

    public static IServiceCollection AddInfrastructureLayer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        var configuredConnectionString = configuration.GetConnectionString("Postgres");
        var connectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
            ? DefaultLocalPostgresConnectionString
            : configuredConnectionString;

        var dataSource = new NpgsqlDataSourceBuilder(ApplyPoolDefaults(connectionString)).Build();
        services.AddSingleton(dataSource);
        services.AddDbContextPool<ShowroomBillingDbContext>((provider, options) =>
            // The registered data source, not the local above: a host that
            // replaces the registration (the contract tests do) moves the
            // context and the health check together.
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>(), npgsql =>
            {
                npgsql.SetPostgresVersion(18, 0);
                npgsql.CommandTimeout(15);
            }));
        services.AddScoped<IMasterSnapshotService, MasterSnapshotService>();
        services.AddScoped<NumberingService>();
        services.AddScoped<BillReadWorkflow>();
        services.AddScoped<BillLifecycleWorkflow>();
        services.AddScoped<BillPostingWorkflow>();
        services.AddScoped<BillAdminWorkflow>();
        services.AddScoped<BillAuditStore>();
        services.AddScoped<TallyComparison>();
        services.AddSingleton<SyntheticBatchOutcomes>();
        services.AddScoped<SyntheticBatchExecutor>();
        services.AddSingleton<EffectiveSettingsCache>();
        services.AddScoped<ICloudSettingsService, CloudSettingsService>();
        services.AddScoped<AdminAuthService>();
        services.AddSingleton<AdminUnlockThrottle>();
        services.AddScoped<PrintAssetService>();
        services.AddScoped<ITallyCompanyHealthService, TallyCompanyHealthService>();

        // Tally (sync, operator-initiated only — no background workers).
        // Safe XML reads retry transient transport blips. Voucher writes are
        // marked and excluded because replaying an uncertain write can duplicate it.
        services.AddHttpClient<ITallyXmlClient, TallyXmlClient>(client =>
            {
                // The client applies the operator's configured timeout to each
                // call. HttpClient's own 100-second default would silently cut
                // off any configured value above it.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddResilienceHandler("tally", pipeline =>
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = args =>
                    {
                        var request = args.Context.GetRequestMessage();
                        var isVoucherWrite = request?.Options.TryGetValue(
                            TallyXmlClient.VoucherWriteRequestKey,
                            out var markedWrite) == true && markedWrite;
                        if (isVoucherWrite)
                        {
                            return ValueTask.FromResult(false);
                        }

                        return ValueTask.FromResult(
                            args.Outcome.Exception is HttpRequestException
                            || args.Outcome.Result is { } response && (int)response.StatusCode >= 500);
                    }
                });
            });
        services.AddScoped<ITallyMasterSource, TallyXmlMasterSource>();
        services.AddScoped<ITallyPoster, TallyPoster>();
        services.AddScoped<ITallyVoucherLookup, TallyVoucherLookup>();
        services.AddScoped<ITallyMasterRefresher, TallyMasterRefresher>();

        services.AddSingleton<StartupStatus>();
        services.AddSingleton<DatabaseHealth>();
        services.AddHostedService<DatabaseInitializationHostedService>();
        services.AddHostedService<StuckPostingRecoveryHostedService>();

        return services;
    }
}
