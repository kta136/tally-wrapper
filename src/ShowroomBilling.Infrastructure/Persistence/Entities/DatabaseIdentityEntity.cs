namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class DatabaseIdentityEntity
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
