using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FoundationSettingsAndOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActorType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bridge_registrations",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    HostName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ServiceVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bridge_registrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cloud_settings",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    ActiveCompanyName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PrintCompanyName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CompanyGstin = table.Column<string>(type: "text", nullable: true),
                    CompanyPhone = table.Column<string>(type: "text", nullable: true),
                    CompanyAddress = table.Column<string>(type: "text", nullable: true),
                    CompanyState = table.Column<string>(type: "text", nullable: true),
                    CompanyCountry = table.Column<string>(type: "text", nullable: true),
                    BankName = table.Column<string>(type: "text", nullable: true),
                    BankAccount = table.Column<string>(type: "text", nullable: true),
                    BankIfsc = table.Column<string>(type: "text", nullable: true),
                    BankUpi = table.Column<string>(type: "text", nullable: true),
                    TermsAndConditions = table.Column<string>(type: "text", nullable: true),
                    InvoicePrefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InvoiceSuffix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CopyOriginalDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CopyDuplicateDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CopyTriplicateDefault = table.Column<bool>(type: "boolean", nullable: false),
                    PrintFontSize = table.Column<int>(type: "integer", nullable: false),
                    PrintTermsFontSize = table.Column<int>(type: "integer", nullable: false),
                    SalesLedger = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CashLedger = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreditDebitLedger = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CgstLedger = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SgstLedger = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RoundOffLedger = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DiscountLedger = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SalesVoucherType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ItemMasterDataJson = table.Column<string>(type: "jsonb", nullable: false),
                    KaratMappingDataJson = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tally_master_snapshot_batches",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceBridgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    MasterType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FetchedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tally_master_snapshot_batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tally_company_snapshots",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RawJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tally_company_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tally_company_snapshots_tally_master_snapshot_batches_Batch~",
                        column: x => x.BatchId,
                        principalSchema: "public",
                        principalTable: "tally_master_snapshot_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_EntityType_EntityId_CreatedAtUtc",
                schema: "public",
                table: "audit_events",
                columns: new[] { "EntityType", "EntityId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_EventType_CreatedAtUtc",
                schema: "public",
                table: "audit_events",
                columns: new[] { "EventType", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_bridge_registrations_ShowroomId_Status",
                schema: "public",
                table: "bridge_registrations",
                columns: new[] { "ShowroomId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_cloud_settings_UpdatedAtUtc",
                schema: "public",
                table: "cloud_settings",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_tally_company_snapshots_BatchId",
                schema: "public",
                table: "tally_company_snapshots",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_tally_company_snapshots_ShowroomId_Name",
                schema: "public",
                table: "tally_company_snapshots",
                columns: new[] { "ShowroomId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_tally_master_snapshot_batches_ShowroomId_MasterType_Fetched~",
                schema: "public",
                table: "tally_master_snapshot_batches",
                columns: new[] { "ShowroomId", "MasterType", "FetchedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "public");

            migrationBuilder.DropTable(
                name: "bridge_registrations",
                schema: "public");

            migrationBuilder.DropTable(
                name: "cloud_settings",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tally_company_snapshots",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tally_master_snapshot_batches",
                schema: "public");
        }
    }
}
