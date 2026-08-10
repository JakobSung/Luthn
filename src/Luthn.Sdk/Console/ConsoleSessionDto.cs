using System.Text.Json.Serialization;

namespace Luthn.Sdk.Console;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleAccessMode
{
    LocalAuto,
    CloudLoginRequired,
    CloudAuthenticated,
    RestrictedOffboarding,
    LocalReclaimRequired
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleSessionState
{
    Anonymous,
    Active,
    LoginRequired,
    Restricted,
    Revoked,
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
    ConfigurationWrite,
    EnrollmentManage,
    OffboardingExport,
    InstallationDetach,
    LocalReclaim
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConsoleSessionDto(
    [property: JsonPropertyName("mode")] ConsoleAccessMode Mode,
    [property: JsonPropertyName("state")] ConsoleSessionState State,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("idleExpiresAt")] DateTimeOffset? IdleExpiresAt,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<ConsoleCapability> Capabilities,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("serverDerived")] bool ServerDerived,
    [property: JsonPropertyName("reason")] string? Reason = null);
