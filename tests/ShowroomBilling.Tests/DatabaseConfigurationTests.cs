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

    [Theory]
    [InlineData("Minimum Pool Size")]
    [InlineData("MinPoolSize")]
    public void ApplyPoolDefaults_KeepsExplicitValues(string poolSizeKey)
    {
        var result = new Npgsql.NpgsqlConnectionStringBuilder(
            ShowroomBilling.Infrastructure.DependencyInjection.ApplyPoolDefaults(
                $"Host=db;Database=x;Username=u;{poolSizeKey}=0;Keepalive=0"));

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
                DatabaseConfigurationStore.LoadPostgresConnectionString("Development", out var loadError));
            Assert.Null(loadError);
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
    public async Task Cancelled_save_preserves_the_previous_connection()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            const string previous = "Host=first;Database=showroom";
            await DatabaseConfigurationStore.SavePostgresConnectionStringAsync(previous, "Production");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DatabaseConfigurationStore.SavePostgresConnectionStringAsync("Host=second;Database=showroom", "Production",
                    new CancellationToken(canceled: true)));

            Assert.Equal(previous, DatabaseConfigurationStore.LoadPostgresConnectionString("Production", out var loadError));
            Assert.Null(loadError);
            Assert.Empty(Directory.EnumerateFiles(DatabaseConfigurationStore.DirectoryPath, "*.tmp"));
        });
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
                new DatabaseConfigurationTestResponse(true, "Connection succeeded. Database identity: PROD."));
            var controller = await BuildMaintenanceControllerAsync(verifier);

            const string connection = "Host=db;Database=showroom;Username=user;Password=secret;SSL Password=ssl-secret;Timeout=19";
            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new DatabaseConfigurationRequest(connection), CancellationToken.None);

            var response = Assert.IsType<DatabaseConfigurationResponse>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(response));
            Assert.Contains("Password=***", response.MaskedConnectionString);
            Assert.True(response.RequiresApiRestart);
            var saved = DatabaseConfigurationStore.LoadPostgresConnectionString("Production", out var loadError);
            Assert.Null(loadError);
            var stored = new Npgsql.NpgsqlConnectionStringBuilder(saved);
            Assert.Equal("secret", stored.Password);
            Assert.Equal("ssl-secret", stored.SslPassword);
            Assert.Equal(19, stored.Timeout);
            Assert.Equal("PROD", verifier.LastExpectedDatabaseIdentity);
        });
    }

    [Fact]
    public async Task Maintenance_save_refuses_a_postgres_url_and_says_which_form_to_use()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var verifier = new FakeDatabaseConnectionVerifier(
                new DatabaseConfigurationTestResponse(true, "Connection succeeded. Database identity: PROD."));
            var controller = await BuildMaintenanceControllerAsync(verifier);

            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new DatabaseConfigurationRequest("postgresql://db_user:db_secret@db.example.test/showroom"),
                CancellationToken.None);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
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
                new DatabaseConfigurationRequest("Not A Connection String"),
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        });
    }

    [Fact]
    public async Task Maintenance_save_refuses_a_database_with_the_wrong_identity_and_stores_nothing()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var controller = await BuildMaintenanceControllerAsync(
                new FakeDatabaseConnectionVerifier(
                    new DatabaseConfigurationTestResponse(false, "Connection succeeded, but database identity is DEV; expected PROD.")));

            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new DatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.False(File.Exists(DatabaseConfigurationStore.ConfigPathForEnvironment("Production")));
        });
    }

    [Fact]
    public async Task Maintenance_test_reports_what_the_database_answered()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var controller = await BuildMaintenanceControllerAsync();

            var result = await controller.TestDatabaseConfigurationForMaintenance(
                new DatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<DatabaseConfigurationTestResponse>(ok.Value);
            Assert.True(response.Success);
            Assert.False(File.Exists(DatabaseConfigurationStore.ConfigPathForEnvironment("Production")));
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
                new DatabaseConfigurationRequest(connection), CancellationToken.None);
            var save = await controller.UpdateDatabaseConfigurationForMaintenance(
                new DatabaseConfigurationRequest(connection), CancellationToken.None);

            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(test).StatusCode);
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(save).StatusCode);
            Assert.False(File.Exists(DatabaseConfigurationStore.ConfigPathForEnvironment("Production")));
        });
    }

    [Fact]
    public async Task Maintenance_actions_are_refused_from_another_machine_even_with_the_token()
    {
        await WithIsolatedConfigurationStoreAsync(async () =>
        {
            var controller = await BuildMaintenanceControllerAsync(remoteAddress: System.Net.IPAddress.Parse("192.168.1.25"));

            var result = await controller.UpdateDatabaseConfigurationForMaintenance(
                new DatabaseConfigurationRequest("Host=db;Database=showroom;Username=user;Password=secret"),
                CancellationToken.None);

            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.False(File.Exists(DatabaseConfigurationStore.ConfigPathForEnvironment("Production")));
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

        var controller = new RuntimeController(
            verifier ?? new FakeDatabaseConnectionVerifier(
                new DatabaseConfigurationTestResponse(true, "Connection succeeded. Database identity: PROD.")),
            new FakeHostEnvironment("Production"),
            new MaintenanceTokenStore());
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

    private sealed class FakeDatabaseConnectionVerifier(DatabaseConfigurationTestResponse result)
        : IDatabaseConnectionVerifier
    {
        public string? LastExpectedDatabaseIdentity { get; private set; }
        public string? LastConnectionString { get; private set; }

        public Task<DatabaseConfigurationTestResponse> VerifyAsync(
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
