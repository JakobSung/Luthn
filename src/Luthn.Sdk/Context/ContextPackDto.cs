using System.Text.Json.Serialization;

namespace Luthn.Sdk.Context;

public sealed record ContextPackRequestDto(
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("maxItems")] int MaxItems = 20,
    [property: JsonPropertyName("query")] string? Query = null,
    [property: JsonPropertyName("projectKey")] string? ProjectKey = null,
    [property: JsonPropertyName("taskKey")] string? TaskKey = null,
    [property: JsonPropertyName("topicTags")] IReadOnlyList<string>? TopicTags = null);

public sealed record ContextPackDto(
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("items")] IReadOnlyList<ContextPackItemDto> Items)
{
    [JsonPropertyName("projectKey")]
    public string? ProjectKey { get; init; }
    [JsonPropertyName("taskKey")]
    public string? TaskKey { get; init; }
    [JsonPropertyName("topicTags")]
    public IReadOnlyList<string> TopicTags { get; init; } = [];
}

public sealed record ContextPackItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("safeSummary")] string SafeSummary,
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags)
{
    [JsonPropertyName("projectKey")]
    public string? ProjectKey { get; init; }
    [JsonPropertyName("taskKey")]
    public string? TaskKey { get; init; }
    [JsonPropertyName("topicTags")]
    public IReadOnlyList<string> TopicTags { get; init; } = [];
    [JsonPropertyName("projectionTimestamp")]
    public DateTimeOffset ProjectionTimestamp { get; init; }
}

public sealed record SafeSearchRequestDto(
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("maxItems")] int MaxItems = 20,
    [property: JsonPropertyName("projectKey")] string? ProjectKey = null,
    [property: JsonPropertyName("taskKey")] string? TaskKey = null,
    [property: JsonPropertyName("topicTags")] IReadOnlyList<string>? TopicTags = null);

public sealed record SafeSearchResponseDto(
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("results")] IReadOnlyList<SafeSearchResultDto> Results)
{
    [JsonPropertyName("projectKey")]
    public string? ProjectKey { get; init; }
    [JsonPropertyName("taskKey")]
    public string? TaskKey { get; init; }
    [JsonPropertyName("topicTags")]
    public IReadOnlyList<string> TopicTags { get; init; } = [];
}

public sealed record SafeSearchResultDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("safeSummary")] string SafeSummary,
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("score")] int Score)
{
    [JsonPropertyName("projectKey")]
    public string? ProjectKey { get; init; }
    [JsonPropertyName("taskKey")]
    public string? TaskKey { get; init; }
    [JsonPropertyName("topicTags")]
    public IReadOnlyList<string> TopicTags { get; init; } = [];
    [JsonPropertyName("projectionTimestamp")]
    public DateTimeOffset ProjectionTimestamp { get; init; }
}
