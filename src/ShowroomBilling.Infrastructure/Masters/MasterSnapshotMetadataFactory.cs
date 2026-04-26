using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Masters;

internal static class MasterSnapshotMetadataFactory
{
    private static readonly TimeSpan FreshThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(6);

    internal static MasterSnapshotAcceptedResponse AcceptedResponse(TallyMasterSnapshotBatchEntity batch) =>
        new(
            MasterType: batch.MasterType,
            BatchId: batch.Id.ToString(),
            ItemCount: batch.ItemCount,
            FetchedAtUtc: batch.FetchedAtUtc,
            StoredAtUtc: DateTimeOffset.UtcNow,
            Message: $"{batch.MasterType} snapshot stored with {batch.ItemCount} item(s).");

    internal static MasterSnapshotMetadata BuildMetadata(TallyMasterSnapshotBatchEntity batch, int itemCount) =>
        new(
            MasterType: batch.MasterType,
            BatchId: batch.Id.ToString(),
            FetchedAtUtc: batch.FetchedAtUtc,
            ItemCount: itemCount,
            Freshness: ClassifyFreshness(batch.FetchedAtUtc));

    internal static MasterSnapshotMetadata EmptyMetadata(string masterType) =>
        new(masterType, null, null, 0, "missing");

    private static string ClassifyFreshness(DateTimeOffset fetchedAtUtc)
    {
        var age = DateTimeOffset.UtcNow - fetchedAtUtc;
        if (age < FreshThreshold) return "fresh";
        if (age < StaleThreshold) return "aging";
        return "stale";
    }
}
