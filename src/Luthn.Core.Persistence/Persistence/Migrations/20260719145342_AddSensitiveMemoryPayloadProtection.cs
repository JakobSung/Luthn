using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSensitiveMemoryPayloadProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sensitive_memory_payloads",
                columns: table => new
                {
                    MemoryItemId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ProtectionScheme = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitive_memory_payloads", x => x.MemoryItemId);
                    table.ForeignKey(
                        name: "FK_sensitive_memory_payloads_shared_memory_items_MemoryItemId",
                        column: x => x.MemoryItemId,
                        principalTable: "shared_memory_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sensitive_memory_payloads");
        }
    }
}
