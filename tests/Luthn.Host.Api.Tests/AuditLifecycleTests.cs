using System.Net;
using System.Text.Json;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api.Tests;

public sealed class AuditCursorContractTests
{
    [Fact]
    public async Task CursorPagesSameTimestampEventsWithoutDuplicatesOrGaps()
    {
        using var factory = CreateFactory();
        var occurredAt = DateTimeOffset.Parse("2026-08-06T08:30:00Z");
        await SeedAsync(factory,
            Audit("audit-access-a", "sensitive_access.requested", occurredAt),
            Audit("audit-access-b", "sensitive_access.approved", occurredAt),
            Audit("audit-access-c", "retrieval.result_read", occurredAt),
            Audit("audit-access-d", "sensitive_access.denied", occurredAt.AddMinutes(-1)));
        using var client = factory.CreateClient();

        using var firstResponse = await client.GetAsync("/api/audit-events?category=access&limit=2");
        using var firstBody = await JsonDocument.ParseAsync(await firstResponse.Content.ReadAsStreamAsync());
        var firstIds = firstBody.RootElement.GetProperty("events").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString();

        using var secondResponse = await client.GetAsync(
            $"/api/audit-events?category=access&limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        using var secondBody = await JsonDocument.ParseAsync(await secondResponse.Content.ReadAsStreamAsync());
        var secondIds = secondBody.RootElement.GetProperty("events").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(["audit-access-a", "audit-access-b"], firstIds);
        Assert.Equal(["audit-access-c", "audit-access-d"], secondIds);
        Assert.Empty(firstIds.Intersect(secondIds, StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, secondBody.RootElement.GetProperty("nextCursor").ValueKind);
        Assert.All(
            secondBody.RootElement.GetProperty("events").EnumerateArray(),
            item =>
            {
                Assert.Equal("Access", item.GetProperty("category").GetString());
                Assert.Equal("access-365d", item.GetProperty("retentionClass").GetString());
                Assert.True(item.TryGetProperty("retainedUntil", out _));
            });
    }

    [Fact]
    public async Task CursorRejectsTamperingAndFilterReuse()
    {
        using var factory = CreateFactory();
        var occurredAt = DateTimeOffset.Parse("2026-08-06T08:30:00Z");
        await SeedAsync(factory,
            Audit("audit-access-a", "sensitive_access.requested", occurredAt),
            Audit("audit-access-b", "sensitive_access.approved", occurredAt.AddMinutes(-1)));
        using var client = factory.CreateClient();

        using var firstResponse = await client.GetAsync("/api/audit-events?category=access&limit=1");
        using var firstBody = await JsonDocument.ParseAsync(await firstResponse.Content.ReadAsStreamAsync());
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString();
        var tamperedCursor = cursor![..^1] + (cursor[^1] == 'A' ? "B" : "A");
        using var reusedResponse = await client.GetAsync(
            $"/api/audit-events?category=security&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        using var invalidResponse = await client.GetAsync("/api/audit-events?cursor=not-a-cursor");
        using var tamperedResponse = await client.GetAsync(
            $"/api/audit-events?category=access&limit=1&cursor={Uri.EscapeDataString(tamperedCursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, reusedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tamperedResponse.StatusCode);
    }

    internal static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting(
                "Luthn:OperatorConfig:Directory",
                Path.Combine(Path.GetTempPath(), "luthn-audit-tests", Guid.NewGuid().ToString("N")));
        });

    internal static AuditEventRecord Audit(
        string id,
        string action,
        DateTimeOffset occurredAt,
        string workspaceId = "default") =>
        AuditEventFactory.ForWorkspace(
            workspaceId,
            "operator-1",
            "service",
            "auditor",
            action,
            $"subject-{id}",
            "metadata-only",
            "content-excluded",
            occurredAt,
            subjectType: "sensitive_access_request",
            outcome: "succeeded",
            correlationId: "correlation-1",
            id: id);

    internal static async Task SeedAsync(
        WebApplicationFactory<Program> factory,
        params AuditEventRecord[] records)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        db.AuditEvents.AddRange(records);
        await db.SaveChangesAsync();
    }
}

public sealed class AuditExportContractTests
{
    [Fact]
    public async Task ExportUsesWorkspaceFiltersAndContainsOnlyBoundedMetadata()
    {
        using var factory = AuditCursorContractTests.CreateFactory();
        var occurredAt = DateTimeOffset.Parse("2026-08-06T08:30:00Z");
        await AuditCursorContractTests.SeedAsync(factory,
            AuditCursorContractTests.Audit(
                "audit-export-default", "sensitive_access.approved", occurredAt),
            AuditCursorContractTests.Audit(
                "audit-export-other", "sensitive_access.denied", occurredAt, "other-workspace"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/audit-events/export?category=access&action=sensitive_access.approved");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(
            "metadata-only-no-protected-content",
            body.RootElement.GetProperty("exportBoundary").GetString());
        var auditEvent = Assert.Single(body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("audit-export-default", auditEvent.GetProperty("id").GetString());
        Assert.Equal("Access", auditEvent.GetProperty("category").GetString());

        string[] forbiddenProperties =
        [
            "workspaceId", "actorUserId", "ownerUserId", "raw", "rawSource", "vaultContent",
            "encryptedPayload", "credential", "secret", "prompt", "transcript", "localPath", "content"
        ];
        foreach (var property in forbiddenProperties)
        {
            Assert.False(auditEvent.TryGetProperty(property, out _), property);
        }

        Assert.DoesNotContain("audit-export-other", body.RootElement.GetRawText(), StringComparison.Ordinal);
    }
}

public sealed class AuditRetentionCleanupTests
{
    [Fact]
    public async Task CleanupIsDisabledByDefaultAndDeletesOnlyExpiredMetadataWithinBatchLimit()
    {
        var now = DateTimeOffset.Parse("2026-08-06T08:30:00Z");
        var options = new AuditRetentionOptions
        {
            CleanupBatchSize = 2,
            AccessDays = 10,
            SecurityDays = 10,
            ConfigurationDays = 10,
            PublicationDays = 10,
            IngestionDays = 10,
            RetentionDays = 10
        };
        Assert.False(options.CleanupEnabled);

        var dbOptions = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new LuthnDbContext(dbOptions);
        db.AuditEvents.AddRange(
            AuditCursorContractTests.Audit(
                "expired-access", "sensitive_access.approved", now.AddDays(-11)),
            AuditCursorContractTests.Audit(
                "expired-configuration", "operator.classification_provider.updated", now.AddDays(-11)),
            AuditCursorContractTests.Audit(
                "expired-ingestion", "source.intake.completed", now.AddDays(-11)),
            AuditCursorContractTests.Audit(
                "recent-access", "sensitive_access.requested", now.AddDays(-9)));
        await db.SaveChangesAsync();
        var processor = new AuditRetentionCleanupProcessor(db, Options.Create(options));

        var first = await processor.ProcessBatchAsync(now, options.CleanupBatchSize);
        var remainingAfterFirst = await db.AuditEvents.AsNoTracking().ToArrayAsync();

        Assert.Equal(2, first.DeletedCount);
        Assert.Contains(remainingAfterFirst, item => item.Id == "expired-ingestion");
        Assert.Contains(remainingAfterFirst, item => item.Id == "recent-access");
        var retentionAudit = Assert.Single(
            remainingAfterFirst,
            item => item.Action == "audit.retention.pruned");
        Assert.Equal("metadata-only", retentionAudit.PayloadClass);
        Assert.Equal("expired-audit-metadata-deleted", retentionAudit.RedactionState);

        var second = await processor.ProcessBatchAsync(now, options.CleanupBatchSize);
        var finalRecords = await db.AuditEvents.AsNoTracking().ToArrayAsync();

        Assert.Equal(1, second.DeletedCount);
        Assert.Contains(finalRecords, item => item.Id == "recent-access");
        Assert.DoesNotContain(finalRecords, item => item.Id.StartsWith("expired-", StringComparison.Ordinal));
    }
}
