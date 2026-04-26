using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminAndPrintAssetsAndDraftLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrintLayoutJson",
                schema: "public",
                table: "cloud_settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "admin_passcodes",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    PasscodeHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasscodeSalt = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Iterations = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_passcodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "admin_sessions",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActorLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "draft_edit_leases",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OwnerCounterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AcquiredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RenewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_draft_edit_leases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "print_assets",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_print_assets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_passcodes_ShowroomId",
                schema: "public",
                table: "admin_passcodes",
                column: "ShowroomId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_ShowroomId_ExpiresAtUtc",
                schema: "public",
                table: "admin_sessions",
                columns: new[] { "ShowroomId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_TokenHash",
                schema: "public",
                table: "admin_sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_draft_edit_leases_BillId",
                schema: "public",
                table: "draft_edit_leases",
                column: "BillId",
                unique: true,
                filter: "\"ReleasedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_draft_edit_leases_ExpiresAtUtc",
                schema: "public",
                table: "draft_edit_leases",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_draft_edit_leases_ShowroomId_BillId",
                schema: "public",
                table: "draft_edit_leases",
                columns: new[] { "ShowroomId", "BillId" });

            migrationBuilder.CreateIndex(
                name: "IX_print_assets_ShowroomId_AssetKind",
                schema: "public",
                table: "print_assets",
                columns: new[] { "ShowroomId", "AssetKind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_passcodes",
                schema: "public");

            migrationBuilder.DropTable(
                name: "admin_sessions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "draft_edit_leases",
                schema: "public");

            migrationBuilder.DropTable(
                name: "print_assets",
                schema: "public");

            migrationBuilder.DropColumn(
                name: "PrintLayoutJson",
                schema: "public",
                table: "cloud_settings");
        }
    }
}
