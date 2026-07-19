using System.Text.Json.Serialization;

namespace Luthn.Sdk.Memory;

public sealed record CreateSharedMemoryItemRequestDto(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("safeSummary")] string SafeSummary,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("visibility")] string Visibility = "SharedAcrossAgents",
    [property: JsonPropertyName("retentionKind")] string RetentionKind = "Durable",
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("sourceSessionId")] string? SourceSessionId = null,
    [property: JsonPropertyName("sensitivity")] string Sensitivity = "Public",
    [property: JsonPropertyName("projectKey")] string? ProjectKey = null,
    [property: JsonPropertyName("taskKey")] string? TaskKey = null,
    [property: JsonPropertyName("topicTags")] IReadOnlyList<string>? TopicTags = null);

public sealed record SharedMemoryQueryRequestDto(
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("maxItems")] int MaxItems = 20,
    [property: JsonPropertyName("projectKey")] string? ProjectKey = null,
    [property: JsonPropertyName("taskKey")] string? TaskKey = null,
    [property: JsonPropertyName("topicTags")] IReadOnlyList<string>? TopicTags = null);

public sealed record SharedMemoryQueryResponseDto(
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("items")] IReadOnlyList<SharedMemoryItemDto> Items)
{
    [JsonPropertyName("retrievalId")]
    public string? RetrievalId { get; init; }
    [JsonPropertyName("projectKey")]
    public string? ProjectKey { get; init; }
    [JsonPropertyName("taskKey")]
    public string? TaskKey { get; init; }
    [JsonPropertyName("topicTags")]
    public IReadOnlyList<string> TopicTags { get; init; } = [];
}

public sealed record SharedMemoryItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("safeSummary")] string SafeSummary,
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("retentionKind")] string RetentionKind,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("sourceSessionId")] string? SourceSessionId,
    [property: JsonPropertyName("allowsAgentContext")] bool AllowsAgentContext,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt)
{
    [JsonPropertyName("projectKey")]
    public string? ProjectKey { get; init; }
    [JsonPropertyName("taskKey")]
    public string? TaskKey { get; init; }
    [JsonPropertyName("topicTags")]
    public IReadOnlyList<string> TopicTags { get; init; } = [];
}
