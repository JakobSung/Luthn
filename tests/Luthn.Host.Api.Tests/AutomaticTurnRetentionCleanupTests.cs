using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api.Tests;

public sealed class AutomaticTurnRetentionCleanupTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-23T12:00:00Z");

    [Fact]
    public async Task CleanupRemovesOldestExpiredTurnCapsuleAndRetainsAuditEvidence()
    {
        await using var db = CreateDatabase();
        AddTurnCapsule(db, "oldest", Now.AddDays(-2), sensitive: true);
        AddTurnCapsule(db, "next", Now.AddDays(-1));
        await db.SaveChangesAsync();

        var processor = new AutomaticTurnRetentionCleanupProcessor(db);
        var first = await processor.ProcessBatchAsync(Now, 1);

        Assert.Equal(1, first.DeletedCount);
        Assert.False(await db.SharedMemoryItems.AnyAsync(record => record.Id == "memory-oldest"));
        Assert.False(await db.SensitiveMemoryPayloads.AnyAsync(record => record.MemoryItemId == "memory-oldest"));
        Assert.False(await db.CollectionProvenance.AnyAsync(record => record.MemoryItemId == "memory-oldest"));
        Assert.False(await db.ClassificationResults.AnyAsync(record => record.SourceEventId == "oldest"));
        Assert.False(await db.SourceEvents.AnyAsync(record => record.Id == "oldest"));
        Assert.True(await db.AuditEvents.AnyAsync(record =>
            record.SubjectId == "oldest" &&
            record.Action == "turn_summary.intake.classified"));
        var cleanupAudit = await db.AuditEvents.SingleAsync(record =>
            record.SubjectId == "oldest" &&
            record.Action == "turn_summary.retention.pruned");
        Assert.Equal("luthn-retention-cleanup", cleanupAudit.Actor);
        Assert.Equal("metadata-only", cleanupAudit.PayloadClass);
        Assert.Equal("expired-turn-capsule-deleted", cleanupAudit.RedactionState);
        Assert.True(await db.SharedMemoryItems.AnyAsync(record => record.Id == "memory-next"));

        var second = await processor.ProcessBatchAsync(Now, 1);
        var retry = await processor.ProcessBatchAsync(Now, 1);

        Assert.Equal(1, second.DeletedCount);
        Assert.Equal(0, retry.DeletedCount);
        Assert.Equal(
            2,
            await db.AuditEvents.CountAsync(record =>
                record.Action == "turn_summary.retention.pruned"));
    }

    [Fact]
    public async Task CleanupExcludesNonAutomaticUnexpiredPublishedAndOutboxLinkedMemory()
    {
        await using var db = CreateDatabase();
        AddTurnCapsule(
            db,
            "durable",
            expiresAt: null,
            retentionKind: MemoryRetentionKind.Durable);
        AddTurnCapsule(
            db,
            "session",
            Now.AddMinutes(-1),
            retentionKind: MemoryRetentionKind.Session);
        AddTurnCapsule(db, "unexpired", Now.AddMinutes(1));
        AddTurnCapsule(db, "non-turn", Now.AddMinutes(-1), sourceType: "note");
        AddTurnCapsule(
            db,
            "approved",
            Now.AddMinutes(-1),
            publicationState: ExternalPublicationState.ApprovedForExternal);
        AddTurnCapsule(
            db,
            "revoked",
            Now.AddMinutes(-1),
            publicationState: ExternalPublicationState.Revoked);
        AddTurnCapsule(db, "outbox", Now.AddMinutes(-1), withOutbox: true);
        AddTurnCapsule(
            db,
            "owner-mismatch",
            Now.AddMinutes(-1),
            ownerUserId: "alice",
            sourceOwnerUserId: "bob");
        AddTurnCapsule(
            db,
            "provenance-owner-mismatch",
            Now.AddMinutes(-1),
            ownerUserId: "alice",
            provenanceOwnerUserId: "bob");
        AddTurnCapsule(
            db,
            "acknowledged-outbox-history",
            Now.AddMinutes(-1),
            withOutbox: true,
            outboxState: SafeProjectionSyncOutboxState.Acknowledged);
        AddManualEphemeralMemory(db, "manual", Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await new AutomaticTurnRetentionCleanupProcessor(db)
            .ProcessBatchAsync(Now, 100);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(11, await db.SharedMemoryItems.CountAsync());
        Assert.Empty(await db.AuditEvents
            .Where(record => record.Action == "turn_summary.retention.pruned")
            .ToArrayAsync());
    }

    [Fact]
    public async Task HostedIterationContainsCleanupFailuresAndCanRunAgain()
    {
        var fake = new FailingOnceCleanupProcessor();
        var services = new ServiceCollection()
            .AddSingleton<IAutomaticTurnRetentionCleanupProcessor>(fake)
            .BuildServiceProvider();
        var hostedService = new AutomaticTurnRetentionCleanupHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LuthnMemoryOptions()),
            TimeProvider.System,
            NullLogger<AutomaticTurnRetentionCleanupHostedService>.Instance);

        await hostedService.RunOnceAsync(10, CancellationToken.None);
        await hostedService.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public void CleanupOptionsHaveBoundedDefaults()
    {
        var options = new LuthnMemoryOptions();

        Assert.True(options.AutomaticTurnCleanupEnabled);
        Assert.Equal(60, options.AutomaticTurnCleanupIntervalMinutes);
        Assert.Equal(TimeSpan.FromHours(1), options.AutomaticTurnCleanupInterval);
        Assert.Equal(100, options.AutomaticTurnCleanupBatchSize);
        Assert.True(options.HasValidAutomaticTurnCleanupInterval);
        Assert.True(options.HasValidAutomaticTurnCleanupBatch);
    }

    private static LuthnDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new LuthnDbContext(options);
    }

    private static void AddTurnCapsule(
        LuthnDbContext db,
        string id,
        DateTimeOffset? expiresAt,
        MemoryRetentionKind retentionKind = MemoryRetentionKind.Ephemeral,
        string sourceType = "turn-summary",
        ExternalPublicationState publicationState = ExternalPublicationState.LocalOnly,
        bool sensitive = false,
        bool withOutbox = false,
        string ownerUserId = "local-owner",
        string? sourceOwnerUserId = null,
        string? provenanceOwnerUserId = null,
        SafeProjectionSyncOutboxState outboxState = SafeProjectionSyncOutboxState.Pending)
    {
        var memoryItemId = $"memory-{id}";
        var sourceOwner = sourceOwnerUserId ?? ownerUserId;
        db.SourceEvents.Add(new SourceEventRecord
        {
            Id = id,
            SourceSystem = "codex",
            SourceType = sourceType,
            ReceivedAt = Now.AddDays(-3),
            ContentDigest = $"sha256:{id}",
            ContainsSensitiveMaterial = sensitive,
            OwnerUserId = sourceOwner
        });
        db.ClassificationResults.Add(new ClassificationResultRecord
        {
            Id = $"classification-{id}",
            SourceEventId = id,
            Sensitivity = sensitive ? SensitivityLevel.Restricted : SensitivityLevel.Public,
            Confidence = 1,
            ContainsSensitiveMaterial = sensitive,
            StorageDecision = sensitive
                ? StorageDecisionKind.SensitiveDbOnly
                : StorageDecisionKind.WikiCandidate
        });
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = memoryItemId,
            Title = $"Memory {id}",
            SafeSummary = sensitive ? "[protected]" : $"Summary {id}",
            Sensitivity = sensitive ? SensitivityLevel.Restricted : SensitivityLevel.Public,
            CoreTags = ["retention"],
            Visibility = sensitive
                ? MemoryVisibility.PrivateToOwner
                : MemoryVisibility.SharedAcrossAgents,
            RetentionKind = retentionKind,
            ExpiresAt = expiresAt,
            AllowsAgentContext = !sensitive,
            CreatedAt = Now.AddDays(-3),
            UpdatedAt = Now.AddDays(-3),
            CreatedBy = "local-agent",
            OwnerUserId = ownerUserId,
            ExternalPublicationState = publicationState
        });
        db.CollectionProvenance.Add(new CollectionProvenanceRecord
        {
            Id = $"provenance-{id}",
            SourceEventId = id,
            MemoryItemId = memoryItemId,
            AuthenticatedActor = "local-agent",
            ActorTrust = CollectionProvenance.ServiceTokenActorTrust,
            ClaimsTrust = CollectionProvenance.NoClaimsTrust,
            AuthenticatedUserId = provenanceOwnerUserId ?? ownerUserId,
            ReceivedAt = Now.AddDays(-3)
        });
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = $"audit-intake-{id}",
            OccurredAt = Now.AddDays(-3),
            Actor = "local-agent",
            Action = "turn_summary.intake.classified",
            SubjectId = id,
            PayloadClass = "metadata-only",
            RedactionState = sensitive ? "encrypted-payload-only" : "safe-projection-only"
        });
        if (sensitive)
        {
            db.SensitiveMemoryPayloads.Add(new SensitiveMemoryPayloadRecord
            {
                MemoryItemId = memoryItemId,
                ProtectionScheme = "test",
                ProtectedPayload = "ciphertext",
                CreatedAt = Now.AddDays(-3),
                UpdatedAt = Now.AddDays(-3)
            });
        }

        if (withOutbox)
        {
            db.SafeProjectionSyncOutbox.Add(new SafeProjectionSyncOutboxRecord
            {
                Id = $"outbox-{id}",
                IdempotencyKey = $"test:{id}:1",
                OriginInstanceId = "test-origin",
                LocalRecordId = memoryItemId,
                OwnerUserId = ownerUserId,
                Revision = 1,
                Operation = SafeProjectionSyncOperation.Upsert,
                SafeEnvelopeJson = "{}",
                CreatedAt = Now.AddDays(-2),
                State = outboxState
            });
        }
    }

    private static void AddManualEphemeralMemory(
        LuthnDbContext db,
        string id,
        DateTimeOffset expiresAt)
    {
        var memoryItemId = $"memory-{id}";
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = memoryItemId,
            Title = "Manual memory",
            SafeSummary = "Manually-created ephemeral memory.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["manual"],
            Visibility = MemoryVisibility.PrivateToOwner,
            RetentionKind = MemoryRetentionKind.Ephemeral,
            ExpiresAt = expiresAt,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now.AddDays(-2),
            CreatedBy = "memory-writer"
        });
        db.CollectionProvenance.Add(new CollectionProvenanceRecord
        {
            Id = $"provenance-{id}",
            MemoryItemId = memoryItemId,
            AuthenticatedActor = "memory-writer",
            ActorTrust = CollectionProvenance.ServiceTokenActorTrust,
            ClaimsTrust = CollectionProvenance.NoClaimsTrust,
            ReceivedAt = Now.AddDays(-2)
        });
    }

    private sealed class FailingOnceCleanupProcessor : IAutomaticTurnRetentionCleanupProcessor
    {
        public int CallCount { get; private set; }

        public Task<AutomaticTurnCleanupResult> ProcessBatchAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return CallCount == 1
                ? throw new InvalidOperationException("simulated cleanup failure")
                : Task.FromResult(new AutomaticTurnCleanupResult(0));
        }
    }
}
