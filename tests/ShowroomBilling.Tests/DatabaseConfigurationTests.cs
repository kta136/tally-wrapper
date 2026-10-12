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
using ShowroomBilling.Api.Clients;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Api.Controllers;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Maintenance;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Tests.TestSupport;

namespace ShowroomBilling.Tests;

public sealed class PoolDefaultsTests
{
    [Fact]
    public void ApplyPoolDefaults_FillsMissingValues()
    {
        var result = new Npgsql.NpgsqlConnectionStringBuilder(
            ShowroomBilling.Infrastructure.DependencyInjection.ApplyPoolDefaults("Host=db;Database=x;Username=u"));

        Assert.Equal(2, result.MinPoolSize);
        Assert.Equal(30, result.KeepAlive);
    }

    [Fact]
    public void ApplyPoolDefaults_KeepsExplicitValues()
    {
        var result = new Npgsql.NpgsqlConnectionStringBuilder(
            ShowroomBilling.Infrastructure.DependencyInjection.ApplyPoolDefaults(
                "Host=db;Database=x;Username=u;Minimum Pool Size=0;Keepalive=0"));

        Assert.Equal(0, result.MinPoolSize);
        Assert.Equal(0, result.KeepAlive);
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
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
    public async Task Maintenance_save_stores_the_override_and_answers_without_the_password()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var verifier = new FakeDatabaseConnectionVerifier(
                DatabaseConnectionVerificationResult.Succeeded("Connection succeeded. Database identity: PROD.", "PROD"));
            var controller = await BuildMaintenanceControllerAsync(verifier);

            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new UpdateDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = Assert.IsType<DatabaseConfigurationResponse>(ok.Value);
            // Nothing in the answer carries the password, whatever field it might hide in.
            Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(response));
            Assert.Contains("Password=***", response.MaskedConnectionString);
            Assert.True(response.IsLocalOverridePresent);
            Assert.True(response.RequiresApiRestart);
            Assert.True(File.Exists(response.ConfigPath));
            Assert.Equal("PROD", verifier.LastExpectedDatabaseIdentity);
        });
    }

    [Fact]
    public async Task Maintenance_save_refuses_a_postgres_url_and_says_which_form_to_use()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var verifier = new FakeDatabaseConnectionVerifier(
                DatabaseConnectionVerificationResult.Succeeded("Connection succeeded. Database identity: PROD.", "PROD"));
            var controller = await BuildMaintenanceControllerAsync(verifier);

            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new UpdateDatabaseConfigurationRequest("postgresql://db_user:db_secret@db.example.test/showroom"),
                CancellationToken.None);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
            Assert.Contains("Host=", problem.Detail);
            Assert.DoesNotContain("db_secret", problem.Detail);
            Assert.Null(verifier.LastConnectionString);
        });
    }

    [Fact]
    public async Task Maintenance_save_refuses_text_that_is_not_a_connection_string()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var controller = await BuildMaintenanceControllerAsync();

            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new UpdateDatabaseConfigurationRequest("Not A Connection String"),
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result.Result);
        });
    }

    [Fact]
    public async Task Maintenance_save_refuses_a_database_with_the_wrong_identity_and_stores_nothing()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var controller = await BuildMaintenanceControllerAsync(
                new FakeDatabaseConnectionVerifier(
                    DatabaseConnectionVerificationResult.Failed(
                        "Connection succeeded, but database identity is DEV; expected PROD.",
                        "DEV")));

            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new UpdateDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.False(DatabaseConfigurationStore.ExistsForEnvironment("Production"));
        });
    }

    [Fact]
    public async Task Maintenance_test_reports_what_the_database_answered()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var controller = await BuildMaintenanceControllerAsync();

            var result = await controller.TestDatabaseConfigurationForMaintenance(
                new TestDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = Assert.IsType<DatabaseConfigurationTestResponse>(ok.Value);
            Assert.True(response.Success);
            Assert.False(DatabaseConfigurationStore.ExistsForEnvironment("Production"));
        });
    }

    [Fact]
    public async Task Maintenance_actions_need_the_maintenance_token()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var controller = await BuildMaintenanceControllerAsync(sendToken: false);
            var connection = "Host=db;Database=showroom;Username=user;Password=secret";

            var test = await controller.TestDatabaseConfigurationForMaintenance(
                new TestDatabaseConfigurationRequest(connection), CancellationToken.None);
            var save = await controller.UpdateDatabaseConfigurationForMaintenance(
                new UpdateDatabaseConfigurationRequest(connection), CancellationToken.None);

            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(test.Result).StatusCode);
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(save.Result).StatusCode);
            Assert.False(DatabaseConfigurationStore.ExistsForEnvironment("Production"));
        });
    }

    [Fact]
    public async Task Maintenance_actions_are_refused_from_another_machine_even_with_the_token()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var controller = await BuildMaintenanceControllerAsync(remoteAddress: System.Net.IPAddress.Parse("192.168.1.25"));

            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new UpdateDatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
            Assert.False(DatabaseConfigurationStore.ExistsForEnvironment("Production"));
        });
    }

    /// <summary>
    /// A Production controller as the server tray reaches it: from the server
    /// itself, with the maintenance token. Call inside an isolated store.
    /// </summary>
    private static async Task<RuntimeController> BuildMaintenanceControllerAsync(
        IDatabaseConnectionVerifier? verifier = null,
        System.Net.IPAddress? remoteAddress = null,
        bool sendToken = true)
    {
        Directory.CreateDirectory(DatabaseConfigurationStore.DirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(DatabaseConfigurationStore.DirectoryPath, MaintenanceTokenConstants.FileName),
            "maintenance-secret");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = string.Empty })
            .Build();

        // The maintenance endpoints never ask the health probe for anything.
        var unusedDataSource = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=test;Username=test;Password=test");
        var controller = new RuntimeController(
            configuration,
            verifier ?? new FakeDatabaseConnectionVerifier(
                DatabaseConnectionVerificationResult.Succeeded("Connection succeeded. Database identity: PROD.", "PROD")),
            new FakeHostEnvironment("Production"),
            new MaintenanceTokenStore(),
            new ClientPresenceRegistry(),
            new ShowroomBilling.Infrastructure.Health.DatabaseHealthProbe(
                unusedDataSource,
                new ShowroomBilling.Infrastructure.Health.PostgresReadinessHealthCheck(unusedDataSource),
                new ShowroomBilling.Infrastructure.Health.DatabaseSchemaState()),
            new ShowroomBilling.Infrastructure.Health.DatabaseHealthCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RuntimeController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Connection.RemoteIpAddress = remoteAddress ?? System.Net.IPAddress.Loopback;
        if (sendToken)
        {
            controller.HttpContext.Request.Headers[MaintenanceTokenConstants.HeaderName] = "maintenance-secret";
        }

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
