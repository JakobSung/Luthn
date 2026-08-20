// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luthn.Sdk.Sync;

public sealed record CloudHubProtocolOptions
{
    public Uri? BaseUri { get; init; }
    public string Audience { get; init; } = "luthn-cloud";

    public bool IsEnabled => BaseUri is not null;

    public Uri Resolve(string relativePath)
    {
        if (BaseUri is null || !BaseUri.IsAbsoluteUri ||
            (!string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !BaseUri.IsLoopback) ||
            !string.IsNullOrEmpty(BaseUri.UserInfo) ||
            !string.IsNullOrEmpty(BaseUri.Query) ||
            !string.IsNullOrEmpty(BaseUri.Fragment) ||
            string.IsNullOrWhiteSpace(Audience) ||
            Audience.Length > 256)
        {
            throw new InvalidOperationException("Cloud Hub protocol configuration is invalid.");
        }

        var origin = BaseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return new Uri($"{origin}/{relativePath.TrimStart('/')}");
    }
}

public sealed record CloudHubKeyMaterial(
    string PrivateKeyPkcs8,
    string KeyId,
    P256PublicJwkDto PublicKey);

public sealed record CloudHubPendingEnrollment(
    string EnrollmentId,
    Uri VerificationUri,
    string UserCode,
    DateTimeOffset ExpiresAt,
    int PollIntervalSeconds,
    DateTimeOffset? LastPolledAt = null);

public sealed record CloudHubSession(
    string InstallationId,
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshCredential,
    DateTimeOffset RefreshExpiresAt,
    string ConfirmationJwkThumbprint,
    IReadOnlyList<string> Scopes);

public sealed record CloudHubApprovedEnrollment(
    string EnrollmentId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ApprovedAt);

public sealed record CloudHubLocalState(
    CloudHubKeyMaterial Key,
    CloudHubPendingEnrollment? PendingEnrollment = null,
    CloudHubSession? Session = null,
    CloudHubApprovedEnrollment? ApprovedEnrollment = null);

public sealed record CloudHubEnrollmentBeginResult(
    CloudHubLocalState State,
    InstallationEnrollmentChallengeDto Challenge);

public sealed record CloudHubEnrollmentPollResult(
    CloudHubLocalState State,
    InstallationEnrollmentStatusDto Status,
    int? RetryAfterSeconds = null);

public sealed record CloudHubProjectionSendResult(
    CloudHubLocalState State,
    bool Accepted,
    string? Checkpoint = null,
    string? ErrorCode = null,
    bool Revoked = false);

public sealed class CloudHubProtocolException(
    string errorCode,
    HttpStatusCode? statusCode = null) : InvalidOperationException(errorCode)
{
    public string ErrorCode { get; } = errorCode;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class CloudHubProtocolClient(HttpClient httpClient, TimeProvider timeProvider)
{
    public const string SafeProjectionCapability = "safe-projection.v2";
    public const string ProjectionWriteScope = "safe-projection.write";
    private const string DpopType = "dpop+jwt";
    private const string Algorithm = "ES256";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static CloudHubLocalState CreateLocalState()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var publicKey = new P256PublicJwkDto(
            "EC",
            "P-256",
            Base64Url(parameters.Q.X!),
            Base64Url(parameters.Q.Y!));
        return new CloudHubLocalState(
            new CloudHubKeyMaterial(
                Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
                ComputeThumbprint(publicKey),
                publicKey));
    }

    public async Task<CloudHubEnrollmentBeginResult> BeginEnrollmentAsync(
        CloudHubLocalState state,
        CloudHubProtocolOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Session is not null)
        {
            throw new CloudHubProtocolException("enrollment.already_connected");
        }

        var startUri = options.Resolve("api/v2/hub-enrollments/");
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, startUri)
        {
            Content = JsonContent.Create(
                new HubEnrollmentStartDto(
                    CloudSyncContractVersions.V2,
                    [SafeProjectionCapability]),
                options: SerializerOptions),
        };
        using var startResponse = await httpClient.SendAsync(
            startRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(startResponse, "enrollment.start_failed", cancellationToken);
        var challenge = await ReadJsonAsync<InstallationEnrollmentChallengeDto>(
            startResponse,
            cancellationToken);
        if (!startResponse.Headers.TryGetValues("DPoP-Nonce", out var nonceValues) ||
            nonceValues.SingleOrDefault() is not { } nonce ||
            !IsOpaque(nonce, 256) ||
            !IsValidChallenge(challenge, timeProvider.GetUtcNow()))
        {
            throw new CloudHubProtocolException("enrollment.invalid_challenge");
        }

        var proofUri = options.Resolve("api/v2/hub-enrollments/proof");
        var proof = CreateEnrollmentProof(
            state.Key,
            challenge.EnrollmentId,
            nonce,
            options.Audience,
            HttpMethod.Post.Method,
            proofUri,
            timeProvider.GetUtcNow());
        using var proofRequest = new HttpRequestMessage(HttpMethod.Post, proofUri)
        {
            Content = JsonContent.Create(
                new HubEnrollmentProofDto(
                    HubEnrollmentProofContractVersions.V2,
                    challenge.EnrollmentId,
                    state.Key.KeyId,
                    state.Key.PublicKey,
                    proof),
                options: SerializerOptions),
        };
        using var proofResponse = await httpClient.SendAsync(
            proofRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(proofResponse, "enrollment.proof_failed", cancellationToken);

        var pending = new CloudHubPendingEnrollment(
            challenge.EnrollmentId,
            challenge.VerificationUri,
            challenge.UserCode,
            challenge.ExpiresAt,
            challenge.PollIntervalSeconds);
        return new CloudHubEnrollmentBeginResult(
            state with { PendingEnrollment = pending, ApprovedEnrollment = null },
            challenge);
    }

    public async Task<CloudHubEnrollmentPollResult> PollEnrollmentAsync(
        CloudHubLocalState state,
        CloudHubProtocolOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var pending = state.PendingEnrollment ??
            throw new CloudHubProtocolException("enrollment.not_pending");
        var now = timeProvider.GetUtcNow();
        if (pending.ExpiresAt <= now)
        {
            var expired = new InstallationEnrollmentStatusDto(
                pending.EnrollmentId,
                InstallationEnrollmentState.Expired,
                pending.ExpiresAt,
                null,
                new BoundedErrorDto("enrollment.expired", false, null, null));
            return new CloudHubEnrollmentPollResult(
                state with { PendingEnrollment = null },
                expired);
        }

        if (pending.LastPolledAt is { } lastPolledAt)
        {
            var nextPollAt = lastPolledAt.AddSeconds(pending.PollIntervalSeconds);
            if (nextPollAt > now)
            {
                return new CloudHubEnrollmentPollResult(
                    state,
                    new InstallationEnrollmentStatusDto(
                        pending.EnrollmentId,
                        InstallationEnrollmentState.Pending,
                        pending.ExpiresAt,
                        null,
                        null),
                    Math.Max(1, (int)Math.Ceiling((nextPollAt - now).TotalSeconds)));
            }
        }

        var pollUri = options.Resolve("api/v2/hub-enrollments/poll");
        using var request = new HttpRequestMessage(HttpMethod.Post, pollUri)
        {
            Content = JsonContent.Create(
                new HubEnrollmentPollRequestDto(pending.EnrollmentId),
                options: SerializerOptions),
        };
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta is { } delta
                ? Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds))
                : pending.PollIntervalSeconds;
            return new CloudHubEnrollmentPollResult(
                state with { PendingEnrollment = pending with { LastPolledAt = now } },
                new InstallationEnrollmentStatusDto(
                    pending.EnrollmentId,
                    InstallationEnrollmentState.Pending,
                    pending.ExpiresAt,
                    null,
                    null),
                retryAfter);
        }

        await EnsureSuccessAsync(response, "enrollment.poll_failed", cancellationToken);
        var payload = await ReadJsonAsync<HubEnrollmentPollResponseDto>(response, cancellationToken);
        ValidatePollResponse(payload, pending, now);
        var nextState = state with
        {
            PendingEnrollment = pending with { LastPolledAt = now },
        };
        if (payload.Status.State == InstallationEnrollmentState.Approved)
        {
            var grant = payload.SessionGrant ??
                throw new CloudHubProtocolException("enrollment.missing_session_grant");
            var session = ValidateGrant(grant, state.Key, now);
            nextState = nextState with
            {
                PendingEnrollment = null,
                Session = session,
                ApprovedEnrollment = new CloudHubApprovedEnrollment(
                    pending.EnrollmentId,
                    pending.ExpiresAt,
                    payload.Capabilities!.IssuedAt),
            };
        }
        else if (payload.Status.State is InstallationEnrollmentState.Denied or
                 InstallationEnrollmentState.Expired or
                 InstallationEnrollmentState.Revoked)
        {
            nextState = nextState with
            {
                PendingEnrollment = null,
                Session = null,
                ApprovedEnrollment = null,
            };
        }

        return new CloudHubEnrollmentPollResult(nextState, payload.Status);
    }

    public async Task<CloudHubProjectionSendResult> SendProjectionAsync(
        CloudHubLocalState state,
        CloudHubProtocolOptions options,
        SafeProjectionSyncEnvelopeV2Dto item,
        CancellationToken cancellationToken)
        => await SendProjectionAsync(
            state,
            options,
            item,
            stateCheckpoint: null,
            cancellationToken);

    public async Task<CloudHubProjectionSendResult> SendProjectionAsync(
        CloudHubLocalState state,
        CloudHubProtocolOptions options,
        SafeProjectionSyncEnvelopeV2Dto item,
        Action<CloudHubLocalState>? stateCheckpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(item);
        var current = await EnsureFreshSessionAsync(state, options, cancellationToken);
        if (!ReferenceEquals(current, state))
        {
            stateCheckpoint?.Invoke(current);
        }
        if (current.Session is null)
        {
            return new CloudHubProjectionSendResult(
                current,
                Accepted: false,
                ErrorCode: "hub.not_connected",
                Revoked: state.Session is not null);
        }

        var first = await SendProjectionCoreAsync(current, options, item, cancellationToken);
        if (first.StatusCode != HttpStatusCode.Unauthorized)
        {
            return first.Result!;
        }

        var refreshed = await RefreshSessionAsync(current, options, cancellationToken);
        stateCheckpoint?.Invoke(refreshed);
        if (refreshed.Session is null)
        {
            return new CloudHubProjectionSendResult(
                refreshed,
                Accepted: false,
                ErrorCode: "hub.revoked",
                Revoked: true);
        }

        var retry = await SendProjectionCoreAsync(refreshed, options, item, cancellationToken);
        return retry.StatusCode == HttpStatusCode.Unauthorized
            ? new CloudHubProjectionSendResult(
                refreshed with { Session = null },
                Accepted: false,
                ErrorCode: "hub.revoked",
                Revoked: true)
            : retry.Result!;
    }

    private async Task<(HttpStatusCode StatusCode, CloudHubProjectionSendResult? Result)> SendProjectionCoreAsync(
        CloudHubLocalState state,
        CloudHubProtocolOptions options,
        SafeProjectionSyncEnvelopeV2Dto item,
        CancellationToken cancellationToken)
    {
        var session = state.Session!;
        var uri = options.Resolve("api/v2/hub/projections");
        var now = timeProvider.GetUtcNow();
        var batchId = OpaqueDigest("batch", JsonSerializer.SerializeToUtf8Bytes(item, SerializerOptions));
        var batch = new SafeProjectionSyncBatchDto(
            CloudSyncContractVersions.V2,
            batchId,
            [SafeProjectionCapability],
            [item],
            now);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(batch, options: SerializerOptions),
        };
        AddDpopHeaders(request, state.Key, session.AccessToken, now);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return (response.StatusCode, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (
                response.StatusCode,
                new CloudHubProjectionSendResult(
                    state,
                    Accepted: false,
                    ErrorCode: await ReadErrorCodeAsync(response, "cloud.rejected", cancellationToken)));
        }

        var result = await ReadJsonAsync<SafeProjectionSyncBatchResponseDto>(response, cancellationToken);
        if (!string.Equals(result.BatchId, batchId, StringComparison.Ordinal) ||
            !IsOpaque(result.Checkpoint.Checkpoint, 256) ||
            !string.Equals(
                result.Checkpoint.LastAcknowledgedOperationId,
                item.OperationId,
                StringComparison.Ordinal) ||
            result.Checkpoint.UpdatedAt == default)
        {
            throw new CloudHubProtocolException("sync.invalid_receipt");
        }
        var receipt = result.Receipts.Count == 1 ? result.Receipts[0] :
            throw new CloudHubProtocolException("sync.invalid_receipt");
        if (!string.Equals(receipt.OperationId, item.OperationId, StringComparison.Ordinal) ||
            !string.Equals(receipt.LocalRecordId, item.LocalRecordId, StringComparison.Ordinal) ||
            receipt.Revision != item.Revision ||
            receipt.AcknowledgedAt == default ||
            receipt.Outcome is not ("Accepted" or "AlreadyApplied" or "Rejected") ||
            receipt.Error is null && receipt.Retryable ||
            receipt.Error is not null &&
            (!IsErrorCode(receipt.Error.Code) || receipt.Error.Retryable != receipt.Retryable))
        {
            throw new CloudHubProtocolException("sync.invalid_receipt");
        }

        var accepted = receipt.Outcome is "Accepted" or "AlreadyApplied" ||
            receipt.Outcome == "Rejected" &&
            !receipt.Retryable &&
            receipt.Error?.Code == "sync.stale_revision";
        return (
            response.StatusCode,
            new CloudHubProjectionSendResult(
                state,
                accepted,
                result.Checkpoint.Checkpoint,
                accepted ? null : receipt.Error?.Code ?? "sync.rejected"));
    }

    private async Task<CloudHubLocalState> EnsureFreshSessionAsync(
        CloudHubLocalState state,
        CloudHubProtocolOptions options,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (state.Session is null || state.Session.RefreshExpiresAt <= now)
        {
            return state with { Session = null };
        }

        return state.Session.AccessExpiresAt > now.AddSeconds(30)
            ? state
            : await RefreshSessionAsync(state, options, cancellationToken);
    }

    private async Task<CloudHubLocalState> RefreshSessionAsync(
        CloudHubLocalState state,
        CloudHubProtocolOptions options,
        CancellationToken cancellationToken)
    {
        var session = state.Session;
        if (session is null)
        {
            return state;
        }

        var uri = options.Resolve("api/v2/hub-sessions/refresh");
        var proof = CreateRequestProof(
            state.Key,
            session.RefreshCredential,
            HttpMethod.Post.Method,
            uri,
            timeProvider.GetUtcNow());
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(
                new RefreshHubSessionRequestDto(session.RefreshCredential, proof),
                options: SerializerOptions),
        };
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return state with { Session = null };
        }

        await EnsureSuccessAsync(response, "hub.refresh_failed", cancellationToken);
        var grant = await ReadJsonAsync<HubSessionGrantDto>(response, cancellationToken);
        return state with { Session = ValidateGrant(grant, state.Key, timeProvider.GetUtcNow()) };
    }

    private void AddDpopHeaders(
        HttpRequestMessage request,
        CloudHubKeyMaterial key,
        string accessToken,
        DateTimeOffset now)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
        request.Headers.TryAddWithoutValidation(
            "DPoP",
            CreateRequestProof(key, accessToken, request.Method.Method, request.RequestUri!, now));
    }

    private static CloudHubSession ValidateGrant(
        HubSessionGrantDto grant,
        CloudHubKeyMaterial key,
        DateTimeOffset now)
    {
        if (!string.Equals(grant.TokenType, "DPoP", StringComparison.Ordinal) ||
            !string.Equals(grant.ConfirmationJwkThumbprint, key.KeyId, StringComparison.Ordinal) ||
            grant.ExpiresInSeconds is < 1 or > 300 ||
            grant.RefreshExpiresAt <= now ||
            !IsOpaque(grant.InstallationId, 128) ||
            !IsOpaque(grant.AccessToken, 4096) ||
            !IsOpaque(grant.RefreshCredential, 4096) ||
            !grant.Scopes.Contains(ProjectionWriteScope, StringComparer.Ordinal))
        {
            throw new CloudHubProtocolException("hub.invalid_session_grant");
        }

        return new CloudHubSession(
            grant.InstallationId,
            grant.AccessToken,
            now.AddSeconds(grant.ExpiresInSeconds),
            grant.RefreshCredential,
            grant.RefreshExpiresAt,
            grant.ConfirmationJwkThumbprint,
            grant.Scopes.ToArray());
    }

    private static string CreateEnrollmentProof(
        CloudHubKeyMaterial key,
        string enrollmentId,
        string nonce,
        string audience,
        string method,
        Uri uri,
        DateTimeOffset now) =>
        CreateProof(
            key,
            new EnrollmentProofPayload(
                NewJti(),
                method,
                NormalizeHtu(uri),
                now.ToUnixTimeSeconds(),
                now.AddMinutes(1).ToUnixTimeSeconds(),
                nonce,
                audience,
                enrollmentId,
                HubEnrollmentProofContractVersions.V2,
                key.KeyId));

    internal static string CreateRequestProof(
        CloudHubKeyMaterial key,
        string credential,
        string method,
        Uri uri,
        DateTimeOffset now) =>
        CreateProof(
            key,
            new RequestProofPayload(
                NewJti(),
                method,
                NormalizeHtu(uri),
                now.ToUnixTimeSeconds(),
                now.AddMinutes(1).ToUnixTimeSeconds(),
                Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(credential)))));

    internal static string CreateProof<T>(CloudHubKeyMaterial key, T payload)
    {
        var header = new ProofHeader(DpopType, Algorithm, key.PublicKey);
        var headerSegment = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header, SerializerOptions));
        var payloadSegment = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions));
        var signingInput = Encoding.ASCII.GetBytes($"{headerSegment}.{payloadSegment}");
        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(Convert.FromBase64String(key.PrivateKeyPkcs8), out _);
        var signature = signer.SignData(
            signingInput,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{headerSegment}.{payloadSegment}.{Base64Url(signature)}";
    }

    internal static string ComputeThumbprint(P256PublicJwkDto key)
    {
        var canonical = Encoding.UTF8.GetBytes(
            $"{{\"crv\":\"{key.Curve}\",\"kty\":\"{key.KeyType}\",\"x\":\"{key.X}\",\"y\":\"{key.Y}\"}}");
        return Base64Url(SHA256.HashData(canonical));
    }

    private static bool IsValidChallenge(
        InstallationEnrollmentChallengeDto challenge,
        DateTimeOffset now) =>
        IsOpaque(challenge.EnrollmentId, 128) &&
        challenge.VerificationUri.IsAbsoluteUri &&
        string.Equals(challenge.VerificationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(challenge.VerificationUri.UserInfo) &&
        string.IsNullOrEmpty(challenge.VerificationUri.Fragment) &&
        IsValidUserCode(challenge.UserCode) &&
        challenge.ExpiresAt > now &&
        challenge.ExpiresAt <= now.AddMinutes(15) &&
        challenge.PollIntervalSeconds is >= 2 and <= 10 &&
        !Uri.UnescapeDataString(challenge.VerificationUri.Query)
            .Contains(challenge.UserCode, StringComparison.OrdinalIgnoreCase);

    private static void ValidatePollResponse(
        HubEnrollmentPollResponseDto payload,
        CloudHubPendingEnrollment pending,
        DateTimeOffset now)
    {
        var status = payload.Status;
        if (!string.Equals(status.EnrollmentId, pending.EnrollmentId, StringComparison.Ordinal) ||
            status.ExpiresAt != pending.ExpiresAt ||
            status.Error is not null &&
            (!IsErrorCode(status.Error.Code) ||
             status.Error.RetryAfterSeconds is < 1 or > 3600 ||
             !status.Error.Retryable && status.Error.RetryAfterSeconds is not null))
        {
            throw new CloudHubProtocolException("enrollment.invalid_status");
        }

        if (status.State == InstallationEnrollmentState.Approved)
        {
            if (!IsOpaque(status.InstallationId, 128) ||
                payload.SessionGrant is null ||
                !string.Equals(
                    payload.SessionGrant.InstallationId,
                    status.InstallationId,
                    StringComparison.Ordinal) ||
                payload.Capabilities is null ||
                payload.Capabilities.ContractVersion != CloudSyncContractVersions.V2 ||
                !payload.Capabilities.SupportedCapabilities.Contains(
                    SafeProjectionCapability,
                    StringComparer.Ordinal) ||
                payload.Capabilities.IssuedAt < pending.ExpiresAt.AddMinutes(-15) ||
                payload.Capabilities.IssuedAt >= pending.ExpiresAt ||
                payload.Capabilities.IssuedAt > now.AddSeconds(30))
            {
                throw new CloudHubProtocolException("enrollment.invalid_status");
            }
            return;
        }

        if (status.InstallationId is not null ||
            payload.SessionGrant is not null ||
            payload.Capabilities is not null ||
            status.State == InstallationEnrollmentState.Pending && status.ExpiresAt <= now)
        {
            throw new CloudHubProtocolException("enrollment.invalid_status");
        }
    }

    private static bool IsValidUserCode(string value) =>
        value.Length == 9 &&
        value[4] == '-' &&
        value.Where((_, index) => index != 4).All(character =>
            character is >= 'A' and <= 'Z' and not ('I' or 'O') or
            >= '2' and <= '9');

    private static bool IsOpaque(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '-' or '_');

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken) ??
        throw new CloudHubProtocolException("cloud.invalid_response", response.StatusCode);

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string fallbackCode,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new CloudHubProtocolException(
                await ReadErrorCodeAsync(response, fallbackCode, cancellationToken),
                response.StatusCode);
        }
    }

    private static async Task<string> ReadErrorCodeAsync(
        HttpResponseMessage response,
        string fallbackCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("code", out var directCode) &&
                IsErrorCode(directCode.GetString()))
            {
                return directCode.GetString()!;
            }
            if (document.RootElement.TryGetProperty("extensions", out var extensions) &&
                extensions.TryGetProperty("code", out var extensionCode) &&
                IsErrorCode(extensionCode.GetString()))
            {
                return extensionCode.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return fallbackCode;
    }

    private static bool IsErrorCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
            >= '0' and <= '9' or '.' or '_' or '-');

    private static string OpaqueDigest(string prefix, byte[] value) =>
        $"{prefix}_{Base64Url(SHA256.HashData(value))}";

    internal static string NewJti() => Base64Url(RandomNumberGenerator.GetBytes(24));

    internal static string NormalizeHtu(Uri uri) =>
        uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

    internal static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowDuplicateProperties = false,
            PropertyNameCaseInsensitive = false,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private sealed record ProofHeader(
        [property: JsonPropertyName("typ")] string Type,
        [property: JsonPropertyName("alg")] string Algorithm,
        [property: JsonPropertyName("jwk")] P256PublicJwkDto PublicKey);

    private sealed record EnrollmentProofPayload(
        [property: JsonPropertyName("jti")] string Jti,
        [property: JsonPropertyName("htm")] string HttpMethod,
        [property: JsonPropertyName("htu")] string HttpUri,
        [property: JsonPropertyName("iat")] long IssuedAtUnixSeconds,
        [property: JsonPropertyName("exp")] long ExpiresAtUnixSeconds,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("aud")] string Audience,
        [property: JsonPropertyName("enrollment_id")] string EnrollmentId,
        [property: JsonPropertyName("contract_version")] int ContractVersion,
        [property: JsonPropertyName("key_id")] string KeyId);

    private sealed record RequestProofPayload(
        [property: JsonPropertyName("jti")] string Jti,
        [property: JsonPropertyName("htm")] string HttpMethod,
        [property: JsonPropertyName("htu")] string HttpUri,
        [property: JsonPropertyName("iat")] long IssuedAtUnixSeconds,
        [property: JsonPropertyName("exp")] long ExpiresAtUnixSeconds,
        [property: JsonPropertyName("ath")] string AccessTokenHash);
}
