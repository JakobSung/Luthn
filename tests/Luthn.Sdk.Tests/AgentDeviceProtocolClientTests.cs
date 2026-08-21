using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Sdk.Sync;

namespace Luthn.Sdk.Tests;

public sealed class AgentDeviceProtocolClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentDeviceProtocolOptions Options = new()
    {
        BaseUri = new Uri("https://cloud.example"),
        Audience = "luthn-cloud-agent",
    };

    [Fact]
    public async Task EnrollmentAndConnectionUseDistinctKeysAndDeviceBoundDpop()
    {
        var state = AgentDeviceProtocolClient.CreateLocalState();
        var requests = new List<CapturedRequest>();
        var deviceId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var handler = new QueueHandler(
            request =>
            {
                requests.Add(Capture(request));
                return Json(
                    HttpStatusCode.Created,
                    $$"""
                    {
                      "enrollmentId":"enrollment_1",
                      "verificationUri":"https://cloud.example/connect/device",
                      "userCode":"ABCD-EFGH",
                      "expiresAt":"{{Now.AddMinutes(10):O}}",
                      "pollIntervalSeconds":5
                    }
                    """,
                    ("DPoP-Nonce", "nonce_1"));
            },
            request =>
            {
                requests.Add(Capture(request));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            },
            request =>
            {
                requests.Add(Capture(request));
                return Json(
                    HttpStatusCode.OK,
                    $$"""
                    {
                      "state":"Approved",
                      "agentDeviceId":"{{deviceId:D}}",
                      "sessionGrant":{
                        "agentDeviceId":"{{deviceId:D}}",
                        "tokenType":"DPoP",
                        "accessToken":"access_token_1",
                        "expiresInSeconds":300,
                        "refreshCredential":"refresh_token_1",
                        "refreshExpiresAt":"{{Now.AddDays(30).AddSeconds(10):O}}",
                        "confirmationJwkThumbprint":"{{state.Key.AuthenticationKey.KeyId}}",
                        "scopes":["agent-connection.write","relay.write","agent-device.rotate"]
                      }
                    }
                    """);
            },
            request =>
            {
                requests.Add(Capture(request));
                return Json(
                    HttpStatusCode.Created,
                    $$"""
                    {
                      "id":"{{connectionId:D}}",
                      "organizationId":"{{organizationId:D}}",
                      "workspaceId":"{{workspaceId:D}}",
                      "agentDeviceId":"{{deviceId:D}}",
                      "agentKind":"codex",
                      "capabilityPreset":"reader",
                      "status":"active",
                      "oauthClientId":null,
                      "createdAt":"{{Now:O}}",
                      "updatedAt":"{{Now:O}}"
                    }
                    """);
            });
        using var httpClient = new HttpClient(handler);
        var client = new AgentDeviceProtocolClient(httpClient, new FixedTimeProvider(Now));

        var started = await client.BeginEnrollmentAsync(
            state,
            Options,
            "Member MacBook",
            CancellationToken.None);
        var approved = await client.PollEnrollmentAsync(
            started.State,
            Options,
            CancellationToken.None);
        var connected = await client.CreateConnectionAsync(
            approved.State,
            Options,
            workspaceId,
            "codex",
            "reader",
            "codex-connection-0001",
            CancellationToken.None);

        Assert.Equal(connectionId, connected.Connection.Id);
        Assert.Single(connected.State.Connections!);
        Assert.NotEqual(state.Key.RelaySenderKey.KeyId, state.Key.SensitiveRecipientKey.KeyId);
        Assert.Equal(32, Decode(state.Key.RelaySenderKey.PublicKey.X).Length);
        Assert.Equal(32, Decode(state.Key.SensitiveRecipientKey.PublicKey.X).Length);
        Assert.Equal(4, requests.Count);
        Assert.DoesNotContain(state.Key.AuthenticationKey.PrivateKeyPkcs8, requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(state.Key.RelaySenderKey.PrivateKey, requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(state.Key.SensitiveRecipientKey.PrivateKey, requests[1].Body, StringComparison.Ordinal);

        var proofRequest = requests[1].Json;
        Assert.Equal(
            state.Key.RelaySenderKey.PublicKey.X,
            proofRequest.GetProperty("relaySenderPublicKey").GetProperty("x").GetString());
        Assert.Equal(
            state.Key.SensitiveRecipientKey.PublicKey.X,
            proofRequest.GetProperty("sensitiveRecipientPublicKey").GetProperty("x").GetString());
        AssertValidProof(
            proofRequest.GetProperty("proof").GetString()!,
            state.Key.AuthenticationKey.PublicKey,
            "POST",
            "https://cloud.example/api/v1/agent-device-enrollments/enrollment_1/proof",
            expectedCredential: null);
        Assert.Equal("DPoP", requests[3].AuthorizationScheme);
        Assert.Equal("access_token_1", requests[3].AuthorizationParameter);
        AssertValidProof(
            requests[3].Dpop!,
            state.Key.AuthenticationKey.PublicKey,
            "POST",
            "https://cloud.example/api/v1/agent/connections",
            "access_token_1");
    }

    [Fact]
    public async Task PendingEnrollmentMayOmitApprovedOnlyProperties()
    {
        var handler = new QueueHandler(
            _ => Json(
                HttpStatusCode.Created,
                $$"""
                {
                  "enrollmentId":"enrollment_1",
                  "verificationUri":"https://cloud.example/connect/device",
                  "userCode":"ABCD-EFGH",
                  "expiresAt":"{{Now.AddMinutes(10):O}}",
                  "pollIntervalSeconds":5
                }
                """,
                ("DPoP-Nonce", "nonce_1")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => Json(
                HttpStatusCode.OK,
                """
                {
                  "state":"Pending"
                }
                """));
        using var httpClient = new HttpClient(handler);
        var client = new AgentDeviceProtocolClient(httpClient, new FixedTimeProvider(Now));

        var started = await client.BeginEnrollmentAsync(
            AgentDeviceProtocolClient.CreateLocalState(),
            Options,
            "Member MacBook",
            CancellationToken.None);
        var pending = await client.PollEnrollmentAsync(
            started.State,
            Options,
            CancellationToken.None);

        Assert.Equal(AgentDeviceEnrollmentState.Pending, pending.StateValue);
        Assert.NotNull(pending.State.PendingEnrollment);
        Assert.Null(pending.State.Session);
    }

    [Fact]
    public async Task ConnectionStatusDistinguishesAContractNotFoundFromAMissingEndpoint()
    {
        var state = AgentDeviceProtocolClient.CreateLocalState();
        state = state with
        {
            Session = new AgentDeviceSession(
                Guid.NewGuid(),
                "access_token_1",
                Now.AddMinutes(5),
                "refresh_token_1",
                Now.AddDays(30),
                state.Key.AuthenticationKey.KeyId,
                [AgentDeviceProtocolClient.ConnectionWriteScope]),
        };
        var connectionId = Guid.NewGuid();
        var handler = new QueueHandler(
            _ => Json(
                HttpStatusCode.NotFound,
                """
                {
                  "extensions": {
                    "code": "agent_connection.not_found"
                  }
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);
        var client = new AgentDeviceProtocolClient(httpClient, new FixedTimeProvider(Now));

        var missingConnection = await client.GetConnectionAsync(
            state,
            Options,
            connectionId,
            CancellationToken.None);
        var missingEndpoint = await Assert.ThrowsAsync<AgentDeviceProtocolException>(() =>
            client.GetConnectionAsync(
                state,
                Options,
                connectionId,
                CancellationToken.None));

        Assert.Null(missingConnection.Connection);
        Assert.Equal("agent_connection.status_failed", missingEndpoint.ErrorCode);
    }

    [Fact]
    public async Task VerificationUriContainingTheUserCodeIsRejectedBeforeProofSubmission()
    {
        var handler = new QueueHandler(
            _ => Json(
                HttpStatusCode.Created,
                $$"""
                {
                  "enrollmentId":"enrollment_1",
                  "verificationUri":"https://cloud.example/connect/device?userCode=ABCD-EFGH",
                  "userCode":"ABCD-EFGH",
                  "expiresAt":"{{Now.AddMinutes(10):O}}",
                  "pollIntervalSeconds":5
                }
                """,
                ("DPoP-Nonce", "nonce_1")));
        using var httpClient = new HttpClient(handler);
        var client = new AgentDeviceProtocolClient(httpClient, new FixedTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<AgentDeviceProtocolException>(() =>
            client.BeginEnrollmentAsync(
                AgentDeviceProtocolClient.CreateLocalState(),
                Options,
                "Member MacBook",
                CancellationToken.None));

        Assert.Equal("agent_device.invalid_challenge", exception.ErrorCode);
        Assert.Equal(0, handler.RemainingResponseCount);
    }

    [Fact]
    public async Task KeyRotationAuthenticatesWithTheOldKeyAndCommitsOnlyTheNewKey()
    {
        var state = AgentDeviceProtocolClient.CreateLocalState();
        var deviceId = Guid.NewGuid();
        state = state with
        {
            Session = new AgentDeviceSession(
                deviceId,
                "access_token_1",
                Now.AddMinutes(5),
                "refresh_token_1",
                Now.AddDays(30),
                state.Key.AuthenticationKey.KeyId,
                [AgentDeviceProtocolClient.ConnectionWriteScope, AgentDeviceProtocolClient.RotateScope]),
        };
        CapturedRequest? beginRequest = null;
        CapturedRequest? completeRequest = null;
        var handler = new QueueHandler(
            request =>
            {
                beginRequest = Capture(request);
                return Json(
                    HttpStatusCode.Created,
                    $$"""
                    {
                      "rotationId":"rotation_1",
                      "nonce":"rotation_nonce_1",
                      "expiresAt":"{{Now.AddMinutes(5):O}}"
                    }
                    """);
            },
            request =>
            {
                completeRequest = Capture(request);
                var nextKeyId = completeRequest.Json
                    .GetProperty("proof")
                    .GetProperty("keyId")
                    .GetString();
                return Json(
                    HttpStatusCode.OK,
                    $$"""
                    {
                      "agentDeviceId":"{{deviceId:D}}",
                      "tokenType":"DPoP",
                      "accessToken":"access_token_2",
                      "expiresInSeconds":300,
                      "refreshCredential":"refresh_token_2",
                      "refreshExpiresAt":"{{Now.AddDays(30):O}}",
                      "confirmationJwkThumbprint":"{{nextKeyId}}",
                      "scopes":["agent-connection.write","relay.write","agent-device.rotate"]
                    }
                    """);
            });
        using var httpClient = new HttpClient(handler);
        var client = new AgentDeviceProtocolClient(httpClient, new FixedTimeProvider(Now));

        var rotated = await client.RotateKeysAsync(state, Options, CancellationToken.None);

        Assert.NotEqual(state.Key.AuthenticationKey.KeyId, rotated.Key.AuthenticationKey.KeyId);
        Assert.Equal(rotated.Key.AuthenticationKey.KeyId, rotated.Session!.ConfirmationJwkThumbprint);
        Assert.NotNull(beginRequest);
        Assert.NotNull(completeRequest);
        AssertValidProof(
            beginRequest.Dpop!,
            state.Key.AuthenticationKey.PublicKey,
            "POST",
            "https://cloud.example/api/v1/agent/key-rotations",
            "access_token_1");
        AssertValidProof(
            completeRequest.Dpop!,
            state.Key.AuthenticationKey.PublicKey,
            "POST",
            "https://cloud.example/api/v1/agent/key-rotations/rotation_1/proof",
            "access_token_1");
        AssertValidProof(
            completeRequest.Json.GetProperty("proof").GetProperty("proof").GetString()!,
            rotated.Key.AuthenticationKey.PublicKey,
            "POST",
            "https://cloud.example/api/v1/agent/key-rotations/rotation_1/proof",
            expectedCredential: null);
    }

    [Fact]
    public void NonTlsRemoteOriginIsRejected()
    {
        var options = new AgentDeviceProtocolOptions
        {
            BaseUri = new Uri("http://cloud.example"),
        };

        Assert.Throws<InvalidOperationException>(() => options.Resolve("mcp"));
    }

    private static void AssertValidProof(
        string proof,
        P256PublicJwkDto publicKey,
        string method,
        string uri,
        string? expectedCredential)
    {
        var segments = proof.Split('.');
        Assert.Equal(3, segments.Length);
        using var payload = JsonDocument.Parse(Decode(segments[1]));
        Assert.Equal(method, payload.RootElement.GetProperty("htm").GetString());
        Assert.Equal(uri, payload.RootElement.GetProperty("htu").GetString());
        if (expectedCredential is not null)
        {
            Assert.Equal(
                Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(expectedCredential))),
                payload.RootElement.GetProperty("ath").GetString());
        }

        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Decode(publicKey.X),
                Y = Decode(publicKey.Y),
            },
        });
        Assert.True(verifier.VerifyData(
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"),
            Decode(segments[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static CapturedRequest Capture(HttpRequestMessage request)
    {
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}";
        using var document = JsonDocument.Parse(body);
        return new CapturedRequest(
            request.RequestUri!,
            body,
            document.RootElement.Clone(),
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("DPoP", out var values) ? values.Single() : null);
    }

    private static HttpResponseMessage Json(
        HttpStatusCode status,
        string json,
        params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record CapturedRequest(
        Uri Uri,
        string Body,
        JsonElement Json,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? Dpop);

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);

        public int RemainingResponseCount => _responses.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotEmpty(_responses);
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
