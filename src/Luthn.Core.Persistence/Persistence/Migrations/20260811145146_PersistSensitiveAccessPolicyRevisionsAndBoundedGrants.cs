using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistSensitiveAccessPolicyRevisionsAndBoundedGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PolicyRevision",
                table: "sensitive_access_requests",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "RequestTimeoutSeconds",
                table: "sensitive_access_requests",
                type: "integer",
                nullable: false,
                defaultValue: 600);

            migrationBuilder.CreateTable(
                name: "sensitive_access_policy_revisions",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    RequestTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    GrantDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaximumSuccessfulReads = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitive_access_policy_revisions", x => new { x.WorkspaceId, x.Revision });
                    table.CheckConstraint("CK_sensitive_access_policy_revisions_grant_duration", "\"GrantDurationSeconds\" BETWEEN 60 AND 3600");
                    table.CheckConstraint("CK_sensitive_access_policy_revisions_maximum_successful_reads", "\"MaximumSuccessfulReads\" BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_sensitive_access_policy_revisions_request_timeout", "\"RequestTimeoutSeconds\" BETWEEN 60 AND 3600");
                    table.CheckConstraint("CK_sensitive_access_policy_revisions_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_sensitive_access_policy_revisions_workspace_id", "\"WorkspaceId\" <> ''");
                });

            migrationBuilder.Sql(
                """
                INSERT INTO sensitive_access_policy_revisions
                    ("WorkspaceId", "Revision", "RequestTimeoutSeconds", "GrantDurationSeconds",
                     "MaximumSuccessfulReads", "CreatedAt", "CreatedBy")
                SELECT
                    request."WorkspaceId",
                    1,
                    600,
                    600,
                    1,
                    MIN(request."CreatedAt"),
                    'migration-backfill'
                FROM sensitive_access_requests AS request
                GROUP BY request."WorkspaceId";

                UPDATE sensitive_access_requests
                SET "PolicyRevision" = 1,
                    "RequestTimeoutSeconds" = 600;

                ALTER TABLE sensitive_access_requests
                    ALTER COLUMN "PolicyRevision" DROP DEFAULT,
                    ALTER COLUMN "RequestTimeoutSeconds" DROP DEFAULT;
                """);

            migrationBuilder.CreateTable(
                name: "sensitive_access_grants",
                columns: table => new
                {
                    SensitiveAccessRequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PolicyRevision = table.Column<int>(type: "integer", nullable: false),
                    GrantDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaximumSuccessfulReads = table.Column<int>(type: "integer", nullable: false),
                    SuccessfulReadCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitive_access_grants", x => x.SensitiveAccessRequestId);
                    table.CheckConstraint("CK_sensitive_access_grants_grant_duration", "\"GrantDurationSeconds\" BETWEEN 60 AND 3600");
                    table.CheckConstraint("CK_sensitive_access_grants_maximum_successful_reads", "\"MaximumSuccessfulReads\" BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_sensitive_access_grants_owner_user_id", "\"OwnerUserId\" <> ''");
                    table.CheckConstraint("CK_sensitive_access_grants_policy_revision", "\"PolicyRevision\" > 0");
                    table.CheckConstraint("CK_sensitive_access_grants_successful_read_count", "\"SuccessfulReadCount\" >= 0 AND \"SuccessfulReadCount\" <= \"MaximumSuccessfulReads\"");
                    table.CheckConstraint("CK_sensitive_access_grants_time_window", "\"StartsAt\" < \"ExpiresAt\"");
                    table.CheckConstraint("CK_sensitive_access_grants_workspace_id", "\"WorkspaceId\" <> ''");
                    table.ForeignKey(
                        name: "FK_sensitive_access_grants_sensitive_access_policy_revisions_W~",
                        columns: x => new { x.WorkspaceId, x.PolicyRevision },
                        principalTable: "sensitive_access_policy_revisions",
                        principalColumns: new[] { "WorkspaceId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sensitive_access_grants_sensitive_access_requests_Sensitive~",
                        column: x => x.SensitiveAccessRequestId,
                        principalTable: "sensitive_access_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO sensitive_access_grants
                    ("SensitiveAccessRequestId", "WorkspaceId", "OwnerUserId", "PolicyRevision",
                     "GrantDurationSeconds", "StartsAt", "ExpiresAt", "MaximumSuccessfulReads",
                     "SuccessfulReadCount")
                SELECT
                    request."Id",
                    request."WorkspaceId",
                    request."OwnerUserId",
                    1,
                    600,
                    COALESCE(request."DecidedAt", request."UpdatedAt", request."CreatedAt"),
                    COALESCE(request."DecidedAt", request."UpdatedAt", request."CreatedAt")
                        + INTERVAL '10 minutes',
                    1,
                    0
                FROM sensitive_access_requests AS request
                WHERE request."Status" = 'Approved';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_requests_WorkspaceId_PolicyRevision",
                table: "sensitive_access_requests",
                columns: new[] { "WorkspaceId", "PolicyRevision" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_sensitive_access_requests_policy_revision",
                table: "sensitive_access_requests",
                sql: "\"PolicyRevision\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_sensitive_access_requests_request_timeout",
                table: "sensitive_access_requests",
                sql: "\"RequestTimeoutSeconds\" BETWEEN 60 AND 3600");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_grants_WorkspaceId_OwnerUserId_ExpiresAt",
                table: "sensitive_access_grants",
                columns: new[] { "WorkspaceId", "OwnerUserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_grants_WorkspaceId_PolicyRevision",
                table: "sensitive_access_grants",
                columns: new[] { "WorkspaceId", "PolicyRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_policy_revisions_WorkspaceId_CreatedAt",
                table: "sensitive_access_policy_revisions",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_sensitive_access_requests_sensitive_access_policy_revisions~",
                table: "sensitive_access_requests",
                columns: new[] { "WorkspaceId", "PolicyRevision" },
                principalTable: "sensitive_access_policy_revisions",
                principalColumns: new[] { "WorkspaceId", "Revision" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sensitive_access_requests_sensitive_access_policy_revisions~",
                table: "sensitive_access_requests");

            migrationBuilder.DropTable(
                name: "sensitive_access_grants");

            migrationBuilder.DropTable(
                name: "sensitive_access_policy_revisions");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_access_requests_WorkspaceId_PolicyRevision",
                table: "sensitive_access_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sensitive_access_requests_policy_revision",
                table: "sensitive_access_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sensitive_access_requests_request_timeout",
                table: "sensitive_access_requests");

            migrationBuilder.DropColumn(
                name: "PolicyRevision",
                table: "sensitive_access_requests");

            migrationBuilder.DropColumn(
                name: "RequestTimeoutSeconds",
                table: "sensitive_access_requests");
        }
    }
}
