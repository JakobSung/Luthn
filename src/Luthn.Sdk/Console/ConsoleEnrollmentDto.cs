using System.Text.Json.Serialization;
using Luthn.Sdk.Sync;

namespace Luthn.Sdk.Console;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleEnrollmentAdapter
{
    Disabled,
    Fake,
    Cloud
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConsoleEnrollmentDto(
    [property: JsonPropertyName("state")] InstallationEnrollmentState? State,
    [property: JsonPropertyName("adapter")] ConsoleEnrollmentAdapter Adapter,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("installationFingerprint")] string InstallationFingerprint,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("providerLabel")] string ProviderLabel,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("serverDerived")] bool ServerDerived,
    [property: JsonPropertyName("verificationUri"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Uri? VerificationUri = null,
    [property: JsonPropertyName("userCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserCode = null);
