using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes the index on a bill's number core unique, so that two bills of one
    /// fiscal year cannot hold the same core. Only the number's text was unique
    /// until now, and the text depends on the prefix, suffix and padding in
    /// Settings: after one of them changes, the same core formats to other text.
    /// <para>
    /// Every statement is conditional, because a migration that fails at
    /// start-up leaves the API in limited mode. A database that already holds
    /// two bills on one core keeps the plain index and says so with a warning;
    /// the app's own check under the sequence lock still applies. After the
    /// duplicates have been dealt with, the index is made unique by hand
    /// (docs/10 §1.1 has the two statements).
    /// </para>
    /// </summary>
    public partial class UniqueInvoiceCore : Migration
    {
        private const string Index = "IX_bills_ShowroomId_FiscalYear_InvoiceCore";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                DO $unique$
                DECLARE
                    repeated bigint;
                BEGIN
                    SELECT count(*) INTO repeated FROM (
                        SELECT 1
                        FROM public.bills
                        WHERE "InvoiceCore" IS NOT NULL
                        GROUP BY "ShowroomId", "FiscalYear", "InvoiceCore"
                        HAVING count(*) > 1) AS held_twice;

                    IF repeated > 0 THEN
                        RAISE WARNING 'bills: % number core(s) are held by more than one bill, so the index on the core was left as it is (not unique). Renumber or delete the extra bills, then make it unique by hand.', repeated;
                    ELSE
                        DROP INDEX IF EXISTS public."{{Index}}";
                        CREATE UNIQUE INDEX IF NOT EXISTS "{{Index}}"
                            ON public.bills ("ShowroomId", "FiscalYear", "InvoiceCore")
                            WHERE "InvoiceCore" IS NOT NULL;
                    END IF;
                END
                $unique$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                DROP INDEX IF EXISTS public."{{Index}}";
                CREATE INDEX IF NOT EXISTS "{{Index}}"
                    ON public.bills ("ShowroomId", "FiscalYear", "InvoiceCore");
                """);
        }
    }
}
