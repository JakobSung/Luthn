using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    [DbContext(typeof(LuthnDbContext))]
    [Migration("20260814090000_AddRequesterBoundProtectedMemoryAccess")]
    public partial class AddRequesterBoundProtectedMemoryAccess : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessHandleDigest",
                table: "sensitive_access_requests",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AccessMode",
                table: "sensitive_access_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "RedactedSummary");

            migrationBuilder.AddColumn<string>(
                name: "RequesterBindingDigest",
                table: "sensitive_access_requests",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_requests_WorkspaceId_OwnerUserId_AccessHandleDigest",
                table: "sensitive_access_requests",
                columns: new[] { "WorkspaceId", "OwnerUserId", "AccessHandleDigest" },
                unique: true,
                filter: "\"AccessMode\" = 'ProtectedMemory' AND \"AccessHandleDigest\" <> ''");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sensitive_access_requests_WorkspaceId_OwnerUserId_AccessHandleDigest",
                table: "sensitive_access_requests");

            migrationBuilder.DropColumn(
                name: "AccessHandleDigest",
                table: "sensitive_access_requests");

            migrationBuilder.DropColumn(
                name: "AccessMode",
                table: "sensitive_access_requests");

            migrationBuilder.DropColumn(
                name: "RequesterBindingDigest",
                table: "sensitive_access_requests");
        }
    }
}
