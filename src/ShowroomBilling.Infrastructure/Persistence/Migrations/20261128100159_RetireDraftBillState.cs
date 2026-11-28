using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetireDraftBillState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE public.bills DROP CONSTRAINT IF EXISTS "CK_bills_state";""");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM public.bills WHERE "State" = 'draft') THEN
                        CREATE TABLE IF NOT EXISTS public.dropped_20261128_bills_draft (
                            "Id" uuid PRIMARY KEY,
                            "CurrentRevisionId" uuid,
                            "UpdatedAtUtc" timestamptz NOT NULL
                        );
                        INSERT INTO public.dropped_20261128_bills_draft
                            SELECT "Id", "CurrentRevisionId", "UpdatedAtUtc" FROM public.bills WHERE "State" = 'draft'
                            ON CONFLICT ("Id") DO UPDATE SET
                                "CurrentRevisionId" = excluded."CurrentRevisionId", "UpdatedAtUtc" = excluded."UpdatedAtUtc";
                        UPDATE public.bills SET "State" = 'pending' WHERE "State" = 'draft';
                    END IF;
                END $$;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_bills_state",
                schema: "public",
                table: "bills",
                sql: "\"State\" IN ('pending', 'posting', 'posted', 'failed', 'reconciliation_required', 'revised', 'voided')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE public.bills DROP CONSTRAINT IF EXISTS "CK_bills_state";""");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bills_state",
                schema: "public",
                table: "bills",
                sql: "\"State\" IN ('draft', 'pending', 'posting', 'posted', 'failed', 'reconciliation_required', 'revised', 'voided')");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('public.dropped_20261128_bills_draft') IS NOT NULL THEN
                        UPDATE public.bills AS bill SET "State" = 'draft'
                          FROM public.dropped_20261128_bills_draft AS original
                         WHERE bill."Id" = original."Id" AND bill."State" = 'pending'
                           AND bill."UpdatedAtUtc" = original."UpdatedAtUtc"
                           AND bill."CurrentRevisionId" IS NOT DISTINCT FROM original."CurrentRevisionId";
                    END IF;
                END $$;
                """);
        }
    }
}
