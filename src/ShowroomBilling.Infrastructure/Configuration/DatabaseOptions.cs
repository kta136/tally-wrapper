namespace ShowroomBilling.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Provider { get; init; } = "PostgreSQL";

    public bool AutoMigrateOnStartup { get; init; }
}
