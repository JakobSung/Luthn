// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Luthn.Sdk.Access;

public static class CloudSensitiveAccessContractVersions
{
    public const int V1 = 1;
}

[JsonConverter(typeof(CloudSensitiveAccessDispositionJsonConverter))]
public enum CloudSensitiveAccessDisposition
{
    Approve,
    Deny,
}

public sealed class CloudSensitiveAccessDispositionJsonConverter()
    : JsonStringEnumConverter<CloudSensitiveAccessDisposition>(
        namingPolicy: null,
        allowIntegerValues: false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloudSensitiveAccessRequestDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("memoryReference")] string MemoryReference,
    [property: JsonPropertyName("purpose")] string Purpose);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloudSensitiveAccessDecisionDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision,
    [property: JsonPropertyName("disposition")] CloudSensitiveAccessDisposition Disposition,
    [property: JsonPropertyName("reasonCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasonCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EncryptedSensitiveResultEnvelopeDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("grantId")] string GrantId,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("senderKeyId")] string SenderKeyId,
    [property: JsonPropertyName("recipientKeyId")] string RecipientKeyId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("kem")] string Kem,
    [property: JsonPropertyName("kdf")] string Kdf,
    [property: JsonPropertyName("aead")] string Aead,
    [property: JsonPropertyName("infoProfile")] string InfoProfile,
    [property: JsonPropertyName("aadProfile")] string AadProfile,
    [property: JsonPropertyName("encapsulatedKey")] string EncapsulatedKey,
    [property: JsonPropertyName("ciphertext")] string Ciphertext,
    [property: JsonPropertyName("infoSha256")] string InfoSha256,
    [property: JsonPropertyName("aadSha256")] string AadSha256,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
