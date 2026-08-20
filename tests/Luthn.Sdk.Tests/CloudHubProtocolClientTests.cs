using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Sdk.Sync;

namespace Luthn.Sdk.Tests;

public sealed class CloudHubProtocolClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly CloudHubProtocolOptions Options = new()
    {
        BaseUri = new Uri("https://cloud.example"),
        Audience = "luthn-cloud",
    };

    [Fact]
    public async Task EnrollmentAndProjectionUseBoundP256ProofsWithoutPrivateKeyOnWire()
    {
        var state = CloudHubProtocolClient.CreateLocalState();
        var requests = new List<CapturedRequest>();
        var handler = new QueueHandler(
            request =>
            {
                requests.Add(Capture(request));
                return Json(
                    HttpStatusCode.Created,
                    $$"""
                    {
                      "enrollmentId":"enrollment_1",
                      "verificationUri":"https://cloud.example/hub/activate",
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
                      "status":{
                        "enrollmentId":"enrollment_1",
                        "state":"Approved",
                        "expiresAt":"{{Now.AddMinutes(10):O}}",
                        "installationId":"installation_1"
                      },
                      "capabilities":{
                        "contractVersion":2,
                        "supportedCapabilities":["safe-projection.v2"],
                        "issuedAt":"{{Now:O}}"
                      },
                      "sessionGrant":{
                        "installationId":"installation_1",
                        "tokenType":"DPoP",
                        "accessToken":"access_token_1",
                        "expiresInSeconds":300,
                        "refreshCredential":"refresh_token_1",
                        "refreshExpiresAt":"{{Now.AddDays(30):O}}",
                        "confirmationJwkThumbprint":"{{state.Key.KeyId}}",
                        "scopes":["safe-projection.write","safe-projection.read"]
                      }
                    }
                    """);
            },
            request =>
            {
                var captured = Capture(request);
                requests.Add(captured);
                var batchId = captured.Json.GetProperty("batchId").GetString();
                return Json(
                    HttpStatusCode.OK,
                    $$"""
                    {
                      "batchId":"{{batchId}}",
                      "receipts":[{
                        "operationId":"operation_1",
                        "localRecordId":"record_1",
                        "revision":1,
                        "outcome":"Accepted",
                        "retryable":false,
                        "acknowledgedAt":"{{Now:O}}"
                      }],
                      "checkpoint":{
                        "checkpoint":"checkpoint_1",
                        "lastAcknowledgedOperationId":"operation_1",
                        "updatedAt":"{{Now:O}}"
                      }
                    }
                    """);
            });
        using var httpClient = new HttpClient(handler);
        var client = new CloudHubProtocolClient(httpClient, new FixedTimeProvider(Now));

        var started = await client.BeginEnrollmentAsync(state, Options, CancellationToken.None);
        var approved = await client.PollEnrollmentAsync(started.State, Options, CancellationToken.None);
        var sent = await client.SendProjectionAsync(
            approved.State,
            Options,
            Projection("operation_1", "record_1"),
            CancellationToken.None);

        Assert.True(sent.Accepted);
        Assert.Equal("checkpoint_1", sent.Checkpoint);
        Assert.NotNull(approved.State.Session);
        Assert.Equal("enrollment_1", approved.State.ApprovedEnrollment!.EnrollmentId);
        Assert.Equal(Now, approved.State.ApprovedEnrollment.ApprovedAt);
        Assert.Equal(4, requests.Count);
        Assert.DoesNotContain("\"d\"", requests[1].Body, StringComparison.Ordinal);
        AssertValidProof(
            requests[1].Json.GetProperty("proof").GetString()!,
            state.Key.PublicKey,
            "POST",
            "https://cloud.example/api/v2/hub-enrollments/proof",
            expectedCredential: null);
        Assert.Equal("DPoP", requests[3].AuthorizationScheme);
        Assert.Equal("access_token_1", requests[3].AuthorizationParameter);
        AssertValidProof(
            requests[3].Dpop!,
            state.Key.PublicKey,
            "POST",
            "https://cloud.example/api/v2/hub/projections",
            "access_token_1");
    }

    [Fact]
    public async Task ExpiredAccessCredentialRefreshesBeforeProjectionAndRotatesStoredCredential()
    {
        var initial = CloudHubProtocolClient.CreateLocalState();
        var state = initial with
        {
            Session = new CloudHubSession(
                "installation_1",
                "expired_access",
                Now,
                "refresh_token_1",
                Now.AddDays(1),
                initial.Key.KeyId,
                [CloudHubProtocolClient.ProjectionWriteScope]),
        };
        CapturedRequest? refreshRequest = null;
        var handler = new QueueHandler(
            request =>
            {
                refreshRequest = Capture(request);
                return Json(
                    HttpStatusCode.OK,
                    $$"""
                    {
                      "installationId":"installation_1",
                      "tokenType":"DPoP",
                      "accessToken":"access_token_2",
                      "expiresInSeconds":300,
                      "refreshCredential":"refresh_token_2",
                      "refreshExpiresAt":"{{Now.AddDays(30):O}}",
                      "confirmationJwkThumbprint":"{{initial.Key.KeyId}}",
                      "scopes":["safe-projection.write"]
                    }
                    """);
            },
            request =>
            {
                var captured = Capture(request);
                var batchId = captured.Json.GetProperty("batchId").GetString();
                return Json(
                    HttpStatusCode.OK,
                    $$"""
                    {
                      "batchId":"{{batchId}}",
                      "receipts":[{
                        "operationId":"operation_2",
                        "localRecordId":"record_2",
                        "revision":1,
                        "outcome":"AlreadyApplied",
                        "retryable":false,
                        "acknowledgedAt":"{{Now:O}}"
                      }],
                      "checkpoint":{
                        "checkpoint":"checkpoint_2",
                        "lastAcknowledgedOperationId":"operation_2",
                        "updatedAt":"{{Now:O}}"
                      }
                    }
                    """);
            });
        using var httpClient = new HttpClient(handler);
        var client = new CloudHubProtocolClient(httpClient, new FixedTimeProvider(Now));

        var result = await client.SendProjectionAsync(
            state,
            Options,
            Projection("operation_2", "record_2"),
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("refresh_token_2", result.State.Session!.RefreshCredential);
        Assert.NotNull(refreshRequest);
        Assert.Equal("refresh_token_1", refreshRequest.Json.GetProperty("refreshCredential").GetString());
        AssertValidProof(
            refreshRequest.Json.GetProperty("proof").GetString()!,
            initial.Key.PublicKey,
            "POST",
            "https://cloud.example/api/v2/hub-sessions/refresh",
            "refresh_token_1");
    }

    [Fact]
    public void NonTlsRemoteCloudOriginIsRejected()
    {
        var options = new CloudHubProtocolOptions
        {
            BaseUri = new Uri("http://cloud.example"),
        };

        Assert.Throws<InvalidOperationException>(() => options.Resolve("api/v2/hub/projections"));
    }

    private static SafeProjectionSyncEnvelopeV2Dto Projection(
        string operationId,
        string localRecordId) =>
        new(
            operationId,
            localRecordId,
            1,
            "Upsert",
            null,
            "safe summary",
            [],
            "shared-memory-safe-projection",
            "metadata-only",
            "safe-projection-only",
            Now.AddHours(-2),
            Now.AddHours(-1),
            Now,
            Now.AddDays(1));

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
            request.Method,
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
        HttpMethod Method,
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
