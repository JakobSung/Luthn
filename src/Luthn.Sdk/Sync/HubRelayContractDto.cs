// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Luthn.Sdk.Sync;

public static class HubRelayContractVersions
{
    public const int V1 = 1;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubRelayEnvelopeDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("envelopeId")] string EnvelopeId,
    [property: JsonPropertyName("senderKeyId")] string SenderKeyId,
    [property: JsonPropertyName("recipientKeyId")] string RecipientKeyId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("kem")] string Kem,
    [property: JsonPropertyName("kdf")] string Kdf,
    [property: JsonPropertyName("aead")] string Aead,
    [property: JsonPropertyName("aadProfile")] string AadProfile,
    [property: JsonPropertyName("encapsulatedKey")] string EncapsulatedKey,
    [property: JsonPropertyName("ciphertext")] string Ciphertext,
    [property: JsonPropertyName("aadSha256")] string AadSha256,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubRelayAcceptanceDto(
    [property: JsonPropertyName("receiptId")] string ReceiptId,
    [property: JsonPropertyName("envelopeId")] string EnvelopeId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("durability")] string Durability,
    [property: JsonPropertyName("acceptedAt")] DateTimeOffset AcceptedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
