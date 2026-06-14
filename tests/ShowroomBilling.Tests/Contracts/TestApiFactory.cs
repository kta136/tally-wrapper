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
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
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
/// <c>DeviceTokenStore</c> is still the real implementation, but each factory
/// points it at an isolated temp file so parallel CI runs do not contend on the
/// runner user's LocalApplicationData token path.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;
    private readonly string _deviceTokenRoot = Path.Combine(
        Path.GetTempPath(),
        $"tally-wrapper-contract-{Guid.NewGuid():N}");

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
            services.RemoveAll<ITallyCompanyHealthService>();
            services.AddScoped<ITallyCompanyHealthService>(_ =>
            {
                var status = _extraConfiguration.TryGetValue("Testing:TallyCompanyHealth:Status", out var configured)
                    ? configured
                    : "healthy";
                return new StubTallyCompanyHealthService(status);
            });
            if (_extraConfiguration.TryGetValue("Testing:Bills:PushThrowsTallyPreflight", out var throwPreflight)
                && bool.TryParse(throwPreflight, out var enabled)
                && enabled)
            {
                services.RemoveAll<IBillService>();
                services.AddScoped<IBillService, TallyPreflightThrowingBillService>();
            }
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

internal sealed class TallyPreflightThrowingBillService : IBillService
{
    public Task<BillResponse> PushAsync(
        Guid billId,
        PushBillRequest request,
        CancellationToken cancellationToken = default) =>
        throw new TallyPreflightUnavailableException("Tally push blocked: Tally is unreachable.");

    public Task<BillResponse> CreateDraftAsync(CreateBillDraftRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillResponse> CreateBackdatedDraftAsync(CreateBillDraftRequest request, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillResponse> UpdateDraftAsync(Guid billId, UpdateBillDraftRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillBatchGetResponse> GetManyAsync(BillBatchGetRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillBatchPushResponse> PushSelectedAsync(PushSelectedBillsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillBatchPushResponse> PushPendingAsync(PushPendingBillsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillResponse> ReviseAsync(Guid billId, ReviseBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillResponse> VoidAsync(Guid billId, VoidBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillAuditResponse?> GetAuditAsync(Guid billId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillPostingStatusResponse?> GetPostingStatusAsync(Guid billId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillPostingStatusResponse> RetryAsync(Guid billId, RetryBillPostingRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillPostingStatusResponse> RepostAsync(Guid billId, RepostBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(Guid billId, ChangeBillNumberRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillResponse> MarkPostedAsync(Guid billId, MarkBillStateRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<BillResponse> MarkPendingAsync(Guid billId, MarkBillStateRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<DeleteBillResponse> DeleteAsync(Guid billId, DeleteBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(DeleteSelectedBillsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
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
