using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceNumbering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoice_number_reservations",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYear = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReservedValue = table.Column<long>(type: "bigint", nullable: false),
                    FormattedNumber = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReservedForReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_number_reservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "invoice_sequences",
                schema: "public",
                columns: table => new
                {
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYear = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NextValue = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_sequences", x => new { x.ShowroomId, x.FiscalYear, x.DocumentType });
                });

            migrationBuilder.CreateIndex(
                name: "IX_invoice_number_reservations_IdempotencyKey",
                schema: "public",
                table: "invoice_number_reservations",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoice_number_reservations_ShowroomId_FiscalYear_DocumentT~",
                schema: "public",
                table: "invoice_number_reservations",
                columns: new[] { "ShowroomId", "FiscalYear", "DocumentType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_number_reservations",
                schema: "public");

            migrationBuilder.DropTable(
                name: "invoice_sequences",
                schema: "public");
        }
    }
}
