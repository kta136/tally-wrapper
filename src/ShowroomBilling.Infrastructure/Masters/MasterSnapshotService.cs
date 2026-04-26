using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Masters;

public sealed class MasterSnapshotService(ShowroomBillingDbContext dbContext) : IMasterSnapshotService
{
    public const string MasterTypeCompanies = "companies";
    public const string MasterTypeLedgers = "ledgers";
    public const string MasterTypeStockItems = "stock_items";
    public const string MasterTypeVoucherTypes = "voucher_types";

    private static readonly string[] AllMasterTypes =
    [
        MasterTypeCompanies,
        MasterTypeLedgers,
        MasterTypeStockItems,
        MasterTypeVoucherTypes
    ];

    private const int SupersededBatchRetention = 3;

    public async Task<MasterSnapshotAcceptedResponse> IngestCompaniesAsync(
        PushCompanySnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(request.ShowroomCode);

        var showroomId = MasterSnapshotNormalization.ResolveShowroomId(request.ShowroomCode);
        var items = request.Companies ?? [];

        return await ReplaceSnapshotAsync(
            showroomId,
            MasterTypeCompanies,
            request.FetchedAtUtc,
            items.Count,
            batch => AddSnapshotRows(items.Select(item => new TallyCompanySnapshotEntity
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                ShowroomId = showroomId,
                Name = (item.Name ?? string.Empty).Trim(),
                IsActive = item.IsActive,
                RawJson = MasterSnapshotNormalization.NormalizeJson(item.RawJson)
            })),
            cancellationToken);
    }

    public async Task<MasterSnapshotAcceptedResponse> IngestLedgersAsync(
        PushLedgerSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(request.ShowroomCode);

        var showroomId = MasterSnapshotNormalization.ResolveShowroomId(request.ShowroomCode);
        var items = request.Ledgers ?? [];

        return await ReplaceSnapshotAsync(
            showroomId,
            MasterTypeLedgers,
            request.FetchedAtUtc,
            items.Count,
            batch => AddSnapshotRows(items.Select(item => new TallyLedgerSnapshotEntity
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                ShowroomId = showroomId,
                Name = (item.Name ?? string.Empty).Trim(),
                Parent = MasterSnapshotNormalization.Normalize(item.Parent),
                PrimaryGroup = MasterSnapshotNormalization.Normalize(item.PrimaryGroup),
                IsRevenue = item.IsRevenue,
                Gstin = MasterSnapshotNormalization.Normalize(item.Gstin),
                RawJson = MasterSnapshotNormalization.NormalizeJson(item.RawJson)
            })),
            cancellationToken);
    }

    public async Task<MasterSnapshotAcceptedResponse> IngestStockItemsAsync(
        PushStockItemSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(request.ShowroomCode);

        var showroomId = MasterSnapshotNormalization.ResolveShowroomId(request.ShowroomCode);
        var items = request.StockItems ?? [];

        return await ReplaceSnapshotAsync(
            showroomId,
            MasterTypeStockItems,
            request.FetchedAtUtc,
            items.Count,
            batch => AddSnapshotRows(items.Select(item => new TallyStockItemSnapshotEntity
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                ShowroomId = showroomId,
                Name = (item.Name ?? string.Empty).Trim(),
                Alias = MasterSnapshotNormalization.Normalize(item.Alias),
                BaseUnit = MasterSnapshotNormalization.Normalize(item.BaseUnit),
                HsnCode = MasterSnapshotNormalization.Normalize(item.HsnCode),
                Karat = MasterSnapshotNormalization.Normalize(item.Karat),
                RawJson = MasterSnapshotNormalization.NormalizeJson(item.RawJson)
            })),
            cancellationToken);
    }

    public async Task<MasterSnapshotAcceptedResponse> IngestVoucherTypesAsync(
        PushVoucherTypeSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(request.ShowroomCode);

        var showroomId = MasterSnapshotNormalization.ResolveShowroomId(request.ShowroomCode);
        var items = request.VoucherTypes ?? [];

        return await ReplaceSnapshotAsync(
            showroomId,
            MasterTypeVoucherTypes,
            request.FetchedAtUtc,
            items.Count,
            batch => AddSnapshotRows(items.Select(item => new TallyVoucherTypeSnapshotEntity
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                ShowroomId = showroomId,
                Name = (item.Name ?? string.Empty).Trim(),
                ParentType = MasterSnapshotNormalization.Normalize(item.ParentType),
                IsDeemedPositive = item.IsDeemedPositive,
                RawJson = MasterSnapshotNormalization.NormalizeJson(item.RawJson)
            })),
            cancellationToken);
    }

    public async Task<CompanySnapshotResponse> GetCompaniesAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null)
    {
        var batch = await GetActiveBatchAsync(MasterTypeCompanies, cancellationToken);
        if (batch is null)
        {
            return new CompanySnapshotResponse(MasterSnapshotMetadataFactory.EmptyMetadata(MasterTypeCompanies), []);
        }

        var rowsQuery = dbContext.TallyCompanySnapshots
            .AsNoTracking()
            .Where(x => x.BatchId == batch.Id);
        rowsQuery = MasterSnapshotQueryFilters.ApplyNameQuery(rowsQuery, query);

        var includeRaw = query?.IncludeRaw == true;
        var items = await MasterSnapshotQueryFilters.ApplyPaging(rowsQuery.OrderBy(x => x.Name), query)
            .Select(row => new CompanySnapshotItem(row.Name, row.IsActive, includeRaw ? row.RawJson : null))
            .ToArrayAsync(cancellationToken);

        return new CompanySnapshotResponse(MasterSnapshotMetadataFactory.BuildMetadata(batch, batch.ItemCount), items);
    }

    public async Task<LedgerSnapshotResponse> GetLedgersAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null)
    {
        var batch = await GetActiveBatchAsync(MasterTypeLedgers, cancellationToken);
        if (batch is null)
        {
            return new LedgerSnapshotResponse(MasterSnapshotMetadataFactory.EmptyMetadata(MasterTypeLedgers), []);
        }

        var rowsQuery = dbContext.TallyLedgerSnapshots
            .AsNoTracking()
            .Where(x => x.BatchId == batch.Id);
        rowsQuery = MasterSnapshotQueryFilters.ApplyNameQuery(rowsQuery, query);

        var includeRaw = query?.IncludeRaw == true;
        var items = await MasterSnapshotQueryFilters.ApplyPaging(rowsQuery.OrderBy(x => x.Name), query)
            .Select(row => new LedgerSnapshotItem(row.Name, row.Parent, row.PrimaryGroup, row.IsRevenue, row.Gstin, includeRaw ? row.RawJson : null))
            .ToArrayAsync(cancellationToken);

        return new LedgerSnapshotResponse(MasterSnapshotMetadataFactory.BuildMetadata(batch, batch.ItemCount), items);
    }

    public async Task<StockItemSnapshotResponse> GetStockItemsAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null)
    {
        var batch = await GetActiveBatchAsync(MasterTypeStockItems, cancellationToken);
        if (batch is null)
        {
            return new StockItemSnapshotResponse(MasterSnapshotMetadataFactory.EmptyMetadata(MasterTypeStockItems), []);
        }

        var rowsQuery = dbContext.TallyStockItemSnapshots
            .AsNoTracking()
            .Where(x => x.BatchId == batch.Id);
        rowsQuery = MasterSnapshotQueryFilters.ApplyNameQuery(rowsQuery, query);

        var includeRaw = query?.IncludeRaw == true;
        var items = await MasterSnapshotQueryFilters.ApplyPaging(rowsQuery.OrderBy(x => x.Name), query)
            .Select(row => new StockItemSnapshotItem(row.Name, row.Alias, row.BaseUnit, row.HsnCode, row.Karat, includeRaw ? row.RawJson : null))
            .ToArrayAsync(cancellationToken);

        return new StockItemSnapshotResponse(MasterSnapshotMetadataFactory.BuildMetadata(batch, batch.ItemCount), items);
    }

    public async Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null)
    {
        var batch = await GetActiveBatchAsync(MasterTypeVoucherTypes, cancellationToken);
        if (batch is null)
        {
            return new VoucherTypeSnapshotResponse(MasterSnapshotMetadataFactory.EmptyMetadata(MasterTypeVoucherTypes), []);
        }

        var rowsQuery = dbContext.TallyVoucherTypeSnapshots
            .AsNoTracking()
            .Where(x => x.BatchId == batch.Id);
        rowsQuery = MasterSnapshotQueryFilters.ApplyNameQuery(rowsQuery, query);

        var includeRaw = query?.IncludeRaw == true;
        var items = await MasterSnapshotQueryFilters.ApplyPaging(rowsQuery.OrderBy(x => x.Name), query)
            .Select(row => new VoucherTypeSnapshotItem(row.Name, row.ParentType, row.IsDeemedPositive, includeRaw ? row.RawJson : null))
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

        var overall = metadatas.All(m => m.Freshness == "fresh") ? "fresh"
            : metadatas.Any(m => m.Freshness == "missing") ? "missing"
            : metadatas.Any(m => m.Freshness == "stale") ? "stale"
            : "aging";

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
        await using var transaction = UsesInMemoryProvider()
            ? null
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);

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

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return MasterSnapshotMetadataFactory.AcceptedResponse(batch);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

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
        await SupersedePriorBatchesAsync(showroomId, masterType, cancellationToken);

        var batch = new TallyMasterSnapshotBatchEntity
        {
            Id = Guid.NewGuid(),
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
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            EntityType = "masters",
            EntityId = batch.Id.ToString(),
            EventType = $"masters.{batch.MasterType}.ingested",
            ActorType = "api",
            ActorId = "api",
            PayloadJson = $"{{\"itemCount\":{batch.ItemCount},\"masterType\":\"{batch.MasterType}\"}}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await PruneSupersededBatchesAsync(batch.ShowroomId, batch.MasterType, cancellationToken);
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

    private async Task SupersedePriorBatchesAsync(
        Guid showroomId,
        string masterType,
        CancellationToken cancellationToken)
    {
        var priorActive = dbContext.TallyMasterSnapshotBatches
            .Where(x => x.ShowroomId == showroomId && x.MasterType == masterType && x.Status == "active");

        if (UsesInMemoryProvider())
        {
            var rows = await priorActive.ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.Status = "superseded";
            }

            return;
        }

        await priorActive.ExecuteUpdateAsync(
            setters => setters.SetProperty(x => x.Status, "superseded"),
            cancellationToken);
    }

    private async Task PruneSupersededBatchesAsync(
        Guid showroomId,
        string masterType,
        CancellationToken cancellationToken)
    {
        var idsToDelete = await dbContext.TallyMasterSnapshotBatches
            .Where(x => x.ShowroomId == showroomId && x.MasterType == masterType && x.Status == "superseded")
            .OrderByDescending(x => x.FetchedAtUtc)
            .Skip(SupersededBatchRetention)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (idsToDelete.Count == 0)
        {
            return;
        }

        var stale = dbContext.TallyMasterSnapshotBatches.Where(x => idsToDelete.Contains(x.Id));
        if (UsesInMemoryProvider())
        {
            dbContext.TallyMasterSnapshotBatches.RemoveRange(await stale.ToListAsync(cancellationToken));
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        await stale.ExecuteDeleteAsync(cancellationToken);
    }

    private bool UsesInMemoryProvider() =>
        string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private static void ValidateCommon(string showroomCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showroomCode);
    }
}
