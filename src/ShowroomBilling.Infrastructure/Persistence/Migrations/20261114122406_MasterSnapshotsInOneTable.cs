using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The four lists read from Tally (companies, ledgers, stock items, voucher
    /// types) move from a batch table and a row table each into one table,
    /// <c>tally_masters</c>: one row per list, the list itself as JSON. A refresh
    /// replaces a whole list and nothing looks inside one, so the rows, their
    /// batches and the indexes on them bought nothing.
    /// <para>
    /// <b>Nothing is lost on the way.</b> The current list of each master is
    /// written into the new table, and each of the five old tables that holds
    /// rows is first copied whole to <c>dropped_20261114_&lt;table&gt;_rows</c>, in
    /// the same transaction. <see cref="Down"/> puts the old tables back from
    /// those copies; on a database that never had the old tables it rebuilds
    /// their rows from the lists. Once the release has settled the copies can be
    /// removed by hand (the names are in <see cref="Backups"/>).
    /// </para>
    /// <para>
    /// Every statement is conditional: a migration that fails at start-up
    /// leaves the API in limited mode.
    /// </para>
    /// <para>
    /// A build older than this one reads the old tables, so its Settings lists
    /// stay empty and "Refresh from Tally" fails on a database this has run on:
    /// the server and the database change together.
    /// </para>
    /// </summary>
    public partial class MasterSnapshotsInOneTable : Migration
    {
        private const string Batches = "tally_master_snapshot_batches";

        // Every bill and snapshot row of the one showroom carries this id (Showrooms.DefaultId).
        private const string DefaultShowroomId = "9b961fc2-035f-3dd3-43e0-4f8f136e7682";

        /// <summary>
        /// A row table, its master type, its columns beside Id/BatchId/ShowroomId/Name with their
        /// definitions, the JSON object an entry becomes, and how a row is read back out of one.
        /// </summary>
        private static readonly (string Table, string MasterType, string ForeignKey, string ExtraColumns, string ExtraDefinitions, string ToJson, string FromJson)[] RowTables =
        [
            ("tally_company_snapshots", "companies",
                "FK_tally_company_snapshots_tally_master_snapshot_batches_Batch~",
                ", \"IsActive\"",
                ", \"IsActive\" boolean NOT NULL",
                "jsonb_build_object('name', item.\"Name\", 'isActive', item.\"IsActive\")",
                ", COALESCE((item->>'isActive')::boolean, false)"),
            ("tally_ledger_snapshots", "ledgers",
                "FK_tally_ledger_snapshots_tally_master_snapshot_batches_BatchId",
                string.Empty,
                string.Empty,
                "jsonb_build_object('name', item.\"Name\")",
                string.Empty),
            ("tally_stock_item_snapshots", "stock_items",
                "FK_tally_stock_item_snapshots_tally_master_snapshot_batches_Ba~",
                ", \"Alias\", \"BaseUnit\", \"HsnCode\", \"Karat\"",
                ", \"Alias\" character varying(256), \"BaseUnit\" character varying(64), \"HsnCode\" character varying(32), \"Karat\" character varying(32)",
                "jsonb_build_object('name', item.\"Name\", 'baseUnit', item.\"BaseUnit\", 'hsnCode', item.\"HsnCode\")",
                ", NULL, left(item->>'baseUnit', 64), left(item->>'hsnCode', 32), NULL"),
            ("tally_voucher_type_snapshots", "voucher_types",
                "FK_tally_voucher_type_snapshots_tally_master_snapshot_batches_~",
                string.Empty,
                string.Empty,
                "jsonb_build_object('name', item.\"Name\")",
                string.Empty),
        ];

        private const string BatchColumns = "\"Id\", \"ShowroomId\", \"MasterType\", \"FetchedAtUtc\", \"Status\", \"ItemCount\"";

        /// <summary>The copy tables this migration leaves behind, one per old table that held rows.</summary>
        public static IReadOnlyList<string> Backups { get; } =
            RowTables.Select(row => BackupTable(row.Table)).Append(BackupTable(Batches)).ToArray();

        private static string BackupTable(string table) => $"dropped_20261114_{table}_rows";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS public.tally_masters (
                    "MasterType" character varying(64) NOT NULL,
                    "FetchedAtUtc" timestamp with time zone NOT NULL,
                    "ItemCount" integer NOT NULL,
                    "ItemsJson" jsonb NOT NULL,
                    CONSTRAINT "PK_tally_masters" PRIMARY KEY ("MasterType"));
                """);

            // 1. Each master's current list (its newest active batch) becomes one row.
            foreach (var row in RowTables)
            {
                migrationBuilder.Sql($$"""
                    DO $convert$
                    BEGIN
                        IF to_regclass('public."{{Batches}}"') IS NULL OR to_regclass('public."{{row.Table}}"') IS NULL THEN
                            RETURN;
                        END IF;

                        EXECUTE $sql$
                            INSERT INTO public.tally_masters ("MasterType", "FetchedAtUtc", "ItemCount", "ItemsJson")
                            SELECT batch."MasterType",
                                   batch."FetchedAtUtc",
                                   count(item."Id"),
                                   COALESCE(
                                       jsonb_agg({{row.ToJson}} ORDER BY lower(item."Name"), item."Name") FILTER (WHERE item."Id" IS NOT NULL),
                                       '[]'::jsonb)
                              FROM (SELECT "Id", "MasterType", "FetchedAtUtc"
                                      FROM public."{{Batches}}"
                                     WHERE "MasterType" = '{{row.MasterType}}' AND "Status" = 'active'
                                     ORDER BY "FetchedAtUtc" DESC
                                     LIMIT 1) AS batch
                              LEFT JOIN public."{{row.Table}}" AS item ON item."BatchId" = batch."Id"
                             GROUP BY batch."MasterType", batch."FetchedAtUtc"
                            ON CONFLICT ("MasterType") DO NOTHING
                        $sql$;
                    END
                    $convert$;
                    """);
            }

            // 2. Copy the old tables aside, whole, where they hold anything.
            foreach (var table in RowTables.Select(row => row.Table).Append(Batches))
            {
                migrationBuilder.Sql($$"""
                    DO $copy$
                    DECLARE
                        has_rows boolean;
                    BEGIN
                        IF to_regclass('public."{{table}}"') IS NULL OR to_regclass('public."{{BackupTable(table)}}"') IS NOT NULL THEN
                            RETURN;
                        END IF;

                        EXECUTE 'SELECT EXISTS (SELECT 1 FROM public."{{table}}")' INTO has_rows;
                        IF has_rows THEN
                            EXECUTE 'CREATE TABLE public."{{BackupTable(table)}}" AS TABLE public."{{table}}"';
                            EXECUTE 'ALTER TABLE public."{{BackupTable(table)}}" ADD PRIMARY KEY ("Id")';
                        END IF;
                    END
                    $copy$;
                    """);
            }

            // 3. Only then drop: the row tables first, they point at the batches.
            foreach (var table in RowTables.Select(row => row.Table).Append(Batches))
            {
                migrationBuilder.Sql($"""DROP TABLE IF EXISTS public."{table}";""");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                CREATE TABLE IF NOT EXISTS public."{Batches}" (
                    "Id" uuid NOT NULL,
                    "ShowroomId" uuid NOT NULL,
                    "MasterType" character varying(64) NOT NULL,
                    "FetchedAtUtc" timestamp with time zone NOT NULL,
                    "Status" character varying(32) NOT NULL,
                    "ItemCount" integer NOT NULL,
                    CONSTRAINT "PK_{Batches}" PRIMARY KEY ("Id"),
                    CONSTRAINT "CK_{Batches}_status" CHECK ("Status" IN ('active', 'superseded')));
                CREATE INDEX IF NOT EXISTS "IX_tally_master_snapshot_batches_ShowroomId_MasterType_Fetched~"
                    ON public."{Batches}" ("ShowroomId", "MasterType", "FetchedAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_tally_master_snapshot_batches_ShowroomId_MasterType_Status"
                    ON public."{Batches}" ("ShowroomId", "MasterType", "Status");
                """);

            foreach (var row in RowTables)
            {
                migrationBuilder.Sql($"""
                    CREATE TABLE IF NOT EXISTS public."{row.Table}" (
                        "Id" uuid NOT NULL,
                        "BatchId" uuid NOT NULL,
                        "ShowroomId" uuid NOT NULL,
                        "Name" character varying(256) NOT NULL{row.ExtraDefinitions},
                        CONSTRAINT "PK_{row.Table}" PRIMARY KEY ("Id"),
                        CONSTRAINT "{row.ForeignKey}" FOREIGN KEY ("BatchId")
                            REFERENCES public."{Batches}" ("Id") ON DELETE CASCADE);
                    CREATE INDEX IF NOT EXISTS "IX_{row.Table}_BatchId_Name" ON public."{row.Table}" ("BatchId", "Name");
                    CREATE INDEX IF NOT EXISTS "IX_{row.Table}_Name_trgm" ON public."{row.Table}" USING gin ("Name" gin_trgm_ops);
                    """);
            }

            // The copies made on the way up put the old rows back exactly. A database that was
            // never on the old tables has no copies: its rows are rebuilt from the lists.
            var rowRestores = string.Concat(RowTables.Select(row => $$"""

                            IF to_regclass('public."{{BackupTable(row.Table)}}"') IS NOT NULL THEN
                                EXECUTE 'INSERT INTO public."{{row.Table}}" ("Id", "BatchId", "ShowroomId", "Name"{{row.ExtraColumns}})
                                         SELECT "Id", "BatchId", "ShowroomId", "Name"{{row.ExtraColumns}} FROM public."{{BackupTable(row.Table)}}"
                                         ON CONFLICT ("Id") DO NOTHING';
                                EXECUTE 'DROP TABLE public."{{BackupTable(row.Table)}}"';
                            END IF;
                """));

            var rowRebuilds = string.Concat(RowTables.Select(row => $$"""

                            EXECUTE $sql$
                                INSERT INTO public."{{row.Table}}" ("Id", "BatchId", "ShowroomId", "Name"{{row.ExtraColumns}})
                                SELECT gen_random_uuid(), batch."Id", batch."ShowroomId", left(COALESCE(item->>'name', ''), 256){{row.FromJson}}
                                  FROM public.tally_masters AS master
                                  JOIN public."{{Batches}}" AS batch ON batch."MasterType" = master."MasterType"
                                 CROSS JOIN LATERAL jsonb_array_elements(master."ItemsJson") AS item
                                 WHERE master."MasterType" = '{{row.MasterType}}'
                            $sql$;
                """));

            migrationBuilder.Sql($$"""
                DO $restore$
                BEGIN
                    IF to_regclass('public."{{BackupTable(Batches)}}"') IS NOT NULL THEN
                        EXECUTE 'INSERT INTO public."{{Batches}}" ({{BatchColumns}})
                                 SELECT {{BatchColumns}} FROM public."{{BackupTable(Batches)}}"
                                 ON CONFLICT ("Id") DO NOTHING';
                        EXECUTE 'DROP TABLE public."{{BackupTable(Batches)}}"';
                {{rowRestores}}
                    ELSIF to_regclass('public.tally_masters') IS NOT NULL THEN
                        EXECUTE $sql$
                            INSERT INTO public."{{Batches}}" ({{BatchColumns}})
                            SELECT gen_random_uuid(), '{{DefaultShowroomId}}', "MasterType", "FetchedAtUtc", 'active', "ItemCount"
                              FROM public.tally_masters
                        $sql$;
                {{rowRebuilds}}
                    END IF;
                END
                $restore$;
                """);

            migrationBuilder.Sql("DROP TABLE IF EXISTS public.tally_masters;");
        }
    }
}
