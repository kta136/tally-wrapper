using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Drops eleven columns that were written and never read:
    /// <list type="bullet">
    /// <item><c>bills.CounterId</c> was always NULL, <c>bills.BillType</c> always <c>sales</c>, and
    /// <c>bills.VoidedAtUtc</c> repeated the time of the bill's <c>bill.voided</c> audit row.</item>
    /// <item><c>bill_revisions.SubmittedAtUtc</c> and <c>FinalizedAtUtc</c> repeated the time of the push,
    /// which the audit rows hold.</item>
    /// <item><c>Parent</c>, <c>PrimaryGroup</c>, <c>IsRevenue</c> and <c>Gstin</c> of a Tally ledger, and
    /// <c>ParentType</c> and <c>IsDeemedPositive</c> of a Tally voucher type: only the names are used.</item>
    /// </list>
    /// <para>
    /// <b>The data is copied aside first.</b> Before a table's columns are dropped, the rows that hold
    /// something in them go into a <c>dropped_20261114_&lt;table&gt;</c> table keyed by the row's Id, in
    /// the same transaction, so this cannot run without the copy having been made. (One copy per table
    /// and not per column: a per-column name for the longest pair does not fit PostgreSQL's 63
    /// characters.) <see cref="Down"/> puts the columns and their values back. Once the release has
    /// settled, the copies can be removed with <c>DROP TABLE public."dropped_20261114_…"</c> (the four
    /// names are in <see cref="Backups"/>).
    /// </para>
    /// <para>
    /// Every statement is conditional, and the copy takes whichever of the columns are there: a
    /// migration that fails at start-up leaves the API in limited mode.
    /// </para>
    /// <para>
    /// A build older than this one still maps these columns, so it cannot read or save a bill on a
    /// database this has run on: the server and the database change together.
    /// </para>
    /// </summary>
    public partial class DropUnreadColumns : Migration
    {
        /// <summary>
        /// Table, dropped column, column type, and the value every row had when the application set
        /// nothing (null: the column is nullable). A row is copied when a column differs from that.
        /// </summary>
        private static readonly (string Table, string Column, string Type, string Default)[] Columns =
        [
            ("bills", "CounterId", "uuid", null),
            ("bills", "BillType", "character varying(32)", "'sales'"),
            ("bills", "VoidedAtUtc", "timestamp with time zone", null),
            ("bill_revisions", "SubmittedAtUtc", "timestamp with time zone", null),
            ("bill_revisions", "FinalizedAtUtc", "timestamp with time zone", null),
            ("tally_ledger_snapshots", "Parent", "character varying(256)", null),
            ("tally_ledger_snapshots", "PrimaryGroup", "character varying(256)", null),
            ("tally_ledger_snapshots", "IsRevenue", "boolean", "false"),
            ("tally_ledger_snapshots", "Gstin", "character varying(32)", null),
            ("tally_voucher_type_snapshots", "ParentType", "character varying(256)", null),
            ("tally_voucher_type_snapshots", "IsDeemedPositive", "boolean", "false"),
        ];

        private static readonly string[] Tables = Columns.Select(column => column.Table).Distinct().ToArray();

        /// <summary>The copy tables this migration leaves behind, one per table it drops columns from.</summary>
        public static IReadOnlyList<string> Backups { get; } = Tables.Select(BackupTable).ToArray();

        private static string BackupTable(string table) => $"dropped_20261114_{table}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Copy the values aside: the rows where a column holds something other than what
            //    every row was created with. Columns that are already gone are left out of the copy.
            foreach (var table in Tables)
            {
                var columns = string.Join(
                    ", ",
                    Columns.Where(column => column.Table == table).Select(column =>
                    {
                        var holdsSomething = column.Default is null
                            ? $"\"{column.Column}\" IS NOT NULL"
                            : $"\"{column.Column}\" IS DISTINCT FROM {column.Default}";
                        return $"('{column.Column}', '{holdsSomething.Replace("'", "''")}')";
                    }));

                migrationBuilder.Sql($$"""
                    DO $copy$
                    DECLARE
                        kept text;
                        held text;
                    BEGIN
                        IF to_regclass('public."{{BackupTable(table)}}"') IS NOT NULL THEN
                            RETURN;
                        END IF;

                        SELECT string_agg(format('%I', dropped.name), ', '), string_agg(dropped.holds_something, ' OR ')
                          INTO kept, held
                          FROM (VALUES {{columns}}) AS dropped(name, holds_something)
                         WHERE EXISTS (
                               SELECT 1 FROM information_schema.columns
                                WHERE table_schema = 'public' AND table_name = '{{table}}' AND column_name::text = dropped.name);

                        IF kept IS NOT NULL THEN
                            EXECUTE format(
                                'CREATE TABLE public.%I AS SELECT "Id", %s FROM public.%I WHERE %s',
                                '{{BackupTable(table)}}', kept, '{{table}}', held);
                            EXECUTE format('ALTER TABLE public.%I ADD PRIMARY KEY ("Id")', '{{BackupTable(table)}}');
                        END IF;
                    END
                    $copy$;
                    """);
            }

            // 2. Only then drop.
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
                var definition = @default is null ? type : $"{type} NOT NULL DEFAULT {@default}";
                migrationBuilder.Sql($"""ALTER TABLE public."{table}" ADD COLUMN IF NOT EXISTS "{column}" {definition};""");
                if (@default is not null)
                {
                    // The column never had a database default; the application supplied the value.
                    migrationBuilder.Sql($"""ALTER TABLE public."{table}" ALTER COLUMN "{column}" DROP DEFAULT;""");
                }
            }

            // Put back whatever the copy holds, then remove the copy.
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($$"""
                    DO $restore$
                    DECLARE
                        assignments text;
                    BEGIN
                        IF to_regclass('public."{{BackupTable(table)}}"') IS NULL THEN
                            RETURN;
                        END IF;

                        SELECT string_agg(format('%1$I = copy.%1$I', column_name::text), ', ')
                          INTO assignments
                          FROM information_schema.columns
                         WHERE table_schema = 'public' AND table_name = '{{BackupTable(table)}}' AND column_name::text <> 'Id';

                        IF assignments IS NOT NULL THEN
                            EXECUTE format(
                                'UPDATE public.%I AS target SET %s FROM public.%I AS copy WHERE target."Id" = copy."Id"',
                                '{{table}}', assignments, '{{BackupTable(table)}}');
                        END IF;

                        EXECUTE format('DROP TABLE public.%I', '{{BackupTable(table)}}');
                    END
                    $restore$;
                    """);
            }
        }
    }
}
