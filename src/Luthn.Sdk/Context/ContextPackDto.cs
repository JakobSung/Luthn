using System.Text.Json.Serialization;

namespace Luthn.Sdk.Context;

public sealed record ContextPackRequestDto(
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("maxItems")] int MaxItems = 20,
    [property: JsonPropertyName("query")] string? Query = null);

public sealed record ContextPackDto(
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("items")] IReadOnlyList<ContextPackItemDto> Items);

public sealed record ContextPackItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("safeSummary")] string SafeSummary,
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags);

public sealed record SafeSearchRequestDto(
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("maxItems")] int MaxItems = 20);

public sealed record SafeSearchResponseDto(
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("results")] IReadOnlyList<SafeSearchResultDto> Results);

public sealed record SafeSearchResultDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("safeSummary")] string SafeSummary,
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("score")] int Score);
