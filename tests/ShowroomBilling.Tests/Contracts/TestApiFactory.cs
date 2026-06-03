using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// Boots the real API in-process for contract tests via Microsoft.AspNetCore.Mvc.Testing.
///
/// Three things are stubbed so the boot doesn't need real infrastructure:
/// 1. The DbContext is swapped to InMemory so hosted services don't try to
///    reach Postgres. (DB-touching contract tests would need Testcontainers;
///    the current tests only validate HTTP wire shapes.)
/// 2. <c>AutoMigrateOnStartup</c> is forced false so the migration hosted
///    service no-ops cleanly.
/// 3. <see cref="ITallyMasterRefresher"/> is replaced with
///    <see cref="StubTallyMasterRefresher"/> so the refresh endpoint
///    returns deterministic results without dialing Tally.
///
/// The real <c>DeviceTokenStore</c> is left in place; tests read the
/// generated token via <see cref="GetDeviceToken"/> rather than substituting.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;

    public TestApiFactory()
        : this(new Dictionary<string, string?>())
    {
    }

    internal TestApiFactory(IReadOnlyDictionary<string, string?> extraConfiguration)
    {
        _extraConfiguration = extraConfiguration;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

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
            services.RemoveAll<DbContextOptions<ShowroomBillingDbContext>>();
            services.AddDbContext<ShowroomBillingDbContext>(options =>
                options.UseInMemoryDatabase($"contract-test-{Guid.NewGuid():N}"));

            services.RemoveAll<ITallyMasterRefresher>();
            services.AddScoped<ITallyMasterRefresher, StubTallyMasterRefresher>();
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
        Services.GetRequiredService<ShowroomBilling.Api.Security.DeviceTokenStore>().GetOrCreateToken();
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
