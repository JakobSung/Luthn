using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentConnectionChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_connection_channels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IntegrationKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConnectorVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConfigurationOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    VerificationState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActivityState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FirstObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_connection_channels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_connection_channels_AgentId_Channel",
                table: "agent_connection_channels",
                columns: new[] { "AgentId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_connection_channels_UpdatedAt",
                table: "agent_connection_channels",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_connection_channels");
        }
    }
}
