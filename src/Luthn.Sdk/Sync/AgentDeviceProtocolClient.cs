// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Luthn.Sdk.Sync;

public sealed record AgentDeviceProtocolOptions
{
    public Uri? BaseUri { get; init; }
    public string Audience { get; init; } = "luthn-cloud-agent";

    public bool IsEnabled => BaseUri is not null;

    public Uri Resolve(string relativePath)
    {
        if (BaseUri is null || !BaseUri.IsAbsoluteUri ||
            (!string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !BaseUri.IsLoopback) ||
            !string.IsNullOrEmpty(BaseUri.UserInfo) ||
            !string.IsNullOrEmpty(BaseUri.Query) ||
            !string.IsNullOrEmpty(BaseUri.Fragment) ||
            !IsOpaque(Audience, 256))
        {
            throw new InvalidOperationException("Cloud AgentDevice protocol configuration is invalid.");
        }

        var origin = BaseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return new Uri($"{origin}/{relativePath.TrimStart('/')}");
    }

    public Uri RemoteMcpUri => Resolve("mcp");

    private static bool IsOpaque(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '-' or '_' or '.');
}

public sealed record X25519KeyMaterial(
    string PrivateKey,
    string KeyId,
    X25519PublicJwkDto PublicKey);

public sealed record AgentDeviceKeyMaterial(
    CloudHubKeyMaterial AuthenticationKey,
    X25519KeyMaterial RelaySenderKey,
    X25519KeyMaterial SensitiveRecipientKey);

public sealed record AgentDevicePendingEnrollment(
    string EnrollmentId,
    Uri VerificationUri,
    string UserCode,
    DateTimeOffset ExpiresAt,
    int PollIntervalSeconds,
    DateTimeOffset? LastPolledAt = null);

public sealed record AgentDeviceSession(
    Guid AgentDeviceId,
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshCredential,
    DateTimeOffset RefreshExpiresAt,
    string ConfirmationJwkThumbprint,
    IReadOnlyList<string> Scopes);

public sealed record AgentDeviceLocalState(
    AgentDeviceKeyMaterial Key,
    AgentDevicePendingEnrollment? PendingEnrollment = null,
    AgentDeviceSession? Session = null,
    IReadOnlyList<CloudAgentConnectionDto>? Connections = null);

public sealed record AgentDeviceEnrollmentBeginResult(
    AgentDeviceLocalState State,
    AgentDeviceEnrollmentChallengeDto Challenge);

public sealed record AgentDeviceEnrollmentPollResult(
    AgentDeviceLocalState State,
    AgentDeviceEnrollmentState StateValue,
    int? RetryAfterSeconds = null);

public sealed record AgentDeviceConnectionCreateResult(
    AgentDeviceLocalState State,
    CloudAgentConnectionDto Connection);

public sealed record AgentDeviceConnectionReadResult(
    AgentDeviceLocalState State,
    CloudAgentConnectionDto? Connection);

public sealed class AgentDeviceProtocolException(
    string errorCode,
    HttpStatusCode? statusCode = null) : InvalidOperationException(errorCode)
{
    public string ErrorCode { get; } = errorCode;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class AgentDeviceProtocolClient(HttpClient httpClient, TimeProvider timeProvider)
{
    public const string ConnectionWriteScope = "agent-connection.write";
    public const string RotateScope = "agent-device.rotate";
    private const string DpopType = "dpop+jwt";
    private const string Algorithm = "ES256";
    private static readonly TimeSpan CredentialClockSkew = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static AgentDeviceLocalState CreateLocalState() =>
        new(CreateKeyMaterial(), Connections: []);

    public async Task<AgentDeviceEnrollmentBeginResult> BeginEnrollmentAsync(
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        string displayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedDisplayName = displayName?.Trim();
        if (state.Session is not null ||
            string.IsNullOrWhiteSpace(normalizedDisplayName) ||
            normalizedDisplayName.Length > 160 ||
            !string.Equals(normalizedDisplayName, displayName, StringComparison.Ordinal))
        {
            throw new AgentDeviceProtocolException("agent_device.invalid_start");
        }

        var startUri = options.Resolve("api/v1/agent-device-enrollments/");
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, startUri)
        {
            Content = JsonContent.Create(
                new AgentDeviceEnrollmentStartDto(
                    AgentDeviceContractVersions.V1,
                    normalizedDisplayName,
                    [
                        AgentDeviceCapabilities.Device,
                        AgentDeviceCapabilities.RelayWrite,
                        AgentDeviceCapabilities.RemoteMcp,
                    ]),
                options: SerializerOptions),
        };
        using var startResponse = await httpClient.SendAsync(
            startRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(startResponse, "agent_device.start_failed", cancellationToken);
        var challenge = await ReadJsonAsync<AgentDeviceEnrollmentChallengeDto>(
            startResponse,
            cancellationToken);
        if (!startResponse.Headers.TryGetValues("DPoP-Nonce", out var nonceValues) ||
            nonceValues.SingleOrDefault() is not { } nonce ||
            !IsOpaque(nonce, 256) ||
            !IsValidChallenge(challenge, timeProvider.GetUtcNow()))
        {
            throw new AgentDeviceProtocolException("agent_device.invalid_challenge");
        }

        var proofUri = options.Resolve(
            $"api/v1/agent-device-enrollments/{challenge.EnrollmentId}/proof");
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
            Content = JsonContent.Create(proof, options: SerializerOptions),
        };
        using var proofResponse = await httpClient.SendAsync(
            proofRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(proofResponse, "agent_device.proof_failed", cancellationToken);

        var pending = new AgentDevicePendingEnrollment(
            challenge.EnrollmentId,
            challenge.VerificationUri,
            challenge.UserCode,
            challenge.ExpiresAt,
            challenge.PollIntervalSeconds);
        return new AgentDeviceEnrollmentBeginResult(
            state with { PendingEnrollment = pending },
            challenge);
    }

    public async Task<AgentDeviceEnrollmentPollResult> PollEnrollmentAsync(
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var pending = state.PendingEnrollment ??
            throw new AgentDeviceProtocolException("agent_device.not_pending");
        var now = timeProvider.GetUtcNow();
        if (pending.ExpiresAt <= now)
        {
            return new AgentDeviceEnrollmentPollResult(
                state with { PendingEnrollment = null },
                AgentDeviceEnrollmentState.Expired);
        }

        if (pending.LastPolledAt is { } lastPolledAt &&
            lastPolledAt.AddSeconds(pending.PollIntervalSeconds) > now)
        {
            var seconds = Math.Max(
                1,
                (int)Math.Ceiling(
                    (lastPolledAt.AddSeconds(pending.PollIntervalSeconds) - now).TotalSeconds));
            return new AgentDeviceEnrollmentPollResult(
                state,
                AgentDeviceEnrollmentState.Pending,
                seconds);
        }

        var pollUri = options.Resolve("api/v1/agent-device-enrollments/poll");
        using var request = new HttpRequestMessage(HttpMethod.Post, pollUri)
        {
            Content = JsonContent.Create(
                new AgentDeviceEnrollmentPollRequestDto(pending.EnrollmentId),
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
            return new AgentDeviceEnrollmentPollResult(
                state with { PendingEnrollment = pending with { LastPolledAt = now } },
                AgentDeviceEnrollmentState.Pending,
                retryAfter);
        }

        await EnsureSuccessAsync(response, "agent_device.poll_failed", cancellationToken);
        var payload = await ReadJsonAsync<AgentDeviceEnrollmentPollResponseDto>(
            response,
            cancellationToken);
        var nextState = state with
        {
            PendingEnrollment = pending with { LastPolledAt = now },
        };
        if (payload.State == AgentDeviceEnrollmentState.Approved)
        {
            var grant = payload.SessionGrant ??
                throw new AgentDeviceProtocolException("agent_device.missing_session_grant");
            var session = ValidateGrant(grant, state.Key, now);
            if (!string.Equals(payload.AgentDeviceId, grant.AgentDeviceId, StringComparison.Ordinal))
            {
                throw new AgentDeviceProtocolException("agent_device.invalid_session_grant");
            }
            nextState = nextState with { PendingEnrollment = null, Session = session };
        }
        else if (payload.State is AgentDeviceEnrollmentState.Denied or
                 AgentDeviceEnrollmentState.Expired or
                 AgentDeviceEnrollmentState.Revoked)
        {
            if (payload.AgentDeviceId is not null || payload.SessionGrant is not null)
            {
                throw new AgentDeviceProtocolException("agent_device.invalid_status");
            }
            nextState = nextState with { PendingEnrollment = null, Session = null };
        }
        else if (payload.AgentDeviceId is not null || payload.SessionGrant is not null)
        {
            throw new AgentDeviceProtocolException("agent_device.invalid_status");
        }

        return new AgentDeviceEnrollmentPollResult(nextState, payload.State);
    }

    public async Task<AgentDeviceConnectionCreateResult> CreateConnectionAsync(
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        Guid workspaceId,
        string agentKind,
        string capabilityPreset,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty ||
            agentKind is not ("codex" or "claude") ||
            capabilityPreset is not ("reader" or "contributor" or "sensitive-requester") ||
            !IsOpaque(idempotencyKey, 128) ||
            idempotencyKey.Length < 16)
        {
            throw new AgentDeviceProtocolException("agent_connection.invalid_request");
        }

        var current = await EnsureFreshSessionAsync(state, options, cancellationToken);
        if (current.Session is null ||
            !current.Session.Scopes.Contains(ConnectionWriteScope, StringComparer.Ordinal))
        {
            throw new AgentDeviceProtocolException("agent_device.not_connected");
        }

        var requestPayload = new CreateCloudAgentConnectionDto(
            workspaceId,
            agentKind,
            capabilityPreset,
            idempotencyKey);
        var first = await SendConnectionAsync(current, options, requestPayload, cancellationToken);
        if (first.StatusCode == HttpStatusCode.Unauthorized)
        {
            current = await RefreshSessionAsync(current, options, cancellationToken);
            if (current.Session is null)
            {
                throw new AgentDeviceProtocolException("agent_device.revoked", HttpStatusCode.Unauthorized);
            }
            first = await SendConnectionAsync(current, options, requestPayload, cancellationToken);
        }
        if (first.StatusCode == HttpStatusCode.Unauthorized || first.Connection is null)
        {
            throw new AgentDeviceProtocolException("agent_device.revoked", HttpStatusCode.Unauthorized);
        }

        var connection = ValidateConnection(first.Connection, current.Session.AgentDeviceId, requestPayload);
        var connections = (current.Connections ?? [])
            .Where(item => item.Id != connection.Id)
            .Append(connection)
            .OrderBy(item => item.AgentKind, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .ToArray();
        return new AgentDeviceConnectionCreateResult(
            current with { Connections = connections },
            connection);
    }

    public async Task<AgentDeviceConnectionReadResult> GetConnectionAsync(
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty)
        {
            throw new AgentDeviceProtocolException("agent_connection.invalid_request");
        }

        var current = await EnsureFreshSessionAsync(state, options, cancellationToken);
        if (current.Session is null ||
            !current.Session.Scopes.Contains(ConnectionWriteScope, StringComparer.Ordinal))
        {
            throw new AgentDeviceProtocolException("agent_device.not_connected");
        }

        var first = await SendConnectionStatusAsync(
            current,
            options,
            connectionId,
            cancellationToken);
        if (first.StatusCode == HttpStatusCode.Unauthorized)
        {
            current = await RefreshSessionAsync(current, options, cancellationToken);
            if (current.Session is null)
            {
                throw new AgentDeviceProtocolException(
                    "agent_device.revoked",
                    HttpStatusCode.Unauthorized);
            }
            first = await SendConnectionStatusAsync(
                current,
                options,
                connectionId,
                cancellationToken);
        }
        if (first.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new AgentDeviceProtocolException(
                "agent_device.revoked",
                HttpStatusCode.Unauthorized);
        }
        if (first.Connection is null)
        {
            if (first.StatusCode == HttpStatusCode.NotFound &&
                string.Equals(
                    first.ErrorCode,
                    "agent_connection.not_found",
                    StringComparison.Ordinal))
            {
                return new AgentDeviceConnectionReadResult(current, null);
            }

            throw new AgentDeviceProtocolException(
                first.ErrorCode ?? "agent_connection.status_failed",
                first.StatusCode);
        }

        var connection = ValidateConnectionStatus(
            first.Connection,
            current.Session.AgentDeviceId,
            connectionId);
        var connections = (current.Connections ?? [])
            .Where(item => item.Id != connection.Id)
            .Append(connection)
            .OrderBy(item => item.AgentKind, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .ToArray();
        return new AgentDeviceConnectionReadResult(
            current with { Connections = connections },
            connection);
    }

    public async Task<AgentDeviceLocalState> RotateKeysAsync(
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        CancellationToken cancellationToken)
    {
        var current = await EnsureFreshSessionAsync(state, options, cancellationToken);
        if (current.Session is null ||
            !current.Session.Scopes.Contains(RotateScope, StringComparer.Ordinal))
        {
            throw new AgentDeviceProtocolException("agent_device.not_connected");
        }

        var beginUri = options.Resolve("api/v1/agent/key-rotations");
        using var beginRequest = new HttpRequestMessage(HttpMethod.Post, beginUri);
        AddDpopHeaders(beginRequest, current.Key.AuthenticationKey, current.Session.AccessToken);
        using var beginResponse = await httpClient.SendAsync(
            beginRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(beginResponse, "agent_device.rotation_start_failed", cancellationToken);
        var challenge = await ReadJsonAsync<AgentDeviceKeyRotationChallengeDto>(
            beginResponse,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (!IsOpaque(challenge.RotationId, 128) ||
            !IsOpaque(challenge.Nonce, 256) ||
            challenge.ExpiresAt <= now ||
            challenge.ExpiresAt > now.AddMinutes(5))
        {
            throw new AgentDeviceProtocolException("agent_device.rotation_invalid");
        }

        var nextKey = CreateKeyMaterial();
        var completeUri = options.Resolve(
            $"api/v1/agent/key-rotations/{challenge.RotationId}/proof");
        var enrollmentProof = CreateEnrollmentProof(
            nextKey,
            challenge.RotationId,
            challenge.Nonce,
            options.Audience,
            HttpMethod.Post.Method,
            completeUri,
            now);
        using var completeRequest = new HttpRequestMessage(HttpMethod.Post, completeUri)
        {
            Content = JsonContent.Create(
                new CompleteAgentDeviceKeyRotationDto(enrollmentProof),
                options: SerializerOptions),
        };
        AddDpopHeaders(completeRequest, current.Key.AuthenticationKey, current.Session.AccessToken);
        using var completeResponse = await httpClient.SendAsync(
            completeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(
            completeResponse,
            "agent_device.rotation_complete_failed",
            cancellationToken);
        var grant = await ReadJsonAsync<AgentDeviceSessionGrantDto>(
            completeResponse,
            cancellationToken);
        return current with { Key = nextKey, Session = ValidateGrant(grant, nextKey, now) };
    }

    private async Task<(HttpStatusCode StatusCode, CloudAgentConnectionDto? Connection)> SendConnectionAsync(
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        CreateCloudAgentConnectionDto payload,
        CancellationToken cancellationToken)
    {
        var uri = options.Resolve("api/v1/agent/connections");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        AddDpopHeaders(request, state.Key.AuthenticationKey, state.Session!.AccessToken);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return (response.StatusCode, null);
        }
        await EnsureSuccessAsync(response, "agent_connection.create_failed", cancellationToken);
        return (
            response.StatusCode,
            await ReadJsonAsync<CloudAgentConnectionDto>(response, cancellationToken));
    }

    private async Task<(
        HttpStatusCode StatusCode,
        CloudAgentConnectionDto? Connection,
        string? ErrorCode)> SendConnectionStatusAsync(
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var uri = options.Resolve($"api/v1/agent/connections/{connectionId:D}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddDpopHeaders(request, state.Key.AuthenticationKey, state.Session!.AccessToken);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (
                response.StatusCode,
                null,
                response.StatusCode == HttpStatusCode.Unauthorized
                    ? null
                    : await ReadErrorCodeAsync(
                        response,
                        "agent_connection.status_failed",
                        cancellationToken));
        }

        return (
            response.StatusCode,
            await ReadJsonAsync<CloudAgentConnectionDto>(response, cancellationToken),
            null);
    }

    private async Task<AgentDeviceLocalState> EnsureFreshSessionAsync(
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
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

    private async Task<AgentDeviceLocalState> RefreshSessionAsync(
        AgentDeviceLocalState state,
        AgentDeviceProtocolOptions options,
        CancellationToken cancellationToken)
    {
        if (state.Session is null) return state;
        var uri = options.Resolve("api/v1/agent-device-sessions/refresh");
        var proof = CloudHubProtocolClient.CreateRequestProof(
            state.Key.AuthenticationKey,
            state.Session.RefreshCredential,
            HttpMethod.Post.Method,
            uri,
            timeProvider.GetUtcNow());
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(
                new RefreshAgentDeviceSessionRequestDto(
                    state.Session.RefreshCredential,
                    proof),
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
        await EnsureSuccessAsync(response, "agent_device.refresh_failed", cancellationToken);
        var grant = await ReadJsonAsync<AgentDeviceSessionGrantDto>(response, cancellationToken);
        return state with { Session = ValidateGrant(grant, state.Key, timeProvider.GetUtcNow()) };
    }

    private void AddDpopHeaders(
        HttpRequestMessage request,
        CloudHubKeyMaterial key,
        string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
        request.Headers.TryAddWithoutValidation(
            "DPoP",
            CloudHubProtocolClient.CreateRequestProof(
                key,
                accessToken,
                request.Method.Method,
                request.RequestUri!,
                timeProvider.GetUtcNow()));
    }

    private static AgentDeviceEnrollmentProofDto CreateEnrollmentProof(
        AgentDeviceKeyMaterial key,
        string enrollmentId,
        string nonce,
        string audience,
        string method,
        Uri uri,
        DateTimeOffset now)
    {
        var payload = new AgentEnrollmentProofPayload(
            CloudHubProtocolClient.NewJti(),
            method,
            CloudHubProtocolClient.NormalizeHtu(uri),
            now.ToUnixTimeSeconds(),
            now.AddMinutes(1).ToUnixTimeSeconds(),
            nonce,
            audience,
            enrollmentId,
            AgentDeviceContractVersions.V1,
            key.AuthenticationKey.KeyId,
            key.RelaySenderKey.KeyId,
            key.SensitiveRecipientKey.KeyId);
        return new AgentDeviceEnrollmentProofDto(
            AgentDeviceContractVersions.V1,
            enrollmentId,
            key.AuthenticationKey.KeyId,
            key.AuthenticationKey.PublicKey,
            key.RelaySenderKey.PublicKey,
            key.SensitiveRecipientKey.PublicKey,
            CloudHubProtocolClient.CreateProof(key.AuthenticationKey, payload));
    }

    private static AgentDeviceSession ValidateGrant(
        AgentDeviceSessionGrantDto grant,
        AgentDeviceKeyMaterial key,
        DateTimeOffset now)
    {
        if (!Guid.TryParseExact(grant.AgentDeviceId, "D", out var agentDeviceId) ||
            !string.Equals(grant.TokenType, "DPoP", StringComparison.Ordinal) ||
            !string.Equals(
                grant.ConfirmationJwkThumbprint,
                key.AuthenticationKey.KeyId,
                StringComparison.Ordinal) ||
            grant.ExpiresInSeconds != 300 ||
            grant.RefreshExpiresAt <= now ||
            grant.RefreshExpiresAt > now.AddDays(30).Add(CredentialClockSkew) ||
            !IsOpaque(grant.AccessToken, 4096) ||
            !IsOpaque(grant.RefreshCredential, 4096) ||
            grant.Scopes is null ||
            !grant.Scopes.Contains(ConnectionWriteScope, StringComparer.Ordinal) ||
            !grant.Scopes.Contains(RotateScope, StringComparer.Ordinal) ||
            grant.Scopes.Count != grant.Scopes.Distinct(StringComparer.Ordinal).Count())
        {
            throw new AgentDeviceProtocolException("agent_device.invalid_session_grant");
        }
        return new AgentDeviceSession(
            agentDeviceId,
            grant.AccessToken,
            now.AddSeconds(grant.ExpiresInSeconds),
            grant.RefreshCredential,
            grant.RefreshExpiresAt,
            grant.ConfirmationJwkThumbprint,
            grant.Scopes.ToArray());
    }

    private static CloudAgentConnectionDto ValidateConnection(
        CloudAgentConnectionDto connection,
        Guid agentDeviceId,
        CreateCloudAgentConnectionDto request)
    {
        if (connection.Id == Guid.Empty ||
            connection.OrganizationId == Guid.Empty ||
            connection.WorkspaceId != request.WorkspaceId ||
            connection.AgentDeviceId != agentDeviceId ||
            !string.Equals(connection.AgentKind, request.AgentKind, StringComparison.Ordinal) ||
            !string.Equals(connection.CapabilityPreset, request.CapabilityPreset, StringComparison.Ordinal) ||
            !string.Equals(connection.Status, "active", StringComparison.Ordinal) ||
            (connection.OauthClientId is not null &&
             !Guid.TryParseExact(connection.OauthClientId, "D", out _)) ||
            connection.CreatedAt == default ||
            connection.UpdatedAt < connection.CreatedAt)
        {
            throw new AgentDeviceProtocolException("agent_connection.invalid_response");
        }
        return connection;
    }

    private static CloudAgentConnectionDto ValidateConnectionStatus(
        CloudAgentConnectionDto connection,
        Guid agentDeviceId,
        Guid connectionId)
    {
        if (connection.Id != connectionId ||
            connection.OrganizationId == Guid.Empty ||
            connection.WorkspaceId == Guid.Empty ||
            connection.AgentDeviceId != agentDeviceId ||
            connection.AgentKind is not ("codex" or "claude") ||
            connection.CapabilityPreset is not ("reader" or "contributor" or "sensitive-requester") ||
            connection.Status is not ("active" or "revoked") ||
            (connection.OauthClientId is not null &&
             !Guid.TryParseExact(connection.OauthClientId, "D", out _)) ||
            connection.CreatedAt == default ||
            connection.UpdatedAt < connection.CreatedAt)
        {
            throw new AgentDeviceProtocolException("agent_connection.invalid_response");
        }
        return connection;
    }

    private static AgentDeviceKeyMaterial CreateKeyMaterial()
    {
        var authentication = CloudHubProtocolClient.CreateLocalState().Key;
        return new AgentDeviceKeyMaterial(
            authentication,
            CreateX25519Key(),
            CreateX25519Key());
    }

    private static X25519KeyMaterial CreateX25519Key()
    {
        var privateKey = new X25519PrivateKeyParameters(new SecureRandom());
        var publicKeyBytes = privateKey.GeneratePublicKey().GetEncoded();
        var privateKeyBytes = privateKey.GetEncoded();
        try
        {
            var publicKey = new X25519PublicJwkDto(
                "OKP",
                "X25519",
                CloudHubProtocolClient.Base64Url(publicKeyBytes));
            var canonical = Encoding.UTF8.GetBytes(
                $"{{\"crv\":\"{publicKey.Curve}\",\"kty\":\"{publicKey.KeyType}\",\"x\":\"{publicKey.X}\"}}");
            return new X25519KeyMaterial(
                Convert.ToBase64String(privateKeyBytes),
                CloudHubProtocolClient.Base64Url(SHA256.HashData(canonical)),
                publicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
            CryptographicOperations.ZeroMemory(publicKeyBytes);
        }
    }

    private static bool IsValidChallenge(
        AgentDeviceEnrollmentChallengeDto challenge,
        DateTimeOffset now) =>
        IsOpaque(challenge.EnrollmentId, 128) &&
        challenge.VerificationUri.IsAbsoluteUri &&
        string.Equals(
            challenge.VerificationUri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(challenge.VerificationUri.UserInfo) &&
        string.IsNullOrEmpty(challenge.VerificationUri.Fragment) &&
        IsValidUserCode(challenge.UserCode) &&
        challenge.ExpiresAt > now &&
        challenge.ExpiresAt <= now.AddMinutes(15) &&
        challenge.PollIntervalSeconds is >= 2 and <= 10 &&
        !Uri.UnescapeDataString(challenge.VerificationUri.Query)
            .Contains(challenge.UserCode, StringComparison.OrdinalIgnoreCase);

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
        throw new AgentDeviceProtocolException("cloud.invalid_response", response.StatusCode);

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string fallbackCode,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentDeviceProtocolException(
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
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("code", out var directCode) &&
                IsErrorCode(directCode.GetString()))
            {
                return directCode.GetString()!;
            }
            if (document.RootElement.TryGetProperty("extensions", out var extensions) &&
                extensions.TryGetProperty("code", out var code) &&
                IsErrorCode(code.GetString()))
            {
                return code.GetString()!;
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

    private sealed record AgentEnrollmentProofPayload(
        [property: JsonPropertyName("jti")] string Jti,
        [property: JsonPropertyName("htm")] string HttpMethod,
        [property: JsonPropertyName("htu")] string HttpUri,
        [property: JsonPropertyName("iat")] long IssuedAtUnixSeconds,
        [property: JsonPropertyName("exp")] long ExpiresAtUnixSeconds,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("aud")] string Audience,
        [property: JsonPropertyName("enrollment_id")] string EnrollmentId,
        [property: JsonPropertyName("contract_version")] int ContractVersion,
        [property: JsonPropertyName("key_id")] string KeyId,
        [property: JsonPropertyName("relay_sender_key_id")] string RelaySenderKeyId,
        [property: JsonPropertyName("sensitive_recipient_key_id")] string SensitiveRecipientKeyId);
}
