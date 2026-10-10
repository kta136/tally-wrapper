using Microsoft.AspNetCore.Authentication;
using ShowroomBilling.Api.Clients;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Api.Middleware;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Application.Logging;
using ShowroomBilling.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 5 * 1024 * 1024);
var windowsServiceName = Environment.GetEnvironmentVariable("SHOWROOM_BILLING_SERVICE_NAME");
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = string.IsNullOrWhiteSpace(windowsServiceName)
        ? "ShowroomBilling.Api"
        : windowsServiceName;
});

// Outside Development, bind only to loopback unless told otherwise: an
// embedded API serves the Desktop on the same PC and nobody else. The shared
// server install is the one case that listens on the network, and it says so
// by setting `ASPNETCORE_URLS` (see ServerInstaller); device auth then limits
// who may call it.
if (!builder.Environment.IsDevelopment()
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5107");
}

// Precedence (last wins): env var > local DPAPI-encrypted override > appsettings.
string? databaseOverrideLoadError = null;
var overrideConnectionString =
    DatabaseConfigurationStore.GetEnvironmentConnectionString() is { Length: > 0 } envCs ? envCs
    : DatabaseConfigurationStore.LoadPostgresConnectionString(builder.Environment.EnvironmentName, out databaseOverrideLoadError);

if (!string.IsNullOrWhiteSpace(overrideConnectionString))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Postgres"] = overrideConnectionString
    });
}
DatabaseConnectionStringConfiguration.NormalizePostgresConnectionString(builder.Configuration);

builder.Logging.AddRollingFile(builder.Configuration, filePrefix: "api");

builder.Services.AddSingleton(new AppliedDatabaseConfiguration(
    DatabaseConnectionStringConfiguration.GetPostgresConnectionString(builder.Configuration)));
builder.Services.Configure<ApiRuntimeOptions>(builder.Configuration.GetSection(ApiRuntimeOptions.SectionName));
builder.Services.Configure<DeviceAuthOptions>(builder.Configuration.GetSection(DeviceAuthOptions.SectionName));
builder.Services.AddSingleton<IDatabaseConnectionVerifier, DatabaseConnectionVerifier>();

builder.Services.AddInfrastructureLayer(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditActorContext, HttpAuditActorContext>();

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

if (databaseOverrideLoadError is not null)
{
    app.Logger.LogWarning(
        "The saved database connection override at {Path} was ignored because {Reason}. The API is using the fallback connection string.",
        DatabaseConfigurationStore.ConfigPathForEnvironment(app.Environment.EnvironmentName),
        databaseOverrideLoadError);
}

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
    service = "Tally Wrapper API",
    environment = app.Environment.EnvironmentName,
    // Swagger is served in Development only.
    docs = app.Environment.IsDevelopment() ? "/swagger" : null,
}));

app.Run();

// Exposed so xUnit + Microsoft.AspNetCore.Mvc.Testing can boot the API
// in-process via `WebApplicationFactory<Program>` for contract tests.
public partial class Program;
