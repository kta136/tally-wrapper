using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>BankChargesLedger</c> to <c>cloud_settings</c>: the Tally ledger a bill's bank charges
    /// are credited to. Every row gets <c>''</c>, which reads as none chosen yet; only a bill with
    /// bank charges needs one, and its push says so when there is none.
    /// <para>
    /// It only adds a column, and the column has a default in the database, so a build older than
    /// this one, which does not name the column, reads and writes the table as before: the
    /// database may be migrated before the server is replaced.
    /// </para>
    /// <para>
    /// Both statements are conditional, because a migration that fails at start-up leaves the API
    /// in limited mode. <see cref="Down"/> drops the column and with it the ledger that was chosen;
    /// it is chosen again in Settings → Ledgers.
    /// </para>
    /// </summary>
    public partial class AddBankChargesLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public.cloud_settings ADD COLUMN IF NOT EXISTS "BankChargesLedger" character varying(256) NOT NULL DEFAULT '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public.cloud_settings DROP COLUMN IF EXISTS "BankChargesLedger";
                """);
        }
    }
}
