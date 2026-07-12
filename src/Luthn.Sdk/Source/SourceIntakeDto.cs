using System.Text.Json.Serialization;
using Luthn.Sdk.Classification;

namespace Luthn.Sdk.Source;

public sealed record SourceIntakeRequestDto(
    [property: JsonPropertyName("sourceSystem")] string SourceSystem,
    [property: JsonPropertyName("sourceType")] string SourceType,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("safeSummary")] string SafeSummary,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags);

public sealed record SourceIntakeResponseDto(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceEventId")] string SourceEventId,
    [property: JsonPropertyName("classificationResultId")] string ClassificationResultId,
    [property: JsonPropertyName("wikiProposalId")] string? WikiProposalId,
    [property: JsonPropertyName("sensitiveReferenceId")] string? SensitiveReferenceId,
    [property: JsonPropertyName("auditEventId")] string AuditEventId,
    [property: JsonPropertyName("classification")] ClassificationResultDto Classification,
    [property: JsonPropertyName("storageDecision")] StorageDecisionDto StorageDecision);
