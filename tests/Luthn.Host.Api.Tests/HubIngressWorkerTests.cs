using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api.Tests;

public sealed class HubIngressWorkerTests
{
    [Fact]
    public async Task DelayedProviderDoesNotBlockIngressAndQueueAgeRemainsVisible()
    {
        var root = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString("N");
        var dbOptions = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(databaseName, root)
            .Options;
        await using var workerDb = new LuthnDbContext(dbOptions);
        await using var ingressDb = new LuthnDbContext(dbOptions);
        await using var statusDb = new LuthnDbContext(dbOptions);
        var protector = CreateProtector();
        var delayedRecord = CreateRecord(protector, "delayed-1", "workspace-a", "delayed decision");
        delayedRecord.AcceptedAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        workerDb.HubIngressQueue.Add(delayedRecord);
        await workerDb.SaveChangesAsync();
        var classifier = new ControlledClassifier();
        var metrics = new HubOperationalMetrics();
        var options = new HubIngressOptions();
        var processor = new HubIngressQueueProcessor(
            workerDb,
            protector,
            classifier,
            new PolicyEngine(),
            Options.Create(options),
            TimeProvider.System,
            metrics);

        var processing = processor.ProcessBatchAsync();
        await classifier.Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(processing.IsCompleted);

        var queue = new HubIngressQueueService(
            ingressDb,
            protector,
            Options.Create(options),
            TimeProvider.System,
            new HubIngressAdmissionCoordinator(),
            metrics);
        const string capsule = "ingress while provider waits";
        var principal = new LuthnRequestPrincipal(
            "member-2",
            "workspace-b",
            LuthnActorKind.Agent,
            "agent",
            false,
            "organization-1",
            "connection-b",
            "codex",
            "session-b");
        var admission = await queue.EnqueueAsync(
            new HubIngressRequest("event-during-delay", Digest(capsule), capsule),
            principal,
            "agent",
            default);
        var status = await new HubOperationalStatusService(
            statusDb,
            new DisabledHubOutboundRelayTransport(),
            metrics,
            TimeProvider.System).ReadAsync(default);

        Assert.Equal(HubIngressAdmissionKind.Accepted, admission.Kind);
        Assert.Equal(1, status.IngressQueue.Pending);
        Assert.Equal(1, status.IngressQueue.Processing);
        Assert.True(status.IngressQueue.OldestPendingAgeSeconds >= 29);
        classifier.Release();
        var completed = await processing;
        Assert.Equal(1, completed.CompletedCount);
    }

    [Fact]
    public async Task FiveAndThirtySecondEquivalentProviderLatencyIsReportedContentFree()
    {
        await using var db = CreateDb();
        var protector = CreateProtector();
        db.HubIngressQueue.AddRange(
            CreateRecord(protector, "latency-5", "workspace-a", "first private decision"),
            CreateRecord(protector, "latency-30", "workspace-b", "second private decision"));
        await db.SaveChangesAsync();
        var timeProvider = new ManualTimeProvider();
        var metrics = new HubOperationalMetrics();
        var processor = new HubIngressQueueProcessor(
            db,
            protector,
            new AdvancingClassifier(timeProvider, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)),
            new PolicyEngine(),
            Options.Create(new HubIngressOptions()),
            timeProvider,
            metrics);

        var result = await processor.ProcessBatchAsync();
        var providerLatency = metrics.Snapshot().ProviderLatency;

        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(2, providerLatency.Count);
        Assert.Equal(35_000, providerLatency.TotalDurationMilliseconds);
        Assert.Equal(30_000, providerLatency.MaxDurationMilliseconds);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(providerLatency), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TenAndFiftyUserLoadDrainsWithoutLossOrDuplicate()
    {
        await using var db = CreateDb();
        var protector = CreateProtector();
        var tenNormalUsers = Enumerable.Range(1, 10)
            .Select(index => CreateRecord(
                protector,
                $"normal-{index}",
                $"workspace-normal-{index}",
                $"normal decision {index}"));
        var fiftyUsers = Enumerable.Range(1, 50)
            .Select(index => CreateRecord(
                protector,
                $"sustained-{index}",
                $"workspace-sustained-{index}",
                $"sustained decision {index}"));
        db.HubIngressQueue.AddRange(tenNormalUsers.Concat(fiftyUsers));
        await db.SaveChangesAsync();
        var processor = CreateProcessor(db, protector, new MockContentClassifier(), new HubIngressOptions
        {
            WorkerBatchSize = 20,
            WorkerPerWorkspaceBatchLimit = 1
        });

        var processed = 0;
        while (true)
        {
            var batch = await processor.ProcessBatchAsync();
            processed += batch.CompletedCount;
            if (batch.ClaimedCount == 0)
            {
                break;
            }
        }

        Assert.Equal(60, processed);
        Assert.Equal(60, await db.HubIngressQueue.CountAsync());
        Assert.Equal(60, await db.HubIngressQueue.CountAsync(record =>
            record.State == HubIngressQueueState.Completed));
        Assert.Equal(60, await db.HubIngressQueue.Select(record => record.ReceiptId).Distinct().CountAsync());
    }

    [Fact]
    public async Task HubWorkerFairnessClaimsAtMostOneItemPerWorkspacePerBatch()
    {
        await using var db = CreateDb();
        var protector = CreateProtector();
        db.HubIngressQueue.AddRange(
            CreateRecord(protector, "a-1", "workspace-a", "first decision"),
            CreateRecord(protector, "a-2", "workspace-a", "second decision"),
            CreateRecord(protector, "b-1", "workspace-b", "other decision"));
        await db.SaveChangesAsync();
        var processor = CreateProcessor(db, protector, new MockContentClassifier(), new HubIngressOptions
        {
            WorkerBatchSize = 2,
            WorkerPerWorkspaceBatchLimit = 1
        });

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(2, result.ClaimedCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(
            2,
            await db.HubIngressQueue.CountAsync(record => record.State == HubIngressQueueState.Completed));
        Assert.Equal(
            1,
            await db.HubIngressQueue.CountAsync(record =>
                record.WorkspaceId == "workspace-a" && record.State == HubIngressQueueState.Completed));
        Assert.Equal(
            1,
            await db.HubIngressQueue.CountAsync(record =>
                record.WorkspaceId == "workspace-b" && record.State == HubIngressQueueState.Completed));
    }

    [Fact]
    public async Task HubWorkerLeaseRecoveryProcessesExpiredWorkAfterRestart()
    {
        await using var db = CreateDb();
        var protector = CreateProtector();
        var record = CreateRecord(protector, "restart-1", "workspace-a", "restart decision");
        record.State = HubIngressQueueState.Processing;
        record.AttemptCount = 1;
        record.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        db.HubIngressQueue.Add(record);
        await db.SaveChangesAsync();
        var processor = CreateProcessor(db, protector, new MockContentClassifier(), new HubIngressOptions());

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(1, result.RecoveredCount);
        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(HubIngressQueueState.Completed, (await db.HubIngressQueue.SingleAsync()).State);
        Assert.Contains(await db.AuditEvents.ToArrayAsync(), audit => audit.Action == "hub.ingress.lease_recovered");
    }

    [Fact]
    public async Task HubWorkerRetryDeadLetterAndReplayAreBoundedAndMetadataOnly()
    {
        await using var db = CreateDb();
        var protector = CreateProtector();
        const string capsule = "credential secret prompt transcript /Users/member/private";
        db.HubIngressQueue.Add(CreateRecord(protector, "failure-1", "workspace-a", capsule));
        await db.SaveChangesAsync();
        var options = new HubIngressOptions { WorkerMaxAttempts = 2, WorkerBaseRetrySeconds = 1 };
        var failing = CreateProcessor(db, protector, new FailingClassifier(), options);

        var first = await failing.ProcessBatchAsync();
        var failed = await db.HubIngressQueue.SingleAsync();
        failed.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        var second = await failing.ProcessBatchAsync();
        var deadLetter = await db.HubIngressQueue.SingleAsync();

        Assert.Equal(1, first.RetryScheduledCount);
        Assert.Equal(1, second.DeadLetterCount);
        Assert.Equal(HubIngressQueueState.DeadLetter, deadLetter.State);
        Assert.Equal("hub.ingress.classification_exhausted", deadLetter.LastErrorCode);
        var auditJson = JsonSerializer.Serialize(await db.AuditEvents.ToArrayAsync());
        Assert.DoesNotContain(capsule, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/member", auditJson, StringComparison.Ordinal);

        var principal = new LuthnRequestPrincipal(
            "member-1", "workspace-a", LuthnActorKind.User, "operator", true);
        var replay = await failing.ReplayDeadLetterAsync(deadLetter.ReceiptId, principal, "operator", default);
        Assert.NotNull(replay);
        Assert.Equal("Pending", replay.State);
        Assert.Equal(0, (await db.HubIngressQueue.SingleAsync()).AttemptCount);

        var succeeding = CreateProcessor(db, protector, new MockContentClassifier(), options);
        var completed = await succeeding.ProcessBatchAsync();
        Assert.Equal(1, completed.CompletedCount);
        Assert.Equal(HubIngressQueueState.Completed, (await db.HubIngressQueue.SingleAsync()).State);
        Assert.Contains(await db.AuditEvents.ToArrayAsync(), audit => audit.Action == "hub.ingress.replayed");
    }

    [Fact]
    public async Task HubWorkerProtectionFailureNeverCreatesSafeClassification()
    {
        await using var db = CreateDb();
        var protector = CreateProtector();
        var record = CreateRecord(protector, "protection-1", "workspace-a", "private value");
        record.ProtectionScheme = "unknown:v9";
        db.HubIngressQueue.Add(record);
        await db.SaveChangesAsync();
        var processor = CreateProcessor(db, protector, new MockContentClassifier(), new HubIngressOptions());

        var result = await processor.ProcessBatchAsync();
        var failed = await db.HubIngressQueue.SingleAsync();

        Assert.Equal(1, result.DeadLetterCount);
        Assert.Equal(HubIngressQueueState.DeadLetter, failed.State);
        Assert.Null(failed.Sensitivity);
        Assert.Null(failed.StorageDecision);
        Assert.Null(failed.ContainsSensitiveMaterial);
        Assert.Equal("hub.ingress.protection_failed", failed.LastErrorCode);
    }

    private static LuthnDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new LuthnDbContext(options);
    }

    private static IHubIngressCapsuleProtector CreateProtector() =>
        new DataProtectionHubIngressCapsuleProtector(new EphemeralDataProtectionProvider());

    private static HubIngressQueueProcessor CreateProcessor(
        LuthnDbContext db,
        IHubIngressCapsuleProtector protector,
        IContentClassifier classifier,
        HubIngressOptions options) =>
        new(db, protector, classifier, new PolicyEngine(), Options.Create(options), TimeProvider.System);

    private static HubIngressQueueRecord CreateRecord(
        IHubIngressCapsuleProtector protector,
        string suffix,
        string workspaceId,
        string capsule)
    {
        var id = $"hub-ingress-{suffix}";
        return new HubIngressQueueRecord
        {
            Id = id,
            ReceiptId = $"hub-receipt-{suffix}",
            OrganizationId = "organization-1",
            WorkspaceId = workspaceId,
            MemberUserId = "member-1",
            AgentConnectionId = $"connection-{suffix}",
            AgentId = "codex",
            SessionId = "session-1",
            TurnId = $"turn-{suffix}",
            IdempotencyKey = $"event-{suffix}",
            ContentDigest = $"sha256:{new string('a', 64)}",
            CapsuleSizeBytes = capsule.Length,
            ProtectionScheme = protector.ProtectionScheme,
            ProtectedCapsule = protector.Protect(id, capsule),
            State = HubIngressQueueState.Pending,
            AcceptedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class FailingClassifier : IContentClassifier
    {
        public ClassificationProviderBoundary Boundary { get; } =
            new("failure", "local-classification-input", "local-only");

        public ValueTask<ClassificationResult> ClassifyAsync(
            PublicRecordId sourceId,
            string content,
            string? sourceType,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ClassificationResult>(
                new ClassificationProviderException("provider unavailable with no content echo"));
    }

    private sealed class ControlledClassifier : IContentClassifier
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public ClassificationProviderBoundary Boundary { get; } =
            new("controlled-delay", "local-classification-input", "local-only");

        public void Release() => _released.TrySetResult();

        public async ValueTask<ClassificationResult> ClassifyAsync(
            PublicRecordId sourceId,
            string content,
            string? sourceType,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return await new MockContentClassifier().ClassifyAsync(
                sourceId,
                content,
                sourceType,
                cancellationToken);
        }
    }

    private sealed class AdvancingClassifier(
        ManualTimeProvider timeProvider,
        params TimeSpan[] durations) : IContentClassifier
    {
        private int _index;
        public ClassificationProviderBoundary Boundary { get; } =
            new("controlled-latency", "local-classification-input", "local-only");

        public async ValueTask<ClassificationResult> ClassifyAsync(
            PublicRecordId sourceId,
            string content,
            string? sourceType,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            timeProvider.Advance(durations[index]);
            return await new MockContentClassifier().ClassifyAsync(
                sourceId,
                content,
                sourceType,
                cancellationToken);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, (long)duration.TotalMilliseconds);
    }

    private static string Digest(string value) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";
}
