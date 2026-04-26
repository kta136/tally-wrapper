using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "database_identity",
                schema: "public",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_database_identity", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_database_identity_value",
                schema: "public",
                table: "database_identity",
                column: "value");

            migrationBuilder.Sql("""
                INSERT INTO public.database_identity (key, value, updated_at_utc)
                VALUES ('environment', 'UNSET', CURRENT_TIMESTAMP)
                ON CONFLICT (key) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "database_identity",
                schema: "public");
        }
    }
}
