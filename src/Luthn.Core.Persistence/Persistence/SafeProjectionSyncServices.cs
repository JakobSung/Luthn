using System.Text.Json;
using System.Text.Json.Serialization;
using Luthn.Core.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Luthn.Core.Persistence;

public static class SafeProjectionSyncServiceCollectionExtensions
{
    public static IServiceCollection AddSafeProjectionSyncFoundation(this IServiceCollection services)
    {
        services.TryAddSingleton<ISafeProjectionSyncTransport, DisabledSafeProjectionSyncTransport>();
        services.AddScoped<LocalInstallationIdentityService>();
        services.AddScoped<SafeProjectionPublicationService>();
        services.AddScoped<SafeProjectionOutboxProcessor>();
        return services;
    }
}

public sealed class LocalInstallationIdentityService(LuthnDbContext db)
{
    public async Task<LocalInstallationStateRecord> GetOrCreateAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.LocalInstallationStates
            .SingleOrDefaultAsync(
                record => record.Id == LocalInstallationStateRecord.SingletonId,
                cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new LocalInstallationStateRecord
        {
            Id = LocalInstallationStateRecord.SingletonId,
            OriginInstanceId = $"instance-{Guid.NewGuid():N}",
            CreatedAt = now
        };
        db.LocalInstallationStates.Add(created);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return created;
        }
        catch (DbUpdateException)
        {
            db.Entry(created).State = EntityState.Detached;
            return await db.LocalInstallationStates.SingleAsync(
                record => record.Id == LocalInstallationStateRecord.SingletonId,
                cancellationToken);
        }
    }
}

public sealed record ExternalPublicationResult(
    string MemoryItemId,
    ExternalPublicationState PublicationState,
    long Revision,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DecidedAt,
    SafeProjectionSyncOutboxState? SyncState);

public sealed class SafeProjectionPublicationException(string message) : InvalidOperationException(message);

public sealed class SafeProjectionPublicationService(
    LuthnDbContext db,
    LocalInstallationIdentityService installationIdentity)
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async Task<ExternalPublicationResult?> GetAsync(
        string memoryItemId,
        CancellationToken cancellationToken)
        => await GetAsync(memoryItemId, "local-owner", isOperator: false, cancellationToken);

    public async Task<ExternalPublicationResult?> GetAsync(
        string memoryItemId,
        string ownerUserId,
        bool isOperator,
        CancellationToken cancellationToken)
    {
        var memory = await db.SharedMemoryItems
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == memoryItemId &&
                    (isOperator || record.OwnerUserId == ownerUserId),
                cancellationToken);
        if (memory is null)
        {
            return null;
        }

        var syncState = await LatestSyncStateAsync(memory.Id, cancellationToken);
        return ToResult(memory, syncState);
    }

    public async Task<ExternalPublicationResult> ApproveAsync(
        string memoryItemId,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => await ApproveAsync(memoryItemId, actor, now, "local-owner", isOperator: false, cancellationToken);

    public async Task<ExternalPublicationResult> ApproveAsync(
        string memoryItemId,
        string actor,
        DateTimeOffset now,
        string ownerUserId,
        bool isOperator,
        CancellationToken cancellationToken)
    {
        var memory = await db.SharedMemoryItems
            .SingleOrDefaultAsync(
                record => record.Id == memoryItemId &&
                    (isOperator || record.OwnerUserId == ownerUserId),
                cancellationToken)
            ?? throw new KeyNotFoundException("Shared memory item was not found.");

        if (memory.ExternalPublicationState == ExternalPublicationState.Revoked)
        {
            throw new SafeProjectionPublicationException("Revoked memory cannot be republished in this slice.");
        }

        if (memory.ExternalPublicationState == ExternalPublicationState.ApprovedForExternal)
        {
            var currentSyncState = await LatestSyncStateAsync(memory.Id, cancellationToken);
            return ToResult(memory, currentSyncState);
        }

        if (!ExternalMemoryProjectionPolicy.AllowsExternalMemoryExport(
                memory.Sensitivity,
                memory.Visibility,
                memory.ExpiresAt,
                now) ||
            !memory.AllowsAgentContext)
        {
            throw new SafeProjectionPublicationException(
                "Only public, agent-visible, non-expired safe memory can be approved for external publication.");
        }

        var installation = await installationIdentity.GetOrCreateAsync(now, cancellationToken);
        memory.ExternalPublicationState = ExternalPublicationState.ApprovedForExternal;
        memory.ExternalPublicationDecidedAt = now;
        memory.ExternalPublicationDecidedBy = BoundActor(actor);
        memory.UpdatedAt = now;
        memory.Revision = Math.Max(1, memory.Revision) + 1;

        var envelope = SafeProjectionSyncPolicy.CreateUpsert(
            installation.OriginInstanceId,
            memory.Id,
            memory.Revision,
            memory.SafeSummary,
            memory.ExternalPublicationState,
            memory.Sensitivity,
            memory.Visibility,
            memory.CreatedAt,
            memory.UpdatedAt,
            now,
            memory.ExpiresAt);
        AddOutbox(envelope, memory.OwnerUserId, now);
        AddAudit(memory.Id, actor, "memory.external_publication.approved", now);

        await SavePublicationAsync(cancellationToken);
        return ToResult(memory, SafeProjectionSyncOutboxState.Pending);
    }

    public async Task<ExternalPublicationResult> RevokeAsync(
        string memoryItemId,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => await RevokeAsync(memoryItemId, actor, now, "local-owner", isOperator: false, cancellationToken);

    public async Task<ExternalPublicationResult> RevokeAsync(
        string memoryItemId,
        string actor,
        DateTimeOffset now,
        string ownerUserId,
        bool isOperator,
        CancellationToken cancellationToken)
    {
        var memory = await db.SharedMemoryItems
            .SingleOrDefaultAsync(
                record => record.Id == memoryItemId &&
                    (isOperator || record.OwnerUserId == ownerUserId),
                cancellationToken)
            ?? throw new KeyNotFoundException("Shared memory item was not found.");

        if (memory.ExternalPublicationState == ExternalPublicationState.LocalOnly)
        {
            throw new SafeProjectionPublicationException("Local-only memory has not been published externally.");
        }

        if (memory.ExternalPublicationState == ExternalPublicationState.Revoked)
        {
            var currentSyncState = await LatestSyncStateAsync(memory.Id, cancellationToken);
            return ToResult(memory, currentSyncState);
        }

        var installation = await installationIdentity.GetOrCreateAsync(now, cancellationToken);
        memory.ExternalPublicationState = ExternalPublicationState.Revoked;
        memory.ExternalPublicationDecidedAt = now;
        memory.ExternalPublicationDecidedBy = BoundActor(actor);
        memory.UpdatedAt = now;
        memory.Revision = Math.Max(1, memory.Revision) + 1;

        var envelope = SafeProjectionSyncPolicy.CreateRevoke(
            installation.OriginInstanceId,
            memory.Id,
            memory.Revision,
            memory.CreatedAt,
            memory.UpdatedAt,
            now);
        AddOutbox(envelope, memory.OwnerUserId, now);
        AddAudit(memory.Id, actor, "memory.external_publication.revoked", now);

        await SavePublicationAsync(cancellationToken);
        return ToResult(memory, SafeProjectionSyncOutboxState.Pending);
    }

    private async Task SavePublicationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException error)
        {
            throw new SafeProjectionPublicationException(
                $"The memory publication state changed concurrently: {error.GetType().Name}.");
        }
    }

    private void AddOutbox(SafeProjectionSyncEnvelope envelope, string ownerUserId, DateTimeOffset now)
    {
        db.SafeProjectionSyncOutbox.Add(new SafeProjectionSyncOutboxRecord
        {
            Id = $"sync-{Guid.NewGuid():N}",
            IdempotencyKey = SafeProjectionSyncPolicy.CreateIdempotencyKey(envelope),
            OriginInstanceId = envelope.OriginInstanceId,
            LocalRecordId = envelope.LocalRecordId,
            OwnerUserId = ownerUserId,
            Revision = envelope.Revision,
            Operation = envelope.Operation,
            ContractVersion = envelope.ContractVersion,
            SafeEnvelopeJson = JsonSerializer.Serialize(envelope, SerializerOptions),
            State = SafeProjectionSyncOutboxState.Pending,
            CreatedAt = now
        });
    }

    private void AddAudit(string memoryItemId, string actor, string action, DateTimeOffset now)
    {
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = $"audit-{Guid.NewGuid():N}",
            OccurredAt = now,
            Actor = BoundActor(actor),
            Action = action,
            SubjectId = memoryItemId,
            PayloadClass = ExternalMemoryAdapterCatalog.MetadataOnlyPayload,
            RedactionState = ExternalMemoryAdapterCatalog.SafeProjectionOnly
        });
    }

    private async Task<SafeProjectionSyncOutboxState?> LatestSyncStateAsync(
        string memoryItemId,
        CancellationToken cancellationToken) =>
        await db.SafeProjectionSyncOutbox
            .AsNoTracking()
            .Where(record => record.LocalRecordId == memoryItemId)
            .OrderByDescending(record => record.Revision)
            .Select(record => (SafeProjectionSyncOutboxState?)record.State)
            .FirstOrDefaultAsync(cancellationToken);

    private static ExternalPublicationResult ToResult(
        SharedMemoryItemRecord memory,
        SafeProjectionSyncOutboxState? syncState) =>
        new(
            memory.Id,
            memory.ExternalPublicationState,
            memory.Revision,
            memory.UpdatedAt,
            memory.ExternalPublicationDecidedAt,
            syncState);

    private static string BoundActor(string actor)
    {
        var value = string.IsNullOrWhiteSpace(actor) ? "local-anonymous" : actor.Trim();
        return value.Length <= 128 ? value : value[..128];
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record SafeProjectionOutboxProcessResult(
    int RecoveredCount,
    int SupersededCount,
    int ClaimedCount,
    int AcknowledgedCount,
    int FailedCount,
    SafeProjectionSyncTransportState TransportState);

public sealed class SafeProjectionOutboxProcessor(
    LuthnDbContext db,
    ISafeProjectionSyncTransport transport)
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async Task<SafeProjectionOutboxProcessResult> ProcessBatchAsync(
        DateTimeOffset now,
        int batchSize = 20,
        int maxAttempts = 5,
        TimeSpan? processingLease = null,
        CancellationToken cancellationToken = default)
    {
        var lease = processingLease ?? TimeSpan.FromMinutes(5);
        if (lease <= TimeSpan.FromTicks(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(processingLease),
                "The processing lease must be longer than one tick.");
        }

        var recoveredCount = await RecoverAbandonedAsync(
            now - lease,
            maxAttempts,
            cancellationToken);
        var supersededCount = await SupersedeOutdatedAsync(cancellationToken);

        if (transport.State != SafeProjectionSyncTransportState.Ready)
        {
            return new SafeProjectionOutboxProcessResult(
                recoveredCount,
                supersededCount,
                0,
                0,
                0,
                transport.State);
        }

        var candidateIds = await db.SafeProjectionSyncOutbox
            .AsNoTracking()
            .Where(record =>
                (record.State == SafeProjectionSyncOutboxState.Pending ||
                    record.State == SafeProjectionSyncOutboxState.Failed) &&
                record.AttemptCount < maxAttempts &&
                (record.NextAttemptAt == null || record.NextAttemptAt <= now) &&
                !db.SafeProjectionSyncOutbox.Any(older =>
                    older.OriginInstanceId == record.OriginInstanceId &&
                    older.LocalRecordId == record.LocalRecordId &&
                    older.Revision < record.Revision &&
                    older.State == SafeProjectionSyncOutboxState.Processing))
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id)
            .Select(record => record.Id)
            .Take(Math.Clamp(batchSize, 1, 100))
            .ToArrayAsync(cancellationToken);

        var claimed = 0;
        var acknowledged = 0;
        var failed = 0;
        foreach (var id in candidateIds)
        {
            if (!await TryClaimAsync(id, now, maxAttempts, cancellationToken))
            {
                continue;
            }

            claimed++;
            var record = await db.SafeProjectionSyncOutbox.SingleAsync(
                item => item.Id == id,
                cancellationToken);
            SafeProjectionSyncTransportResult result;
            try
            {
                var envelope = JsonSerializer.Deserialize<SafeProjectionSyncEnvelope>(
                    record.SafeEnvelopeJson,
                    SerializerOptions) ?? throw new InvalidOperationException("Sync envelope is empty.");
                using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendCancellation.CancelAfter(TimeSpan.FromTicks(lease.Ticks / 2));
                result = await transport.SendAsync(envelope, sendCancellation.Token);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                result = new SafeProjectionSyncTransportResult(false, ErrorCode: "transport.failure");
            }

            record.LastAttemptAt = now;
            if (result.Accepted)
            {
                record.State = SafeProjectionSyncOutboxState.Acknowledged;
                record.AcknowledgedAt = now;
                record.NextAttemptAt = null;
                record.LastErrorCode = null;
                record.RemoteCheckpoint = Bound(result.Checkpoint, 512);
                if (!string.IsNullOrWhiteSpace(result.Checkpoint))
                {
                    await UpsertCheckpointAsync(result.Checkpoint, now, cancellationToken);
                }
                acknowledged++;
            }
            else
            {
                record.State = SafeProjectionSyncOutboxState.Failed;
                record.LastErrorCode = Bound(result.ErrorCode, 128) ?? "transport.rejected";
                record.NextAttemptAt = record.AttemptCount < maxAttempts
                    ? now.Add(Backoff(record.AttemptCount))
                    : null;
                failed++;
            }

            record.ProcessingStartedAt = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new SafeProjectionOutboxProcessResult(
            recoveredCount,
            supersededCount,
            claimed,
            acknowledged,
            failed,
            transport.State);
    }

    public async Task<int> RecoverAbandonedAsync(
        DateTimeOffset leaseExpiredBefore,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var abandoned = await db.SafeProjectionSyncOutbox
            .Where(record =>
                record.State == SafeProjectionSyncOutboxState.Processing &&
                record.ProcessingStartedAt != null &&
                record.ProcessingStartedAt <= leaseExpiredBefore)
            .ToArrayAsync(cancellationToken);
        foreach (var record in abandoned)
        {
            var attemptsExhausted = record.AttemptCount >= maxAttempts;
            record.State = attemptsExhausted
                ? SafeProjectionSyncOutboxState.Failed
                : SafeProjectionSyncOutboxState.Pending;
            record.ProcessingStartedAt = null;
            record.NextAttemptAt = null;
            record.LastErrorCode = attemptsExhausted
                ? "processing.lease_expired_attempts_exhausted"
                : "processing.lease_expired";
        }

        if (abandoned.Length > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return abandoned.Length;
    }

    private async Task<int> SupersedeOutdatedAsync(CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            var affected = await db.SafeProjectionSyncOutbox
                .Where(record =>
                    (record.State == SafeProjectionSyncOutboxState.Pending ||
                        record.State == SafeProjectionSyncOutboxState.Failed) &&
                    db.SafeProjectionSyncOutbox.Any(newer =>
                        newer.OriginInstanceId == record.OriginInstanceId &&
                        newer.LocalRecordId == record.LocalRecordId &&
                        newer.Revision > record.Revision))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.State, SafeProjectionSyncOutboxState.Superseded)
                    .SetProperty(record => record.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(record => record.LastErrorCode, "operation.superseded"),
                    cancellationToken);
            db.ChangeTracker.Clear();
            return affected;
        }

        var records = await db.SafeProjectionSyncOutbox.ToArrayAsync(cancellationToken);
        var outdated = records
            .Where(record =>
                record.State is SafeProjectionSyncOutboxState.Pending or SafeProjectionSyncOutboxState.Failed &&
                records.Any(newer =>
                    newer.OriginInstanceId == record.OriginInstanceId &&
                    newer.LocalRecordId == record.LocalRecordId &&
                    newer.Revision > record.Revision))
            .ToArray();
        foreach (var record in outdated)
        {
            record.State = SafeProjectionSyncOutboxState.Superseded;
            record.NextAttemptAt = null;
            record.LastErrorCode = "operation.superseded";
        }

        if (outdated.Length > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return outdated.Length;
    }

    private async Task<bool> TryClaimAsync(
        string id,
        DateTimeOffset now,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            var affected = await db.SafeProjectionSyncOutbox
                .Where(record =>
                    record.Id == id &&
                    (record.State == SafeProjectionSyncOutboxState.Pending ||
                        record.State == SafeProjectionSyncOutboxState.Failed) &&
                    record.AttemptCount < maxAttempts &&
                    (record.NextAttemptAt == null || record.NextAttemptAt <= now) &&
                    !db.SafeProjectionSyncOutbox.Any(older =>
                        older.OriginInstanceId == record.OriginInstanceId &&
                        older.LocalRecordId == record.LocalRecordId &&
                        older.Revision < record.Revision &&
                        older.State == SafeProjectionSyncOutboxState.Processing))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.State, SafeProjectionSyncOutboxState.Processing)
                    .SetProperty(record => record.ProcessingStartedAt, now)
                    .SetProperty(record => record.AttemptCount, record => record.AttemptCount + 1),
                    cancellationToken);
            db.ChangeTracker.Clear();
            return affected == 1;
        }

        var record = await db.SafeProjectionSyncOutbox.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken);
        if (record is null ||
            record.State is not (SafeProjectionSyncOutboxState.Pending or SafeProjectionSyncOutboxState.Failed) ||
            record.AttemptCount >= maxAttempts ||
            record.NextAttemptAt is { } nextAttemptAt && nextAttemptAt > now ||
            await db.SafeProjectionSyncOutbox.AnyAsync(
                older =>
                    older.OriginInstanceId == record.OriginInstanceId &&
                    older.LocalRecordId == record.LocalRecordId &&
                    older.Revision < record.Revision &&
                    older.State == SafeProjectionSyncOutboxState.Processing,
                cancellationToken))
        {
            return false;
        }

        record.State = SafeProjectionSyncOutboxState.Processing;
        record.ProcessingStartedAt = now;
        record.AttemptCount++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task UpsertCheckpointAsync(
        string checkpoint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var transportName = transport.Name;
        var record = await db.SafeProjectionSyncCheckpoints.SingleOrDefaultAsync(
            item => item.TransportName == transportName,
            cancellationToken);
        if (record is null)
        {
            db.SafeProjectionSyncCheckpoints.Add(new SafeProjectionSyncCheckpointRecord
            {
                TransportName = transportName,
                Checkpoint = Bound(checkpoint, 512)!,
                UpdatedAt = now
            });
            return;
        }

        record.Checkpoint = Bound(checkpoint, 512)!;
        record.UpdatedAt = now;
    }

    private static TimeSpan Backoff(int attemptCount) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Clamp(attemptCount, 1, 8))));

    private static string? Bound(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
