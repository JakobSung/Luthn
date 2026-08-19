// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Luthn.Sdk.Sync;

public static class CloudSyncContractVersions
{
    public const int V2 = 2;
}

[JsonConverter(typeof(InstallationEnrollmentStateJsonConverter))]
public enum InstallationEnrollmentState
{
    Pending,
    Approved,
    Denied,
    Expired,
    Revoked
}

public sealed class InstallationEnrollmentStateJsonConverter()
    : JsonStringEnumConverter<InstallationEnrollmentState>(
        namingPolicy: null,
        allowIntegerValues: false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstallationEnrollmentChallengeDto(
    [property: JsonPropertyName("enrollmentId")] string EnrollmentId,
    [property: JsonPropertyName("verificationUri")] Uri VerificationUri,
    [property: JsonPropertyName("userCode")] string UserCode,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("pollIntervalSeconds")] int PollIntervalSeconds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstallationEnrollmentStatusDto(
    [property: JsonPropertyName("enrollmentId")] string EnrollmentId,
    [property: JsonPropertyName("state")] InstallationEnrollmentState State,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("installationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstallationId,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BoundedErrorDto? Error);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstallationCapabilitySetDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("supportedCapabilities")] IReadOnlyList<string> SupportedCapabilities,
    [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt);

/// <summary>
/// Describes authenticated installation authority. Tenant and workspace scope
/// are resolved by the receiver and are absent from caller-controlled payloads.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthenticatedInstallationAuthorityDto(
    [property: JsonPropertyName("installationId")] string InstallationId,
    [property: JsonPropertyName("authorityKind")] string AuthorityKind,
    [property: JsonPropertyName("authenticatedAt")] DateTimeOffset AuthenticatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafeProjectionSyncEnvelopeV2Dto(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("localRecordId")] string LocalRecordId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    [property: JsonPropertyName("safeSummary"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SafeSummary,
    [property: JsonPropertyName("coreTags")] IReadOnlyList<string> CoreTags,
    [property: JsonPropertyName("projectionKind")] string ProjectionKind,
    [property: JsonPropertyName("payloadClass")] string PayloadClass,
    [property: JsonPropertyName("redactionState")] string RedactionState,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("decidedAt")] DateTimeOffset DecidedAt,
    [property: JsonPropertyName("expiresAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafeProjectionSyncBatchDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("batchId")] string BatchId,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("items")] IReadOnlyList<SafeProjectionSyncEnvelopeV2Dto> Items,
    [property: JsonPropertyName("sentAt")] DateTimeOffset SentAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BoundedErrorDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("retryAfterSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterSeconds,
    [property: JsonPropertyName("correlationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CorrelationId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafeProjectionSyncReceiptDto(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("localRecordId")] string LocalRecordId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("acknowledgedAt")] DateTimeOffset AcknowledgedAt,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BoundedErrorDto? Error);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafeProjectionSyncCheckpointDto(
    [property: JsonPropertyName("checkpoint")] string Checkpoint,
    [property: JsonPropertyName("lastAcknowledgedOperationId")] string LastAcknowledgedOperationId,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);
