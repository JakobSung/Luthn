using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceScopedDataPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wiki_proposals_OwnerUserId_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals");

            migrationBuilder.DropIndex(
                name: "IX_source_events_OwnerUserId",
                table: "source_events");

            migrationBuilder.DropIndex(
                name: "IX_shared_memory_items_OwnerUserId_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_record_references_OwnerUserId",
                table: "sensitive_record_references");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_access_requests_OwnerUserId_Status_UpdatedAt",
                table: "sensitive_access_requests");

            migrationBuilder.DropIndex(
                name: "IX_safe_projection_sync_outbox_IdempotencyKey",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropIndex(
                name: "IX_safe_projection_sync_outbox_OriginInstanceId_LocalRecordId_~",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropIndex(
                name: "IX_safe_projection_sync_outbox_OwnerUserId_State_CreatedAt",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropPrimaryKey(
                name: "PK_safe_projection_sync_checkpoints",
                table: "safe_projection_sync_checkpoints");

            migrationBuilder.DropIndex(
                name: "IX_agent_connection_channels_OwnerUserId_AgentId_Channel",
                table: "agent_connection_channels");

            migrationBuilder.DropIndex(
                name: "IX_agent_connection_channels_UpdatedAt",
                table: "agent_connection_channels");

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "wiki_proposals",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "source_events",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "shared_memory_items",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "sensitive_record_references",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "sensitive_access_requests",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "safe_projection_sync_outbox",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "safe_projection_sync_checkpoints",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "collection_provenance",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "agent_connection_channels",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE wiki_proposals
                SET "WorkspaceId" = CASE
                    WHEN "OwnerUserId" = 'local-owner' THEN 'default'
                    ELSE 'personal:' || lower("OwnerUserId")
                END;

                UPDATE source_events
                SET "WorkspaceId" = CASE
                    WHEN "OwnerUserId" = 'local-owner' THEN 'default'
                    ELSE 'personal:' || lower("OwnerUserId")
                END;

                UPDATE shared_memory_items
                SET "WorkspaceId" = CASE
                    WHEN "OwnerUserId" = 'local-owner' THEN 'default'
                    ELSE 'personal:' || lower("OwnerUserId")
                END;

                SET CONSTRAINTS
                    source_event_requires_collection_provenance,
                    memory_item_requires_collection_provenance
                    IMMEDIATE;

                UPDATE sensitive_record_references
                SET "WorkspaceId" = CASE
                    WHEN "OwnerUserId" = 'local-owner' THEN 'default'
                    ELSE 'personal:' || lower("OwnerUserId")
                END;

                UPDATE sensitive_access_requests
                SET "WorkspaceId" = CASE
                    WHEN "OwnerUserId" = 'local-owner' THEN 'default'
                    ELSE 'personal:' || lower("OwnerUserId")
                END;

                UPDATE safe_projection_sync_outbox
                SET "WorkspaceId" = CASE
                    WHEN "OwnerUserId" = 'local-owner' THEN 'default'
                    ELSE 'personal:' || lower("OwnerUserId")
                END;

                ALTER TABLE collection_provenance
                    DISABLE TRIGGER collection_provenance_immutable;

                UPDATE collection_provenance
                SET "WorkspaceId" = CASE
                    WHEN "AuthenticatedUserId" = 'local-owner' THEN 'default'
                    ELSE 'personal:' || lower("AuthenticatedUserId")
                END;

                ALTER TABLE collection_provenance
                    ENABLE TRIGGER collection_provenance_immutable;

                UPDATE agent_connection_channels
                SET "WorkspaceId" = CASE
                    WHEN "OwnerUserId" = 'local-owner' THEN 'default'
                    ELSE 'personal:' || lower("OwnerUserId")
                END;

                UPDATE safe_projection_sync_checkpoints
                SET "WorkspaceId" = 'default';

                UPDATE safe_projection_sync_outbox
                SET "SafeEnvelopeJson" = jsonb_set(
                        jsonb_set(
                            "SafeEnvelopeJson",
                            '{workspaceId}',
                            to_jsonb("WorkspaceId"),
                            true),
                        '{contractVersion}',
                        '2'::jsonb,
                        true),
                    "ContractVersion" = 2;

                ALTER TABLE wiki_proposals ALTER COLUMN "WorkspaceId" SET NOT NULL;
                ALTER TABLE source_events ALTER COLUMN "WorkspaceId" SET NOT NULL;
                ALTER TABLE shared_memory_items ALTER COLUMN "WorkspaceId" SET NOT NULL;
                ALTER TABLE sensitive_record_references ALTER COLUMN "WorkspaceId" SET NOT NULL;
                ALTER TABLE sensitive_access_requests ALTER COLUMN "WorkspaceId" SET NOT NULL;
                ALTER TABLE safe_projection_sync_outbox ALTER COLUMN "WorkspaceId" SET NOT NULL;
                ALTER TABLE safe_projection_sync_checkpoints ALTER COLUMN "WorkspaceId" SET NOT NULL;
                ALTER TABLE collection_provenance ALTER COLUMN "WorkspaceId" SET NOT NULL;
                ALTER TABLE agent_connection_channels ALTER COLUMN "WorkspaceId" SET NOT NULL;
                """);

            migrationBuilder.AddPrimaryKey(
                name: "PK_safe_projection_sync_checkpoints",
                table: "safe_projection_sync_checkpoints",
                columns: new[] { "WorkspaceId", "TransportName" });

            migrationBuilder.CreateIndex(
                name: "IX_wiki_proposals_WorkspaceId_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals",
                columns: new[] { "WorkspaceId", "ProjectKey", "TaskKey", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_wiki_proposals_workspace_id",
                table: "wiki_proposals",
                sql: "\"WorkspaceId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_source_events_WorkspaceId_ReceivedAt",
                table: "source_events",
                columns: new[] { "WorkspaceId", "ReceivedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_source_events_workspace_id",
                table: "source_events",
                sql: "\"WorkspaceId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_shared_memory_items_WorkspaceId_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items",
                columns: new[] { "WorkspaceId", "ProjectKey", "TaskKey", "UpdatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_memory_items_workspace_id",
                table: "shared_memory_items",
                sql: "\"WorkspaceId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_record_references_WorkspaceId_ReceivedAt",
                table: "sensitive_record_references",
                columns: new[] { "WorkspaceId", "ReceivedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_sensitive_record_references_workspace_id",
                table: "sensitive_record_references",
                sql: "\"WorkspaceId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_requests_WorkspaceId_Status_UpdatedAt",
                table: "sensitive_access_requests",
                columns: new[] { "WorkspaceId", "Status", "UpdatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_sensitive_access_requests_workspace_id",
                table: "sensitive_access_requests",
                sql: "\"WorkspaceId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_safe_projection_sync_outbox_WorkspaceId_IdempotencyKey",
                table: "safe_projection_sync_outbox",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_safe_projection_sync_outbox_WorkspaceId_OriginInstanceId_Lo~",
                table: "safe_projection_sync_outbox",
                columns: new[] { "WorkspaceId", "OriginInstanceId", "LocalRecordId", "Revision", "Operation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_safe_projection_sync_outbox_WorkspaceId_State_CreatedAt",
                table: "safe_projection_sync_outbox",
                columns: new[] { "WorkspaceId", "State", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_safe_projection_sync_outbox_workspace_id",
                table: "safe_projection_sync_outbox",
                sql: "\"WorkspaceId\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_safe_projection_sync_checkpoints_workspace_id",
                table: "safe_projection_sync_checkpoints",
                sql: "\"WorkspaceId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_collection_provenance_WorkspaceId_ReceivedAt",
                table: "collection_provenance",
                columns: new[] { "WorkspaceId", "ReceivedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_collection_provenance_workspace_id",
                table: "collection_provenance",
                sql: "\"WorkspaceId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_agent_connection_channels_WorkspaceId_AgentId_Channel",
                table: "agent_connection_channels",
                columns: new[] { "WorkspaceId", "AgentId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_connection_channels_WorkspaceId_UpdatedAt",
                table: "agent_connection_channels",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_agent_connection_channels_workspace_id",
                table: "agent_connection_channels",
                sql: "\"WorkspaceId\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wiki_proposals_WorkspaceId_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_wiki_proposals_workspace_id",
                table: "wiki_proposals");

            migrationBuilder.DropIndex(
                name: "IX_source_events_WorkspaceId_ReceivedAt",
                table: "source_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_source_events_workspace_id",
                table: "source_events");

            migrationBuilder.DropIndex(
                name: "IX_shared_memory_items_WorkspaceId_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_memory_items_workspace_id",
                table: "shared_memory_items");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_record_references_WorkspaceId_ReceivedAt",
                table: "sensitive_record_references");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sensitive_record_references_workspace_id",
                table: "sensitive_record_references");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_access_requests_WorkspaceId_Status_UpdatedAt",
                table: "sensitive_access_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sensitive_access_requests_workspace_id",
                table: "sensitive_access_requests");

            migrationBuilder.DropIndex(
                name: "IX_safe_projection_sync_outbox_WorkspaceId_IdempotencyKey",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropIndex(
                name: "IX_safe_projection_sync_outbox_WorkspaceId_OriginInstanceId_Lo~",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropIndex(
                name: "IX_safe_projection_sync_outbox_WorkspaceId_State_CreatedAt",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_safe_projection_sync_outbox_workspace_id",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropPrimaryKey(
                name: "PK_safe_projection_sync_checkpoints",
                table: "safe_projection_sync_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_safe_projection_sync_checkpoints_workspace_id",
                table: "safe_projection_sync_checkpoints");

            migrationBuilder.DropIndex(
                name: "IX_collection_provenance_WorkspaceId_ReceivedAt",
                table: "collection_provenance");

            migrationBuilder.DropCheckConstraint(
                name: "CK_collection_provenance_workspace_id",
                table: "collection_provenance");

            migrationBuilder.DropIndex(
                name: "IX_agent_connection_channels_WorkspaceId_AgentId_Channel",
                table: "agent_connection_channels");

            migrationBuilder.DropIndex(
                name: "IX_agent_connection_channels_WorkspaceId_UpdatedAt",
                table: "agent_connection_channels");

            migrationBuilder.DropCheckConstraint(
                name: "CK_agent_connection_channels_workspace_id",
                table: "agent_connection_channels");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "wiki_proposals");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "source_events");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "sensitive_record_references");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "sensitive_access_requests");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "safe_projection_sync_outbox");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "safe_projection_sync_checkpoints");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "collection_provenance");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "agent_connection_channels");

            migrationBuilder.AddPrimaryKey(
                name: "PK_safe_projection_sync_checkpoints",
                table: "safe_projection_sync_checkpoints",
                column: "TransportName");

            migrationBuilder.CreateIndex(
                name: "IX_wiki_proposals_OwnerUserId_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals",
                columns: new[] { "OwnerUserId", "ProjectKey", "TaskKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_source_events_OwnerUserId",
                table: "source_events",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_shared_memory_items_OwnerUserId_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items",
                columns: new[] { "OwnerUserId", "ProjectKey", "TaskKey", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_record_references_OwnerUserId",
                table: "sensitive_record_references",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_requests_OwnerUserId_Status_UpdatedAt",
                table: "sensitive_access_requests",
                columns: new[] { "OwnerUserId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_safe_projection_sync_outbox_IdempotencyKey",
                table: "safe_projection_sync_outbox",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_safe_projection_sync_outbox_OriginInstanceId_LocalRecordId_~",
                table: "safe_projection_sync_outbox",
                columns: new[] { "OriginInstanceId", "LocalRecordId", "Revision", "Operation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_safe_projection_sync_outbox_OwnerUserId_State_CreatedAt",
                table: "safe_projection_sync_outbox",
                columns: new[] { "OwnerUserId", "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_connection_channels_OwnerUserId_AgentId_Channel",
                table: "agent_connection_channels",
                columns: new[] { "OwnerUserId", "AgentId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_connection_channels_UpdatedAt",
                table: "agent_connection_channels",
                column: "UpdatedAt");
        }
    }
}
