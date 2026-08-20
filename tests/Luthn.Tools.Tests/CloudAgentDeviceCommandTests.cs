using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Tools;

namespace Luthn.Tools.Tests;

public sealed class CloudAgentDeviceCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"luthn-cloud-agent-command-{Guid.NewGuid():N}");

    [Fact]
    public async Task EnrollmentPersistsProtectedStateAndThenCreatesConnection()
    {
        var deviceId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        string? keyId = null;
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
            request =>
            {
                using var proof = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
                keyId = proof.RootElement.GetProperty("keyId").GetString();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            },
            _ => Json(
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
                    "refreshExpiresAt":"{{Now.AddDays(30):O}}",
                    "confirmationJwkThumbprint":"{{keyId}}",
                    "scopes":["agent-connection.write","relay.write","agent-device.rotate"]
                  }
                }
                """),
            request =>
            {
                Assert.Equal("DPoP", request.Headers.Authorization?.Scheme);
                Assert.Equal("access_token_1", request.Headers.Authorization?.Parameter);
                Assert.True(request.Headers.Contains("DPoP"));
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
        var command = new CloudAgentDeviceCommand(httpClient, new FixedTimeProvider(Now));
        var arguments = Arguments(workspaceId);

        var firstOutput = new StringWriter();
        var firstExitCode = await command.ExecuteAsync(arguments, firstOutput, TextWriter.Null);
        using var firstResult = JsonDocument.Parse(firstOutput.ToString());

        Assert.Equal(0, firstExitCode);
        Assert.Equal("approval-required", firstResult.RootElement.GetProperty("state").GetString());
        Assert.Equal("https://cloud.example/connect/device", firstResult.RootElement.GetProperty("verificationUri").GetString());
        Assert.Equal("ABCD-EFGH", firstResult.RootElement.GetProperty("userCode").GetString());

        var stateFile = await File.ReadAllTextAsync(
            Path.Combine(_directory, "cloud-agent-device-state.json"));
        Assert.DoesNotContain("ABCD-EFGH", stateFile, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", stateFile, StringComparison.OrdinalIgnoreCase);

        var secondOutput = new StringWriter();
        var secondExitCode = await command.ExecuteAsync(arguments, secondOutput, TextWriter.Null);
        using var secondResult = JsonDocument.Parse(secondOutput.ToString());

        Assert.Equal(0, secondExitCode);
        Assert.Equal("connected", secondResult.RootElement.GetProperty("state").GetString());
        Assert.Equal(connectionId, secondResult.RootElement.GetProperty("agentConnectionId").GetGuid());
        Assert.Equal("https://cloud.example/mcp", secondResult.RootElement.GetProperty("remoteMcpUrl").GetString());
        Assert.DoesNotContain("access_token_1", secondOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("refresh_token_1", secondOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, handler.RemainingResponseCount);
    }

    [Fact]
    public async Task PlainHttpNonLoopbackOriginIsRejectedWithoutNetworkCall()
    {
        var handler = new QueueHandler();
        using var httpClient = new HttpClient(handler);
        var command = new CloudAgentDeviceCommand(httpClient, new FixedTimeProvider(Now));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await command.ExecuteAsync(
            Arguments(Guid.NewGuid(), "http://cloud.example"),
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Contains("invalid", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RemainingResponseCount);
    }

    private string[] Arguments(Guid workspaceId, string baseUrl = "https://cloud.example")
    {
        var keyFile = Path.Combine(_directory, "state-key");
        Directory.CreateDirectory(_directory);
        if (!File.Exists(keyFile))
        {
            File.WriteAllText(keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }
        return
        [
            "--base-url", baseUrl,
            "--state-dir", _directory,
            "--state-key-file", keyFile,
            "--workspace", workspaceId.ToString("D"),
            "--agent", "codex",
            "--device-name", "Member MacBook",
        ];
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static HttpResponseMessage Json(
        HttpStatusCode statusCode,
        string json,
        params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

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

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
