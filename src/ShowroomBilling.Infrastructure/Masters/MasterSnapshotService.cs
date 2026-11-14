using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Masters;

/// <summary>
/// The lists last read from Tally: companies, ledgers, stock items and voucher
/// types. Each is one row of <c>tally_masters</c> holding the whole list as
/// JSON, because a refresh replaces the whole list and nothing looks inside one.
/// </summary>
public sealed class MasterSnapshotService(ShowroomBillingDbContext dbContext) : IMasterSnapshotService
{
    private static readonly TimeSpan FreshThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions ItemsJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] AllMasterTypes =
    [
        MasterTypes.Companies,
        MasterTypes.Ledgers,
        MasterTypes.StockItemsSnapshot,
        MasterTypes.VoucherTypesSnapshot,
    ];

    public Task<MasterSnapshotMetadata> IngestCompaniesAsync(
        IReadOnlyList<CompanySnapshotItem> companies,
        CancellationToken cancellationToken = default) =>
        ReplaceAsync(
            MasterTypes.Companies,
            companies.Select(item => item with { Name = Name(item.Name) }),
            item => item.Name,
            cancellationToken);

    public Task<MasterSnapshotMetadata> IngestLedgersAsync(
        IReadOnlyList<LedgerSnapshotItem> ledgers,
        CancellationToken cancellationToken = default) =>
        ReplaceAsync(
            MasterTypes.Ledgers,
            ledgers.Select(item => new LedgerSnapshotItem(Name(item.Name))),
            item => item.Name,
            cancellationToken);

    public Task<MasterSnapshotMetadata> IngestStockItemsAsync(
        IReadOnlyList<StockItemSnapshotItem> stockItems,
        CancellationToken cancellationToken = default) =>
        ReplaceAsync(
            MasterTypes.StockItemsSnapshot,
            stockItems.Select(item => new StockItemSnapshotItem(Name(item.Name), Optional(item.BaseUnit), Optional(item.HsnCode))),
            item => item.Name,
            cancellationToken);

    public Task<MasterSnapshotMetadata> IngestVoucherTypesAsync(
        IReadOnlyList<VoucherTypeSnapshotItem> voucherTypes,
        CancellationToken cancellationToken = default) =>
        ReplaceAsync(
            MasterTypes.VoucherTypesSnapshot,
            voucherTypes.Select(item => new VoucherTypeSnapshotItem(Name(item.Name))),
            item => item.Name,
            cancellationToken);

    public async Task<CompanySnapshotResponse> GetCompaniesAsync(CancellationToken cancellationToken = default)
    {
        var (metadata, items) = await ReadAsync<CompanySnapshotItem>(MasterTypes.Companies, cancellationToken);
        return new CompanySnapshotResponse(metadata, items);
    }

    public async Task<LedgerSnapshotResponse> GetLedgersAsync(CancellationToken cancellationToken = default)
    {
        var (metadata, items) = await ReadAsync<LedgerSnapshotItem>(MasterTypes.Ledgers, cancellationToken);
        return new LedgerSnapshotResponse(metadata, items);
    }

    public async Task<StockItemSnapshotResponse> GetStockItemsAsync(CancellationToken cancellationToken = default)
    {
        var (metadata, items) = await ReadAsync<StockItemSnapshotItem>(MasterTypes.StockItemsSnapshot, cancellationToken);
        return new StockItemSnapshotResponse(metadata, items);
    }

    public async Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(CancellationToken cancellationToken = default)
    {
        var (metadata, items) = await ReadAsync<VoucherTypeSnapshotItem>(MasterTypes.VoucherTypesSnapshot, cancellationToken);
        return new VoucherTypeSnapshotResponse(metadata, items);
    }

    public async Task<MasterFreshnessSummaryResponse> GetFreshnessSummaryAsync(CancellationToken cancellationToken = default)
    {
        // The lists themselves are not read: the row carries its own count.
        var stored = await dbContext.TallyMasters
            .AsNoTracking()
            .Select(x => new { x.MasterType, x.FetchedAtUtc, x.ItemCount })
            .ToDictionaryAsync(x => x.MasterType, cancellationToken);

        var metadatas = AllMasterTypes
            .Select(type => stored.TryGetValue(type, out var row)
                ? Stored(type, row.FetchedAtUtc, row.ItemCount)
                : Missing(type))
            .ToArray();

        var overall = metadatas.All(m => m.Freshness == MasterFreshness.Fresh) ? MasterFreshness.Fresh
            : metadatas.Any(m => m.Freshness == MasterFreshness.Missing) ? MasterFreshness.Missing
            : metadatas.Any(m => m.Freshness == MasterFreshness.Stale) ? MasterFreshness.Stale
            : MasterFreshness.Aging;

        return new MasterFreshnessSummaryResponse(overall, metadatas);
    }

    /// <summary>
    /// Stores <paramref name="items"/> as the list of <paramref name="masterType"/>, in place of
    /// whatever was there, and records that in the audit trail. An upsert, so two refreshes of a
    /// master that has never been stored cannot trip over each other.
    /// </summary>
    private async Task<MasterSnapshotMetadata> ReplaceAsync<T>(
        string masterType,
        IEnumerable<T> items,
        Func<T, string> name,
        CancellationToken cancellationToken)
    {
        var list = items
            .OrderBy(name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name, StringComparer.Ordinal)
            .ToArray();
        var json = JsonSerializer.Serialize(list, ItemsJsonOptions);
        var now = DateTimeOffset.UtcNow;
        // To the microsecond, which is what PostgreSQL keeps, so a later read returns this same time.
        var fetchedAt = now.AddTicks(-(now.Ticks % 10));

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO public.tally_masters (""MasterType"", ""FetchedAtUtc"", ""ItemCount"", ""ItemsJson"")
VALUES ({masterType}, {fetchedAt}, {list.Length}, {json}::jsonb)
ON CONFLICT (""MasterType"") DO UPDATE
SET ""FetchedAtUtc"" = EXCLUDED.""FetchedAtUtc"", ""ItemCount"" = EXCLUDED.""ItemCount"", ""ItemsJson"" = EXCLUDED.""ItemsJson""",
                cancellationToken);

            dbContext.AuditEvents.Add(AuditRow.Create(
                "masters",
                masterType,
                $"masters.{masterType}.ingested",
                new AuditActor("api", "api"),
                fetchedAt,
                new { itemCount = list.Length, masterType }));
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // Leave nothing half-added behind for whoever uses this context next.
            dbContext.ChangeTracker.Clear();
            throw;
        }

        return Stored(masterType, fetchedAt, list.Length);
    }

    private async Task<(MasterSnapshotMetadata Metadata, IReadOnlyList<T> Items)> ReadAsync<T>(
        string masterType,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.TallyMasters
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.MasterType == masterType, cancellationToken);
        if (row is null)
        {
            return (Missing(masterType), []);
        }

        var items = JsonSerializer.Deserialize<T[]>(row.ItemsJson, ItemsJsonOptions) ?? [];
        return (Stored(masterType, row.FetchedAtUtc, items.Length), items);
    }

    private static MasterSnapshotMetadata Stored(string masterType, DateTimeOffset fetchedAtUtc, int itemCount) =>
        new(masterType, fetchedAtUtc, itemCount, ClassifyFreshness(fetchedAtUtc));

    private static MasterSnapshotMetadata Missing(string masterType) =>
        new(masterType, null, 0, MasterFreshness.Missing);

    private static string ClassifyFreshness(DateTimeOffset fetchedAtUtc)
    {
        var age = DateTimeOffset.UtcNow - fetchedAtUtc;
        if (age < FreshThreshold) return MasterFreshness.Fresh;
        if (age < StaleThreshold) return MasterFreshness.Aging;
        return MasterFreshness.Stale;
    }

    private static string Name(string? value) => (value ?? string.Empty).Trim();

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
