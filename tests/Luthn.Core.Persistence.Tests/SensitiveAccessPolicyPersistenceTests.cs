using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Luthn.Core.Persistence.Tests;

public sealed class SensitiveAccessPolicyPersistenceTests
{
    [Fact]
    public async Task PersistsBoundedPolicyRequestSnapshotAndOneGrantPerRequest()
    {
        await using var db = CreateInMemoryDbContext();
        var observedAt = DateTimeOffset.Parse("2026-08-11T00:00:00Z");
        db.SensitiveAccessPolicyRevisions.Add(new SensitiveAccessPolicyRevisionRecord
        {
            WorkspaceId = "workspace-a",
            Revision = 1,
            RequestTimeoutSeconds = 600,
            GrantDurationSeconds = 600,
            MaximumSuccessfulReads = 1,
            CreatedAt = observedAt,
            CreatedBy = "operator"
        });
        db.SensitiveAccessRequests.Add(new SensitiveAccessRequestRecord
        {
            Id = "access-a",
            SensitiveRecordReferenceId = "reference-a",
            RequestedBy = "agent",
            SessionId = "session-a",
            RequestReason = "bounded access",
            Status = SensitiveAccessRequestStatus.Approved,
            CreatedAt = observedAt,
            ExpiresAt = observedAt.AddMinutes(10),
            UpdatedAt = observedAt,
            WorkspaceId = "workspace-a",
            OwnerUserId = "owner-a",
            PolicyRevision = 1,
            RequestTimeoutSeconds = 600
        });
        db.SensitiveAccessGrants.Add(new SensitiveAccessGrantRecord
        {
            SensitiveAccessRequestId = "access-a",
            WorkspaceId = "workspace-a",
            OwnerUserId = "owner-a",
            PolicyRevision = 1,
            GrantDurationSeconds = 600,
            StartsAt = observedAt,
            ExpiresAt = observedAt.AddMinutes(10),
            MaximumSuccessfulReads = 1
        });

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.SensitiveAccessPolicyRevisions.CountAsync());
        Assert.Equal(1, (await db.SensitiveAccessRequests.SingleAsync()).PolicyRevision);
        Assert.Equal(1, await db.SensitiveAccessGrants.CountAsync());
        Assert.True(db.Model.FindEntityType(typeof(SensitiveAccessGrantRecord))!
            .FindPrimaryKey()!.Properties.Single().Name == nameof(SensitiveAccessGrantRecord.SensitiveAccessRequestId));
    }

    [Fact]
    public async Task RejectsPolicyAndGrantValuesOutsideTheBoundedContract()
    {
        await using var db = CreateInMemoryDbContext();
        db.SensitiveAccessPolicyRevisions.Add(new SensitiveAccessPolicyRevisionRecord
        {
            WorkspaceId = "workspace-a",
            Revision = 1,
            RequestTimeoutSeconds = 59,
            GrantDurationSeconds = 600,
            MaximumSuccessfulReads = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "operator"
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Assert.Contains("60..3600", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationBackfillsPolicyAndApprovedGrantsThenDropsTemporaryDefaults()
    {
        using var db = CreatePostgresMetadataDbContext();
        var script = db.GetService<IMigrator>()
            .GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("INSERT INTO sensitive_access_policy_revisions", script, StringComparison.Ordinal);
        Assert.Contains("UPDATE sensitive_access_requests", script, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO sensitive_access_grants", script, StringComparison.Ordinal);
        Assert.Contains("WHERE request.\"Status\" = 'Approved'", script, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"PolicyRevision\" DROP DEFAULT", script, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"RequestTimeoutSeconds\" DROP DEFAULT", script, StringComparison.Ordinal);
    }

    private static LuthnDbContext CreateInMemoryDbContext() =>
        new(new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static LuthnDbContext CreatePostgresMetadataDbContext() =>
        new(new DbContextOptionsBuilder<LuthnDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=luthn;Username=luthn")
            .Options);
}
