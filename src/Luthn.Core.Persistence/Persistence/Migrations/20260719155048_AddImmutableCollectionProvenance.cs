using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImmutableCollectionProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collection_provenance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    SourceEventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MemoryItemId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AuthenticatedActor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActorTrust = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClaimsTrust = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClaimedUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ApplicationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PluginId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ConnectorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ConnectorVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_provenance", x => x.Id);
                    table.CheckConstraint("CK_collection_provenance_subject", "\"SourceEventId\" IS NOT NULL OR \"MemoryItemId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_collection_provenance_shared_memory_items_MemoryItemId",
                        column: x => x.MemoryItemId,
                        principalTable: "shared_memory_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_collection_provenance_source_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "source_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_collection_provenance_MemoryItemId",
                table: "collection_provenance",
                column: "MemoryItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_collection_provenance_SourceEventId",
                table: "collection_provenance",
                column: "SourceEventId",
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO collection_provenance
                    ("Id", "ContractVersion", "SourceEventId", "MemoryItemId", "AuthenticatedActor", "ActorTrust", "ClaimsTrust", "ReceivedAt")
                SELECT
                    'provenance-' || md5('source-event:' || source."Id"),
                    1,
                    source."Id",
                    memory."Id",
                    'legacy-unknown',
                    'legacy-unknown',
                    'legacy-unknown',
                    source."ReceivedAt"
                FROM source_events AS source
                LEFT JOIN shared_memory_items AS memory
                    ON memory."Id" = 'memory-' || source."Id";

                INSERT INTO collection_provenance
                    ("Id", "ContractVersion", "MemoryItemId", "AuthenticatedActor", "ActorTrust", "ClaimsTrust", "ReceivedAt")
                SELECT
                    'provenance-' || md5('memory-item:' || memory."Id"),
                    1,
                    memory."Id",
                    'legacy-unknown',
                    'legacy-unknown',
                    'legacy-unknown',
                    memory."CreatedAt"
                FROM shared_memory_items AS memory
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM collection_provenance AS provenance
                    WHERE provenance."MemoryItemId" = memory."Id");

                CREATE FUNCTION luthn_prevent_collection_provenance_update()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' AND pg_trigger_depth() > 1 THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'collection provenance records are immutable'
                        USING ERRCODE = '55000';
                END;
                $function$;

                CREATE TRIGGER collection_provenance_immutable
                BEFORE UPDATE OR DELETE ON collection_provenance
                FOR EACH ROW
                EXECUTE FUNCTION luthn_prevent_collection_provenance_update();

                CREATE FUNCTION luthn_require_source_event_provenance()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM collection_provenance
                        WHERE "SourceEventId" = NEW."Id") THEN
                        RAISE EXCEPTION 'source event requires collection provenance'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER source_event_requires_collection_provenance
                AFTER INSERT OR UPDATE ON source_events
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION luthn_require_source_event_provenance();

                CREATE FUNCTION luthn_require_memory_item_provenance()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM collection_provenance
                        WHERE "MemoryItemId" = NEW."Id") THEN
                        RAISE EXCEPTION 'shared memory item requires collection provenance'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER memory_item_requires_collection_provenance
                AFTER INSERT OR UPDATE ON shared_memory_items
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION luthn_require_memory_item_provenance();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collection_provenance");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS luthn_prevent_collection_provenance_update();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS luthn_require_source_event_provenance();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS luthn_require_memory_item_provenance();");
        }
    }
}
