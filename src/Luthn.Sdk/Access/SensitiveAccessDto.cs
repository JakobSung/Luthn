using System.Text.Json.Serialization;

namespace Luthn.Sdk.Access;

public sealed record SensitiveAccessCreateRequestDto(
    [property: JsonPropertyName("sensitiveReferenceId")] string SensitiveReferenceId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds);

public sealed record SensitiveAccessDecisionRequestDto(
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("redactedSummary")] string? RedactedSummary = null);

public sealed record SensitiveAccessRequestDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sensitiveReferenceId")] string SensitiveReferenceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("requestedBy")] string RequestedBy,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("decidedBy")] string? DecidedBy,
    [property: JsonPropertyName("decidedAt")] DateTimeOffset? DecidedAt,
    [property: JsonPropertyName("redactedOutputAvailable")] bool RedactedOutputAvailable,
    [property: JsonPropertyName("outputPolicy")] string OutputPolicy)
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }
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
    [property: JsonPropertyName("redactionState")] string RedactionState);

public sealed record SensitiveAccessResultDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sensitiveReferenceId")] string SensitiveReferenceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("outputPolicy")] string OutputPolicy,
    [property: JsonPropertyName("redactedOutputAvailable")] bool RedactedOutputAvailable,
    [property: JsonPropertyName("redactedOutput")] string? RedactedOutput,
    [property: JsonPropertyName("payloadClass")] string PayloadClass,
    [property: JsonPropertyName("redactionState")] string RedactionState,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);
