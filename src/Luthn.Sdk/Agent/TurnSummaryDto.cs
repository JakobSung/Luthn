using System.Text.Json.Serialization;
using Luthn.Sdk.Classification;
using Luthn.Sdk.Provenance;

namespace Luthn.Sdk.Agent;

public sealed record TurnSummaryIntakeRequestDto(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("sourceAgent")] string SourceAgent,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("turnId")] string? TurnId = null,
    [property: JsonPropertyName("turnRange")] string? TurnRange = null,
    [property: JsonPropertyName("contentDigest")] string? ContentDigest = null,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("projectKey")] string? ProjectKey = null,
    [property: JsonPropertyName("taskKey")] string? TaskKey = null,
    [property: JsonPropertyName("topicTags")] IReadOnlyList<string>? TopicTags = null,
    [property: JsonPropertyName("provenance")] CollectionProvenanceClaimsDto? Provenance = null);

public sealed record TurnSummaryIntakeResponseDto(
    [property: JsonPropertyName("summaryId")] string SummaryId,
    [property: JsonPropertyName("sourceEventId")] string SourceEventId,
    [property: JsonPropertyName("classificationResultId")] string ClassificationResultId,
    [property: JsonPropertyName("memoryItemId")] string? MemoryItemId,
    [property: JsonPropertyName("auditEventId")] string AuditEventId,
    [property: JsonPropertyName("allowsAgentContext")] bool AllowsAgentContext,
    [property: JsonPropertyName("duplicate")] bool Duplicate,
    [property: JsonPropertyName("classification")] ClassificationResultDto Classification,
    [property: JsonPropertyName("storageDecision")] StorageDecisionDto StorageDecision);
