using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceReadOptimizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql(@"
CREATE INDEX IF NOT EXISTS ""IX_bills_ShowroomId_State_CreatedAtUtc_Id""
ON public.bills (""ShowroomId"", ""State"", ""CreatedAtUtc"", ""Id"");");

            migrationBuilder.Sql(@"
CREATE INDEX IF NOT EXISTS ""IX_bill_revisions_BillDate_Id""
ON public.bill_revisions (""BillDate"", ""Id"");");

            migrationBuilder.Sql(@"
CREATE INDEX IF NOT EXISTS ""IX_audit_events_EntityType_EntityId_EventType_CreatedAtUtc""
ON public.audit_events (""EntityType"", ""EntityId"", ""EventType"", ""CreatedAtUtc"" DESC);");

            migrationBuilder.Sql(@"
CREATE INDEX IF NOT EXISTS ""IX_tally_company_snapshots_Name_trgm""
ON public.tally_company_snapshots USING gin (""Name"" gin_trgm_ops);");

            migrationBuilder.Sql(@"
CREATE INDEX IF NOT EXISTS ""IX_tally_ledger_snapshots_Name_trgm""
ON public.tally_ledger_snapshots USING gin (""Name"" gin_trgm_ops);");

            migrationBuilder.Sql(@"
CREATE INDEX IF NOT EXISTS ""IX_tally_stock_item_snapshots_Name_trgm""
ON public.tally_stock_item_snapshots USING gin (""Name"" gin_trgm_ops);");

            migrationBuilder.Sql(@"
CREATE INDEX IF NOT EXISTS ""IX_tally_voucher_type_snapshots_Name_trgm""
ON public.tally_voucher_type_snapshots USING gin (""Name"" gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS public.""IX_tally_voucher_type_snapshots_Name_trgm"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS public.""IX_tally_stock_item_snapshots_Name_trgm"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS public.""IX_tally_ledger_snapshots_Name_trgm"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS public.""IX_tally_company_snapshots_Name_trgm"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS public.""IX_audit_events_EntityType_EntityId_EventType_CreatedAtUtc"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS public.""IX_bill_revisions_BillDate_Id"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS public.""IX_bills_ShowroomId_State_CreatedAtUtc_Id"";");
        }
    }
}
