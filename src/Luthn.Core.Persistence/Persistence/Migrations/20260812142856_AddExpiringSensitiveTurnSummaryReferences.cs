using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExpiringSensitiveTurnSummaryReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "sensitive_record_references",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemoryItemId",
                table: "sensitive_record_references",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "sensitive_memory_payloads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_record_references_MemoryItemId",
                table: "sensitive_record_references",
                column: "MemoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_record_references_WorkspaceId_OwnerUserId_Expires~",
                table: "sensitive_record_references",
                columns: new[] { "WorkspaceId", "OwnerUserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_memory_payloads_ExpiresAt",
                table: "sensitive_memory_payloads",
                column: "ExpiresAt");

            migrationBuilder.AddForeignKey(
                name: "FK_sensitive_record_references_shared_memory_items_MemoryItemId",
                table: "sensitive_record_references",
                column: "MemoryItemId",
                principalTable: "shared_memory_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sensitive_record_references_shared_memory_items_MemoryItemId",
                table: "sensitive_record_references");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_record_references_MemoryItemId",
                table: "sensitive_record_references");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_record_references_WorkspaceId_OwnerUserId_Expires~",
                table: "sensitive_record_references");

            migrationBuilder.DropIndex(
                name: "IX_sensitive_memory_payloads_ExpiresAt",
                table: "sensitive_memory_payloads");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "sensitive_record_references");

            migrationBuilder.DropColumn(
                name: "MemoryItemId",
                table: "sensitive_record_references");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "sensitive_memory_payloads");
        }
    }
}
