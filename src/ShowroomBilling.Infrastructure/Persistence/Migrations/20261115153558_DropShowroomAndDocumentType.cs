using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Drops <c>ShowroomId</c> from the five tables that carried it (<c>bills</c>,
    /// <c>invoice_sequences</c>, <c>admin_passcodes</c>, <c>admin_sessions</c>, <c>print_assets</c>) and
    /// <c>DocumentType</c> from <c>invoice_sequences</c>. The application runs for one showroom and
    /// numbers one kind of document, so every row held the same two values; a sequence is now keyed by
    /// its fiscal year alone, and the indexes that began with the showroom begin with their next column.
    /// <para>
    /// <b>The data is copied aside first.</b> The rows that held another showroom's id go into a
    /// <c>dropped_20261115_&lt;table&gt;</c> table keyed by the row's Id, in the same transaction, and
    /// a warning says how many there were: without the column they are rows like any other. For
    /// <c>invoice_sequences</c>, whose key changes, the whole table is copied when any of its rows held
    /// another showroom or document type, and the rows of one fiscal year then become one row that
    /// stands where the furthest of them stood, so that no number is issued twice.
    /// <see cref="Down"/> puts the columns, their values, the key and the indexes back. Once the release
    /// has settled, the copies can be removed with <c>DROP TABLE public."dropped_20261115_…"</c> (the
    /// names are in <see cref="Backups"/>).
    /// </para>
    /// <para>
    /// Every statement is conditional, because a migration that fails at start-up leaves the API in
    /// limited mode. For the same reason an index that should be unique is made a plain one, with a
    /// warning, on a database where two rows already hold the same value.
    /// </para>
    /// <para>
    /// A build older than this one still maps these columns, so it cannot read or save a bill on a
    /// database this has run on: the server and the database change together.
    /// </para>
    /// </summary>
    public partial class DropShowroomAndDocumentType : Migration
    {
        /// <summary>What every row held: the id of the one showroom, and the one kind of document.</summary>
        private const string TheShowroom = "9b961fc2-035f-3dd3-43e0-4f8f136e7682";
        private const string SalesInvoice = "sales_invoice";

        private const string Sequences = "invoice_sequences";
        private static readonly string[] KeyedById = ["bills", "admin_passcodes", "admin_sessions", "print_assets"];

        /// <summary>The copy tables this migration leaves behind, one per table it drops a column from.</summary>
        public static IReadOnlyList<string> Backups { get; } = [.. KeyedById.Select(BackupTable), BackupTable(Sequences)];

        private static string BackupTable(string table) => $"dropped_20261115_{table}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Copy aside: the rows that held another showroom's id.
            foreach (var table in KeyedById)
            {
                migrationBuilder.Sql($$"""
                    DO $copy$
                    DECLARE
                        others bigint;
                    BEGIN
                        IF to_regclass('public."{{BackupTable(table)}}"') IS NOT NULL
                           OR NOT EXISTS (
                               SELECT 1 FROM information_schema.columns
                                WHERE table_schema = 'public' AND table_name = '{{table}}' AND column_name = 'ShowroomId') THEN
                            RETURN;
                        END IF;

                        EXECUTE 'CREATE TABLE public."{{BackupTable(table)}}" AS
                                 SELECT "Id", "ShowroomId" FROM public."{{table}}"
                                  WHERE "ShowroomId" IS DISTINCT FROM ''{{TheShowroom}}''';
                        EXECUTE 'ALTER TABLE public."{{BackupTable(table)}}" ADD PRIMARY KEY ("Id")';

                        EXECUTE 'SELECT count(*) FROM public."{{BackupTable(table)}}"' INTO others;
                        IF others > 0 THEN
                            RAISE WARNING '{{table}}: % row(s) held the id of another showroom and are now rows like any other. Their ids are in {{BackupTable(table)}}.', others;
                        END IF;
                    END
                    $copy$;
                    """);
            }

            //    The sequences have no Id and their key changes, so the whole table is copied when any
            //    row held something else. A column that is already gone is left out of the question.
            migrationBuilder.Sql($$"""
                DO $copy$
                DECLARE
                    differs text;
                    others bigint;
                BEGIN
                    IF to_regclass('public."{{BackupTable(Sequences)}}"') IS NOT NULL THEN
                        RETURN;
                    END IF;

                    SELECT string_agg(format('%I IS DISTINCT FROM %L', dropped.name, dropped.every_row_held), ' OR ')
                      INTO differs
                      FROM (VALUES ('ShowroomId', '{{TheShowroom}}'), ('DocumentType', '{{SalesInvoice}}')) AS dropped(name, every_row_held)
                     WHERE EXISTS (
                           SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'public' AND table_name = '{{Sequences}}' AND column_name::text = dropped.name);

                    IF differs IS NULL THEN
                        RETURN;
                    END IF;

                    EXECUTE format(
                        'CREATE TABLE public.%I AS SELECT * FROM public.%I WHERE EXISTS (SELECT 1 FROM public.%I WHERE %s)',
                        '{{BackupTable(Sequences)}}', '{{Sequences}}', '{{Sequences}}', differs);

                    EXECUTE format('SELECT count(*) FROM public.%I', '{{BackupTable(Sequences)}}') INTO others;
                    IF others > 0 THEN
                        RAISE WARNING '{{Sequences}}: a row held another showroom or document type, so the % row(s) as they were are in {{BackupTable(Sequences)}}. The rows of one fiscal year are now one row.', others;
                    END IF;
                END
                $copy$;
                """);

            // 2. One sequence row a fiscal year, standing where the furthest of that year's rows stood.
            //    (Nothing to do where a year has one row, which is every database the application wrote.)
            migrationBuilder.Sql($"""
                UPDATE public."{Sequences}" AS kept
                   SET "NextValue" = furthest.next_value, "MinNextValue" = furthest.min_next_value
                  FROM (SELECT "FiscalYear", max("NextValue") AS next_value, max("MinNextValue") AS min_next_value
                          FROM public."{Sequences}"
                         GROUP BY "FiscalYear"
                        HAVING count(*) > 1) AS furthest
                 WHERE kept."FiscalYear" = furthest."FiscalYear";

                DELETE FROM public."{Sequences}" AS extra
                 USING public."{Sequences}" AS kept
                 WHERE extra."FiscalYear" = kept."FiscalYear" AND extra.ctid > kept.ctid;
                """);

            // 3. Only then drop. A column takes the key and the indexes it is part of with it; the
            //    indexes are named all the same, so that it is plain here what goes.
            foreach (var index in new[]
                     {
                         "IX_bills_ShowroomId_State_CreatedAtUtc_Id",
                         "IX_bills_ShowroomId_CreatedAtUtc",
                         "IX_bills_ShowroomId_FiscalYear_InvoiceNumber",
                         "IX_bills_ShowroomId_FiscalYear_InvoiceCore",
                         "IX_admin_passcodes_ShowroomId",
                         "IX_admin_sessions_ShowroomId_ExpiresAtUtc",
                         "IX_print_assets_ShowroomId_AssetKind",
                     })
            {
                migrationBuilder.Sql($"""DROP INDEX IF EXISTS public."{index}";""");
            }

            foreach (var table in KeyedById.Append(Sequences))
            {
                migrationBuilder.Sql($"""ALTER TABLE public."{table}" DROP COLUMN IF EXISTS "ShowroomId";""");
            }

            migrationBuilder.Sql($"""ALTER TABLE public."{Sequences}" DROP COLUMN IF EXISTS "DocumentType";""");

            // 4. The key and the indexes without the showroom.
            migrationBuilder.Sql($"""
                DO $key$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                         WHERE conrelid = 'public."{Sequences}"'::regclass AND contype = 'p') THEN
                        ALTER TABLE public."{Sequences}" ADD CONSTRAINT "PK_invoice_sequences" PRIMARY KEY ("FiscalYear");
                    END IF;
                END
                $key$;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_bills_State_CreatedAtUtc_Id" ON public.bills ("State", "CreatedAtUtc", "Id");
                CREATE INDEX IF NOT EXISTS "IX_bills_CreatedAtUtc" ON public.bills ("CreatedAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_admin_sessions_ExpiresAtUtc" ON public.admin_sessions ("ExpiresAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_print_assets_AssetKind" ON public.print_assets ("AssetKind");
                """);
            migrationBuilder.Sql(UniqueUnlessRepeated(
                "IX_bills_FiscalYear_InvoiceNumber", "bills", ["FiscalYear", "InvoiceNumber"], "\"InvoiceNumber\" IS NOT NULL"));
            migrationBuilder.Sql(UniqueUnlessRepeated(
                "IX_bills_FiscalYear_InvoiceCore", "bills", ["FiscalYear", "InvoiceCore"], "\"InvoiceCore\" IS NOT NULL"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var index in new[]
                     {
                         "IX_bills_State_CreatedAtUtc_Id",
                         "IX_bills_CreatedAtUtc",
                         "IX_bills_FiscalYear_InvoiceNumber",
                         "IX_bills_FiscalYear_InvoiceCore",
                         "IX_admin_sessions_ExpiresAtUtc",
                         "IX_print_assets_AssetKind",
                     })
            {
                migrationBuilder.Sql($"""DROP INDEX IF EXISTS public."{index}";""");
            }

            // The columns come back holding what every row held. They never had a database default;
            // the application supplied the value.
            foreach (var table in KeyedById.Append(Sequences))
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE public."{table}" ADD COLUMN IF NOT EXISTS "ShowroomId" uuid NOT NULL DEFAULT '{TheShowroom}';
                    ALTER TABLE public."{table}" ALTER COLUMN "ShowroomId" DROP DEFAULT;
                    """);
            }

            migrationBuilder.Sql($"""
                ALTER TABLE public."{Sequences}" ADD COLUMN IF NOT EXISTS "DocumentType" character varying(32) NOT NULL DEFAULT '{SalesInvoice}';
                ALTER TABLE public."{Sequences}" ALTER COLUMN "DocumentType" DROP DEFAULT;
                """);

            // Put back whatever the copies hold, then remove them.
            foreach (var table in KeyedById)
            {
                migrationBuilder.Sql($$"""
                    DO $restore$
                    BEGIN
                        IF to_regclass('public."{{BackupTable(table)}}"') IS NULL THEN
                            RETURN;
                        END IF;

                        EXECUTE 'UPDATE public."{{table}}" AS target SET "ShowroomId" = copy."ShowroomId"
                                   FROM public."{{BackupTable(table)}}" AS copy WHERE target."Id" = copy."Id"';
                        EXECUTE 'DROP TABLE public."{{BackupTable(table)}}"';
                    END
                    $restore$;
                    """);
            }

            // The sequences: the old key, and the rows of another showroom or document type as they
            // were. The row each fiscal year has now stays the one showroom's, standing where it stands
            // today, because numbers have been issued from it since. (A copy made from a table that had
            // already lost one of the two columns cannot be put back as rows, and is left where it is.)
            migrationBuilder.Sql($$"""
                ALTER TABLE public."{{Sequences}}" DROP CONSTRAINT IF EXISTS "PK_invoice_sequences";

                DO $restore$
                BEGIN
                    IF to_regclass('public."{{BackupTable(Sequences)}}"') IS NULL THEN
                        RETURN;
                    END IF;

                    IF (SELECT count(*) FROM information_schema.columns
                         WHERE table_schema = 'public' AND table_name = '{{BackupTable(Sequences)}}'
                           AND column_name IN ('ShowroomId', 'DocumentType')) < 2 THEN
                        RAISE WARNING '{{BackupTable(Sequences)}} does not hold both dropped columns, so its rows were not put back. It was left where it is.';
                        RETURN;
                    END IF;

                    EXECUTE 'INSERT INTO public."{{Sequences}}" ("ShowroomId", "FiscalYear", "DocumentType", "NextValue", "MinNextValue", "UpdatedAtUtc")
                             SELECT copy."ShowroomId", copy."FiscalYear", copy."DocumentType", copy."NextValue", copy."MinNextValue", copy."UpdatedAtUtc"
                               FROM public."{{BackupTable(Sequences)}}" AS copy
                              WHERE (copy."ShowroomId" <> ''{{TheShowroom}}'' OR copy."DocumentType" <> ''{{SalesInvoice}}'')
                                AND NOT EXISTS (
                                    SELECT 1 FROM public."{{Sequences}}" AS present
                                     WHERE present."ShowroomId" = copy."ShowroomId"
                                       AND present."FiscalYear" = copy."FiscalYear"
                                       AND present."DocumentType" = copy."DocumentType")';
                    EXECUTE 'DROP TABLE public."{{BackupTable(Sequences)}}"';
                END
                $restore$;

                ALTER TABLE public."{{Sequences}}" ADD CONSTRAINT "PK_invoice_sequences" PRIMARY KEY ("ShowroomId", "FiscalYear", "DocumentType");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_bills_ShowroomId_State_CreatedAtUtc_Id" ON public.bills ("ShowroomId", "State", "CreatedAtUtc", "Id");
                CREATE INDEX IF NOT EXISTS "IX_bills_ShowroomId_CreatedAtUtc" ON public.bills ("ShowroomId", "CreatedAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_admin_sessions_ShowroomId_ExpiresAtUtc" ON public.admin_sessions ("ShowroomId", "ExpiresAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_print_assets_ShowroomId_AssetKind" ON public.print_assets ("ShowroomId", "AssetKind");
                """);
            migrationBuilder.Sql(UniqueUnlessRepeated(
                "IX_bills_ShowroomId_FiscalYear_InvoiceNumber", "bills", ["ShowroomId", "FiscalYear", "InvoiceNumber"], "\"InvoiceNumber\" IS NOT NULL"));
            migrationBuilder.Sql(UniqueUnlessRepeated(
                "IX_bills_ShowroomId_FiscalYear_InvoiceCore", "bills", ["ShowroomId", "FiscalYear", "InvoiceCore"], "\"InvoiceCore\" IS NOT NULL"));
            migrationBuilder.Sql(UniqueUnlessRepeated(
                "IX_admin_passcodes_ShowroomId", "admin_passcodes", ["ShowroomId"], filter: null));
        }

        /// <summary>
        /// Creates a unique index, or a plain one with a warning where two rows already hold the same
        /// values: a unique index that cannot be built would fail the migration. Rows with a NULL in
        /// one of the columns are left out of the count, as a unique index leaves them out.
        /// </summary>
        private static string UniqueUnlessRepeated(string index, string table, string[] columns, string filter)
        {
            var quoted = string.Join(", ", columns.Select(column => $"\"{column}\""));
            var counted = string.Join(" AND ", columns.Select(column => $"\"{column}\" IS NOT NULL"));
            var where = filter is null ? string.Empty : $" WHERE {filter}";

            return $$"""
                DO $unique$
                DECLARE
                    repeated bigint;
                BEGIN
                    IF to_regclass('public."{{index}}"') IS NOT NULL THEN
                        RETURN;
                    END IF;

                    SELECT count(*) INTO repeated FROM (
                        SELECT 1
                        FROM public."{{table}}"
                        WHERE {{counted}}
                        GROUP BY {{quoted}}
                        HAVING count(*) > 1) AS held_twice;

                    IF repeated > 0 THEN
                        RAISE WARNING '{{table}}: % value(s) of ({{string.Join(", ", columns)}}) are held by more than one row, so the index {{index}} was made a plain one (not unique). Deal with the extra rows, then make it unique by hand.', repeated;
                        CREATE INDEX "{{index}}" ON public."{{table}}" ({{quoted}}){{where}};
                    ELSE
                        CREATE UNIQUE INDEX "{{index}}" ON public."{{table}}" ({{quoted}}){{where}};
                    END IF;
                END
                $unique$;
                """;
        }
    }
}
