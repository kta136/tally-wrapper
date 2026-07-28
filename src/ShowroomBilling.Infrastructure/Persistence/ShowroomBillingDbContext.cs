using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Persistence;

public sealed class ShowroomBillingDbContext(DbContextOptions<ShowroomBillingDbContext> options) : DbContext(options)
{
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    public DbSet<CloudSettingsEntity> CloudSettings => Set<CloudSettingsEntity>();

    public DbSet<TallyCompanySnapshotEntity> TallyCompanySnapshots => Set<TallyCompanySnapshotEntity>();

    public DbSet<TallyLedgerSnapshotEntity> TallyLedgerSnapshots => Set<TallyLedgerSnapshotEntity>();

    public DbSet<TallyStockItemSnapshotEntity> TallyStockItemSnapshots => Set<TallyStockItemSnapshotEntity>();

    public DbSet<TallyVoucherTypeSnapshotEntity> TallyVoucherTypeSnapshots => Set<TallyVoucherTypeSnapshotEntity>();

    public DbSet<TallyMasterSnapshotBatchEntity> TallyMasterSnapshotBatches => Set<TallyMasterSnapshotBatchEntity>();

    public DbSet<InvoiceSequenceEntity> InvoiceSequences => Set<InvoiceSequenceEntity>();

    public DbSet<InvoiceNumberReservationEntity> InvoiceNumberReservations => Set<InvoiceNumberReservationEntity>();

    public DbSet<BillEntity> Bills => Set<BillEntity>();

    public DbSet<BillRevisionEntity> BillRevisions => Set<BillRevisionEntity>();

    public DbSet<DraftEditLeaseEntity> DraftEditLeases => Set<DraftEditLeaseEntity>();

    public DbSet<AdminPasscodeEntity> AdminPasscodes => Set<AdminPasscodeEntity>();

    public DbSet<AdminSessionEntity> AdminSessions => Set<AdminSessionEntity>();

    public DbSet<PrintAssetEntity> PrintAssets => Set<PrintAssetEntity>();

    public DbSet<DatabaseIdentityEntity> DatabaseIdentity => Set<DatabaseIdentityEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");

        modelBuilder.Entity<DatabaseIdentityEntity>(entity =>
        {
            entity.ToTable("database_identity");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasColumnName("key").HasMaxLength(64);
            entity.Property(x => x.Value).HasColumnName("value").HasMaxLength(64);
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.HasIndex(x => x.Value);
        });

        modelBuilder.Entity<CloudSettingsEntity>(entity =>
        {
            entity.ToTable("cloud_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Host).HasMaxLength(256);
            entity.Property(x => x.ActiveCompanyName).HasMaxLength(256);
            entity.Property(x => x.PrintCompanyName).HasMaxLength(256);
            entity.Property(x => x.CompanyGstin).HasMaxLength(32);
            entity.Property(x => x.CompanyPhone).HasMaxLength(64);
            entity.Property(x => x.CompanyAddress).HasMaxLength(2000);
            entity.Property(x => x.CompanyState).HasMaxLength(128);
            entity.Property(x => x.CompanyCountry).HasMaxLength(128);
            entity.Property(x => x.BankName).HasMaxLength(256);
            entity.Property(x => x.BankAccount).HasMaxLength(128);
            entity.Property(x => x.BankIfsc).HasMaxLength(32);
            entity.Property(x => x.BankUpi).HasMaxLength(256);
            entity.Property(x => x.TermsAndConditions).HasMaxLength(10000);
            entity.Property(x => x.InvoicePrefix).HasMaxLength(64);
            entity.Property(x => x.InvoiceSuffix).HasMaxLength(64);
            entity.Property(x => x.InvoicePadding).HasDefaultValue(4);
            entity.Property(x => x.SalesLedger).HasMaxLength(256);
            entity.Property(x => x.CashLedger).HasMaxLength(256);
            entity.Property(x => x.CreditDebitLedger).HasMaxLength(256);
            entity.Property(x => x.CgstLedger).HasMaxLength(256);
            entity.Property(x => x.SgstLedger).HasMaxLength(256);
            entity.Property(x => x.RoundOffLedger).HasMaxLength(256);
            entity.Property(x => x.DiscountLedger).HasMaxLength(256);
            entity.Property(x => x.SalesVoucherType).HasMaxLength(256);
            entity.Property(x => x.ItemMasterDataJson).HasColumnType("jsonb");
            entity.Property(x => x.KaratMappingDataJson).HasColumnType("jsonb");
            entity.Property(x => x.PrintLayoutJson).HasColumnType("jsonb");
            entity.HasIndex(x => x.UpdatedAtUtc);
        });

        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EntityType).HasMaxLength(128);
            entity.Property(x => x.EntityId).HasMaxLength(128);
            entity.Property(x => x.EventType).HasMaxLength(128);
            entity.Property(x => x.ActorType).HasMaxLength(64);
            entity.Property(x => x.ActorId).HasMaxLength(128);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.EntityType, x.EntityId, x.EventType, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.EventType, x.CreatedAtUtc });
        });

        modelBuilder.Entity<TallyMasterSnapshotBatchEntity>(entity =>
        {
            entity.ToTable("tally_master_snapshot_batches", table =>
                table.HasCheckConstraint("CK_tally_master_snapshot_batches_status", "\"Status\" IN ('active', 'superseded')"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MasterType).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.HasIndex(x => new { x.ShowroomId, x.MasterType, x.FetchedAtUtc });
            entity.HasIndex(x => new { x.ShowroomId, x.MasterType, x.Status });
        });

        modelBuilder.Entity<TallyCompanySnapshotEntity>(entity =>
        {
            entity.ToTable("tally_company_snapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.Property(x => x.RawJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ShowroomId, x.Name });
            entity.HasIndex(x => x.BatchId);
            entity.HasIndex(x => new { x.BatchId, x.Name });
            entity.HasOne(x => x.Batch)
                .WithMany(x => x.CompanySnapshots)
                .HasForeignKey(x => x.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TallyLedgerSnapshotEntity>(entity =>
        {
            entity.ToTable("tally_ledger_snapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.Property(x => x.Parent).HasMaxLength(256);
            entity.Property(x => x.PrimaryGroup).HasMaxLength(256);
            entity.Property(x => x.Gstin).HasMaxLength(32);
            entity.Property(x => x.RawJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ShowroomId, x.Name });
            entity.HasIndex(x => x.BatchId);
            entity.HasIndex(x => new { x.BatchId, x.Name });
            entity.HasOne(x => x.Batch)
                .WithMany(x => x.LedgerSnapshots)
                .HasForeignKey(x => x.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TallyStockItemSnapshotEntity>(entity =>
        {
            entity.ToTable("tally_stock_item_snapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.Property(x => x.Alias).HasMaxLength(256);
            entity.Property(x => x.BaseUnit).HasMaxLength(64);
            entity.Property(x => x.HsnCode).HasMaxLength(32);
            entity.Property(x => x.Karat).HasMaxLength(32);
            entity.Property(x => x.RawJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ShowroomId, x.Name });
            entity.HasIndex(x => x.BatchId);
            entity.HasIndex(x => new { x.BatchId, x.Name });
            entity.HasOne(x => x.Batch)
                .WithMany(x => x.StockItemSnapshots)
                .HasForeignKey(x => x.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TallyVoucherTypeSnapshotEntity>(entity =>
        {
            entity.ToTable("tally_voucher_type_snapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.Property(x => x.ParentType).HasMaxLength(256);
            entity.Property(x => x.RawJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ShowroomId, x.Name });
            entity.HasIndex(x => x.BatchId);
            entity.HasIndex(x => new { x.BatchId, x.Name });
            entity.HasOne(x => x.Batch)
                .WithMany(x => x.VoucherTypeSnapshots)
                .HasForeignKey(x => x.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InvoiceSequenceEntity>(entity =>
        {
            entity.ToTable("invoice_sequences", table =>
                table.HasCheckConstraint("CK_invoice_sequences_next_value", "\"NextValue\" > 0"));
            entity.HasKey(x => new { x.ShowroomId, x.FiscalYear, x.DocumentType });
            entity.Property(x => x.FiscalYear).HasMaxLength(16);
            entity.Property(x => x.DocumentType).HasMaxLength(32);
        });

        modelBuilder.Entity<InvoiceNumberReservationEntity>(entity =>
        {
            entity.ToTable("invoice_number_reservations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.FiscalYear).HasMaxLength(16);
            entity.Property(x => x.DocumentType).HasMaxLength(32);
            entity.Property(x => x.FormattedNumber).HasMaxLength(128);
            entity.Property(x => x.ReservedForReference).HasMaxLength(128);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasIndex(x => new { x.ShowroomId, x.FiscalYear, x.DocumentType });
        });

        modelBuilder.Entity<BillEntity>(entity =>
        {
            entity.ToTable("bills", table =>
                table.HasCheckConstraint(
                    "CK_bills_state",
                    "\"State\" IN ('draft', 'pending', 'posting', 'posted', 'failed', 'reconciliation_required', 'revised', 'voided')"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BillType).HasMaxLength(32);
            entity.Property(x => x.State).HasMaxLength(32);
            entity.Property(x => x.InvoiceNumber).HasMaxLength(128);
            entity.Property(x => x.FiscalYear).HasMaxLength(16);
            entity.HasIndex(x => new { x.ShowroomId, x.State, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.ShowroomId, x.State, x.CreatedAtUtc, x.Id });
            entity.HasIndex(x => new { x.ShowroomId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.ShowroomId, x.FiscalYear, x.InvoiceNumber })
                .IsUnique()
                .HasFilter("\"InvoiceNumber\" IS NOT NULL");
        });

        modelBuilder.Entity<BillRevisionEntity>(entity =>
        {
            entity.ToTable("bill_revisions", table =>
            {
                table.HasCheckConstraint("CK_bill_revisions_revision_no", "\"RevisionNo\" > 0");
                table.HasCheckConstraint("CK_bill_revisions_grand_total", "\"GrandTotal\" >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SnapshotJson).HasColumnType("jsonb");
            entity.Property(x => x.TotalsJson).HasColumnType("jsonb");
            entity.Property(x => x.PartyName).HasMaxLength(256);
            entity.Property(x => x.GrandTotal).HasColumnType("numeric(18,3)");
            entity.HasIndex(x => new { x.BillId, x.RevisionNo }).IsUnique();
            entity.HasIndex(x => new { x.BillId, x.CreatedAtUtc });
            entity.HasIndex(x => x.BillDate);
            entity.HasIndex(x => new { x.BillDate, x.Id });
            entity.HasOne(x => x.Bill)
                .WithMany(x => x.Revisions)
                .HasForeignKey(x => x.BillId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DraftEditLeaseEntity>(entity =>
        {
            entity.ToTable("draft_edit_leases");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerActorId).HasMaxLength(128);
            entity.Property(x => x.OwnerCounterName).HasMaxLength(128);
            entity.HasIndex(x => new { x.ShowroomId, x.BillId });
            entity.HasIndex(x => x.BillId)
                .IsUnique()
                .HasFilter("\"ReleasedAtUtc\" IS NULL");
            entity.HasIndex(x => x.ExpiresAtUtc);
        });

        modelBuilder.Entity<AdminPasscodeEntity>(entity =>
        {
            entity.ToTable("admin_passcodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PasscodeHash).HasMaxLength(256);
            entity.Property(x => x.PasscodeSalt).HasMaxLength(128);
            entity.HasIndex(x => x.ShowroomId).IsUnique();
        });

        modelBuilder.Entity<AdminSessionEntity>(entity =>
        {
            entity.ToTable("admin_sessions", table =>
                table.HasCheckConstraint("CK_admin_sessions_expiry", "\"ExpiresAtUtc\" > \"IssuedAtUtc\""));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(256);
            entity.Property(x => x.ActorLabel).HasMaxLength(128);
            entity.Property(x => x.RevokedReason).HasMaxLength(256);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.ShowroomId, x.ExpiresAtUtc });
        });

        modelBuilder.Entity<PrintAssetEntity>(entity =>
        {
            entity.ToTable("print_assets", table =>
            {
                table.HasCheckConstraint("CK_print_assets_kind", "\"AssetKind\" IN ('logo', 'signature', 'watermark')");
                table.HasCheckConstraint("CK_print_assets_byte_length", "\"ByteLength\" > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AssetKind).HasMaxLength(32);
            entity.Property(x => x.FileName).HasMaxLength(256);
            entity.Property(x => x.ContentType).HasMaxLength(128);
            entity.HasIndex(x => new { x.ShowroomId, x.AssetKind });
        });

        base.OnModelCreating(modelBuilder);
    }
}
