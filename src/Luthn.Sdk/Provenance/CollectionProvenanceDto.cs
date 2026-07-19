using System.Text.Json.Serialization;

namespace Luthn.Sdk.Provenance;

public sealed record CollectionProvenanceClaimsDto(
    [property: JsonPropertyName("userId")] string? UserId = null,
    [property: JsonPropertyName("agentId")] string? AgentId = null,
    [property: JsonPropertyName("applicationId")] string? ApplicationId = null,
    [property: JsonPropertyName("pluginId")] string? PluginId = null,
    [property: JsonPropertyName("connectorId")] string? ConnectorId = null,
    [property: JsonPropertyName("connectorVersion")] string? ConnectorVersion = null,
    [property: JsonPropertyName("collectedAt")] DateTimeOffset? CollectedAt = null);

public sealed record CollectionProvenanceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("sourceEventId")] string? SourceEventId,
    [property: JsonPropertyName("memoryItemId")] string? MemoryItemId,
    [property: JsonPropertyName("authenticatedActor")] string AuthenticatedActor,
    [property: JsonPropertyName("actorTrust")] string ActorTrust,
    [property: JsonPropertyName("claimsTrust")] string ClaimsTrust,
    [property: JsonPropertyName("claimedUserId")] string? ClaimedUserId,
    [property: JsonPropertyName("agentId")] string? AgentId,
    [property: JsonPropertyName("applicationId")] string? ApplicationId,
    [property: JsonPropertyName("pluginId")] string? PluginId,
    [property: JsonPropertyName("connectorId")] string? ConnectorId,
    [property: JsonPropertyName("connectorVersion")] string? ConnectorVersion,
    [property: JsonPropertyName("collectedAt")] DateTimeOffset? CollectedAt,
    [property: JsonPropertyName("receivedAt")] DateTimeOffset ReceivedAt);
