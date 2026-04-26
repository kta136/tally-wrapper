using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_bills_ShowroomId_CreatedAtUtc",
                schema: "public",
                table: "bills",
                columns: new[] { "ShowroomId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_bill_revisions_BillDate",
                schema: "public",
                table: "bill_revisions",
                column: "BillDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_bills_ShowroomId_CreatedAtUtc",
                schema: "public",
                table: "bills");

            migrationBuilder.DropIndex(
                name: "IX_bill_revisions_BillDate",
                schema: "public",
                table: "bill_revisions");
        }
    }
}
