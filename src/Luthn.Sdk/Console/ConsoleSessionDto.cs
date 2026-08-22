using System.Text.Json.Serialization;

namespace Luthn.Sdk.Console;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleAccessMode
{
    LocalAuto
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleSessionState
{
    Anonymous,
    Active,
    Expired
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleCapability
{
    AccessReview,
    AccessDecision,
    AuditRead,
    ClassificationOperate,
    SourceIntake,
    PublicationOperate,
    AgentConnectionRead,
    ConfigurationWrite
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConsoleSessionDto(
    [property: JsonPropertyName("mode")] ConsoleAccessMode Mode,
    [property: JsonPropertyName("state")] ConsoleSessionState State,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("idleExpiresAt")] DateTimeOffset? IdleExpiresAt,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<ConsoleCapability> Capabilities,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("serverDerived")] bool ServerDerived);
