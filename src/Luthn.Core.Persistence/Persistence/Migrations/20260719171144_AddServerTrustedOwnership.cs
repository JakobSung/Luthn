using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServerTrustedOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wiki_proposals_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals");

            migrationBuilder.DropIndex(
                name: "IX_shared_memory_items_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "wiki_proposals",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "local-owner");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "source_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "local-owner");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "shared_memory_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "local-owner");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "sensitive_record_references",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "local-owner");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "sensitive_access_requests",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "local-owner");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "safe_projection_sync_outbox",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "local-owner");

            migrationBuilder.AddColumn<string>(
                name: "AuthenticatedUserId",
                table: "collection_provenance",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "local-owner");

            DropBackfillDefaults(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "IX_wiki_proposals_OwnerUserId_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals",
                columns: new[] { "OwnerUserId", "ProjectKey", "TaskKey", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_wiki_proposals_owner_user_id",
                table: "wiki_proposals",
                sql: "\"OwnerUserId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_source_events_OwnerUserId",
                table: "source_events",
                column: "OwnerUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_source_events_owner_user_id",
                table: "source_events",
                sql: "\"OwnerUserId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_shared_memory_items_OwnerUserId_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items",
                columns: new[] { "OwnerUserId", "ProjectKey", "TaskKey", "UpdatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_memory_items_owner_user_id",
                table: "shared_memory_items",
                sql: "\"OwnerUserId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_record_references_OwnerUserId",
                table: "sensitive_record_references",
                column: "OwnerUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_sensitive_record_references_owner_user_id",
                table: "sensitive_record_references",
                sql: "\"OwnerUserId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_requests_OwnerUserId_Status_UpdatedAt",
                table: "sensitive_access_requests",
                columns: new[] { "OwnerUserId", "Status", "UpdatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_sensitive_access_requests_owner_user_id",
                table: "sensitive_access_requests",
                sql: "\"OwnerUserId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_safe_projection_sync_outbox_OwnerUserId_State_CreatedAt",
                table: "safe_projection_sync_outbox",
                columns: new[] { "OwnerUserId", "State", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_safe_projection_sync_outbox_owner_user_id",
                table: "safe_projection_sync_outbox",
                sql: "\"OwnerUserId\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_collection_provenance_authenticated_user_id",
                table: "collection_provenance",
                sql: "\"AuthenticatedUserId\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wiki_proposals_OwnerUserId_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_wiki_proposals_owner_user_id",
                table: "wiki_proposals");

            migrationBuilder.DropIndex(
                name: "IX_source_events_OwnerUserId",
                table: "source_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_source_events_owner_user_id",
                table: "source_events");

            migrationBuilder.DropIndex(
                name: "IX_shared_memory_items_OwnerUserId_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_memory_items_owner_user_id",
                table: "shared_memory_items");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_record_references_OwnerUserId",
                table: "sensitive_record_references");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sensitive_record_references_owner_user_id",
                table: "sensitive_record_references");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_access_requests_OwnerUserId_Status_UpdatedAt",
                table: "sensitive_access_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sensitive_access_requests_owner_user_id",
                table: "sensitive_access_requests");

            migrationBuilder.DropIndex(
                name: "IX_safe_projection_sync_outbox_OwnerUserId_State_CreatedAt",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_safe_projection_sync_outbox_owner_user_id",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_collection_provenance_authenticated_user_id",
                table: "collection_provenance");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "wiki_proposals");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "source_events");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "sensitive_record_references");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "sensitive_access_requests");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropColumn(
                name: "AuthenticatedUserId",
                table: "collection_provenance");

            migrationBuilder.CreateIndex(
                name: "IX_wiki_proposals_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals",
                columns: new[] { "ProjectKey", "TaskKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_memory_items_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items",
                columns: new[] { "ProjectKey", "TaskKey", "UpdatedAt" });
        }

        private static void DropBackfillDefaults(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in new[]
            {
                ("wiki_proposals", "OwnerUserId"),
                ("source_events", "OwnerUserId"),
                ("shared_memory_items", "OwnerUserId"),
                ("sensitive_record_references", "OwnerUserId"),
                ("sensitive_access_requests", "OwnerUserId"),
                ("safe_projection_sync_outbox", "OwnerUserId"),
                ("collection_provenance", "AuthenticatedUserId")
            })
            {
                migrationBuilder.AlterColumn<string>(
                    name: column,
                    table: table,
                    type: "character varying(128)",
                    maxLength: 128,
                    nullable: false,
                    oldClrType: typeof(string),
                    oldType: "character varying(128)",
                    oldMaxLength: 128,
                    oldDefaultValue: "local-owner");
            }
        }
    }
}
