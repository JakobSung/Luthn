using System.Text.Json.Serialization;

namespace Luthn.Sdk.Console;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleCloudLoginProvider
{
    Disabled,
    Fake
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleMembershipState
{
    Active,
    Removed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleEntitlementState
{
    Active,
    Restricted
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConsoleCloudLoginDto(
    [property: JsonPropertyName("provider")] ConsoleCloudLoginProvider Provider,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("sessionState")] ConsoleSessionState SessionState,
    [property: JsonPropertyName("membership")] ConsoleMembershipState? Membership,
    [property: JsonPropertyName("entitlement")] ConsoleEntitlementState? Entitlement,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<ConsoleCapability> Capabilities,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("serverDerived")] bool ServerDerived);
