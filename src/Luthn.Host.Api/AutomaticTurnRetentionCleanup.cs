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

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
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
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var candidates = await (
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
                        && memory.OwnerUserId == source.OwnerUserId
                        && memory.OwnerUserId == provenance.AuthenticatedUserId
                        && !db.SafeProjectionSyncOutbox.Any(
                            outbox => outbox.LocalRecordId == memory.Id)
                    orderby memory.ExpiresAt, memory.CreatedAt, memory.Id
                    select new AutomaticTurnCleanupCandidate(
                        memory.Id,
                        source.Id,
                        memory.OwnerUserId))
                .AsNoTracking()
                .Take(batchSize)
                .ToArrayAsync(cancellationToken);

            if (candidates.Length == 0)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return new AutomaticTurnCleanupResult(0);
            }

            var memoryIds = candidates.Select(candidate => candidate.MemoryItemId).ToArray();
            var sourceEventIds = candidates.Select(candidate => candidate.SourceEventId).ToArray();
            var memories = await db.SharedMemoryItems
                .Where(record => memoryIds.Contains(record.Id))
                .ToArrayAsync(cancellationToken);
            var sources = await db.SourceEvents
                .Where(record => sourceEventIds.Contains(record.Id))
                .ToArrayAsync(cancellationToken);
            var classifications = await db.ClassificationResults
                .Where(record => sourceEventIds.Contains(record.SourceEventId))
                .ToArrayAsync(cancellationToken);
            var provenanceLinks = await db.CollectionProvenance
                .AsNoTracking()
                .Where(record => memoryIds.Contains(record.MemoryItemId!) &&
                    sourceEventIds.Contains(record.SourceEventId!))
                .Select(record => new
                {
                    record.MemoryItemId,
                    record.SourceEventId,
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
                await db.SensitiveMemoryPayloads
                    .Where(record => memoryIds.Contains(record.MemoryItemId))
                    .LoadAsync(cancellationToken);
                await db.CollectionProvenance
                    .Where(record => memoryIds.Contains(record.MemoryItemId) ||
                        sourceEventIds.Contains(record.SourceEventId!))
                    .LoadAsync(cancellationToken);
            }

            var completeCandidates = candidates
                .Where(candidate =>
                    memories.Any(memory =>
                        memory.Id == candidate.MemoryItemId &&
                        memory.OwnerUserId == candidate.OwnerUserId &&
                        memory.RetentionKind == MemoryRetentionKind.Ephemeral &&
                        memory.ExpiresAt is not null &&
                        memory.ExpiresAt <= now &&
                        memory.ExternalPublicationState == ExternalPublicationState.LocalOnly) &&
                    sources.Any(source =>
                        source.Id == candidate.SourceEventId &&
                        source.OwnerUserId == candidate.OwnerUserId &&
                        source.SourceType == "turn-summary") &&
                    provenanceLinks.Any(provenance =>
                        provenance.MemoryItemId == candidate.MemoryItemId &&
                        provenance.SourceEventId == candidate.SourceEventId &&
                        provenance.AuthenticatedUserId == candidate.OwnerUserId) &&
                    !outboxMemoryIds.Contains(candidate.MemoryItemId))
                .ToArray();
            if (completeCandidates.Length != candidates.Length)
            {
                throw new DbUpdateConcurrencyException(
                    "An automatic turn cleanup candidate changed before the batch could be deleted.");
            }

            db.ClassificationResults.RemoveRange(classifications);
            db.SharedMemoryItems.RemoveRange(memories);
            db.SourceEvents.RemoveRange(sources);
            db.AuditEvents.AddRange(completeCandidates.Select(candidate => new AuditEventRecord
            {
                Id = $"audit-{Guid.NewGuid():N}",
                OccurredAt = now,
                Actor = "luthn-retention-cleanup",
                Action = "turn_summary.retention.pruned",
                SubjectId = candidate.SourceEventId,
                PayloadClass = "metadata-only",
                RedactionState = "expired-turn-capsule-deleted"
            }));

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

            throw;
        }
    }

    private sealed record AutomaticTurnCleanupCandidate(
        string MemoryItemId,
        string SourceEventId,
        string OwnerUserId);
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
