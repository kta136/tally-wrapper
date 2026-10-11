using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Drops <c>invoice_number_reservations</c>. Builds up to 10 October 2026
    /// wrote one row there for every bill created; a bill's own row has
    /// carried its number and core all along, and a repeated Save is
    /// recognised by <c>bills.CreateOperationId</c>, so nothing reads the
    /// table and, since the build of 11 October, nothing writes it.
    /// <para>
    /// <b>Rows are copied aside first</b>, as in
    /// <see cref="DropDraftEditLeases"/>: if the table holds anything, it is
    /// copied to <see cref="Backup"/> in the same transaction before the drop,
    /// and <see cref="Down"/> puts the table, its indexes and those rows back.
    /// An empty table leaves no copy behind. Once the release has settled,
    /// remove a copy with
    /// <c>DROP TABLE IF EXISTS public."dropped_20261011_invoice_number_reservations"</c>.
    /// </para>
    /// <para>
    /// Every statement is conditional: a migration that fails at start-up
    /// leaves the API in limited mode.
    /// </para>
    /// </summary>
    public partial class DropInvoiceNumberReservations : Migration
    {
        /// <summary>The copy this migration leaves behind when the table held rows.</summary>
        public const string Backup = "dropped_20261011_invoice_number_reservations";

        private const string ColumnList =
            "\"Id\", \"IdempotencyKey\", \"ShowroomId\", \"FiscalYear\", \"DocumentType\", " +
            "\"ReservedValue\", \"FormattedNumber\", \"ReservedForReference\", \"ReservedAtUtc\"";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Copy the rows aside, if there are any.
            migrationBuilder.Sql($$"""
                DO $copy$
                DECLARE has_rows boolean;
                BEGIN
                    IF to_regclass('public.invoice_number_reservations') IS NOT NULL
                       AND to_regclass('public."{{Backup}}"') IS NULL
                    THEN
                        EXECUTE 'SELECT EXISTS (SELECT 1 FROM public.invoice_number_reservations)' INTO has_rows;
                        IF has_rows THEN
                            EXECUTE 'CREATE TABLE public."{{Backup}}" AS
                                     SELECT {{ColumnList}} FROM public.invoice_number_reservations';
                            EXECUTE 'ALTER TABLE public."{{Backup}}" ADD PRIMARY KEY ("Id")';
                        END IF;
                    END IF;
                END
                $copy$;
                """);

            // 2. Only then drop. The indexes go with the table.
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.invoice_number_reservations;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS public.invoice_number_reservations (
                    "Id" uuid NOT NULL,
                    "IdempotencyKey" character varying(128) NOT NULL,
                    "ShowroomId" uuid NOT NULL,
                    "FiscalYear" character varying(16) NOT NULL,
                    "DocumentType" character varying(32) NOT NULL,
                    "ReservedValue" bigint NOT NULL,
                    "FormattedNumber" character varying(128) NOT NULL,
                    "ReservedForReference" character varying(128),
                    "ReservedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_invoice_number_reservations" PRIMARY KEY ("Id")
                );
                """);

            migrationBuilder.Sql($$"""
                DO $restore$
                BEGIN
                    IF to_regclass('public."{{Backup}}"') IS NOT NULL THEN
                        EXECUTE 'INSERT INTO public.invoice_number_reservations ({{ColumnList}})
                                 SELECT {{ColumnList}} FROM public."{{Backup}}"
                                 ON CONFLICT ("Id") DO NOTHING';
                        EXECUTE 'DROP TABLE public."{{Backup}}"';
                    END IF;
                END
                $restore$;
                """);

            // The second name is the one EF gave it, cut at PostgreSQL's 63 characters.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_invoice_number_reservations_IdempotencyKey"
                    ON public.invoice_number_reservations ("IdempotencyKey");
                CREATE INDEX IF NOT EXISTS "IX_invoice_number_reservations_ShowroomId_FiscalYear_DocumentT~"
                    ON public.invoice_number_reservations ("ShowroomId", "FiscalYear", "DocumentType");
                """);
        }
    }
}
