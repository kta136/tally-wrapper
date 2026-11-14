namespace ShowroomBilling.Contracts.Runtime;

public sealed record RuntimeHealthResponse(
    string Status,
    bool DatabaseReachable,
    string Message,
    string? DatabaseIdentity = null,
    string? ExpectedDatabaseIdentity = null,
    bool? DatabaseIdentityMatches = null,
    // How many of this build's schema migrations the database has not had.
    // Null: not checked yet. Above zero, bills and numbering cannot work even
    // though the database is reachable. When the database does not answer,
    // this is the last count found.
    int? PendingMigrationCount = null);
