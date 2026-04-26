using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MasterReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_tally_voucher_type_snapshots_BatchId_Name",
                schema: "public",
                table: "tally_voucher_type_snapshots",
                columns: new[] { "BatchId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_tally_stock_item_snapshots_BatchId_Name",
                schema: "public",
                table: "tally_stock_item_snapshots",
                columns: new[] { "BatchId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_tally_ledger_snapshots_BatchId_Name",
                schema: "public",
                table: "tally_ledger_snapshots",
                columns: new[] { "BatchId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_tally_company_snapshots_BatchId_Name",
                schema: "public",
                table: "tally_company_snapshots",
                columns: new[] { "BatchId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tally_voucher_type_snapshots_BatchId_Name",
                schema: "public",
                table: "tally_voucher_type_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_tally_stock_item_snapshots_BatchId_Name",
                schema: "public",
                table: "tally_stock_item_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_tally_ledger_snapshots_BatchId_Name",
                schema: "public",
                table: "tally_ledger_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_tally_company_snapshots_BatchId_Name",
                schema: "public",
                table: "tally_company_snapshots");
        }
    }
}
