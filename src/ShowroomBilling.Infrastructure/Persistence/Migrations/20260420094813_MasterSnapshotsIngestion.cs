using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MasterSnapshotsIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ItemCount",
                schema: "public",
                table: "tally_master_snapshot_batches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "tally_ledger_snapshots",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Parent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PrimaryGroup = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsRevenue = table.Column<bool>(type: "boolean", nullable: false),
                    Gstin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RawJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tally_ledger_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tally_ledger_snapshots_tally_master_snapshot_batches_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "public",
                        principalTable: "tally_master_snapshot_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tally_stock_item_snapshots",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Alias = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BaseUnit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HsnCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Karat = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RawJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tally_stock_item_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tally_stock_item_snapshots_tally_master_snapshot_batches_Ba~",
                        column: x => x.BatchId,
                        principalSchema: "public",
                        principalTable: "tally_master_snapshot_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tally_voucher_type_snapshots",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ParentType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsDeemedPositive = table.Column<bool>(type: "boolean", nullable: false),
                    RawJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tally_voucher_type_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tally_voucher_type_snapshots_tally_master_snapshot_batches_~",
                        column: x => x.BatchId,
                        principalSchema: "public",
                        principalTable: "tally_master_snapshot_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tally_master_snapshot_batches_ShowroomId_MasterType_Status",
                schema: "public",
                table: "tally_master_snapshot_batches",
                columns: new[] { "ShowroomId", "MasterType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_tally_ledger_snapshots_BatchId",
                schema: "public",
                table: "tally_ledger_snapshots",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_tally_ledger_snapshots_ShowroomId_Name",
                schema: "public",
                table: "tally_ledger_snapshots",
                columns: new[] { "ShowroomId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_tally_stock_item_snapshots_BatchId",
                schema: "public",
                table: "tally_stock_item_snapshots",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_tally_stock_item_snapshots_ShowroomId_Name",
                schema: "public",
                table: "tally_stock_item_snapshots",
                columns: new[] { "ShowroomId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_tally_voucher_type_snapshots_BatchId",
                schema: "public",
                table: "tally_voucher_type_snapshots",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_tally_voucher_type_snapshots_ShowroomId_Name",
                schema: "public",
                table: "tally_voucher_type_snapshots",
                columns: new[] { "ShowroomId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tally_ledger_snapshots",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tally_stock_item_snapshots",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tally_voucher_type_snapshots",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "IX_tally_master_snapshot_batches_ShowroomId_MasterType_Status",
                schema: "public",
                table: "tally_master_snapshot_batches");

            migrationBuilder.DropColumn(
                name: "ItemCount",
                schema: "public",
                table: "tally_master_snapshot_batches");
        }
    }
}
