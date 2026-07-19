using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerScopedAgentConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agent_connection_channels_AgentId_Channel",
                table: "agent_connection_channels");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "agent_connection_channels",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "local-owner");

            migrationBuilder.AlterColumn<string>(
                name: "OwnerUserId",
                table: "agent_connection_channels",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldDefaultValue: "local-owner");

            migrationBuilder.CreateIndex(
                name: "IX_agent_connection_channels_OwnerUserId_AgentId_Channel",
                table: "agent_connection_channels",
                columns: new[] { "OwnerUserId", "AgentId", "Channel" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_agent_connection_channels_owner_user_id",
                table: "agent_connection_channels",
                sql: "\"OwnerUserId\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agent_connection_channels_OwnerUserId_AgentId_Channel",
                table: "agent_connection_channels");

            migrationBuilder.DropCheckConstraint(
                name: "CK_agent_connection_channels_owner_user_id",
                table: "agent_connection_channels");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "agent_connection_channels");

            migrationBuilder.CreateIndex(
                name: "IX_agent_connection_channels_AgentId_Channel",
                table: "agent_connection_channels",
                columns: new[] { "AgentId", "Channel" },
                unique: true);
        }
    }
}
