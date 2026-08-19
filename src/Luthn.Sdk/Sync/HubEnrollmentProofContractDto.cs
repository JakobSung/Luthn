// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Luthn.Sdk.Sync;

public static class HubEnrollmentProofContractVersions
{
    public const int V2 = 2;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record P256PublicJwkDto(
    [property: JsonPropertyName("kty")] string KeyType,
    [property: JsonPropertyName("crv")] string Curve,
    [property: JsonPropertyName("x")] string X,
    [property: JsonPropertyName("y")] string Y);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubEnrollmentProofDto(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("enrollmentId")] string EnrollmentId,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("publicKey")] P256PublicJwkDto PublicKey,
    [property: JsonPropertyName("proof")] string Proof);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubSessionGrantDto(
    [property: JsonPropertyName("installationId")] string InstallationId,
    [property: JsonPropertyName("tokenType")] string TokenType,
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds,
    [property: JsonPropertyName("refreshCredential")] string RefreshCredential,
    [property: JsonPropertyName("refreshExpiresAt")] DateTimeOffset RefreshExpiresAt,
    [property: JsonPropertyName("confirmationJwkThumbprint")] string ConfirmationJwkThumbprint,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes);
