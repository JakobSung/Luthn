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
