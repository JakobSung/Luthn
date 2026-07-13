using System.Text.Json.Serialization;

namespace Luthn.Sdk.Sync;

public sealed record SafeProjectionSyncEnvelopeDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("originInstanceId")] string OriginInstanceId,
    [property: JsonPropertyName("localRecordId")] string LocalRecordId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    [property: JsonPropertyName("safeSummary"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SafeSummary,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("projectionKind")] string ProjectionKind,
    [property: JsonPropertyName("payloadClass")] string PayloadClass,
    [property: JsonPropertyName("redactionState")] string RedactionState,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("decidedAt")] DateTimeOffset DecidedAt,
    [property: JsonPropertyName("expiresAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("provenanceDigest"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProvenanceDigest);

public sealed record ExternalPublicationStatusDto(
    [property: JsonPropertyName("memoryItemId")] string MemoryItemId,
    [property: JsonPropertyName("publicationState")] string PublicationState,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("decidedAt")] DateTimeOffset? DecidedAt,
    [property: JsonPropertyName("syncState")] string SyncState);

public sealed record SafeProjectionSyncStatusDto(
    [property: JsonPropertyName("connectionState")] string ConnectionState,
    [property: JsonPropertyName("outboxState")] string OutboxState,
    [property: JsonPropertyName("pendingCount")] int PendingCount,
    [property: JsonPropertyName("failedCount")] int FailedCount,
    [property: JsonPropertyName("acknowledgedCount")] int AcknowledgedCount);
