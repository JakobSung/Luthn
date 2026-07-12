using System.Text.Json.Serialization;

namespace Luthn.Sdk.AgentConnections;

public sealed record AgentConnectionObservationRequestDto(
    [property: JsonPropertyName("agentName")] string AgentName,
    [property: JsonPropertyName("integrationKind")] string IntegrationKind,
    [property: JsonPropertyName("connectorVersion")] string ConnectorVersion,
    [property: JsonPropertyName("channels")] IReadOnlyList<AgentConnectionChannelObservationDto> Channels);

public sealed record AgentConnectionChannelObservationDto(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("verificationState")] string VerificationState = "Unknown",
    [property: JsonPropertyName("activityState")] string ActivityState = "Unknown",
    [property: JsonPropertyName("failureCode")] string? FailureCode = null);

public sealed record AgentConnectionListDto(
    [property: JsonPropertyName("connections")] IReadOnlyList<AgentConnectionDto> Connections);

public sealed record AgentConnectionDto(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("agentName")] string AgentName,
    [property: JsonPropertyName("integrationKind")] string IntegrationKind,
    [property: JsonPropertyName("connectorVersion")] string ConnectorVersion,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("lastSuccessfulActivityAt")] DateTimeOffset? LastSuccessfulActivityAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("channels")] IReadOnlyList<AgentConnectionChannelDto> Channels);

public sealed record AgentConnectionChannelDto(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("verificationState")] string VerificationState,
    [property: JsonPropertyName("activityState")] string ActivityState,
    [property: JsonPropertyName("lastVerifiedAt")] DateTimeOffset? LastVerifiedAt,
    [property: JsonPropertyName("lastActivityAt")] DateTimeOffset? LastActivityAt,
    [property: JsonPropertyName("lastSuccessfulActivityAt")] DateTimeOffset? LastSuccessfulActivityAt,
    [property: JsonPropertyName("failureCode")] string? FailureCode,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);
