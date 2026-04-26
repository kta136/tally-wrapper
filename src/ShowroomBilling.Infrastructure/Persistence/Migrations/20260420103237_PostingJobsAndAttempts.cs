using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShowroomBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PostingJobsAndAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tally_posting_jobs",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowroomId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseOwnerBridgeId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRemoteId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tally_posting_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tally_posting_attempts",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNo = table.Column<int>(type: "integer", nullable: false),
                    RemoteId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    XmlShape = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestExcerpt = table.Column<string>(type: "text", nullable: true),
                    ResponseExcerpt = table.Column<string>(type: "text", nullable: true),
                    OutcomeType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tally_posting_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tally_posting_attempts_tally_posting_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "public",
                        principalTable: "tally_posting_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tally_posting_attempts_JobId_AttemptNo",
                schema: "public",
                table: "tally_posting_attempts",
                columns: new[] { "JobId", "AttemptNo" });

            migrationBuilder.CreateIndex(
                name: "IX_tally_posting_attempts_RemoteId",
                schema: "public",
                table: "tally_posting_attempts",
                column: "RemoteId");

            migrationBuilder.CreateIndex(
                name: "IX_tally_posting_jobs_BillRevisionId",
                schema: "public",
                table: "tally_posting_jobs",
                column: "BillRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_tally_posting_jobs_IdempotencyKey",
                schema: "public",
                table: "tally_posting_jobs",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tally_posting_jobs_LeaseOwnerBridgeId_LeaseExpiresAtUtc",
                schema: "public",
                table: "tally_posting_jobs",
                columns: new[] { "LeaseOwnerBridgeId", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_tally_posting_jobs_State_NextAttemptAtUtc",
                schema: "public",
                table: "tally_posting_jobs",
                columns: new[] { "State", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tally_posting_attempts",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tally_posting_jobs",
                schema: "public");
        }
    }
}
