using System.Text.Json.Serialization;

namespace Luthn.Sdk.Console;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleOrganizationState
{
    Active,
    RestrictedOffboarding,
    Detached
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleReclaimMethod
{
    CloudOwnerReauthentication,
    OfflineRecovery
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleRecoveryVerifier
{
    Disabled,
    Fake
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConsoleLifecycleDto(
    [property: JsonPropertyName("organizationState")] ConsoleOrganizationState OrganizationState,
    [property: JsonPropertyName("membership")] ConsoleMembershipState? Membership,
    [property: JsonPropertyName("connectionAuthorityActive")] bool ConnectionAuthorityActive,
    [property: JsonPropertyName("recoveryVerifier")] ConsoleRecoveryVerifier RecoveryVerifier,
    [property: JsonPropertyName("allowedActions")] IReadOnlyList<string> AllowedActions,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("serverDerived")] bool ServerDerived);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConsoleReclaimRequestDto(
    [property: JsonPropertyName("method")] ConsoleReclaimMethod Method);
