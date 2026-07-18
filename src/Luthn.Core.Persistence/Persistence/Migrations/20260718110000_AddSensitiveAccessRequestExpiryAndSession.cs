using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luthn.Core.Persistence.Persistence.Migrations;

[DbContext(typeof(LuthnDbContext))]
[Migration("20260718110000_AddSensitiveAccessRequestExpiryAndSession")]
public partial class AddSensitiveAccessRequestExpiryAndSession : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ExpiresAt",
            table: "sensitive_access_requests",
            type: "timestamp with time zone",
            nullable: false,
            defaultValueSql: "CURRENT_TIMESTAMP + INTERVAL '10 minutes'");

        migrationBuilder.AddColumn<string>(
            name: "SessionId",
            table: "sensitive_access_requests",
            type: "character varying(128)",
            maxLength: 128,
            nullable: false,
            defaultValue: "legacy-session");

        migrationBuilder.DropIndex(
            name: "IX_sensitive_access_requests_Status_UpdatedAt",
            table: "sensitive_access_requests");

        migrationBuilder.CreateIndex(
            name: "IX_sensitive_access_requests_Status_ExpiresAt_UpdatedAt",
            table: "sensitive_access_requests",
            columns: new[] { "Status", "ExpiresAt", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_sensitive_access_requests_Status_ExpiresAt_UpdatedAt",
            table: "sensitive_access_requests");

        migrationBuilder.DropColumn(name: "ExpiresAt", table: "sensitive_access_requests");
        migrationBuilder.DropColumn(name: "SessionId", table: "sensitive_access_requests");

        migrationBuilder.CreateIndex(
            name: "IX_sensitive_access_requests_Status_UpdatedAt",
            table: "sensitive_access_requests",
            columns: new[] { "Status", "UpdatedAt" });
    }
}
