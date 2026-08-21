// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Luthn.Sdk.Sync;

public static class AgentDeviceContractVersions
{
    public const int V1 = 1;
}

public static class AgentDeviceCapabilities
{
    public const string Device = "agent-device.v1";
    public const string RelayWrite = "relay.write.v1";
    public const string RemoteMcp = "remote-mcp.v1";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record X25519PublicJwkDto(
    [property: JsonPropertyName("kty")] string KeyType,
    [property: JsonPropertyName("crv")] string Curve,
    [property: JsonPropertyName("x")] string X);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDeviceEnrollmentStartDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDeviceEnrollmentChallengeDto(
    [property: JsonPropertyName("enrollmentId")] string EnrollmentId,
    [property: JsonPropertyName("verificationUri")] Uri VerificationUri,
    [property: JsonPropertyName("userCode")] string UserCode,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("pollIntervalSeconds")] int PollIntervalSeconds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDeviceEnrollmentProofDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("enrollmentId")] string EnrollmentId,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("authenticationPublicKey")] P256PublicJwkDto AuthenticationPublicKey,
    [property: JsonPropertyName("relaySenderPublicKey")] X25519PublicJwkDto RelaySenderPublicKey,
    [property: JsonPropertyName("sensitiveRecipientPublicKey")] X25519PublicJwkDto SensitiveRecipientPublicKey,
    [property: JsonPropertyName("proof")] string Proof);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDeviceEnrollmentPollRequestDto(
    [property: JsonPropertyName("enrollmentId")] string EnrollmentId);

[JsonConverter(typeof(AgentDeviceEnrollmentStateJsonConverter))]
public enum AgentDeviceEnrollmentState
{
    Pending,
    Approved,
    Denied,
    Expired,
    Revoked,
}

public sealed class AgentDeviceEnrollmentStateJsonConverter()
    : JsonStringEnumConverter<AgentDeviceEnrollmentState>(
        namingPolicy: null,
        allowIntegerValues: false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDeviceSessionGrantDto(
    [property: JsonPropertyName("agentDeviceId")] string AgentDeviceId,
    [property: JsonPropertyName("tokenType")] string TokenType,
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds,
    [property: JsonPropertyName("refreshCredential")] string RefreshCredential,
    [property: JsonPropertyName("refreshExpiresAt")] DateTimeOffset RefreshExpiresAt,
    [property: JsonPropertyName("confirmationJwkThumbprint")] string ConfirmationJwkThumbprint,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDeviceEnrollmentPollResponseDto
{
    [JsonPropertyName("state")]
    public required AgentDeviceEnrollmentState State { get; init; }

    [JsonPropertyName("agentDeviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentDeviceId { get; init; }

    [JsonPropertyName("sessionGrant")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentDeviceSessionGrantDto? SessionGrant { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefreshAgentDeviceSessionRequestDto(
    [property: JsonPropertyName("refreshCredential")] string RefreshCredential,
    [property: JsonPropertyName("proof")] string Proof);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCloudAgentConnectionDto(
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("agentKind")] string AgentKind,
    [property: JsonPropertyName("capabilityPreset")] string CapabilityPreset,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloudAgentConnectionDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("organizationId")] Guid OrganizationId,
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("agentDeviceId")] Guid AgentDeviceId,
    [property: JsonPropertyName("agentKind")] string AgentKind,
    [property: JsonPropertyName("capabilityPreset")] string CapabilityPreset,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("oauthClientId")] string? OauthClientId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDeviceKeyRotationChallengeDto(
    [property: JsonPropertyName("rotationId")] string RotationId,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompleteAgentDeviceKeyRotationDto(
    [property: JsonPropertyName("proof")] AgentDeviceEnrollmentProofDto Proof);
