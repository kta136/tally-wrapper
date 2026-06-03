using Microsoft.AspNetCore.Authentication;
using QuestPDF.Infrastructure;
using ShowroomBilling.Api.Clients;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Api.Middleware;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Logging;
using ShowroomBilling.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
var windowsServiceName = Environment.GetEnvironmentVariable("SHOWROOM_BILLING_SERVICE_NAME");
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = string.IsNullOrWhiteSpace(windowsServiceName)
        ? "ShowroomBilling.Api"
        : windowsServiceName;
});

// Outside Development, bind only to loopback. The Desktop dials 127.0.0.1
// directly; there is no scenario where we want the API listening on a
// network-accessible address. `ASPNETCORE_URLS` still overrides this if
// someone explicitly wants to run on a different port.
if (!builder.Environment.IsDevelopment()
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5107");
}

// Precedence (last wins): env var > local DPAPI-encrypted override > appsettings.
var overrideConnectionString =
    DatabaseConfigurationStore.GetEnvironmentConnectionString() is { Length: > 0 } envCs ? envCs
    : DatabaseConfigurationStore.LoadPostgresConnectionString(builder.Environment.EnvironmentName);

if (!string.IsNullOrWhiteSpace(overrideConnectionString))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Postgres"] = overrideConnectionString
    });
}

builder.Logging.AddRollingFile(builder.Configuration, filePrefix: "api");

builder.Services.AddSingleton(new AppliedDatabaseConfiguration(
    builder.Configuration.GetConnectionString("Postgres") ?? string.Empty));
builder.Services.Configure<ApiRuntimeOptions>(builder.Configuration.GetSection(ApiRuntimeOptions.SectionName));
builder.Services.Configure<DeviceAuthOptions>(builder.Configuration.GetSection(DeviceAuthOptions.SectionName));
builder.Services.AddSingleton<IDatabaseConnectionVerifier, DatabaseConnectionVerifier>();

builder.Services.AddApplicationLayer();
builder.Services.AddInfrastructureLayer(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

builder.Services.AddSingleton<DeviceTokenStore>();
builder.Services.AddSingleton<MaintenanceTokenStore>();
builder.Services.AddSingleton<ClientPresenceRegistry>();
builder.Services
    .AddAuthentication(AdminAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, AdminAuthenticationHandler>(
        AdminAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(
        DeviceAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AdminPolicy.PolicyName, policy => policy
        .AddAuthenticationSchemes(AdminAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
    options.AddPolicy(DevicePolicy.PolicyName, policy => policy
        .AddAuthenticationSchemes(DeviceAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
});

var app = builder.Build();

// Ensure the device token file exists on disk before the Desktop tries to
// read it. First-run idempotent; no-op on subsequent boots.
var deviceAuth = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceAuthOptions>>().Value;
if (!deviceAuth.IsTrustedLan)
{
    app.Services.GetRequiredService<DeviceTokenStore>().GetOrCreateToken();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
// No UseHttpsRedirection: Desktop dials the configured loopback API URL
// (prod defaults to 5107; dev uses 5108 to avoid attaching to prod).
// The dev `https` launch profile is intentional for Swagger browsing only.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/", () => Results.Ok(new
{
    service = "Showroom Billing V2 API",
    mode = "foundation",
    docs = "/swagger",
}));

app.Run();

// Exposed so xUnit + Microsoft.AspNetCore.Mvc.Testing can boot the API
// in-process via `WebApplicationFactory<Program>` for contract tests.
public partial class Program;
