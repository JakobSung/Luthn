using System.Text.Json.Serialization;

namespace Luthn.Sdk.Access;

public sealed record SensitiveAccessCreateRequestDto(
    [property: JsonPropertyName("sensitiveReferenceId")] string SensitiveReferenceId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds);

public sealed record ProtectedInformationAccessRequestDto(
    [property: JsonPropertyName("memoryItemId")] string MemoryItemId,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public sealed record ProtectedInformationAccessResponseDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("requestId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestId = null,
    [property: JsonPropertyName("accessHandle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AccessHandle = null);

public sealed record ProtectedInformationResultRequestDto(
    [property: JsonPropertyName("accessHandle")] string AccessHandle);

public sealed record ProtectedInformationResultDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("contentAvailable")] bool ContentAvailable,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content,
    [property: JsonPropertyName("grantExpiresAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? GrantExpiresAt,
    [property: JsonPropertyName("remainingReads"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RemainingReads,
    [property: JsonPropertyName("maxReads"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxReads,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);

public sealed record SensitiveAccessDecisionRequestDto(
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("redactedSummary")] string? RedactedSummary = null,
    [property: JsonPropertyName("grantDurationSeconds")] int? GrantDurationSeconds = null,
    [property: JsonPropertyName("maximumSuccessfulReads")] int? MaximumSuccessfulReads = null);

public abstract record SensitiveAccessReadDto;

public sealed record SensitiveAccessRequestDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sensitiveReferenceId")] string SensitiveReferenceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("requestedBy")] string RequestedBy,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("decidedBy")] string? DecidedBy,
    [property: JsonPropertyName("decidedAt")] DateTimeOffset? DecidedAt,
    [property: JsonPropertyName("redactedOutputAvailable")] bool RedactedOutputAvailable,
    [property: JsonPropertyName("outputPolicy")] string OutputPolicy) : SensitiveAccessReadDto
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("statusCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusCode { get; init; }

    [JsonPropertyName("requestExpiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RequestExpiresAt { get; init; }

    [JsonPropertyName("grantExpiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? GrantExpiresAt { get; init; }

    [JsonPropertyName("remainingReads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RemainingReads { get; init; }

    [JsonPropertyName("usedReads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UsedReads { get; init; }

    [JsonPropertyName("maxReads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxReads { get; init; }

    [JsonPropertyName("accessMode")]
    public string AccessMode { get; init; } = "RedactedSummary";
}

/// <summary>
/// Local or self-hosted Hub operator context for a sensitive record reference.
/// This contract is not agent-safe and must not be included in Cloud safe-projection sync.
/// </summary>
public sealed record SensitiveAccessOperatorReferenceDto(
    [property: JsonPropertyName("sourceSystem")] string SourceSystem,
    [property: JsonPropertyName("sourceType")] string SourceType,
    [property: JsonPropertyName("referenceLabel")] string ReferenceLabel,
    [property: JsonPropertyName("redactedSummary")] string RedactedSummary,
    [property: JsonPropertyName("receivedAt")] DateTimeOffset ReceivedAt);

/// <summary>
/// Operator-only decision context. It deliberately excludes workspace, owner, raw source,
/// protected payload, credential, and Vault fields.
/// </summary>
public sealed record SensitiveAccessOperatorDetailDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sensitiveReferenceId")] string SensitiveReferenceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("requestedBy")] string RequestedBy,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("requestReason")] string RequestReason,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("decidedBy")] string? DecidedBy,
    [property: JsonPropertyName("decidedAt")] DateTimeOffset? DecidedAt,
    [property: JsonPropertyName("decisionReason")] string? DecisionReason,
    [property: JsonPropertyName("redactedOutputAvailable")] bool RedactedOutputAvailable,
    [property: JsonPropertyName("outputPolicy")] string OutputPolicy,
    [property: JsonPropertyName("reference")] SensitiveAccessOperatorReferenceDto Reference,
    [property: JsonPropertyName("payloadClass")] string PayloadClass,
    [property: JsonPropertyName("redactionState")] string RedactionState)
{
    [JsonPropertyName("accessMode")]
    public string AccessMode { get; init; } = "RedactedSummary";
}

public sealed record SensitiveAccessResultDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sensitiveReferenceId")] string SensitiveReferenceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("outputPolicy")] string OutputPolicy,
    [property: JsonPropertyName("redactedOutputAvailable")] bool RedactedOutputAvailable,
    [property: JsonPropertyName("redactedOutput")] string? RedactedOutput,
    [property: JsonPropertyName("payloadClass")] string PayloadClass,
    [property: JsonPropertyName("redactionState")] string RedactionState,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons) : SensitiveAccessReadDto
{
    [JsonPropertyName("accessMode")]
    public string AccessMode { get; init; } = "RedactedSummary";

    [JsonPropertyName("statusCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusCode { get; init; }

    [JsonPropertyName("requestExpiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RequestExpiresAt { get; init; }

    [JsonPropertyName("grantExpiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? GrantExpiresAt { get; init; }

    [JsonPropertyName("remainingReads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RemainingReads { get; init; }

    [JsonPropertyName("usedReads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UsedReads { get; init; }

    [JsonPropertyName("maxReads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxReads { get; init; }
}

public sealed record SensitiveAccessTombstoneDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("outputPolicy")] string OutputPolicy) : SensitiveAccessReadDto;
