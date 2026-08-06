using System.Text.Json.Serialization;

namespace Luthn.Sdk.Audit;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditEventCategory
{
    Access,
    Security,
    Configuration,
    Publication,
    Ingestion,
    Retention
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuditEventMetadataDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("category")] AuditEventCategory Category,
    [property: JsonPropertyName("scopeKind")] string ScopeKind,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("actorKind")] string ActorKind,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("subjectType")] string SubjectType,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("correlationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CorrelationId,
    [property: JsonPropertyName("payloadVersion")] int PayloadVersion,
    [property: JsonPropertyName("payloadClass")] string PayloadClass,
    [property: JsonPropertyName("redactionState")] string RedactionState);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuditEventQueryDto(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("action"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action,
    [property: JsonPropertyName("actionPrefix"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActionPrefix,
    [property: JsonPropertyName("outcome"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Outcome,
    [property: JsonPropertyName("subjectId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubjectId,
    [property: JsonPropertyName("subjectType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubjectType,
    [property: JsonPropertyName("actorKind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActorKind,
    [property: JsonPropertyName("correlationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CorrelationId,
    [property: JsonPropertyName("from"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? From,
    [property: JsonPropertyName("to"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? To,
    [property: JsonPropertyName("cursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cursor,
    [property: JsonPropertyName("limit")] int Limit = 50);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuditEventPageDto(
    [property: JsonPropertyName("events")] IReadOnlyList<AuditEventMetadataDto> Events,
    [property: JsonPropertyName("nextCursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor);
