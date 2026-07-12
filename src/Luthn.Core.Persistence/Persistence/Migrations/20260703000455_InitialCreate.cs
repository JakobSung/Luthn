using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadClass = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RedactionState = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "source_events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContentDigest = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContainsSensitiveMaterial = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "classification_results",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceEventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Sensitivity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Categories = table.Column<string>(type: "jsonb", nullable: false),
                    ContainsSensitiveMaterial = table.Column<bool>(type: "boolean", nullable: false),
                    StorageDecision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classification_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_classification_results_source_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "source_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sensitive_record_references",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceEventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContainsSensitiveMaterial = table.Column<bool>(type: "boolean", nullable: false),
                    ReferenceLabel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitive_record_references", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sensitive_record_references_source_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "source_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wiki_proposals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceEventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SafeSummary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Sensitivity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CoreTags = table.Column<string>(type: "jsonb", nullable: false),
                    AllowsAgentContext = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wiki_proposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wiki_proposals_source_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "source_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_classification_results_SourceEventId",
                table: "classification_results",
                column: "SourceEventId");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_record_references_SourceEventId",
                table: "sensitive_record_references",
                column: "SourceEventId");

            migrationBuilder.CreateIndex(
                name: "IX_wiki_proposals_SourceEventId",
                table: "wiki_proposals",
                column: "SourceEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "classification_results");

            migrationBuilder.DropTable(
                name: "sensitive_record_references");

            migrationBuilder.DropTable(
                name: "wiki_proposals");

            migrationBuilder.DropTable(
                name: "source_events");
        }
    }
}
