using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BillsAndRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bills",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    CounterId = table.Column<Guid>(type: "uuid", nullable: true),
                    BillType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InvoiceNumber = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FiscalYear = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SupersededByBillId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VoidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bill_revisions",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNo = table.Column<int>(type: "integer", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    TotalsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PartyName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BillDate = table.Column<DateOnly>(type: "date", nullable: true),
                    GrandTotal = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    SupersedesRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinalizedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bill_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bill_revisions_bills_BillId",
                        column: x => x.BillId,
                        principalSchema: "public",
                        principalTable: "bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bill_revisions_BillId_CreatedAtUtc",
                schema: "public",
                table: "bill_revisions",
                columns: new[] { "BillId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_bill_revisions_BillId_RevisionNo",
                schema: "public",
                table: "bill_revisions",
                columns: new[] { "BillId", "RevisionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bills_ShowroomId_FiscalYear_InvoiceNumber",
                schema: "public",
                table: "bills",
                columns: new[] { "ShowroomId", "FiscalYear", "InvoiceNumber" },
                unique: true,
                filter: "\"InvoiceNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bills_ShowroomId_State_CreatedAtUtc",
                schema: "public",
                table: "bills",
                columns: new[] { "ShowroomId", "State", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bill_revisions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "bills",
                schema: "public");
        }
    }
}
