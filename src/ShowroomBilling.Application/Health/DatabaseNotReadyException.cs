namespace ShowroomBilling.Application.Health;

/// <summary>
/// The database did not become ready during this API start. Raised to those
/// waiting on <c>StartupStatus.WaitForDatabaseReadyAsync</c>; the
/// message is the failure the database initialiser recorded.
/// </summary>
public sealed class DatabaseNotReadyException(string message) : InvalidOperationException(message);
