namespace ShowroomBilling.Contracts.Settings;

public sealed record SettingsUpdateResponse(
    string SettingsSource,
    string Summary,
    IReadOnlyList<string> SavedSections,
    DateTimeOffset UpdatedAtUtc);
