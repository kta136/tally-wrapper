using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// Boots the real API in-process for contract tests via Microsoft.AspNetCore.Mvc.Testing.
///
/// Three things are set up so the boot needs nothing outside the test run:
/// 1. The API's <see cref="NpgsqlDataSource"/> is replaced with one for a
///    database of this factory's own on the test PostgreSQL server (see
///    <see cref="PostgresFixture"/>). The context and the readiness check both
///    use that registration, so nothing in the API can reach another database,
///    whatever connection string the machine's configuration holds.
/// 2. <c>AutoMigrateOnStartup</c> is forced false: the database is created
///    already migrated.
/// 3. <see cref="ITallyMasterRefresher"/> is replaced with
///    <see cref="StubTallyMasterRefresher"/> so the refresh endpoint
///    returns deterministic results without dialing Tally.
///
/// <c>DeviceTokenStore</c> is still the real implementation, but each factory
/// points it at an isolated temp file so parallel CI runs do not contend on the
/// runner user's LocalApplicationData token path.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;
    private readonly PostgresFixture _postgres;
    private PostgresFixture.PostgresTestDatabase? _database;
    private readonly string _deviceTokenRoot = Path.Combine(
        Path.GetTempPath(),
        $"tally-wrapper-contract-{Guid.NewGuid():N}");

    public TestApiFactory(PostgresFixture postgres)
        : this(postgres, new Dictionary<string, string?>())
    {
    }

    internal TestApiFactory(PostgresFixture postgres, IReadOnlyDictionary<string, string?> extraConfiguration)
    {
        _postgres = postgres;
        _extraConfiguration = extraConfiguration;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var database = _database ??= _postgres.CreateDatabase();

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:AutoMigrateOnStartup"] = "false",
                ["ConnectionStrings:Postgres"] = "Host=test-not-used;Database=test;Username=test;Password=test"
            });
            config.AddInMemoryCollection(_extraConfiguration);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(_ => NpgsqlDataSource.Create(database.ConnectionString));

            services.RemoveAll<ITallyMasterRefresher>();
            services.AddScoped<ITallyMasterRefresher, StubTallyMasterRefresher>();
            services.RemoveAll<ITallyCompanyHealthService>();
            services.AddScoped<ITallyCompanyHealthService>(_ =>
            {
                var status = _extraConfiguration.TryGetValue("Testing:TallyCompanyHealth:Status", out var configured)
                    ? configured
                    : "healthy";
                return new StubTallyCompanyHealthService(status);
            });
            services.RemoveAll<DeviceTokenStore>();
            services.AddSingleton(new DeviceTokenStore(Path.Combine(_deviceTokenRoot, "device_token.txt")));
            services.AddSingleton<IStartupFilter, LoopbackRemoteAddressStartupFilter>();

            services.PostConfigure<DeviceAuthOptions>(options =>
            {
                if (_extraConfiguration.TryGetValue("DeviceAuth:Mode", out var mode)
                    && !string.IsNullOrWhiteSpace(mode))
                {
                    options.Mode = mode;
                }

                var trustedNetworks = _extraConfiguration
                    .Where(pair => pair.Key.StartsWith("DeviceAuth:TrustedNetworks:", StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray();

                if (trustedNetworks.Length > 0)
                {
                    options.TrustedNetworks = trustedNetworks;
                }
            });
        });
    }

    public string GetDeviceToken() =>
        Services.GetRequiredService<DeviceTokenStore>().GetOrCreateToken();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        // After the host: its data source is closed by then.
        _database?.Dispose();
        _database = null;

        try
        {
            if (Directory.Exists(_deviceTokenRoot))
            {
                Directory.Delete(_deviceTokenRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort test temp cleanup.
        }
    }
}

internal sealed class LoopbackRemoteAddressStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware();
            });

            next(app);
    };
}

internal sealed class StubTallyCompanyHealthService(string? status) : ITallyCompanyHealthService
{
    public Task<TallyCompanyHealthResponse> CheckAsync(CancellationToken cancellationToken = default)
    {
        var healthy = string.Equals(status, "healthy", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new TallyCompanyHealthResponse(
            Status: healthy ? "healthy" : "unhealthy",
            TallyReachable: healthy,
            ActiveCompanyName: "Contract Test Company",
            ActiveCompanyOpen: healthy,
            CompanyCount: healthy ? 1 : 0,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Message: healthy
                ? "Tally OK - active company 'Contract Test Company' is open."
                : "Tally is unreachable. Check that Tally is running and the connection settings are correct."));
    }
}

internal sealed class StubTallyMasterRefresher : ITallyMasterRefresher
{
    public Task<TallyMasterRefreshResult> RefreshCompaniesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TallyMasterRefreshResult("companies", true, 5, "batch-companies", null));

    public Task<TallyMasterRefreshResult> RefreshLedgersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TallyMasterRefreshResult("ledgers", true, 12, "batch-ledgers", null));

    public Task<TallyMasterRefreshResult> RefreshStockItemsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TallyMasterRefreshResult("stock-items", true, 30, "batch-stock", null));

    public Task<TallyMasterRefreshResult> RefreshVoucherTypesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TallyMasterRefreshResult("voucher-types", true, 7, "batch-voucher", null));

    public async Task<IReadOnlyList<TallyMasterRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default) =>
        new[]
        {
            await RefreshCompaniesAsync(cancellationToken),
            await RefreshLedgersAsync(cancellationToken),
            await RefreshStockItemsAsync(cancellationToken),
            await RefreshVoucherTypesAsync(cancellationToken),
        };
}
