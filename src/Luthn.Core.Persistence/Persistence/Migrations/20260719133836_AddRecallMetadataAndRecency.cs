using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecallMetadataAndRecency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProjectKey",
                table: "wiki_proposals",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskKey",
                table: "wiki_proposals",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TopicTags",
                table: "wiki_proposals",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "ProjectKey",
                table: "shared_memory_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskKey",
                table: "shared_memory_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TopicTags",
                table: "shared_memory_items",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.CreateIndex(
                name: "IX_wiki_proposals_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals",
                columns: new[] { "ProjectKey", "TaskKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_memory_items_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items",
                columns: new[] { "ProjectKey", "TaskKey", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wiki_proposals_ProjectKey_TaskKey_CreatedAt",
                table: "wiki_proposals");

            migrationBuilder.DropIndex(
                name: "IX_shared_memory_items_ProjectKey_TaskKey_UpdatedAt",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "ProjectKey",
                table: "wiki_proposals");

            migrationBuilder.DropColumn(
                name: "TaskKey",
                table: "wiki_proposals");

            migrationBuilder.DropColumn(
                name: "TopicTags",
                table: "wiki_proposals");

            migrationBuilder.DropColumn(
                name: "ProjectKey",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "TaskKey",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "TopicTags",
                table: "shared_memory_items");
        }
    }
}
