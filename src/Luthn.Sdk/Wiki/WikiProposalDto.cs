using System.Text.Json.Serialization;

namespace Luthn.Sdk.Wiki;

public sealed record WikiProposalDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags);
