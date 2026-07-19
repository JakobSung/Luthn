using System.Text.Json;
using Luthn.Core.Classification;
using Luthn.Core.Persistence;
using Luthn.Core.Search;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api;

public sealed record SensitiveMemoryPayload(
    int ContractVersion,
    string Title,
    string SafeSummary,
    IReadOnlyList<string> CoreTags,
    string? ProjectKey,
    string? TaskKey,
    IReadOnlyList<string> TopicTags,
    string? SourceSessionId)
{
    public const int CurrentContractVersion = 1;
}

public interface ISensitiveMemoryPayloadProtector
{
    string ProtectionScheme { get; }
    string Protect(string memoryItemId, SensitiveMemoryPayload payload);
    SensitiveMemoryPayload Unprotect(string memoryItemId, string protectedPayload);
    void ValidateKeyRing();
}

public sealed class DataProtectionSensitiveMemoryPayloadProtector(
    IDataProtectionProvider dataProtectionProvider) : ISensitiveMemoryPayloadProtector
{
    public const string Scheme = "aspnet-data-protection-v1";
    private const string RootPurpose = "Luthn.SensitiveMemoryPayload.v1";
    private const string KeyRingValidationPurpose = "key-ring-validation";
    private const string KeyRingValidationValue = "luthn-sensitive-memory-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _rootProtector = dataProtectionProvider.CreateProtector(RootPurpose);

    public string ProtectionScheme => Scheme;

    public string Protect(string memoryItemId, SensitiveMemoryPayload payload)
    {
        ValidateMemoryItemId(memoryItemId);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.ContractVersion != SensitiveMemoryPayload.CurrentContractVersion)
        {
            throw new InvalidOperationException("Unsupported sensitive memory payload contract version.");
        }
        ValidatePayload(payload);

        var serialized = JsonSerializer.Serialize(payload, JsonOptions);
        return ForMemoryItem(memoryItemId).Protect(serialized);
    }

    public SensitiveMemoryPayload Unprotect(string memoryItemId, string protectedPayload)
    {
        ValidateMemoryItemId(memoryItemId);
        if (string.IsNullOrWhiteSpace(protectedPayload))
        {
            throw new InvalidOperationException("Protected sensitive memory payload is missing.");
        }

        var serialized = ForMemoryItem(memoryItemId).Unprotect(protectedPayload);
        var payload = JsonSerializer.Deserialize<SensitiveMemoryPayload>(serialized, JsonOptions)
            ?? throw new InvalidOperationException("Protected sensitive memory payload is invalid.");
        if (payload.ContractVersion != SensitiveMemoryPayload.CurrentContractVersion)
        {
            throw new InvalidOperationException("Unsupported sensitive memory payload contract version.");
        }
        ValidatePayload(payload);

        return payload;
    }

    public void ValidateKeyRing()
    {
        var protector = _rootProtector.CreateProtector(KeyRingValidationPurpose);
        var protectedValue = protector.Protect(KeyRingValidationValue);
        if (!string.Equals(
                KeyRingValidationValue,
                protector.Unprotect(protectedValue),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Sensitive memory key ring validation failed.");
        }
    }

    private IDataProtector ForMemoryItem(string memoryItemId) =>
        _rootProtector.CreateProtector("memory-item", memoryItemId);

    private static void ValidateMemoryItemId(string memoryItemId)
    {
        if (string.IsNullOrWhiteSpace(memoryItemId) || memoryItemId.Length > 128)
        {
            throw new ArgumentException("A bounded memory item id is required.", nameof(memoryItemId));
        }
    }

    private static void ValidatePayload(SensitiveMemoryPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Title) ||
            payload.Title.Length > ApiValidation.TitleMaxLength ||
            string.IsNullOrWhiteSpace(payload.SafeSummary) ||
            payload.SafeSummary.Length > ApiValidation.SafeSummaryMaxLength ||
            payload.CoreTags is null ||
            payload.CoreTags.Count > ApiValidation.CoreTagMaxCount ||
            payload.CoreTags.Any(tag =>
                string.IsNullOrWhiteSpace(tag) ||
                tag.Length > ApiValidation.CoreTagMaxLength) ||
            payload.ProjectKey?.Length > RecallMetadata.MaximumKeyLength ||
            payload.TaskKey?.Length > RecallMetadata.MaximumKeyLength ||
            payload.TopicTags is null ||
            payload.TopicTags.Count > RecallMetadata.MaximumTopicTags ||
            payload.TopicTags.Any(tag =>
                string.IsNullOrWhiteSpace(tag) ||
                tag.Length > RecallMetadata.MaximumTopicTagLength) ||
            payload.SourceSessionId?.Length > ApiValidation.PublicRecordIdMaxLength)
        {
            throw new InvalidOperationException("Sensitive memory payload contract validation failed.");
        }
    }
}

internal static class SensitiveMemoryPersistence
{
    public const string ProtectedTitle = "[protected-memory]";
    public const string ProtectedSummary = "[protected-payload]";

    public static bool RequiresProtection(SharedMemoryItemRecord record) =>
        !record.AllowsAgentContext || record.Sensitivity != SensitivityLevel.Public;

    public static SensitiveMemoryPayload FromRecord(SharedMemoryItemRecord record) => new(
        SensitiveMemoryPayload.CurrentContractVersion,
        record.Title,
        record.SafeSummary,
        record.CoreTags.ToArray(),
        record.ProjectKey,
        record.TaskKey,
        record.TopicTags.ToArray(),
        record.SourceSessionId);

    public static SensitiveMemoryPayloadRecord Protect(
        SharedMemoryItemRecord record,
        SensitiveMemoryPayload payload,
        ISensitiveMemoryPayloadProtector protector,
        DateTimeOffset now)
    {
        if (!RequiresProtection(record))
        {
            throw new InvalidOperationException("Only non-agent-visible memory can use the protected payload store.");
        }

        var protectedPayload = protector.Protect(record.Id, payload);
        ApplyInertProjection(record);
        return new SensitiveMemoryPayloadRecord
        {
            MemoryItemId = record.Id,
            ContractVersion = payload.ContractVersion,
            ProtectionScheme = protector.ProtectionScheme,
            ProtectedPayload = protectedPayload,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static bool IsInertProjection(SharedMemoryItemRecord record) =>
        record.Title == ProtectedTitle &&
        record.SafeSummary == ProtectedSummary &&
        record.CoreTags.Count == 0 &&
        record.ProjectKey is null &&
        record.TaskKey is null &&
        record.TopicTags.Count == 0 &&
        record.SourceSessionId is null &&
        !record.AllowsAgentContext;

    private static void ApplyInertProjection(SharedMemoryItemRecord record)
    {
        record.Title = ProtectedTitle;
        record.SafeSummary = ProtectedSummary;
        record.CoreTags = [];
        record.ProjectKey = null;
        record.TaskKey = null;
        record.TopicTags = [];
        record.SourceSessionId = null;
        record.AllowsAgentContext = false;
    }
}

public sealed class SensitiveMemoryProtectionState
{
    private int _ready;
    private long _migratedRecords;

    public bool IsReady => Volatile.Read(ref _ready) == 1;
    public long MigratedRecords => Interlocked.Read(ref _migratedRecords);

    internal void MarkReady(long migratedRecords)
    {
        Interlocked.Exchange(ref _migratedRecords, Math.Max(0, migratedRecords));
        Volatile.Write(ref _ready, 1);
    }
}

public sealed class SensitiveMemoryPayloadMigrator(
    LuthnDbContext db,
    ISensitiveMemoryPayloadProtector protector,
    SensitiveMemoryProtectionState state,
    TimeProvider timeProvider,
    ILogger<SensitiveMemoryPayloadMigrator> logger)
{
    public async Task MigrateAndVerifyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            protector.ValidateKeyRing();
            var executionStrategy = db.Database.CreateExecutionStrategy();
            var migrated = await executionStrategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(cancellationToken)
                    : null;

                var protectedRecords = await db.SensitiveMemoryPayloads
                    .AsNoTracking()
                    .OrderBy(record => record.MemoryItemId)
                    .ToArrayAsync(cancellationToken);
                var protectedIds = protectedRecords
                    .Select(record => record.MemoryItemId)
                    .ToHashSet(StringComparer.Ordinal);
                var protectedMemory = protectedIds.Count == 0
                    ? []
                    : await db.SharedMemoryItems
                        .AsNoTracking()
                        .Where(record => protectedIds.Contains(record.Id))
                        .ToArrayAsync(cancellationToken);
                var protectedMemoryById = protectedMemory.ToDictionary(record => record.Id, StringComparer.Ordinal);

                foreach (var protectedRecord in protectedRecords)
                {
                    if (protectedRecord.ContractVersion != SensitiveMemoryPayload.CurrentContractVersion ||
                        protectedRecord.ProtectionScheme != protector.ProtectionScheme ||
                        !protectedMemoryById.TryGetValue(protectedRecord.MemoryItemId, out var memory) ||
                        !SensitiveMemoryPersistence.IsInertProjection(memory))
                    {
                        throw new InvalidOperationException("Sensitive memory protection metadata is inconsistent.");
                    }

                    _ = protector.Unprotect(protectedRecord.MemoryItemId, protectedRecord.ProtectedPayload);
                }

                var legacyRecords = await db.SharedMemoryItems
                    .Where(record =>
                        (!record.AllowsAgentContext || record.Sensitivity != SensitivityLevel.Public) &&
                        !protectedIds.Contains(record.Id))
                    .OrderBy(record => record.Id)
                    .ToArrayAsync(cancellationToken);
                var now = timeProvider.GetUtcNow();
                foreach (var legacyRecord in legacyRecords)
                {
                    var payload = SensitiveMemoryPersistence.FromRecord(legacyRecord);
                    db.SensitiveMemoryPayloads.Add(SensitiveMemoryPersistence.Protect(
                        legacyRecord,
                        payload,
                        protector,
                        now));
                }

                if (legacyRecords.Length > 0)
                {
                    db.AuditEvents.Add(new AuditEventRecord
                    {
                        Id = $"audit-{Guid.NewGuid():N}",
                        OccurredAt = now,
                        Actor = "local-runtime",
                        Action = "memory.protection.migrated",
                        SubjectId = "sensitive-memory-payloads",
                        PayloadClass = "metadata-only",
                        RedactionState = "encrypted-payload-only"
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return (long)legacyRecords.Length;
            });

            state.MarkReady(migrated);
            logger.LogInformation(
                "Sensitive memory protection is ready; migrated {MigratedRecordCount} legacy records.",
                migrated);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "Sensitive memory protection startup verification failed with {FailureType}.",
                error.GetType().Name);
            throw new InvalidOperationException("Sensitive memory protection startup verification failed.");
        }
    }
}
