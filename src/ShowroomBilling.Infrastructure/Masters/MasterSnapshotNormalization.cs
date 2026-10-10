namespace ShowroomBilling.Infrastructure.Masters;

internal static class MasterSnapshotNormalization
{
    internal static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
