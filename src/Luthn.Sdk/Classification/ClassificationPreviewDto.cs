using System.Text.Json.Serialization;

namespace Luthn.Sdk.Classification;

public sealed record ClassificationPreviewRequestDto(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("sourceType")] string SourceType = "note");

public sealed record ClassificationPreviewDto(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("classification")] ClassificationResultDto Classification,
    [property: JsonPropertyName("storageDecision")] StorageDecisionDto StorageDecision);

public sealed record ClassificationResultDto(
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,
    [property: JsonPropertyName("containsSensitiveMaterial")] bool ContainsSensitiveMaterial);

public sealed record StorageDecisionDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("allowsWikiProjection")] bool AllowsWikiProjection,
    [property: JsonPropertyName("allowsAgentContext")] bool AllowsAgentContext,
    [property: JsonPropertyName("requiresHumanReview")] bool RequiresHumanReview);
