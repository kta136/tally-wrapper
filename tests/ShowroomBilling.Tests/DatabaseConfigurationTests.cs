using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Api.Controllers;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests;

public sealed class DatabaseConfigurationTests
{
    [Fact]
    public async Task Store_WritesEnvironmentScopedEncryptedLocalOverrideJson()
    {
        var previous = Environment.GetEnvironmentVariable("SHOWROOM_BILLING_APPDATA");
        var root = Path.Combine(Path.GetTempPath(), $"showroom-db-config-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("SHOWROOM_BILLING_APPDATA", root);
        try
        {
            await DatabaseConfigurationStore.SavePostgresConnectionStringAsync(
                "Host=db;Database=showroom;Username=user;Password=secret",
                "Development");

            var json = await File.ReadAllTextAsync(DatabaseConfigurationStore.ConfigPathForEnvironment("Development"));

            Assert.Contains("\"connectionStringProtected\"", json);
            Assert.DoesNotContain("Password=secret", json);
            Assert.Equal(
                "Host=db;Database=showroom;Username=user;Password=secret",
                DatabaseConfigurationStore.LoadPostgresConnectionString("Development"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHOWROOM_BILLING_APPDATA", previous);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void UpdateDatabaseConfiguration_IsAdminProtected()
    {
        var method = typeof(RuntimeController).GetMethod(nameof(RuntimeController.UpdateDatabaseConfiguration));

        Assert.NotNull(method);
        var authorize = method!.GetCustomAttributes(inherit: true)
            .OfType<AuthorizeAttribute>()
            .SingleOrDefault();
        Assert.NotNull(authorize);
        Assert.Equal(AdminPolicy.PolicyName, authorize!.Policy);
    }

    [Fact]
    public void GetDatabaseConfiguration_MasksPassword_AndReportsRestartWhenAppliedConnectionDiffers()
    {
        var controller = BuildController(
            configured: "Host=new-db;Database=showroom;Username=user;Password=new-secret",
            applied: "Host=old-db;Database=showroom;Username=user;Password=old-secret");

        var result = controller.GetDatabaseConfiguration();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DatabaseConfigurationResponse>(ok.Value);

        Assert.Equal(string.Empty, response.ConnectionString);
        Assert.DoesNotContain("new-secret", response.MaskedConnectionString);
        Assert.Contains("Password=***", response.MaskedConnectionString);
        Assert.True(response.RequiresApiRestart);
    }

    [Fact]
    public async Task UpdateDatabaseConfiguration_RejectsInvalidConnectionString()
    {
        var controller = BuildController(
            configured: "Host=old-db;Database=showroom;Username=user;Password=old-secret",
            applied: "Host=old-db;Database=showroom;Username=user;Password=old-secret");

        var result = await controller.UpdateDatabaseConfiguration(
            new UpdateDatabaseConfigurationRequest("Not A Connection String"),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private static RuntimeController BuildController(string configured, string applied)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = configured
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks();
        using var provider = services.BuildServiceProvider();

        return new RuntimeController(
            configuration,
            new FakeCloudSettingsService(),
            new ShowroomBillingDbContext(new DbContextOptionsBuilder<ShowroomBillingDbContext>()
                .UseNpgsql("Host=localhost;Database=test;Username=test;Password=test")
                .Options),
            provider.GetRequiredService<HealthCheckService>(),
            new AppliedDatabaseConfiguration(applied),
            new FakeHostEnvironment("Development"),
            Options.Create(new ApiRuntimeOptions()));
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "ShowroomBilling.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeCloudSettingsService : ICloudSettingsService
    {
        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
            UpdateEffectiveSettingsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
            string companyName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
            UpdatePrintLayoutRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
