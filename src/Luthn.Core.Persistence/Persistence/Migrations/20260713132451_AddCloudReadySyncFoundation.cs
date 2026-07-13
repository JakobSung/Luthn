using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudReadySyncFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExternalPublicationDecidedAt",
                table: "shared_memory_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalPublicationDecidedBy",
                table: "shared_memory_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalPublicationState",
                table: "shared_memory_items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "LocalOnly");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "shared_memory_items",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "shared_memory_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE shared_memory_items SET \"UpdatedAt\" = \"CreatedAt\" WHERE \"UpdatedAt\" IS NULL;");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "shared_memory_items",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "local_installation_state",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OriginInstanceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_installation_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "safe_projection_sync_checkpoints",
                columns: table => new
                {
                    TransportName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Checkpoint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_safe_projection_sync_checkpoints", x => x.TransportName);
                });

            migrationBuilder.CreateTable(
                name: "safe_projection_sync_outbox",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    OriginInstanceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LocalRecordId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    SafeEnvelopeJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessingStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RemoteCheckpoint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_safe_projection_sync_outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_local_installation_state_OriginInstanceId",
                table: "local_installation_state",
                column: "OriginInstanceId",
                unique: true);

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
                name: "IX_safe_projection_sync_outbox_State_NextAttemptAt_CreatedAt",
                table: "safe_projection_sync_outbox",
                columns: new[] { "State", "NextAttemptAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_installation_state");

            migrationBuilder.DropTable(
                name: "safe_projection_sync_checkpoints");

            migrationBuilder.DropTable(
                name: "safe_projection_sync_outbox");

            migrationBuilder.DropColumn(
                name: "ExternalPublicationDecidedAt",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "ExternalPublicationDecidedBy",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "ExternalPublicationState",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "shared_memory_items");
        }
    }
}
