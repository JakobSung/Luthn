using System.Text.Json;
using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Luthn.Core.Persistence.Tests;

public sealed class SafeProjectionPublicationTests
{
    [Fact]
    public async Task ApproveCreatesSafeOutboxAndAuditWithoutRawFields()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        db.SharedMemoryItems.Add(CreateSafeMemory(now));
        await db.SaveChangesAsync();

        var service = new SafeProjectionPublicationService(
            db,
            new LocalInstallationIdentityService(db));

        var result = await service.ApproveAsync("memory-1", "operator", now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(ExternalPublicationState.ApprovedForExternal, result.PublicationState);
        Assert.Equal(2, result.Revision);
        Assert.Equal(SafeProjectionSyncOutboxState.Pending, result.SyncState);
        var outbox = await db.SafeProjectionSyncOutbox.SingleAsync();
        using var envelope = JsonDocument.Parse(outbox.SafeEnvelopeJson);
        var propertyNames = envelope.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Contains("safeSummary", propertyNames);
        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("raw", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("transcript", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, await db.LocalInstallationStates.CountAsync());
        Assert.Contains(await db.AuditEvents.ToListAsync(), audit =>
            audit.Action == "memory.external_publication.approved");
    }

    [Fact]
    public async Task RepeatedApprovalIsIdempotentAndRevokeEmitsBodyFreeTombstone()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        db.SharedMemoryItems.Add(CreateSafeMemory(now));
        await db.SaveChangesAsync();
        var service = new SafeProjectionPublicationService(
            db,
            new LocalInstallationIdentityService(db));

        await service.ApproveAsync("memory-1", "operator", now.AddMinutes(1), CancellationToken.None);
        await service.ApproveAsync("memory-1", "operator", now.AddMinutes(2), CancellationToken.None);
        var revoked = await service.RevokeAsync("memory-1", "operator", now.AddMinutes(3), CancellationToken.None);

        Assert.Equal(ExternalPublicationState.Revoked, revoked.PublicationState);
        Assert.Equal(3, revoked.Revision);
        Assert.Equal(2, await db.SafeProjectionSyncOutbox.CountAsync());
        var tombstone = await db.SafeProjectionSyncOutbox
            .SingleAsync(record => record.Operation == SafeProjectionSyncOperation.Revoke);
        using var envelope = JsonDocument.Parse(tombstone.SafeEnvelopeJson);
        var propertyNames = envelope.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("title", propertyNames);
        Assert.DoesNotContain("safeSummary", propertyNames);
        Assert.DoesNotContain("expiresAt", propertyNames);
        Assert.DoesNotContain("provenanceDigest", propertyNames);
    }

    [Fact]
    public async Task ApprovalRejectsMemoryThatIsNotAgentSafe()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        var memory = CreateSafeMemory(now);
        memory.Sensitivity = SensitivityLevel.Confidential;
        memory.AllowsAgentContext = false;
        db.SharedMemoryItems.Add(memory);
        await db.SaveChangesAsync();
        var service = new SafeProjectionPublicationService(
            db,
            new LocalInstallationIdentityService(db));

        await Assert.ThrowsAsync<SafeProjectionPublicationException>(() =>
            service.ApproveAsync("memory-1", "operator", now.AddMinutes(1), CancellationToken.None));

        Assert.Empty(await db.SafeProjectionSyncOutbox.ToListAsync());
    }

    [Fact]
    public async Task ProcessorLeavesPendingRowsUntouchedWhenTransportIsDisabled()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        db.SafeProjectionSyncOutbox.Add(CreateOutbox(now));
        await db.SaveChangesAsync();
        var processor = new SafeProjectionOutboxProcessor(db, new DisabledSafeProjectionSyncTransport());

        var result = await processor.ProcessBatchAsync(now, cancellationToken: CancellationToken.None);

        Assert.Equal(SafeProjectionSyncTransportState.Disabled, result.TransportState);
        Assert.Equal(0, result.ClaimedCount);
        var record = await db.SafeProjectionSyncOutbox.SingleAsync();
        Assert.Equal(SafeProjectionSyncOutboxState.Pending, record.State);
        Assert.Equal(0, record.AttemptCount);
    }

    [Fact]
    public async Task ProcessorAcknowledgesReadyTransportAndPersistsCheckpoint()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        db.SafeProjectionSyncOutbox.Add(CreateOutbox(now));
        await db.SaveChangesAsync();
        var processor = new SafeProjectionOutboxProcessor(db, new AcceptingTransport());

        var result = await processor.ProcessBatchAsync(now, cancellationToken: CancellationToken.None);

        Assert.Equal(1, result.AcknowledgedCount);
        Assert.Equal(SafeProjectionSyncOutboxState.Acknowledged, (await db.SafeProjectionSyncOutbox.SingleAsync()).State);
        var checkpoint = await db.SafeProjectionSyncCheckpoints.SingleAsync();
        Assert.Equal("test-ready", checkpoint.TransportName);
        Assert.Equal("checkpoint-1", checkpoint.Checkpoint);
    }

    [Fact]
    public async Task ProcessorRetriesFailureAndDoesNotResendAcknowledgedRows()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        db.SafeProjectionSyncOutbox.Add(CreateOutbox(now));
        await db.SaveChangesAsync();
        var transport = new SequencedTransport(
            new SafeProjectionSyncTransportResult(false, ErrorCode: "remote.unavailable"),
            new SafeProjectionSyncTransportResult(true, "checkpoint-2"));
        var processor = new SafeProjectionOutboxProcessor(db, transport);

        var failed = await processor.ProcessBatchAsync(now, cancellationToken: CancellationToken.None);
        var failedRecord = await db.SafeProjectionSyncOutbox.SingleAsync();

        Assert.Equal(1, failed.FailedCount);
        Assert.Equal(SafeProjectionSyncOutboxState.Failed, failedRecord.State);
        Assert.Equal("remote.unavailable", failedRecord.LastErrorCode);
        Assert.True(failedRecord.NextAttemptAt > now);

        var acknowledged = await processor.ProcessBatchAsync(now.AddMinutes(1), cancellationToken: CancellationToken.None);
        var duplicatePass = await processor.ProcessBatchAsync(now.AddMinutes(2), cancellationToken: CancellationToken.None);

        Assert.Equal(1, acknowledged.AcknowledgedCount);
        Assert.Equal(0, duplicatePass.ClaimedCount);
        Assert.Equal(2, transport.SendCount);
        Assert.Equal(2, (await db.SafeProjectionSyncOutbox.SingleAsync()).AttemptCount);
    }

    [Fact]
    public async Task ProcessorRecoversExpiredLeaseAfterRestart()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        var outbox = CreateOutbox(now.AddMinutes(-10));
        outbox.State = SafeProjectionSyncOutboxState.Processing;
        outbox.ProcessingStartedAt = now.AddMinutes(-10);
        outbox.AttemptCount = 1;
        db.SafeProjectionSyncOutbox.Add(outbox);
        await db.SaveChangesAsync();
        var transport = new AcceptingTransport();
        var processor = new SafeProjectionOutboxProcessor(db, transport);

        var result = await processor.ProcessBatchAsync(
            now,
            processingLease: TimeSpan.FromMinutes(5),
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, result.RecoveredCount);
        Assert.Equal(1, result.AcknowledgedCount);
        Assert.Equal(SafeProjectionSyncOutboxState.Acknowledged, (await db.SafeProjectionSyncOutbox.SingleAsync()).State);
    }

    [Fact]
    public async Task ProcessorSupersedesOlderFailedUpsertBeforeSendingRevoke()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        var upsert = SafeProjectionSyncPolicy.CreateUpsert(
            "instance-test",
            "memory-1",
            2,
            "Safe runbook",
            "Public-safe deployment steps.",
            ["runbook"],
            ExternalPublicationState.ApprovedForExternal,
            SensitivityLevel.Public,
            MemoryVisibility.SharedAcrossAgents,
            now.AddDays(-1),
            now.AddMinutes(-2),
            now.AddMinutes(-2),
            expiresAt: null);
        var revoke = SafeProjectionSyncPolicy.CreateRevoke(
            "instance-test",
            "memory-1",
            3,
            now.AddDays(-1),
            now,
            now);
        var older = CreateOutbox(upsert, now.AddMinutes(-2));
        older.State = SafeProjectionSyncOutboxState.Failed;
        older.NextAttemptAt = now;
        db.SafeProjectionSyncOutbox.AddRange(older, CreateOutbox(revoke, now));
        await db.SaveChangesAsync();
        var transport = new CapturingTransport();
        var processor = new SafeProjectionOutboxProcessor(db, transport);

        var result = await processor.ProcessBatchAsync(now, cancellationToken: CancellationToken.None);

        Assert.Equal(1, result.SupersededCount);
        Assert.Equal([SafeProjectionSyncOperation.Revoke], transport.Operations);
        Assert.Equal(
            SafeProjectionSyncOutboxState.Superseded,
            (await db.SafeProjectionSyncOutbox.SingleAsync(record => record.Revision == 2)).State);
        Assert.Equal(
            SafeProjectionSyncOutboxState.Acknowledged,
            (await db.SafeProjectionSyncOutbox.SingleAsync(record => record.Revision == 3)).State);
    }

    [Fact]
    public async Task RecoveryMarksLeaseExpiredFinalAttemptAsFailed()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        var outbox = CreateOutbox(now.AddMinutes(-10));
        outbox.State = SafeProjectionSyncOutboxState.Processing;
        outbox.ProcessingStartedAt = now.AddMinutes(-10);
        outbox.AttemptCount = 5;
        db.SafeProjectionSyncOutbox.Add(outbox);
        await db.SaveChangesAsync();
        var processor = new SafeProjectionOutboxProcessor(db, new DisabledSafeProjectionSyncTransport());

        var result = await processor.ProcessBatchAsync(
            now,
            maxAttempts: 5,
            processingLease: TimeSpan.FromMinutes(5),
            cancellationToken: CancellationToken.None);

        var recovered = await db.SafeProjectionSyncOutbox.SingleAsync();
        Assert.Equal(1, result.RecoveredCount);
        Assert.Equal(SafeProjectionSyncOutboxState.Failed, recovered.State);
        Assert.Equal("processing.lease_expired_attempts_exhausted", recovered.LastErrorCode);
    }

    [Fact]
    public async Task LocalInstallationIdentitySurvivesDbContextRestart()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var root = new InMemoryDatabaseRoot();
        var now = DateTimeOffset.Parse("2026-07-13T01:00:00Z");
        string originInstanceId;

        await using (var firstDb = CreateDbContext(databaseName, root))
        {
            originInstanceId = (await new LocalInstallationIdentityService(firstDb)
                .GetOrCreateAsync(now, CancellationToken.None)).OriginInstanceId;
        }

        await using (var restartedDb = CreateDbContext(databaseName, root))
        {
            var restartedIdentity = await new LocalInstallationIdentityService(restartedDb)
                .GetOrCreateAsync(now.AddDays(1), CancellationToken.None);
            Assert.Equal(originInstanceId, restartedIdentity.OriginInstanceId);
            Assert.Equal(now, restartedIdentity.CreatedAt);
        }
    }

    private static SharedMemoryItemRecord CreateSafeMemory(DateTimeOffset now) => new()
    {
        Id = "memory-1",
        Title = "Safe runbook",
        SafeSummary = "Public-safe deployment steps.",
        Sensitivity = SensitivityLevel.Public,
        CoreTags = ["runbook"],
        Visibility = MemoryVisibility.SharedAcrossAgents,
        RetentionKind = MemoryRetentionKind.Durable,
        AllowsAgentContext = true,
        CreatedAt = now,
        UpdatedAt = now,
        CreatedBy = "local-tools"
    };

    private static SafeProjectionSyncOutboxRecord CreateOutbox(DateTimeOffset now)
    {
        var envelope = SafeProjectionSyncPolicy.CreateRevoke(
            "instance-test",
            "memory-1",
            2,
            now,
            now,
            now);
        return CreateOutbox(envelope, now);
    }

    private static SafeProjectionSyncOutboxRecord CreateOutbox(
        SafeProjectionSyncEnvelope envelope,
        DateTimeOffset now) =>
        new()
        {
            Id = $"sync-{envelope.Revision}",
            IdempotencyKey = SafeProjectionSyncPolicy.CreateIdempotencyKey(envelope),
            OriginInstanceId = envelope.OriginInstanceId,
            LocalRecordId = envelope.LocalRecordId,
            Revision = envelope.Revision,
            Operation = envelope.Operation,
            ContractVersion = envelope.ContractVersion,
            SafeEnvelopeJson = JsonSerializer.Serialize(envelope),
            State = SafeProjectionSyncOutboxState.Pending,
            CreatedAt = now
        };

    private static LuthnDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new LuthnDbContext(options);
    }

    private static LuthnDbContext CreateDbContext(string databaseName, InMemoryDatabaseRoot root)
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(databaseName, root)
            .Options;
        return new LuthnDbContext(options);
    }

    private sealed class AcceptingTransport : ISafeProjectionSyncTransport
    {
        public string Name => "test-ready";
        public SafeProjectionSyncTransportState State => SafeProjectionSyncTransportState.Ready;

        public Task<SafeProjectionSyncTransportResult> SendAsync(
            SafeProjectionSyncEnvelope envelope,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SafeProjectionSyncTransportResult(true, "checkpoint-1"));
    }

    private sealed class SequencedTransport(params SafeProjectionSyncTransportResult[] results)
        : ISafeProjectionSyncTransport
    {
        private readonly Queue<SafeProjectionSyncTransportResult> _results = new(results);

        public string Name => "test-sequenced";
        public SafeProjectionSyncTransportState State => SafeProjectionSyncTransportState.Ready;
        public int SendCount { get; private set; }

        public Task<SafeProjectionSyncTransportResult> SendAsync(
            SafeProjectionSyncEnvelope envelope,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CapturingTransport : ISafeProjectionSyncTransport
    {
        public string Name => "test-capturing";
        public SafeProjectionSyncTransportState State => SafeProjectionSyncTransportState.Ready;
        public List<SafeProjectionSyncOperation> Operations { get; } = [];

        public Task<SafeProjectionSyncTransportResult> SendAsync(
            SafeProjectionSyncEnvelope envelope,
            CancellationToken cancellationToken)
        {
            Operations.Add(envelope.Operation);
            return Task.FromResult(new SafeProjectionSyncTransportResult(true));
        }
    }
}
