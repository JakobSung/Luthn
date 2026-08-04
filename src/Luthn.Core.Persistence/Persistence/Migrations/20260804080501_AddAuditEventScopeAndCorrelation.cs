using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEventScopeAndCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorKind",
                table: "audit_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<string>(
                name: "ActorUserId",
                table: "audit_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "audit_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "audit_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "unspecified");

            migrationBuilder.AddColumn<string>(
                name: "ScopeKind",
                table: "audit_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Workspace");

            migrationBuilder.AddColumn<string>(
                name: "SubjectType",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "audit_events",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ScopeKind_WorkspaceId_OccurredAt",
                table: "audit_events",
                columns: new[] { "ScopeKind", "WorkspaceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_WorkspaceId_CorrelationId",
                table: "audit_events",
                columns: new[] { "WorkspaceId", "CorrelationId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_audit_events_scope_workspace",
                table: "audit_events",
                sql: "(\"ScopeKind\" = 'Workspace' AND \"WorkspaceId\" <> '') OR (\"ScopeKind\" = 'Installation' AND \"WorkspaceId\" = '')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_audit_events_ScopeKind_WorkspaceId_OccurredAt",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_WorkspaceId_CorrelationId",
                table: "audit_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_audit_events_scope_workspace",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "ActorKind",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "ActorUserId",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "ScopeKind",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "SubjectType",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "audit_events");
        }
    }
}
