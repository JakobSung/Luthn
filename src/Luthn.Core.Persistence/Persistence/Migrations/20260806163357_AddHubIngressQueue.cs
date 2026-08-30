using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHubIngressQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hub_ingress_queue",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReceiptId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    MemberUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AgentConnectionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TurnId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContentDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    CapsuleSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    ProtectionScheme = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtectedCapsule = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessingStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Sensitivity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StorageDecision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ContainsSensitiveMaterial = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hub_ingress_queue", x => x.Id);
                    table.CheckConstraint("CK_hub_ingress_queue_capsule_size", "\"CapsuleSizeBytes\" > 0");
                    table.CheckConstraint("CK_hub_ingress_queue_member_user_id", "\"MemberUserId\" <> ''");
                    table.CheckConstraint("CK_hub_ingress_queue_organization_id", "\"OrganizationId\" <> ''");
                    table.CheckConstraint("CK_hub_ingress_queue_workspace_id", "\"WorkspaceId\" <> ''");
                });

            migrationBuilder.CreateIndex(
                name: "IX_hub_ingress_queue_OrganizationId_State_AcceptedAt",
                table: "hub_ingress_queue",
                columns: new[] { "OrganizationId", "State", "AcceptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_hub_ingress_queue_ReceiptId",
                table: "hub_ingress_queue",
                column: "ReceiptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_hub_ingress_queue_State_NextAttemptAt_AcceptedAt",
                table: "hub_ingress_queue",
                columns: new[] { "State", "NextAttemptAt", "AcceptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_hub_ingress_queue_WorkspaceId_AgentConnectionId_Idempotency~",
                table: "hub_ingress_queue",
                columns: new[] { "WorkspaceId", "AgentConnectionId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_hub_ingress_queue_WorkspaceId_AgentId_AcceptedAt",
                table: "hub_ingress_queue",
                columns: new[] { "WorkspaceId", "AgentId", "AcceptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_hub_ingress_queue_WorkspaceId_MemberUserId_AcceptedAt",
                table: "hub_ingress_queue",
                columns: new[] { "WorkspaceId", "MemberUserId", "AcceptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_hub_ingress_queue_WorkspaceId_State_AcceptedAt",
                table: "hub_ingress_queue",
                columns: new[] { "WorkspaceId", "State", "AcceptedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hub_ingress_queue");
        }
    }
}
