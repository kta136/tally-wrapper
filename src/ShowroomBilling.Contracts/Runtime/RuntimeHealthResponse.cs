namespace ShowroomBilling.Contracts.Runtime;

public sealed record RuntimeHealthResponse(
    string Status,
    bool ApiAvailable,
    bool DatabaseConfigured,
    bool DatabaseReachable,
    bool SettingsLoadedFromApi,
    string Message,
    string? DatabaseIdentity = null,
    string? ExpectedDatabaseIdentity = null,
    bool? DatabaseIdentityMatches = null,
    int ActiveClientCount = 0,
    // How many of this build's schema migrations the database has not had.
    // Null: not checked yet. Above zero, bills and numbering cannot work even
    // though the database is reachable. When the database does not answer,
    // this is the last count found.
    int? PendingMigrationCount = null);
