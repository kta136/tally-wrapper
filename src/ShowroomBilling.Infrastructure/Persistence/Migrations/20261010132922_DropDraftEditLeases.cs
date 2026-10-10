using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Drops <c>draft_edit_leases</c>. The table was meant to hold a lock on a
    /// bill while a counter edited it, but no build of the Desktop ever took
    /// one, so nothing wrote it; the endpoints and the admin panel that read it
    /// are gone.
    /// <para>
    /// <b>Rows are copied aside first</b>, as in
    /// <see cref="DropUnusedColumnsAndIndexes"/>: if the table holds anything,
    /// it is copied to <see cref="Backup"/> in the same transaction before the
    /// drop, and <see cref="Down"/> puts the table, its indexes and those rows
    /// back. An empty table (the expected case) leaves no copy behind. Once the
    /// release has settled, remove a copy with
    /// <c>DROP TABLE IF EXISTS public."dropped_20261010_draft_edit_leases"</c>.
    /// </para>
    /// <para>
    /// Every statement is conditional: a migration that fails at start-up
    /// leaves the API in limited mode.
    /// </para>
    /// </summary>
    public partial class DropDraftEditLeases : Migration
    {
        /// <summary>The copy this migration leaves behind when the table held rows.</summary>
        public const string Backup = "dropped_20261010_draft_edit_leases";

        private const string ColumnList =
            "\"Id\", \"BillId\", \"ShowroomId\", \"OwnerActorId\", \"OwnerCounterName\", " +
            "\"AcquiredAtUtc\", \"ExpiresAtUtc\", \"RenewedAtUtc\", \"ReleasedAtUtc\"";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Copy the rows aside, if there are any.
            migrationBuilder.Sql($$"""
                DO $copy$
                DECLARE has_rows boolean;
                BEGIN
                    IF to_regclass('public.draft_edit_leases') IS NOT NULL
                       AND to_regclass('public."{{Backup}}"') IS NULL
                    THEN
                        EXECUTE 'SELECT EXISTS (SELECT 1 FROM public.draft_edit_leases)' INTO has_rows;
                        IF has_rows THEN
                            EXECUTE 'CREATE TABLE public."{{Backup}}" AS
                                     SELECT {{ColumnList}} FROM public.draft_edit_leases';
                            EXECUTE 'ALTER TABLE public."{{Backup}}" ADD PRIMARY KEY ("Id")';
                        END IF;
                    END IF;
                END
                $copy$;
                """);

            // 2. Only then drop. The indexes go with the table.
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.draft_edit_leases;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS public.draft_edit_leases (
                    "Id" uuid NOT NULL,
                    "BillId" uuid NOT NULL,
                    "ShowroomId" uuid NOT NULL,
                    "OwnerActorId" character varying(128) NOT NULL,
                    "OwnerCounterName" character varying(128),
                    "AcquiredAtUtc" timestamp with time zone NOT NULL,
                    "ExpiresAtUtc" timestamp with time zone NOT NULL,
                    "RenewedAtUtc" timestamp with time zone NOT NULL,
                    "ReleasedAtUtc" timestamp with time zone,
                    CONSTRAINT "PK_draft_edit_leases" PRIMARY KEY ("Id")
                );
                """);

            migrationBuilder.Sql($$"""
                DO $restore$
                BEGIN
                    IF to_regclass('public."{{Backup}}"') IS NOT NULL THEN
                        EXECUTE 'INSERT INTO public.draft_edit_leases ({{ColumnList}})
                                 SELECT {{ColumnList}} FROM public."{{Backup}}"
                                 ON CONFLICT ("Id") DO NOTHING';
                        EXECUTE 'DROP TABLE public."{{Backup}}"';
                    END IF;
                END
                $restore$;
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_draft_edit_leases_BillId"
                    ON public.draft_edit_leases ("BillId") WHERE "ReleasedAtUtc" IS NULL;
                CREATE INDEX IF NOT EXISTS "IX_draft_edit_leases_ExpiresAtUtc"
                    ON public.draft_edit_leases ("ExpiresAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_draft_edit_leases_ShowroomId_BillId"
                    ON public.draft_edit_leases ("ShowroomId", "BillId");
                """);
        }
    }
}
