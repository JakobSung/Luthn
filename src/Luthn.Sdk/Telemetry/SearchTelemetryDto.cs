using System.Text.Json.Serialization;

namespace Luthn.Sdk.Telemetry;

public sealed record SearchObservationRequestDto(
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("cacheStatus")] string CacheStatus,
    [property: JsonPropertyName("durationMilliseconds")] long DurationMilliseconds,
    [property: JsonPropertyName("resultCount")] int ResultCount);

public sealed record SearchFeedbackRequestDto(
    [property: JsonPropertyName("retrievalId")] string RetrievalId,
    [property: JsonPropertyName("judgment")] string Judgment);

public sealed record SearchTelemetryAcceptedDto(
    [property: JsonPropertyName("accepted")] bool Accepted);
