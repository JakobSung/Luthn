using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDbBackedRetrievalSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchTagKeys",
                table: "wiki_proposals",
                type: "text",
                nullable: false,
                defaultValue: "||");

            migrationBuilder.AddColumn<string>(
                name: "SearchTerms",
                table: "wiki_proposals",
                type: "text",
                nullable: false,
                defaultValue: "||");

            migrationBuilder.AddColumn<string>(
                name: "SearchTagKeys",
                table: "shared_memory_items",
                type: "text",
                nullable: false,
                defaultValue: "||");

            migrationBuilder.AddColumn<string>(
                name: "SearchTerms",
                table: "shared_memory_items",
                type: "text",
                nullable: false,
                defaultValue: "||");

            BackfillSearchIndexes(migrationBuilder, "wiki_proposals");
            BackfillSearchIndexes(migrationBuilder, "shared_memory_items");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SearchTagKeys",
                table: "wiki_proposals");

            migrationBuilder.DropColumn(
                name: "SearchTerms",
                table: "wiki_proposals");

            migrationBuilder.DropColumn(
                name: "SearchTagKeys",
                table: "shared_memory_items");

            migrationBuilder.DropColumn(
                name: "SearchTerms",
                table: "shared_memory_items");
        }

        private static void BackfillSearchIndexes(MigrationBuilder migrationBuilder, string tableName)
        {
            migrationBuilder.Sql($$"""
                UPDATE "{{tableName}}" AS record
                SET "SearchTerms" = '|' || COALESCE((
                        SELECT string_agg(DISTINCT term, '|' ORDER BY term)
                        FROM regexp_split_to_table(
                            lower(concat_ws(
                                ' ',
                                record."Title",
                                record."SafeSummary",
                                (
                                    SELECT string_agg(tag, ' ')
                                    FROM jsonb_array_elements_text(record."CoreTags") AS tags(tag)
                                ))),
                            '[^[:alnum:]]+') AS terms(term)
                        WHERE term <> ''
                    ), '') || '|',
                    "SearchTagKeys" = '|' || COALESCE((
                        SELECT string_agg(
                            DISTINCT replace(
                                encode(convert_to(lower(trim(tag)), 'UTF8'), 'base64'),
                                chr(10),
                                ''),
                            '|' ORDER BY replace(
                                encode(convert_to(lower(trim(tag)), 'UTF8'), 'base64'),
                                chr(10),
                                ''))
                        FROM jsonb_array_elements_text(record."CoreTags") AS tags(tag)
                    ), '') || '|';
                """);
        }
    }
}
