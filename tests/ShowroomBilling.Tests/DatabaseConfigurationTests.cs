using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
using ShowroomBilling.Contracts.Maintenance;
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
    public void Store_UsesAppDataOverrideAsExactRoot()
    {
        WithIsolatedConfigurationStore(() =>
        {
            Directory.CreateDirectory(DatabaseConfigurationStore.DirectoryPath);
            var tokenPath = Path.Combine(DatabaseConfigurationStore.DirectoryPath, MaintenanceTokenConstants.FileName);
            File.WriteAllText(tokenPath, "maintenance-secret");

            Assert.Equal(
                Environment.GetEnvironmentVariable("SHOWROOM_BILLING_APPDATA"),
                DatabaseConfigurationStore.DirectoryPath);
            Assert.True(new MaintenanceTokenStore().Validate("maintenance-secret"));
        });
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
        WithIsolatedConfigurationStore(() =>
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
            Assert.True(response.CanBootstrapWithoutAdmin);
        });
    }

    [Fact]
    public void ServerMode_GetDatabaseConfiguration_RejectsNonLoopbackRequest()
    {
        var controller = BuildController(
            configured: "Host=db;Database=showroom;Username=user;Password=secret",
            applied: "Host=db;Database=showroom;Username=user;Password=secret",
            deviceAuthOptions: new DeviceAuthOptions { Mode = "TrustedLan", TrustedNetworks = ["192.168.0.0/16"] },
            remoteAddress: System.Net.IPAddress.Parse("192.168.1.25"));

        var result = controller.GetDatabaseConfiguration();

        var denied = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);
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

    [Fact]
    public async Task BootstrapDatabaseConfiguration_AcceptsPsqlWrappedPostgresUri()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var verifier = new FakeDatabaseConnectionVerifier(
                DatabaseConnectionVerificationResult.Succeeded("Connection succeeded. Database identity: PROD.", "PROD"));
            var controller = BuildController(
                configured: string.Empty,
                applied: string.Empty,
                environmentName: "Production",
                verifier: verifier);

            var result = await controller.BootstrapDatabaseConfiguration(
                new UpdateDatabaseConfigurationRequest(
                    "psql 'postgresql://db_user:db_secret@example.neon.tech/showroom?sslmode=require&channel_binding=require'"),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = Assert.IsType<DatabaseConfigurationResponse>(ok.Value);
            Assert.Contains("Host=example.neon.tech", response.MaskedConnectionString);
            Assert.Contains("Username=db_user", verifier.LastConnectionString);
            Assert.Contains("Database=showroom", verifier.LastConnectionString);
            Assert.Contains("SSL Mode=Require", verifier.LastConnectionString);
            Assert.Contains("Channel Binding=Require", verifier.LastConnectionString);
            Assert.DoesNotContain("psql", verifier.LastConnectionString, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task BootstrapDatabaseConfiguration_SavesLocalOverrideWithoutAdmin_WhenBootstrapIsOpen()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var verifier = new FakeDatabaseConnectionVerifier(
                DatabaseConnectionVerificationResult.Succeeded("Connection succeeded. Database identity: PROD.", "PROD"));
            var controller = BuildController(
                configured: string.Empty,
                applied: string.Empty,
                environmentName: "Production",
                verifier: verifier);

            var result = await controller.BootstrapDatabaseConfiguration(
                new UpdateDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = Assert.IsType<DatabaseConfigurationResponse>(ok.Value);
            Assert.Equal(string.Empty, response.ConnectionString);
            Assert.DoesNotContain("secret", response.MaskedConnectionString);
            Assert.Contains("Password=***", response.MaskedConnectionString);
            Assert.True(response.IsLocalOverridePresent);
            Assert.False(response.CanBootstrapWithoutAdmin);
            Assert.True(File.Exists(response.ConfigPath));
            Assert.Equal("PROD", verifier.LastExpectedDatabaseIdentity);
        });
    }

    [Fact]
    public async Task BootstrapDatabaseConfiguration_RejectsWhenLocalOverrideExists()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            Directory.CreateDirectory(DatabaseConfigurationStore.DirectoryPath);
            await File.WriteAllTextAsync(
                DatabaseConfigurationStore.ConfigPathForEnvironment("Production"),
                "{}");
            var controller = BuildController(
                configured: string.Empty,
                applied: string.Empty,
                environmentName: "Production");

            var result = await controller.BootstrapDatabaseConfiguration(
                new UpdateDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            Assert.IsType<ConflictObjectResult>(result.Result);
        });
    }

    [Fact]
    public async Task BootstrapDatabaseConfiguration_RejectsWhenEnvironmentOverrideExists()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var previousPostgres = Environment.GetEnvironmentVariable(DatabaseConfigurationStore.PostgresConnectionStringEnvironmentVariable);
            Environment.SetEnvironmentVariable(
                DatabaseConfigurationStore.PostgresConnectionStringEnvironmentVariable,
                "Host=env-db;Database=showroom;Username=user;Password=secret");
            try
            {
                var controller = BuildController(
                    configured: string.Empty,
                    applied: string.Empty,
                    environmentName: "Production");

                var result = await controller.BootstrapDatabaseConfiguration(
                    new UpdateDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                    CancellationToken.None);

                Assert.IsType<ConflictObjectResult>(result.Result);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    DatabaseConfigurationStore.PostgresConnectionStringEnvironmentVariable,
                    previousPostgres);
            }
        });
    }

    [Fact]
    public async Task BootstrapDatabaseConfiguration_RejectsWhenIdentityDoesNotMatch()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var controller = BuildController(
                configured: string.Empty,
                applied: string.Empty,
                environmentName: "Production",
                verifier: new FakeDatabaseConnectionVerifier(
                    DatabaseConnectionVerificationResult.Failed(
                        "Connection succeeded, but database identity is DEV; expected PROD.",
                        "DEV")));

            var result = await controller.BootstrapDatabaseConfiguration(
                new UpdateDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result.Result);
        });
    }

    [Fact]
    public async Task ServerMode_DatabaseTest_RejectsNonLoopbackRequest()
    {
        var controller = BuildController(
            configured: string.Empty,
            applied: string.Empty,
            deviceAuthOptions: new DeviceAuthOptions { Mode = "TrustedLan", TrustedNetworks = ["192.168.0.0/16"] },
            remoteAddress: System.Net.IPAddress.Parse("192.168.1.25"));

        var result = await controller.TestDatabaseConfiguration(
            new TestDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
            CancellationToken.None);

        var denied = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task MaintenanceDatabaseConfiguration_UsesTokenWithoutAdminAuth()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            Directory.CreateDirectory(DatabaseConfigurationStore.DirectoryPath);
            await File.WriteAllTextAsync(
                Path.Combine(DatabaseConfigurationStore.DirectoryPath, MaintenanceTokenConstants.FileName),
                "maintenance-secret");
            var controller = BuildController(
                configured: string.Empty,
                applied: string.Empty,
                environmentName: "Production",
                verifier: new FakeDatabaseConnectionVerifier(
                    DatabaseConnectionVerificationResult.Succeeded("Connection succeeded. Database identity: PROD.", "PROD")));
            controller.HttpContext.Request.Headers[MaintenanceTokenConstants.HeaderName] = "maintenance-secret";

            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new UpdateDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = Assert.IsType<DatabaseConfigurationResponse>(ok.Value);
            Assert.True(response.IsLocalOverridePresent);
        });
    }

    private static RuntimeController BuildController(
        string configured,
        string applied,
        string environmentName = "Development",
        IDatabaseConnectionVerifier? verifier = null,
        DeviceAuthOptions? deviceAuthOptions = null,
        System.Net.IPAddress? remoteAddress = null)
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

        var controller = new RuntimeController(
            configuration,
            new FakeCloudSettingsService(),
            new ShowroomBillingDbContext(new DbContextOptionsBuilder<ShowroomBillingDbContext>()
                .UseNpgsql("Host=localhost;Database=test;Username=test;Password=test")
                .Options),
            provider.GetRequiredService<HealthCheckService>(),
            verifier ?? new FakeDatabaseConnectionVerifier(
                DatabaseConnectionVerificationResult.Succeeded("Connection succeeded. Database identity: DEV.", "DEV")),
            new AppliedDatabaseConfiguration(applied),
            new FakeHostEnvironment(environmentName),
            Options.Create(new ApiRuntimeOptions()),
            Options.Create(deviceAuthOptions ?? new DeviceAuthOptions()),
            new MaintenanceTokenStore());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Connection.RemoteIpAddress = remoteAddress ?? System.Net.IPAddress.Loopback;
        return controller;
    }

    private static void WithIsolatedConfigurationStore(Action action)
    {
        var previous = Environment.GetEnvironmentVariable("SHOWROOM_BILLING_APPDATA");
        var root = Path.Combine(Path.GetTempPath(), $"showroom-db-config-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("SHOWROOM_BILLING_APPDATA", root);
        try
        {
            action();
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

    private static async Task WithIsolatedConfigurationStoreAsync(Func<Task> action)
    {
        var previous = Environment.GetEnvironmentVariable("SHOWROOM_BILLING_APPDATA");
        var root = Path.Combine(Path.GetTempPath(), $"showroom-db-config-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("SHOWROOM_BILLING_APPDATA", root);
        try
        {
            await action();
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

    private sealed class FakeDatabaseConnectionVerifier(DatabaseConnectionVerificationResult result)
        : IDatabaseConnectionVerifier
    {
        public string? LastExpectedDatabaseIdentity { get; private set; }
        public string? LastConnectionString { get; private set; }

        public Task<DatabaseConnectionVerificationResult> VerifyAsync(
            string connectionString,
            string expectedDatabaseIdentity,
            CancellationToken cancellationToken = default)
        {
            LastConnectionString = connectionString;
            LastExpectedDatabaseIdentity = expectedDatabaseIdentity;
            return Task.FromResult(result);
        }
    }
}
