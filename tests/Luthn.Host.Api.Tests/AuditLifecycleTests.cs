using System.Net;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public async Task CursorRejectsReuseAcrossAuthenticatedWorkspaces()
    {
        const string workspaceABearer = "audit-workspace-a";
        const string workspaceBBearer = "audit-workspace-b";
        using var factory = CreateFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Luthn:Identity:Mode", "MultiUser");
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConfigureOperatorToken(builder, 0, workspaceABearer, "workspace-a");
            ConfigureOperatorToken(builder, 1, workspaceBBearer, "workspace-b");
        });
        var occurredAt = DateTimeOffset.Parse("2026-08-06T08:30:00Z");
        await SeedAsync(factory,
            Audit("audit-workspace-a-1", "sensitive_access.requested", occurredAt, "workspace-a"),
            Audit("audit-workspace-a-2", "sensitive_access.approved", occurredAt.AddMinutes(-1), "workspace-a"),
            Audit("audit-workspace-b-1", "sensitive_access.requested", occurredAt, "workspace-b"),
            Audit("audit-workspace-b-2", "sensitive_access.approved", occurredAt.AddMinutes(-1), "workspace-b"));
        using var client = factory.CreateClient();

        client.SetBearer(workspaceABearer);
        using var firstResponse = await client.GetAsync("/api/audit-events?category=access&limit=1");
        using var firstBody = await JsonDocument.ParseAsync(await firstResponse.Content.ReadAsStreamAsync());
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString();

        client.SetBearer(workspaceBBearer);
        using var reusedResponse = await client.GetAsync(
            $"/api/audit-events?category=access&limit=1&cursor={Uri.EscapeDataString(cursor!)}");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reusedResponse.StatusCode);
    }

    [Theory]
    [InlineData("hub.ingress.accepted", "Ingestion")]
    [InlineData("classification.provider.failed", "Security")]
    [InlineData("turn_summary.classification_provider.failed", "Security")]
    [InlineData("authorization.scope_denied", "Security")]
    [InlineData("console.session.revoked", "Security")]
    public void CategoriesClassifyOperationalActionFamilies(string action, string category)
    {
        Assert.Equal(category, AuditEventCategories.FromAction(action));
    }

    [Fact]
    public async Task RetentionKeepsNestedClassificationProviderEventsAtTheSecurityWindow()
    {
        var now = DateTimeOffset.Parse("2026-08-06T08:30:00Z");
        var options = new AuditRetentionOptions
        {
            AccessDays = 365,
            SecurityDays = 365,
            ConfigurationDays = 365,
            PublicationDays = 365,
            IngestionDays = 90,
            RetentionDays = 365
        };
        var dbOptions = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new LuthnDbContext(dbOptions);
        db.AuditEvents.AddRange(
            Audit(
                "expired-ingestion", "turn_summary.intake.completed", now.AddDays(-91)),
            Audit(
                "security-provider", "turn_summary.classification_provider.completed", now.AddDays(-91)));
        await db.SaveChangesAsync();
        var processor = new AuditRetentionCleanupProcessor(db, Options.Create(options));

        var result = await processor.ProcessBatchAsync(now, 10);
        var remaining = await db.AuditEvents.AsNoTracking().ToArrayAsync();

        Assert.Equal(1, result.DeletedCount);
        Assert.DoesNotContain(remaining, item => item.Id == "expired-ingestion");
        Assert.Contains(remaining, item => item.Id == "security-provider");
        Assert.DoesNotContain(
            AuditEventCategories.Apply(db.AuditEvents.AsNoTracking(), AuditEventCategories.Ingestion),
            item => item.Id == "security-provider");
    }

    [Fact]
    public async Task AuditQueryAcceptsHubConsoleAndAuthorizationActionFamilies()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var actionPrefix in new[] { "hub.ingress.", "console.", "authorization." })
        {
            using var response = await client.GetAsync($"/api/audit-events?actionPrefix={Uri.EscapeDataString(actionPrefix)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
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

    private static void ConfigureOperatorToken(
        IWebHostBuilder builder,
        int index,
        string bearer,
        string workspaceId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(bearer));
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Name", $"auditor-{index}");
        builder.UseSetting(
            $"Luthn:Auth:Tokens:{index}:Sha256Digest",
            $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}");
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Scopes:0", "audit.read");
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:WorkspaceId", workspaceId);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:IsOperator", "true");
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

    [Fact]
    public async Task CleanupSelectsOldestExpiredEventsAcrossCategories()
    {
        var now = DateTimeOffset.Parse("2026-08-06T08:30:00Z");
        var options = new AuditRetentionOptions
        {
            AccessDays = 10,
            SecurityDays = 10,
            ConfigurationDays = 10,
            PublicationDays = 10,
            IngestionDays = 10,
            RetentionDays = 10
        };
        var dbOptions = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new LuthnDbContext(dbOptions);
        db.AuditEvents.AddRange(
            AuditCursorContractTests.Audit(
                "access-newer", "sensitive_access.approved", now.AddDays(-11)),
            AuditCursorContractTests.Audit(
                "access-older", "sensitive_access.requested", now.AddDays(-12)),
            AuditCursorContractTests.Audit(
                "configuration-oldest", "operator.classification_provider.updated", now.AddDays(-30)));
        await db.SaveChangesAsync();
        var processor = new AuditRetentionCleanupProcessor(db, Options.Create(options));

        var result = await processor.ProcessBatchAsync(now, 2);
        var remaining = await db.AuditEvents.AsNoTracking().ToArrayAsync();

        Assert.Equal(2, result.DeletedCount);
        Assert.DoesNotContain(remaining, item => item.Id == "configuration-oldest");
        Assert.DoesNotContain(remaining, item => item.Id == "access-older");
        Assert.Contains(remaining, item => item.Id == "access-newer");
    }
}
