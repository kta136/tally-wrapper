using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowWatermarkPrintAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_print_assets_kind",
                schema: "public",
                table: "print_assets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_print_assets_kind",
                schema: "public",
                table: "print_assets",
                sql: "\"AssetKind\" IN ('logo', 'signature', 'watermark')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_print_assets_kind",
                schema: "public",
                table: "print_assets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_print_assets_kind",
                schema: "public",
                table: "print_assets",
                sql: "\"AssetKind\" IN ('logo', 'signature')");
        }
    }
}
