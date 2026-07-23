using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticTurnCleanupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_shared_memory_items_cleanup_candidates",
                table: "shared_memory_items",
                columns: new[]
                {
                    "RetentionKind",
                    "ExternalPublicationState",
                    "ExpiresAt",
                    "CreatedAt",
                    "Id"
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shared_memory_items_cleanup_candidates",
                table: "shared_memory_items");
        }
    }
}
