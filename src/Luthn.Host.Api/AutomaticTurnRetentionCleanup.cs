using System.Data;
using System.Security.Cryptography;
using System.Text;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed record AutomaticTurnCleanupResult(int DeletedCount);

public interface IAutomaticTurnRetentionCleanupProcessor
{
    Task<AutomaticTurnCleanupResult> ProcessBatchAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default);
}

public sealed class AutomaticTurnRetentionCleanupProcessor(LuthnDbContext db)
    : IAutomaticTurnRetentionCleanupProcessor
{
    public async Task<AutomaticTurnCleanupResult> ProcessBatchAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            batchSize,
            LuthnMemoryOptions.MinimumAutomaticTurnCleanupBatchSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            batchSize,
            LuthnMemoryOptions.MaximumAutomaticTurnCleanupBatchSize);

        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(
                () => ProcessBatchWithinExecutionStrategyAsync(now, batchSize, cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new AutomaticTurnCleanupResult(0);
        }
    }

    private async Task<AutomaticTurnCleanupResult> ProcessBatchWithinExecutionStrategyAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var lifecycleGateHeld = false;
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            var ordinaryCandidates = await (
                    from memory in db.SharedMemoryItems
                    join provenance in db.CollectionProvenance
                        on memory.Id equals provenance.MemoryItemId
                    join source in db.SourceEvents
                        on provenance.SourceEventId equals source.Id
                    where memory.RetentionKind == MemoryRetentionKind.Ephemeral
                        && memory.ExpiresAt != null
                        && memory.ExpiresAt <= now
                        && memory.ExternalPublicationState == ExternalPublicationState.LocalOnly
                        && source.SourceType == "turn-summary"
                        && memory.WorkspaceId == source.WorkspaceId
                        && memory.WorkspaceId == provenance.WorkspaceId
                        && memory.OwnerUserId == source.OwnerUserId
                        && memory.OwnerUserId == provenance.AuthenticatedUserId
                        && !db.SafeProjectionSyncOutbox.Any(
                            outbox => outbox.WorkspaceId == memory.WorkspaceId &&
                                outbox.LocalRecordId == memory.Id)
                        && !db.SensitiveRecordReferences.Any(
                            reference =>
                                reference.MemoryItemId == memory.Id ||
                                reference.SourceEventId == source.Id)
                    orderby memory.ExpiresAt, memory.CreatedAt, memory.Id
                    select new AutomaticTurnCleanupCandidate(
                        memory.Id,
                        source.Id,
                        memory.WorkspaceId,
                        memory.OwnerUserId,
                        memory.ExpiresAt!.Value,
                        SensitiveReferenceId: null))
                .AsNoTracking()
                .Take(batchSize)
                .ToArrayAsync(cancellationToken);

            var sensitiveCandidates = await (
                    from reference in db.SensitiveRecordReferences
                    join memory in db.SharedMemoryItems
                        on reference.MemoryItemId equals memory.Id
                    join payload in db.SensitiveMemoryPayloads
                        on memory.Id equals payload.MemoryItemId
                    join provenance in db.CollectionProvenance
                        on memory.Id equals provenance.MemoryItemId
                    join source in db.SourceEvents
                        on provenance.SourceEventId equals source.Id
                    where reference.MemoryItemId != null
                        && reference.ExpiresAt != null
                        && reference.ExpiresAt <= now
                        && reference.SourceEventId == source.Id
                        && reference.SourceSystem == source.SourceSystem
                        && reference.SourceType == "turn-summary"
                        && source.SourceType == "turn-summary"
                        && memory.RetentionKind == MemoryRetentionKind.Ephemeral
                        && memory.ExpiresAt == reference.ExpiresAt
                        && payload.ExpiresAt == reference.ExpiresAt
                        && memory.ExternalPublicationState == ExternalPublicationState.LocalOnly
                        && reference.WorkspaceId == memory.WorkspaceId
                        && reference.WorkspaceId == source.WorkspaceId
                        && reference.WorkspaceId == provenance.WorkspaceId
                        && reference.OwnerUserId == memory.OwnerUserId
                        && reference.OwnerUserId == source.OwnerUserId
                        && reference.OwnerUserId == provenance.AuthenticatedUserId
                        && !db.SafeProjectionSyncOutbox.Any(
                            outbox => outbox.WorkspaceId == memory.WorkspaceId &&
                                outbox.LocalRecordId == memory.Id)
                        && !db.SensitiveRecordReferences.Any(other =>
                            other.Id != reference.Id &&
                            (other.MemoryItemId == memory.Id || other.SourceEventId == source.Id))
                    orderby reference.ExpiresAt, memory.CreatedAt, memory.Id
                    select new AutomaticTurnCleanupCandidate(
                        memory.Id,
                        source.Id,
                        memory.WorkspaceId,
                        memory.OwnerUserId,
                        reference.ExpiresAt.GetValueOrDefault(),
                        reference.Id))
                .AsNoTracking()
                .Take(batchSize)
                .ToArrayAsync(cancellationToken);

            var candidates = ordinaryCandidates
                .Concat(sensitiveCandidates)
                .OrderBy(candidate => candidate.ExpiresAt)
                .ThenBy(candidate => candidate.MemoryItemId, StringComparer.Ordinal)
                .Take(batchSize)
                .ToArray();

            if (candidates.Length == 0)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return new AutomaticTurnCleanupResult(0);
            }

            if (candidates.Any(candidate => candidate.SensitiveReferenceId is not null))
            {
                await SensitiveAccessLifecycleGate.Instance.WaitAsync(cancellationToken);
                lifecycleGateHeld = true;
            }

            var memoryIds = candidates.Select(candidate => candidate.MemoryItemId).ToArray();
            var sourceEventIds = candidates.Select(candidate => candidate.SourceEventId).ToArray();
            var sensitiveReferenceIds = candidates
                .Where(candidate => candidate.SensitiveReferenceId is not null)
                .Select(candidate => candidate.SensitiveReferenceId!)
                .ToArray();
            var memories = await db.SharedMemoryItems
                .Where(record => memoryIds.Contains(record.Id))
                .ToArrayAsync(cancellationToken);
            var sources = await db.SourceEvents
                .Where(record => sourceEventIds.Contains(record.Id))
                .ToArrayAsync(cancellationToken);
            var classifications = await db.ClassificationResults
                .Where(record => sourceEventIds.Contains(record.SourceEventId))
                .ToArrayAsync(cancellationToken);
            var wikiProposals = await db.WikiProposals
                .Where(record => sourceEventIds.Contains(record.SourceEventId))
                .ToArrayAsync(cancellationToken);
            var payloads = await db.SensitiveMemoryPayloads
                .Where(record => memoryIds.Contains(record.MemoryItemId))
                .ToArrayAsync(cancellationToken);
            var references = await db.SensitiveRecordReferences
                .Where(record => sensitiveReferenceIds.Contains(record.Id))
                .ToArrayAsync(cancellationToken);
            var accessRequests = await db.SensitiveAccessRequests
                .Where(record => sensitiveReferenceIds.Contains(record.SensitiveRecordReferenceId))
                .ToArrayAsync(cancellationToken);
            var accessRequestIds = accessRequests.Select(record => record.Id).ToArray();
            var accessDecisions = await db.SensitiveAccessDecisions
                .Where(record => accessRequestIds.Contains(record.SensitiveAccessRequestId))
                .ToArrayAsync(cancellationToken);
            var accessGrants = await db.SensitiveAccessGrants
                .Where(record => accessRequestIds.Contains(record.SensitiveAccessRequestId))
                .ToArrayAsync(cancellationToken);
            var existingTombstones = await db.SensitiveAccessTombstones
                .Where(record => accessRequestIds.Contains(record.Id))
                .ToArrayAsync(cancellationToken);
            var provenanceLinks = await db.CollectionProvenance
                .AsNoTracking()
                .Where(record => memoryIds.Contains(record.MemoryItemId!) &&
                    sourceEventIds.Contains(record.SourceEventId!))
                .Select(record => new
                {
                    record.MemoryItemId,
                    record.SourceEventId,
                    record.WorkspaceId,
                    record.AuthenticatedUserId
                })
                .ToArrayAsync(cancellationToken);
            var outboxMemoryIds = await db.SafeProjectionSyncOutbox
                .AsNoTracking()
                .Where(record => memoryIds.Contains(record.LocalRecordId))
                .Select(record => record.LocalRecordId)
                .Distinct()
                .ToArrayAsync(cancellationToken);

            if (!db.Database.IsRelational())
            {
                await db.CollectionProvenance
                    .Where(record => memoryIds.Contains(record.MemoryItemId!) ||
                        sourceEventIds.Contains(record.SourceEventId!))
                    .LoadAsync(cancellationToken);
            }

            var completeCandidates = candidates
                .Where(candidate =>
                    memories.Any(memory =>
                        memory.Id == candidate.MemoryItemId &&
                        memory.WorkspaceId == candidate.WorkspaceId &&
                        memory.OwnerUserId == candidate.OwnerUserId &&
                        memory.RetentionKind == MemoryRetentionKind.Ephemeral &&
                        memory.ExpiresAt is not null &&
                        memory.ExpiresAt <= now &&
                        memory.ExternalPublicationState == ExternalPublicationState.LocalOnly) &&
                    sources.Any(source =>
                        source.Id == candidate.SourceEventId &&
                        source.WorkspaceId == candidate.WorkspaceId &&
                        source.OwnerUserId == candidate.OwnerUserId &&
                        source.SourceType == "turn-summary") &&
                    provenanceLinks.Any(provenance =>
                        provenance.MemoryItemId == candidate.MemoryItemId &&
                        provenance.SourceEventId == candidate.SourceEventId &&
                        provenance.WorkspaceId == candidate.WorkspaceId &&
                        provenance.AuthenticatedUserId == candidate.OwnerUserId) &&
                    !outboxMemoryIds.Contains(candidate.MemoryItemId) &&
                    IsSensitiveCandidateComplete(
                        candidate,
                        references,
                        payloads,
                        existingTombstones,
                        accessRequests))
                .ToArray();
            if (completeCandidates.Length != candidates.Length)
            {
                throw new DbUpdateConcurrencyException(
                    "An automatic turn cleanup candidate changed before the batch could be deleted.");
            }

            var graphCleanupAudits = completeCandidates
                .Where(candidate => !db.AuditEvents.Local.Any(audit =>
                    audit.Id == CleanupAuditId(candidate.WorkspaceId, candidate.SourceEventId)))
                .Select(candidate => AuditEventFactory.ForWorkspace(
                    candidate.WorkspaceId,
                    actorUserId: null,
                    actorKind: "system",
                    actor: "luthn-retention-cleanup",
                    action: "turn_summary.retention.pruned",
                    subjectId: candidate.SourceEventId,
                    payloadClass: "metadata-only",
                    redactionState: "expired-turn-capsule-deleted",
                    occurredAt: now,
                    subjectType: "source_event",
                    outcome: "pruned",
                    id: CleanupAuditId(candidate.WorkspaceId, candidate.SourceEventId)))
                .ToArray();
            var accessCleanupAudits = accessRequests.Select(request => AuditEventFactory.ForWorkspace(
                request.WorkspaceId,
                actorUserId: null,
                actorKind: "system",
                actor: "luthn-retention-cleanup",
                action: "sensitive_access.content_pruned",
                subjectId: request.Id,
                payloadClass: "metadata-only",
                redactionState: "expired-no-output",
                occurredAt: now,
                subjectType: "sensitive_access_request",
                outcome: "pruned",
                id: AccessCleanupAuditId(request.WorkspaceId, request.Id)))
                .ToArray();
            var cleanupAudits = graphCleanupAudits.Concat(accessCleanupAudits).ToArray();
            var cleanupAuditIds = cleanupAudits.Select(audit => audit.Id).ToArray();
            var existingCleanupAuditIds = await db.AuditEvents
                .AsNoTracking()
                .Where(audit => cleanupAuditIds.Contains(audit.Id))
                .Select(audit => audit.Id)
                .ToArrayAsync(cancellationToken);

            var tombstones = accessRequests
                .Where(request => existingTombstones.All(tombstone => tombstone.Id != request.Id))
                .Select(request =>
                {
                    var candidate = completeCandidates.Single(item =>
                        item.SensitiveReferenceId == request.SensitiveRecordReferenceId);
                    return new SensitiveAccessTombstoneRecord
                    {
                        Id = request.Id,
                        Status = SensitiveAccessRequestStatus.Expired,
                        ExpiredAt = candidate.ExpiresAt,
                        CleanedAt = now,
                        WorkspaceId = request.WorkspaceId,
                        OwnerUserId = request.OwnerUserId
                    };
                })
                .ToArray();

            db.SensitiveAccessTombstones.AddRange(tombstones);
            db.SensitiveAccessGrants.RemoveRange(accessGrants);
            db.SensitiveAccessDecisions.RemoveRange(accessDecisions);
            db.SensitiveAccessRequests.RemoveRange(accessRequests);
            db.SensitiveRecordReferences.RemoveRange(references);
            db.WikiProposals.RemoveRange(wikiProposals);
            db.ClassificationResults.RemoveRange(classifications);
            db.SensitiveMemoryPayloads.RemoveRange(payloads);
            db.SharedMemoryItems.RemoveRange(memories);
            db.SourceEvents.RemoveRange(sources);
            db.AuditEvents.AddRange(cleanupAudits.Where(audit =>
                !existingCleanupAuditIds.Contains(audit.Id)));

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new AutomaticTurnCleanupResult(completeCandidates.Length);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            db.ChangeTracker.Clear();

            throw;
        }
        finally
        {
            if (lifecycleGateHeld)
            {
                SensitiveAccessLifecycleGate.Instance.Release();
            }
        }
    }

    private sealed record AutomaticTurnCleanupCandidate(
        string MemoryItemId,
        string SourceEventId,
        string WorkspaceId,
        string OwnerUserId,
        DateTimeOffset ExpiresAt,
        string? SensitiveReferenceId);

    private static bool IsSensitiveCandidateComplete(
        AutomaticTurnCleanupCandidate candidate,
        IReadOnlyCollection<SensitiveRecordReferenceRecord> references,
        IReadOnlyCollection<SensitiveMemoryPayloadRecord> payloads,
        IReadOnlyCollection<SensitiveAccessTombstoneRecord> tombstones,
        IReadOnlyCollection<SensitiveAccessRequestRecord> requests)
    {
        if (candidate.SensitiveReferenceId is null)
        {
            return true;
        }

        var reference = references.SingleOrDefault(record => record.Id == candidate.SensitiveReferenceId);
        if (reference is null ||
            reference.MemoryItemId != candidate.MemoryItemId ||
            reference.SourceEventId != candidate.SourceEventId ||
            reference.WorkspaceId != candidate.WorkspaceId ||
            reference.OwnerUserId != candidate.OwnerUserId ||
            reference.SourceType != "turn-summary" ||
            reference.ExpiresAt != candidate.ExpiresAt ||
            !payloads.Any(payload =>
                payload.MemoryItemId == candidate.MemoryItemId &&
                payload.ExpiresAt == candidate.ExpiresAt))
        {
            return false;
        }

        return requests
            .Where(request => request.SensitiveRecordReferenceId == reference.Id)
            .All(request =>
                request.WorkspaceId == candidate.WorkspaceId &&
                request.OwnerUserId == candidate.OwnerUserId &&
                tombstones.All(tombstone =>
                    tombstone.Id != request.Id ||
                    (tombstone.WorkspaceId == request.WorkspaceId &&
                     tombstone.OwnerUserId == request.OwnerUserId &&
                     tombstone.Status == SensitiveAccessRequestStatus.Expired)));
    }

    private static string CleanupAuditId(string workspaceId, string sourceEventId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"turn_summary.retention.pruned\n{workspaceId}\n{sourceEventId}"));
        return $"audit-retention-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static string AccessCleanupAuditId(string workspaceId, string requestId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"sensitive_access.content_pruned\n{workspaceId}\n{requestId}"));
        return $"audit-sensitive-cleanup-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}

internal sealed class AutomaticTurnRetentionCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<LuthnMemoryOptions> options,
    TimeProvider timeProvider,
    ILogger<AutomaticTurnRetentionCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cleanupOptions = options.Value;
        if (!cleanupOptions.AutomaticTurnCleanupEnabled)
        {
            logger.LogInformation("Automatic turn retention cleanup is disabled.");
            return;
        }

        logger.LogInformation(
            "Automatic turn retention cleanup started: intervalMinutes={IntervalMinutes}, batchSize={BatchSize}.",
            cleanupOptions.AutomaticTurnCleanupIntervalMinutes,
            cleanupOptions.AutomaticTurnCleanupBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(cleanupOptions.AutomaticTurnCleanupBatchSize, stoppingToken);

            try
            {
                await Task.Delay(
                    cleanupOptions.AutomaticTurnCleanupInterval,
                    timeProvider,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var processor = scope.ServiceProvider
                .GetRequiredService<IAutomaticTurnRetentionCleanupProcessor>();
            var result = await processor.ProcessBatchAsync(
                timeProvider.GetUtcNow(),
                batchSize,
                cancellationToken);
            if (result.DeletedCount > 0)
            {
                logger.LogInformation(
                    "Automatic turn retention cleanup completed: deleted={DeletedCount}.",
                    result.DeletedCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "Automatic turn retention cleanup failed; the API remains available.");
        }
    }
}
