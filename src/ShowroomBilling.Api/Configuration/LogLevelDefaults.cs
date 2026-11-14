using Microsoft.Extensions.Configuration.Memory;

namespace ShowroomBilling.Api.Configuration;

/// <summary>
/// The log levels the API starts from. They are set here and not in
/// <c>appsettings.json</c> because the installed server has no
/// <c>appsettings.json</c>: it is published as one file without it. While the
/// levels lived only in that file, the server ran at the framework's own
/// default and wrote ASP.NET Core's seven or so Information lines for every
/// request, health polls included.
/// </summary>
public static class LogLevelDefaults
{
    private static readonly Dictionary<string, string?> Levels = new()
    {
        ["Logging:LogLevel:Default"] = "Information",
        // Request start and finish, routing and action lines: one bill save
        // or health poll is otherwise several lines of framework chatter.
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
    };

    /// <summary>
    /// Adds the levels beneath every other source, so that a settings file or
    /// an environment variable (<c>Logging__LogLevel__Default=Debug</c>) still
    /// changes them.
    /// </summary>
    public static void AddTo(IConfigurationBuilder configuration) =>
        configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = Levels });
}
