using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Persistence;

public sealed class ShowroomBillingDbContext(DbContextOptions<ShowroomBillingDbContext> options) : DbContext(options)
{
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    public DbSet<CloudSettingsEntity> CloudSettings => Set<CloudSettingsEntity>();

    public DbSet<TallyMasterEntity> TallyMasters => Set<TallyMasterEntity>();

    public DbSet<InvoiceSequenceEntity> InvoiceSequences => Set<InvoiceSequenceEntity>();


    public DbSet<BillEntity> Bills => Set<BillEntity>();

    public DbSet<BillRevisionEntity> BillRevisions => Set<BillRevisionEntity>();

    public DbSet<AdminPasscodeEntity> AdminPasscodes => Set<AdminPasscodeEntity>();

    public DbSet<AdminSessionEntity> AdminSessions => Set<AdminSessionEntity>();

    public DbSet<PrintAssetEntity> PrintAssets => Set<PrintAssetEntity>();

    public DbSet<DatabaseIdentityEntity> DatabaseIdentity => Set<DatabaseIdentityEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.HasDefaultSchema("public");

        modelBuilder.Entity<DatabaseIdentityEntity>(entity =>
        {
            entity.ToTable("database_identity");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasColumnName("key").HasMaxLength(64);
            entity.Property(x => x.Value).HasColumnName("value").HasMaxLength(64);
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
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
            // Defaulted in the database too, as BatchHoursJson is below.
            entity.Property(x => x.BankChargesLedger).HasMaxLength(256).HasDefaultValue(string.Empty);
            entity.Property(x => x.SalesVoucherType).HasMaxLength(256);
            entity.Property(x => x.ItemMasterDataJson).HasColumnType("jsonb");
            entity.Property(x => x.KaratMappingDataJson).HasColumnType("jsonb");
            entity.Property(x => x.PrintLayoutJson).HasColumnType("jsonb");
            // The default is the database's too, so a row written by a build from before the
            // column (which does not name it) still gets a value.
            entity.Property(x => x.BatchHoursJson).HasColumnType("text").HasDefaultValue("{}");
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

        modelBuilder.Entity<TallyMasterEntity>(entity =>
        {
            // One row per master type; the list itself is the JSON.
            entity.ToTable("tally_masters");
            entity.HasKey(x => x.MasterType);
            entity.Property(x => x.MasterType).HasMaxLength(64);
            entity.Property(x => x.ItemsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<InvoiceSequenceEntity>(entity =>
        {
            entity.ToTable("invoice_sequences", table =>
                table.HasCheckConstraint("CK_invoice_sequences_next_value", "\"NextValue\" > 0"));
            entity.HasKey(x => x.FiscalYear);
            entity.Property(x => x.FiscalYear).HasMaxLength(16);
        });

        modelBuilder.Entity<BillEntity>(entity =>
        {
            entity.ToTable("bills", table =>
                table.HasCheckConstraint(
                    "CK_bills_state",
                    "\"State\" IN ('draft', 'pending', 'posting', 'posted', 'failed', 'reconciliation_required', 'revised', 'voided')"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.State).HasMaxLength(32);
            entity.Property(x => x.InvoiceNumber).HasMaxLength(128);
            entity.Property(x => x.FiscalYear).HasMaxLength(16);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => new { x.State, x.CreatedAtUtc, x.Id });
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => new { x.FiscalYear, x.InvoiceNumber })
                .IsUnique()
                .HasFilter("\"InvoiceNumber\" IS NOT NULL");
            // Unique: the number's text alone does not keep two bills off one
            // core, because the prefix, suffix and padding are settings and
            // the same core formats to other text once one of them changes.
            entity.HasIndex(x => new { x.FiscalYear, x.InvoiceCore })
                .IsUnique()
                .HasFilter("\"InvoiceCore\" IS NOT NULL");
            entity.HasIndex(x => x.CreateOperationId)
                .IsUnique()
                .HasFilter("\"CreateOperationId\" IS NOT NULL");
            entity.HasIndex(x => x.InvoiceNumber)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops")
                .IsCreatedConcurrently();
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
            entity.Property(x => x.PartyName).HasMaxLength(256);
            entity.Property(x => x.GrandTotal).HasColumnType("numeric(18,3)");
            entity.HasIndex(x => new { x.BillId, x.RevisionNo }).IsUnique();
            entity.HasIndex(x => new { x.BillId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.BillDate, x.Id });
            entity.HasIndex(x => x.PartyName)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops")
                .IsCreatedConcurrently();
            entity.HasOne(x => x.Bill)
                .WithMany(x => x.Revisions)
                .HasForeignKey(x => x.BillId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminPasscodeEntity>(entity =>
        {
            entity.ToTable("admin_passcodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PasscodeHash).HasMaxLength(256);
            entity.Property(x => x.PasscodeSalt).HasMaxLength(128);
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
            entity.HasIndex(x => x.ExpiresAtUtc);
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
            entity.HasIndex(x => x.AssetKind);
        });

        base.OnModelCreating(modelBuilder);
    }
}
