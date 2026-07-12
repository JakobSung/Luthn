using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSensitiveAccessControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sensitive_access_requests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SensitiveRecordReferenceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitive_access_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sensitive_access_requests_sensitive_record_references_Sensi~",
                        column: x => x.SensitiveRecordReferenceId,
                        principalTable: "sensitive_record_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sensitive_access_decisions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SensitiveAccessRequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Decision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DecisionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadClass = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RedactionState = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitive_access_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sensitive_access_decisions_sensitive_access_requests_Sensit~",
                        column: x => x.SensitiveAccessRequestId,
                        principalTable: "sensitive_access_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_decisions_SensitiveAccessRequestId",
                table: "sensitive_access_decisions",
                column: "SensitiveAccessRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_access_requests_SensitiveRecordReferenceId",
                table: "sensitive_access_requests",
                column: "SensitiveRecordReferenceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sensitive_access_decisions");

            migrationBuilder.DropTable(
                name: "sensitive_access_requests");
        }
    }
}
