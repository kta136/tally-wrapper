namespace ShowroomBilling.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public bool AutoMigrateOnStartup { get; init; }
}
