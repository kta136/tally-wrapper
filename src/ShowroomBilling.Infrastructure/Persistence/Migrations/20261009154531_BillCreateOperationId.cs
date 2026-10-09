using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BillCreateOperationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreateOperationId",
                schema: "public",
                table: "bills",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_bills_CreateOperationId",
                schema: "public",
                table: "bills",
                column: "CreateOperationId",
                unique: true,
                filter: "\"CreateOperationId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_bills_CreateOperationId",
                schema: "public",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "CreateOperationId",
                schema: "public",
                table: "bills");
        }
    }
}
