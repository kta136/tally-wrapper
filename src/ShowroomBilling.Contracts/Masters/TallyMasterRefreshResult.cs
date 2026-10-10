namespace ShowroomBilling.Contracts.Masters;

/// <summary>
/// What one master refresh did. <c>POST /api/masters/refresh</c> returns one of
/// these per master fetched, and the Desktop reads the same shape.
/// </summary>
public sealed record TallyMasterRefreshResult(
    string MasterType,
    bool Succeeded,
    int ItemCount,
    string? BatchId,
    string? ErrorMessage);
