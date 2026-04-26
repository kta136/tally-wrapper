namespace ShowroomBilling.Contracts.Health;

public sealed record TallyCompanyHealthResponse(
    string Status,
    bool TallyReachable,
    string? ActiveCompanyName,
    bool ActiveCompanyOpen,
    int CompanyCount,
    DateTimeOffset CheckedAtUtc,
    string Message);
