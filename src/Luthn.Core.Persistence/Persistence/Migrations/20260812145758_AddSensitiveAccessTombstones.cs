using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSensitiveAccessTombstones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sensitive_access_tombstones",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CleanedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitive_access_tombstones", x => x.Id);
                    table.CheckConstraint("CK_sensitive_access_tombstones_expired_status", "\"Status\" = 'Expired'");
                    table.CheckConstraint("CK_sensitive_access_tombstones_owner_user_id", "\"OwnerUserId\" <> ''");
                    table.CheckConstraint("CK_sensitive_access_tombstones_workspace_id", "\"WorkspaceId\" <> ''");
                });

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_tombstones_WorkspaceId_OwnerUserId_Cleaned~",
                table: "sensitive_access_tombstones",
                columns: new[] { "WorkspaceId", "OwnerUserId", "CleanedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sensitive_access_tombstones");
        }
    }
}
