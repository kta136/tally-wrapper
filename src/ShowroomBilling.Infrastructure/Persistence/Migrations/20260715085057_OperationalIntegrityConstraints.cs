using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationalIntegrityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TermsAndConditions",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyState",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyPhone",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyGstin",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyCountry",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyAddress",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BankUpi",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BankName",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BankIfsc",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BankAccount",
                schema: "public",
                table: "cloud_settings",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_tally_master_snapshot_batches_status",
                schema: "public",
                table: "tally_master_snapshot_batches",
                sql: "\"Status\" IN ('active', 'superseded')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_print_assets_byte_length",
                schema: "public",
                table: "print_assets",
                sql: "\"ByteLength\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_print_assets_kind",
                schema: "public",
                table: "print_assets",
                sql: "\"AssetKind\" IN ('logo', 'signature')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_invoice_sequences_next_value",
                schema: "public",
                table: "invoice_sequences",
                sql: "\"NextValue\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bills_state",
                schema: "public",
                table: "bills",
                sql: "\"State\" IN ('draft', 'pending', 'posting', 'posted', 'failed', 'reconciliation_required', 'revised', 'voided')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bill_revisions_grand_total",
                schema: "public",
                table: "bill_revisions",
                sql: "\"GrandTotal\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bill_revisions_revision_no",
                schema: "public",
                table: "bill_revisions",
                sql: "\"RevisionNo\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_admin_sessions_expiry",
                schema: "public",
                table: "admin_sessions",
                sql: "\"ExpiresAtUtc\" > \"IssuedAtUtc\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tally_master_snapshot_batches_status",
                schema: "public",
                table: "tally_master_snapshot_batches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_print_assets_byte_length",
                schema: "public",
                table: "print_assets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_print_assets_kind",
                schema: "public",
                table: "print_assets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_invoice_sequences_next_value",
                schema: "public",
                table: "invoice_sequences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bills_state",
                schema: "public",
                table: "bills");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bill_revisions_grand_total",
                schema: "public",
                table: "bill_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bill_revisions_revision_no",
                schema: "public",
                table: "bill_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_admin_sessions_expiry",
                schema: "public",
                table: "admin_sessions");

            migrationBuilder.AlterColumn<string>(
                name: "TermsAndConditions",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10000)",
                oldMaxLength: 10000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyState",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyPhone",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyGstin",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyCountry",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompanyAddress",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BankUpi",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BankName",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BankIfsc",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BankAccount",
                schema: "public",
                table: "cloud_settings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }
    }
}
