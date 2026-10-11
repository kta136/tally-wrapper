using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Masters;

public sealed class MasterSnapshotService(ShowroomBillingDbContext dbContext) : IMasterSnapshotService
{
    public const string MasterTypeCompanies = MasterTypes.Companies;
    public const string MasterTypeLedgers = MasterTypes.Ledgers;
    public const string MasterTypeStockItems = MasterTypes.StockItemsSnapshot;
    public const string MasterTypeVoucherTypes = MasterTypes.VoucherTypesSnapshot;

    private static readonly string[] AllMasterTypes =
    [
        MasterTypeCompanies,
        MasterTypeLedgers,
        MasterTypeStockItems,
        MasterTypeVoucherTypes
    ];

    public async Task<MasterSnapshotAcceptedResponse> IngestCompaniesAsync(
        PushCompanySnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(request.ShowroomCode);

        var showroomId = Showrooms.IdFor(request.ShowroomCode);
        var items = request.Companies ?? [];

        return await ReplaceSnapshotAsync(
            showroomId,
            MasterTypeCompanies,
            request.FetchedAtUtc,
            items.Count,
            batch => AddSnapshotRows(items.Select(item => new TallyCompanySnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                BatchId = batch.Id,
                ShowroomId = showroomId,
                Name = (item.Name ?? string.Empty).Trim(),
                IsActive = item.IsActive
            })),
            cancellationToken);
    }

    public async Task<MasterSnapshotAcceptedResponse> IngestLedgersAsync(
        PushLedgerSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(request.ShowroomCode);

        var showroomId = Showrooms.IdFor(request.ShowroomCode);
        var items = request.Ledgers ?? [];

        return await ReplaceSnapshotAsync(
            showroomId,
            MasterTypeLedgers,
            request.FetchedAtUtc,
            items.Count,
            batch => AddSnapshotRows(items.Select(item => new TallyLedgerSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                BatchId = batch.Id,
                ShowroomId = showroomId,
                Name = (item.Name ?? string.Empty).Trim(),
                Parent = MasterSnapshotNormalization.Normalize(item.Parent),
                PrimaryGroup = MasterSnapshotNormalization.Normalize(item.PrimaryGroup),
                IsRevenue = item.IsRevenue,
                Gstin = MasterSnapshotNormalization.Normalize(item.Gstin)
            })),
            cancellationToken);
    }

    public async Task<MasterSnapshotAcceptedResponse> IngestStockItemsAsync(
        PushStockItemSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(request.ShowroomCode);

        var showroomId = Showrooms.IdFor(request.ShowroomCode);
        var items = request.StockItems ?? [];

        return await ReplaceSnapshotAsync(
            showroomId,
            MasterTypeStockItems,
            request.FetchedAtUtc,
            items.Count,
            batch => AddSnapshotRows(items.Select(item => new TallyStockItemSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                BatchId = batch.Id,
                ShowroomId = showroomId,
                Name = (item.Name ?? string.Empty).Trim(),
                Alias = MasterSnapshotNormalization.Normalize(item.Alias),
                BaseUnit = MasterSnapshotNormalization.Normalize(item.BaseUnit),
                HsnCode = MasterSnapshotNormalization.Normalize(item.HsnCode),
                Karat = MasterSnapshotNormalization.Normalize(item.Karat)
            })),
            cancellationToken);
    }

    public async Task<MasterSnapshotAcceptedResponse> IngestVoucherTypesAsync(
        PushVoucherTypeSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(request.ShowroomCode);

        var showroomId = Showrooms.IdFor(request.ShowroomCode);
        var items = request.VoucherTypes ?? [];

        return await ReplaceSnapshotAsync(
            showroomId,
            MasterTypeVoucherTypes,
            request.FetchedAtUtc,
            items.Count,
            batch => AddSnapshotRows(items.Select(item => new TallyVoucherTypeSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                BatchId = batch.Id,
                ShowroomId = showroomId,
                Name = (item.Name ?? string.Empty).Trim(),
                ParentType = MasterSnapshotNormalization.Normalize(item.ParentType),
                IsDeemedPositive = item.IsDeemedPositive
            })),
            cancellationToken);
    }

    public async Task<CompanySnapshotResponse> GetCompaniesAsync(CancellationToken cancellationToken = default)
    {
        var batch = await GetActiveBatchAsync(MasterTypeCompanies, cancellationToken);
        if (batch is null)
        {
            return new CompanySnapshotResponse(MasterSnapshotMetadataFactory.EmptyMetadata(MasterTypeCompanies), []);
        }

        var items = await dbContext.TallyCompanySnapshots
            .AsNoTracking()
            .Where(x => x.BatchId == batch.Id)
            .OrderBy(x => x.Name)
            .Select(row => new CompanySnapshotItem(row.Name, row.IsActive))
            .ToArrayAsync(cancellationToken);

        return new CompanySnapshotResponse(MasterSnapshotMetadataFactory.BuildMetadata(batch, batch.ItemCount), items);
    }

    public async Task<LedgerSnapshotResponse> GetLedgersAsync(CancellationToken cancellationToken = default)
    {
        var batch = await GetActiveBatchAsync(MasterTypeLedgers, cancellationToken);
        if (batch is null)
        {
            return new LedgerSnapshotResponse(MasterSnapshotMetadataFactory.EmptyMetadata(MasterTypeLedgers), []);
        }

        var items = await dbContext.TallyLedgerSnapshots
            .AsNoTracking()
            .Where(x => x.BatchId == batch.Id)
            .OrderBy(x => x.Name)
            .Select(row => new LedgerSnapshotItem(row.Name, row.Parent, row.PrimaryGroup, row.IsRevenue, row.Gstin))
            .ToArrayAsync(cancellationToken);

        return new LedgerSnapshotResponse(MasterSnapshotMetadataFactory.BuildMetadata(batch, batch.ItemCount), items);
    }

    public async Task<StockItemSnapshotResponse> GetStockItemsAsync(CancellationToken cancellationToken = default)
    {
        var batch = await GetActiveBatchAsync(MasterTypeStockItems, cancellationToken);
        if (batch is null)
        {
            return new StockItemSnapshotResponse(MasterSnapshotMetadataFactory.EmptyMetadata(MasterTypeStockItems), []);
        }

        var items = await dbContext.TallyStockItemSnapshots
            .AsNoTracking()
            .Where(x => x.BatchId == batch.Id)
            .OrderBy(x => x.Name)
            .Select(row => new StockItemSnapshotItem(row.Name, row.Alias, row.BaseUnit, row.HsnCode, row.Karat))
            .ToArrayAsync(cancellationToken);

        return new StockItemSnapshotResponse(MasterSnapshotMetadataFactory.BuildMetadata(batch, batch.ItemCount), items);
    }

    public async Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(CancellationToken cancellationToken = default)
    {
        var batch = await GetActiveBatchAsync(MasterTypeVoucherTypes, cancellationToken);
        if (batch is null)
        {
            return new VoucherTypeSnapshotResponse(MasterSnapshotMetadataFactory.EmptyMetadata(MasterTypeVoucherTypes), []);
        }

        var items = await dbContext.TallyVoucherTypeSnapshots
            .AsNoTracking()
            .Where(x => x.BatchId == batch.Id)
            .OrderBy(x => x.Name)
            .Select(row => new VoucherTypeSnapshotItem(row.Name, row.ParentType, row.IsDeemedPositive))
            .ToArrayAsync(cancellationToken);

        return new VoucherTypeSnapshotResponse(MasterSnapshotMetadataFactory.BuildMetadata(batch, batch.ItemCount), items);
    }

    public async Task<MasterFreshnessSummaryResponse> GetFreshnessSummaryAsync(CancellationToken cancellationToken = default)
    {
        var activeBatches = await dbContext.TallyMasterSnapshotBatches
            .AsNoTracking()
            .Where(x => x.Status == "active")
            .ToListAsync(cancellationToken);

        var byType = activeBatches
            .GroupBy(x => x.MasterType)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.FetchedAtUtc).First());

        var metadatas = AllMasterTypes
            .Select(type => byType.TryGetValue(type, out var batch)
                ? MasterSnapshotMetadataFactory.BuildMetadata(batch, batch.ItemCount)
                : MasterSnapshotMetadataFactory.EmptyMetadata(type))
            .ToArray();

        var overall = metadatas.All(m => m.Freshness == MasterFreshness.Fresh) ? MasterFreshness.Fresh
            : metadatas.Any(m => m.Freshness == MasterFreshness.Missing) ? MasterFreshness.Missing
            : metadatas.Any(m => m.Freshness == MasterFreshness.Stale) ? MasterFreshness.Stale
            : MasterFreshness.Aging;

        return new MasterFreshnessSummaryResponse(overall, metadatas);
    }

    private async Task<MasterSnapshotAcceptedResponse> ReplaceSnapshotAsync(
        Guid showroomId,
        string masterType,
        DateTimeOffset fetchedAtUtc,
        int itemCount,
        Action<TallyMasterSnapshotBatchEntity> addRows,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var batch = await CreateBatchAsync(
                showroomId,
                masterType,
                fetchedAtUtc,
                itemCount,
                cancellationToken);

            addRows(batch);

            await FinalizeAsync(batch, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return MasterSnapshotMetadataFactory.AcceptedResponse(batch);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);

            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<TallyMasterSnapshotBatchEntity> CreateBatchAsync(
        Guid showroomId,
        string masterType,
        DateTimeOffset fetchedAtUtc,
        int itemCount,
        CancellationToken cancellationToken)
    {
        await DeletePriorBatchesAsync(showroomId, masterType, cancellationToken);

        var batch = new TallyMasterSnapshotBatchEntity
        {
            Id = Guid.CreateVersion7(),
            ShowroomId = showroomId,
            MasterType = masterType,
            FetchedAtUtc = fetchedAtUtc == default ? DateTimeOffset.UtcNow : fetchedAtUtc.ToUniversalTime(),
            Status = "active",
            ItemCount = itemCount
        };

        dbContext.TallyMasterSnapshotBatches.Add(batch);
        return batch;
    }

    private async Task FinalizeAsync(
        TallyMasterSnapshotBatchEntity batch,
        CancellationToken cancellationToken)
    {
        dbContext.AuditEvents.Add(AuditRow.Create(
            "masters",
            batch.Id.ToString(),
            $"masters.{batch.MasterType}.ingested",
            new AuditActor("api", "api"),
            DateTimeOffset.UtcNow,
            new { itemCount = batch.ItemCount, masterType = batch.MasterType }));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<TallyMasterSnapshotBatchEntity?> GetActiveBatchAsync(
        string masterType,
        CancellationToken cancellationToken)
    {
        return await dbContext.TallyMasterSnapshotBatches
            .AsNoTracking()
            .Where(x => x.MasterType == masterType && x.Status == "active")
            .OrderByDescending(x => x.FetchedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private void AddSnapshotRows<T>(IEnumerable<T> rows) where T : class
    {
        var prior = dbContext.ChangeTracker.AutoDetectChangesEnabled;
        try
        {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            dbContext.AddRange(rows);
        }
        finally
        {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = prior;
        }
    }

    /// <summary>
    /// Only the newest snapshot is ever read, so the one it replaces is deleted
    /// in the transaction that stores the new one; its rows go by cascade.
    /// This also clears the "superseded" batches earlier builds kept.
    /// </summary>
    private Task DeletePriorBatchesAsync(
        Guid showroomId,
        string masterType,
        CancellationToken cancellationToken)
        => dbContext.TallyMasterSnapshotBatches
            .Where(x => x.ShowroomId == showroomId && x.MasterType == masterType)
            .ExecuteDeleteAsync(cancellationToken);

    private static void ValidateCommon(string showroomCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showroomCode);
    }
}
