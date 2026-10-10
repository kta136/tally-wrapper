using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Drops six columns nothing reads and twelve indexes no query can need:
    /// <list type="bullet">
    /// <item><c>bills.CreatedBy</c> was never set.</item>
    /// <item><c>bill_revisions.TotalsJson</c> repeated what <c>SnapshotJson</c> and <c>GrandTotal</c> hold.</item>
    /// <item><c>RawJson</c> on the four Tally master tables held the raw XML of every row and was never returned.</item>
    /// <item>Indexes that are a prefix of another index, an index by showroom on tables only read by batch,
    /// and indexes on the two single-row tables.</item>
    /// </list>
    /// <para>
    /// <b>The data is copied aside first.</b> Before a column is dropped its values go
    /// into a <c>dropped_20261010_*</c> table keyed by the row's Id, in the same
    /// transaction, so this cannot run without the copy having been made.
    /// <see cref="Down"/> puts the columns and their values back. Once the release
    /// has settled, the copies can be removed with
    /// <c>DROP TABLE public."dropped_20261010_…"</c> (the six names are in <see cref="Backups"/>).
    /// </para>
    /// <para>
    /// Every statement is conditional. Earlier databases have been found with
    /// indexes missing or created by hand, and a migration that fails at start-up
    /// leaves the API in limited mode.
    /// </para>
    /// </summary>
    public partial class DropUnusedColumnsAndIndexes : Migration
    {
        /// <summary>Table, dropped column, column type, and the default <see cref="Down"/> restores it with (null: nullable).</summary>
        private static readonly (string Table, string Column, string Type, string Default)[] Columns =
        [
            ("bills", "CreatedBy", "uuid", null),
            ("bill_revisions", "TotalsJson", "jsonb", "'{}'::jsonb"),
            ("tally_company_snapshots", "RawJson", "jsonb", "'{}'::jsonb"),
            ("tally_ledger_snapshots", "RawJson", "jsonb", "'{}'::jsonb"),
            ("tally_stock_item_snapshots", "RawJson", "jsonb", "'{}'::jsonb"),
            ("tally_voucher_type_snapshots", "RawJson", "jsonb", "'{}'::jsonb"),
        ];

        /// <summary>Index name, table, and the column list <see cref="Down"/> recreates it with.</summary>
        private static readonly (string Name, string Table, string ColumnList)[] Indexes =
        [
            ("IX_bills_ShowroomId_State_CreatedAtUtc", "bills", "\"ShowroomId\", \"State\", \"CreatedAtUtc\""),
            ("IX_bill_revisions_BillDate", "bill_revisions", "\"BillDate\""),
            ("IX_cloud_settings_UpdatedAtUtc", "cloud_settings", "\"UpdatedAtUtc\""),
            ("IX_database_identity_value", "database_identity", "\"value\""),
            ("IX_tally_company_snapshots_BatchId", "tally_company_snapshots", "\"BatchId\""),
            ("IX_tally_company_snapshots_ShowroomId_Name", "tally_company_snapshots", "\"ShowroomId\", \"Name\""),
            ("IX_tally_ledger_snapshots_BatchId", "tally_ledger_snapshots", "\"BatchId\""),
            ("IX_tally_ledger_snapshots_ShowroomId_Name", "tally_ledger_snapshots", "\"ShowroomId\", \"Name\""),
            ("IX_tally_stock_item_snapshots_BatchId", "tally_stock_item_snapshots", "\"BatchId\""),
            ("IX_tally_stock_item_snapshots_ShowroomId_Name", "tally_stock_item_snapshots", "\"ShowroomId\", \"Name\""),
            ("IX_tally_voucher_type_snapshots_BatchId", "tally_voucher_type_snapshots", "\"BatchId\""),
            ("IX_tally_voucher_type_snapshots_ShowroomId_Name", "tally_voucher_type_snapshots", "\"ShowroomId\", \"Name\""),
        ];

        /// <summary>The copy tables this migration leaves behind, one per dropped column.</summary>
        public static IReadOnlyList<string> Backups { get; } =
            Columns.Select(column => BackupTable(column.Table, column.Column)).ToArray();

        private static string BackupTable(string table, string column) =>
            $"dropped_20261010_{table}_{ToSnakeCase(column)}";

        private static string ToSnakeCase(string name) =>
            string.Concat(name.Select((character, index) =>
                index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Copy the values aside. Only rows that hold something are kept:
            //    a NULL, or the empty object every row was created with, says nothing.
            foreach (var (table, column, _, _) in Columns)
            {
                var backup = BackupTable(table, column);
                migrationBuilder.Sql($$"""
                    DO $copy$
                    BEGIN
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'public' AND table_name = '{{table}}' AND column_name = '{{column}}')
                           AND to_regclass('public."{{backup}}"') IS NULL
                        THEN
                            EXECUTE 'CREATE TABLE public."{{backup}}" AS
                                     SELECT "Id", "{{column}}" FROM public."{{table}}"
                                     WHERE "{{column}}" IS NOT NULL AND "{{column}}"::text <> ''{}''';
                            EXECUTE 'ALTER TABLE public."{{backup}}" ADD PRIMARY KEY ("Id")';
                        END IF;
                    END
                    $copy$;
                    """);
            }

            // 2. Only then drop.
            foreach (var (name, _, _) in Indexes)
            {
                migrationBuilder.Sql($"""DROP INDEX IF EXISTS public."{name}";""");
            }

            foreach (var (table, column, _, _) in Columns)
            {
                migrationBuilder.Sql($"""ALTER TABLE public."{table}" DROP COLUMN IF EXISTS "{column}";""");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column, type, @default) in Columns)
            {
                var backup = BackupTable(table, column);
                var definition = @default is null ? type : $"{type} NOT NULL DEFAULT {@default}";
                migrationBuilder.Sql($"""ALTER TABLE public."{table}" ADD COLUMN IF NOT EXISTS "{column}" {definition};""");
                if (@default is not null)
                {
                    // The column never had a database default; the application supplied the value.
                    migrationBuilder.Sql($"""ALTER TABLE public."{table}" ALTER COLUMN "{column}" DROP DEFAULT;""");
                }

                migrationBuilder.Sql($$"""
                    DO $restore$
                    BEGIN
                        IF to_regclass('public."{{backup}}"') IS NOT NULL THEN
                            EXECUTE 'UPDATE public."{{table}}" AS target
                                     SET "{{column}}" = copy."{{column}}"
                                     FROM public."{{backup}}" AS copy
                                     WHERE target."Id" = copy."Id"';
                            EXECUTE 'DROP TABLE public."{{backup}}"';
                        END IF;
                    END
                    $restore$;
                    """);
            }

            foreach (var (name, table, columnList) in Indexes)
            {
                migrationBuilder.Sql($"""CREATE INDEX IF NOT EXISTS "{name}" ON public."{table}" ({columnList});""");
            }
        }
    }
}
