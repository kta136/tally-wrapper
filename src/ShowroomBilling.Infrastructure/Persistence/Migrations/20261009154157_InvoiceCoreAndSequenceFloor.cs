using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceCoreAndSequenceFloor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MinNextValue",
                schema: "public",
                table: "invoice_sequences",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "InvoiceCore",
                schema: "public",
                table: "bills",
                type: "bigint",
                nullable: true);

            // Backfill the numeric core, most reliable evidence first:
            //  1. the bill still carries the number its reservation issued, so
            //     the reservation's ReservedValue is the core;
            //  2. the number ends with the current suffix: take the digits just
            //     before it (a digit-bearing suffix such as "-A2" would
            //     otherwise parse as core 2);
            //  3. otherwise the trailing digits, which is what occupancy used
            //     before this column existed.
            // Legacy custom numbers with no digits stay NULL and keep the
            // parse fallback in NumberingService / NumberingSequenceReclaimer.
            migrationBuilder.Sql("""
                UPDATE public.bills AS b
                SET "InvoiceCore" = r."ReservedValue"
                FROM public.invoice_number_reservations AS r
                WHERE b."InvoiceCore" IS NULL
                  AND b."InvoiceNumber" IS NOT NULL
                  AND r."IdempotencyKey" = 'draft:' || replace(b."Id"::text, '-', '')
                  AND r."FormattedNumber" = b."InvoiceNumber";
                """);
            migrationBuilder.Sql("""
                WITH s AS (
                    SELECT btrim("InvoiceSuffix") AS suffix
                    FROM public.cloud_settings
                    ORDER BY "UpdatedAtUtc" DESC
                    LIMIT 1
                )
                UPDATE public.bills AS b
                SET "InvoiceCore" = substring(left(b."InvoiceNumber", length(b."InvoiceNumber") - length(s.suffix)) FROM '([0-9]{1,18})$')::bigint
                FROM s
                WHERE b."InvoiceCore" IS NULL
                  AND b."InvoiceNumber" IS NOT NULL
                  AND coalesce(s.suffix, '') <> ''
                  AND right(b."InvoiceNumber", length(s.suffix)) = s.suffix
                  AND left(b."InvoiceNumber", length(b."InvoiceNumber") - length(s.suffix)) ~ '(^|[^0-9])[0-9]{1,18}$';
                """);
            migrationBuilder.Sql("""
                UPDATE public.bills
                SET "InvoiceCore" = substring("InvoiceNumber" FROM '([0-9]{1,18})$')::bigint
                WHERE "InvoiceCore" IS NULL
                  AND "InvoiceNumber" ~ '(^|[^0-9])[0-9]{1,18}$';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_bills_ShowroomId_FiscalYear_InvoiceCore",
                schema: "public",
                table: "bills",
                columns: new[] { "ShowroomId", "FiscalYear", "InvoiceCore" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_bills_ShowroomId_FiscalYear_InvoiceCore",
                schema: "public",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "MinNextValue",
                schema: "public",
                table: "invoice_sequences");

            migrationBuilder.DropColumn(
                name: "InvoiceCore",
                schema: "public",
                table: "bills");
        }
    }
}
