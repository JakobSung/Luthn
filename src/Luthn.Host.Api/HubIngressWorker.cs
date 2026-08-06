using System.Security.Cryptography;
using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed record HubIngressProcessResult(
    int RecoveredCount,
    int ClaimedCount,
    int CompletedCount,
    int RetryScheduledCount,
    int DeadLetterCount);

public sealed class HubIngressQueueProcessor(
    LuthnDbContext db,
    IHubIngressCapsuleProtector protector,
    IContentClassifier classifier,
    IPolicyEngine policy,
    IOptions<HubIngressOptions> options,
    TimeProvider timeProvider)
{
    private readonly HubIngressOptions _options = options.Value;

    public async Task<HubIngressProcessResult> ProcessBatchAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var recovered = await RecoverExpiredLeasesAsync(now, cancellationToken);
        var candidateIds = await SelectFairCandidatesAsync(now, cancellationToken);
        var claimed = 0;
        var completed = 0;
        var retryScheduled = 0;
        var deadLetter = recovered.DeadLetterCount;

        foreach (var id in candidateIds)
        {
            db.ChangeTracker.Clear();
            var record = await db.HubIngressQueue.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (record is null ||
                record.State is not (HubIngressQueueState.Pending or HubIngressQueueState.Failed) ||
                record.NextAttemptAt is { } nextAttemptAt && nextAttemptAt > now)
            {
                continue;
            }

            record.State = HubIngressQueueState.Processing;
            record.AttemptCount++;
            record.ProcessingStartedAt = now;
            record.LeaseExpiresAt = now.AddSeconds(_options.WorkerLeaseSeconds);
            record.NextAttemptAt = null;
            await db.SaveChangesAsync(cancellationToken);
            claimed++;

            try
            {
                if (!string.Equals(record.ProtectionScheme, protector.ProtectionScheme, StringComparison.Ordinal))
                {
                    throw new HubIngressProtectionException();
                }
                var capsule = protector.Unprotect(record.Id, record.ProtectedCapsule);
                var classification = await classifier.ClassifyAsync(
                    new PublicRecordId(record.Id),
                    capsule,
                    "hub-turn-capsule",
                    cancellationToken);
                var decision = policy.Decide(classification);

                record.State = HubIngressQueueState.Completed;
                record.CompletedAt = timeProvider.GetUtcNow();
                record.LeaseExpiresAt = null;
                record.LastErrorCode = null;
                record.Sensitivity = classification.Sensitivity.ToString();
                record.StorageDecision = decision.Kind.ToString();
                record.ContainsSensitiveMaterial = classification.ContainsSensitiveMaterial;
                db.AuditEvents.Add(CreateAudit(record, "hub.ingress.classified", "completed", record.CompletedAt.Value));
                await db.SaveChangesAsync(cancellationToken);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is ClassificationProviderException or
                HubIngressProtectionException or CryptographicException)
            {
                var permanentProtectionFailure = error is HubIngressProtectionException or CryptographicException;
                var exhausted = permanentProtectionFailure || record.AttemptCount >= _options.WorkerMaxAttempts;
                record.State = exhausted ? HubIngressQueueState.DeadLetter : HubIngressQueueState.Failed;
                record.LeaseExpiresAt = null;
                record.ProcessingStartedAt = null;
                record.LastErrorCode = permanentProtectionFailure
                    ? "hub.ingress.protection_failed"
                    : exhausted
                        ? "hub.ingress.classification_exhausted"
                        : "hub.ingress.classification_retry";
                record.NextAttemptAt = exhausted
                    ? null
                    : now.AddSeconds(ComputeBackoffSeconds(record.AttemptCount));
                db.AuditEvents.Add(CreateAudit(
                    record,
                    exhausted ? "hub.ingress.dead_lettered" : "hub.ingress.retry_scheduled",
                    exhausted ? "dead_letter" : "retry",
                    timeProvider.GetUtcNow()));
                await db.SaveChangesAsync(cancellationToken);
                if (exhausted)
                {
                    deadLetter++;
                }
                else
                {
                    retryScheduled++;
                }
            }
        }

        return new HubIngressProcessResult(
            recovered.RecoveredCount,
            claimed,
            completed,
            retryScheduled,
            deadLetter);
    }

    public async Task<HubIngressReceipt?> ReplayDeadLetterAsync(
        string receiptId,
        LuthnRequestPrincipal principal,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var record = await db.HubIngressQueue.SingleOrDefaultAsync(item =>
            item.ReceiptId == receiptId &&
            item.WorkspaceId == principal.WorkspaceId &&
            item.State == HubIngressQueueState.DeadLetter,
            cancellationToken);
        if (record is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        record.State = HubIngressQueueState.Pending;
        record.AttemptCount = 0;
        record.NextAttemptAt = null;
        record.LeaseExpiresAt = null;
        record.ProcessingStartedAt = null;
        record.CompletedAt = null;
        record.LastErrorCode = null;
        record.Sensitivity = null;
        record.StorageDecision = null;
        record.ContainsSensitiveMaterial = null;
        db.AuditEvents.Add(AuditEventFactory.ForWorkspace(
            principal,
            actor,
            "hub.ingress.replayed",
            record.Id,
            "metadata-only",
            "dead-letter-requeued-no-content",
            now,
            subjectType: "hub_ingress_item",
            outcome: "requeued",
            correlationId: record.ReceiptId));
        await db.SaveChangesAsync(cancellationToken);
        return new HubIngressReceipt(record.ReceiptId, record.State.ToString(), false, record.AcceptedAt);
    }

    private async Task<HubIngressLeaseRecovery> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expired = await db.HubIngressQueue
            .Where(record =>
                record.State == HubIngressQueueState.Processing &&
                record.LeaseExpiresAt != null &&
                record.LeaseExpiresAt <= now)
            .OrderBy(record => record.LeaseExpiresAt)
            .ThenBy(record => record.Id)
            .Take(_options.WorkerBatchSize)
            .ToArrayAsync(cancellationToken);
        var deadLetters = 0;
        foreach (var record in expired)
        {
            var exhausted = record.AttemptCount >= _options.WorkerMaxAttempts;
            record.State = exhausted ? HubIngressQueueState.DeadLetter : HubIngressQueueState.Failed;
            record.NextAttemptAt = exhausted ? null : now;
            record.LeaseExpiresAt = null;
            record.ProcessingStartedAt = null;
            record.LastErrorCode = exhausted
                ? "hub.ingress.lease_exhausted"
                : "hub.ingress.lease_recovered";
            db.AuditEvents.Add(CreateAudit(
                record,
                exhausted ? "hub.ingress.dead_lettered" : "hub.ingress.lease_recovered",
                exhausted ? "dead_letter" : "recovered",
                now));
            if (exhausted)
            {
                deadLetters++;
            }
        }
        if (expired.Length > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return new HubIngressLeaseRecovery(expired.Length, deadLetters);
    }

    private async Task<string[]> SelectFairCandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidates = await db.HubIngressQueue.AsNoTracking()
            .Where(record =>
                (record.State == HubIngressQueueState.Pending || record.State == HubIngressQueueState.Failed) &&
                (record.NextAttemptAt == null || record.NextAttemptAt <= now))
            .OrderBy(record => record.AcceptedAt)
            .ThenBy(record => record.Id)
            .Take(_options.WorkerBatchSize * 8)
            .Select(record => new { record.Id, record.WorkspaceId, record.AcceptedAt })
            .ToArrayAsync(cancellationToken);

        var queues = candidates
            .GroupBy(candidate => candidate.WorkspaceId, StringComparer.Ordinal)
            .OrderBy(group => group.Min(candidate => candidate.AcceptedAt))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Queue<string>(group
                    .Take(_options.WorkerPerWorkspaceBatchLimit)
                    .Select(candidate => candidate.Id)),
                StringComparer.Ordinal);
        var selected = new List<string>(_options.WorkerBatchSize);
        while (selected.Count < _options.WorkerBatchSize && queues.Values.Any(queue => queue.Count > 0))
        {
            foreach (var queue in queues.Values)
            {
                if (queue.TryDequeue(out var id))
                {
                    selected.Add(id);
                    if (selected.Count == _options.WorkerBatchSize)
                    {
                        break;
                    }
                }
            }
        }
        return selected.ToArray();
    }

    private int ComputeBackoffSeconds(int attemptCount)
    {
        var multiplier = 1 << Math.Clamp(attemptCount - 1, 0, 10);
        return Math.Min(_options.WorkerBaseRetrySeconds * multiplier, 3600);
    }

    private static AuditEventRecord CreateAudit(
        HubIngressQueueRecord record,
        string action,
        string outcome,
        DateTimeOffset occurredAt) =>
        AuditEventFactory.ForWorkspace(
            record.WorkspaceId,
            record.MemberUserId,
            "system",
            "luthn-hub-worker",
            action,
            record.Id,
            "metadata-only",
            "queue-state-only-no-content",
            occurredAt,
            subjectType: "hub_ingress_item",
            outcome: outcome,
            correlationId: record.ReceiptId);

    private sealed record HubIngressLeaseRecovery(int RecoveredCount, int DeadLetterCount);
    private sealed class HubIngressProtectionException : Exception;
}

internal sealed class HubIngressWorkerHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<HubIngressOptions> options,
    ILogger<HubIngressWorkerHostedService> logger) : BackgroundService
{
    private readonly HubIngressOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.WorkerEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<HubIngressQueueProcessor>();
                await processor.ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Hub ingress worker batch failed without emitting capsule content.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.WorkerPollSeconds), stoppingToken);
        }
    }
}
