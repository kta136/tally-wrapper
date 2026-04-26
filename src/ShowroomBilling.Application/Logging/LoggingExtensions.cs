using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShowroomBilling.Application.Logging;

public static class LoggingExtensions
{
    public static ILoggingBuilder AddRollingFile(
        this ILoggingBuilder builder,
        IConfiguration configuration,
        string filePrefix)
    {
        var section = configuration.GetSection(RollingFileLoggerOptions.SectionName);
        builder.Services.AddOptions<RollingFileLoggerOptions>()
            .Configure(options =>
            {
                section.Bind(options);
                if (string.IsNullOrWhiteSpace(options.FilePrefix) || options.FilePrefix == "host")
                {
                    options.FilePrefix = filePrefix;
                }
                if (string.IsNullOrWhiteSpace(options.Directory))
                {
                    options.Directory = RollingFileLoggerOptions.DefaultDirectory();
                }
            });

        builder.Services.AddSingleton<ILoggerProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<RollingFileLoggerOptions>>();
            return new RollingFileLoggerProvider(opts);
        });

        return builder;
    }
}
