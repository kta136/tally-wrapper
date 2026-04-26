using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropBridgeRegistrationsAndSourceBridgeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bridge_registrations",
                schema: "public");

            migrationBuilder.DropColumn(
                name: "SourceBridgeId",
                schema: "public",
                table: "tally_master_snapshot_batches");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceBridgeId",
                schema: "public",
                table: "tally_master_snapshot_batches",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "bridge_registrations",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: true),
                    HostName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ServiceVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bridge_registrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bridge_registrations_ShowroomId_Status",
                schema: "public",
                table: "bridge_registrations",
                columns: new[] { "ShowroomId", "Status" });
        }
    }
}
