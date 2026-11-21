using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>BatchHoursJson</c> to <c>cloud_settings</c>: the shop hours a batch of the Batch Data
    /// Scheduler keeps to (opening and closing time, the weekly closed day, the gap between bills),
    /// as JSON. Every row gets <c>{}</c>, which reads as no hours set.
    /// <para>
    /// It only adds a column, and the column has a default in the database, so a build older than
    /// this one, which does not name the column, reads and writes the table as before: the server
    /// and the database need not change together.
    /// </para>
    /// <para>
    /// Both statements are conditional, because a migration that fails at start-up leaves the API
    /// in limited mode. <see cref="Down"/> drops the column and with it the hours that were saved;
    /// they are set again in Settings → Admin.
    /// </para>
    /// </summary>
    public partial class AddBatchHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public.cloud_settings ADD COLUMN IF NOT EXISTS "BatchHoursJson" text NOT NULL DEFAULT '{}';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public.cloud_settings DROP COLUMN IF EXISTS "BatchHoursJson";
                """);
        }
    }
}
