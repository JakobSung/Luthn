using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_wiki_proposals_AllowsAgentContext_Sensitivity_CreatedAt",
                table: "wiki_proposals",
                columns: new[] { "AllowsAgentContext", "Sensitivity", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_memory_items_AllowsAgentContext_Sensitivity_Visibili~",
                table: "shared_memory_items",
                columns: new[] { "AllowsAgentContext", "Sensitivity", "Visibility", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_requests_Status_UpdatedAt",
                table: "sensitive_access_requests",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_SubjectId_OccurredAt",
                table: "audit_events",
                columns: new[] { "SubjectId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wiki_proposals_AllowsAgentContext_Sensitivity_CreatedAt",
                table: "wiki_proposals");

            migrationBuilder.DropIndex(
                name: "IX_shared_memory_items_AllowsAgentContext_Sensitivity_Visibili~",
                table: "shared_memory_items");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_access_requests_Status_UpdatedAt",
                table: "sensitive_access_requests");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_SubjectId_OccurredAt",
                table: "audit_events");
        }
    }
}
