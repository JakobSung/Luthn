using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Luthn.Host.Api.Tests;

public sealed class HostMcpProfileTests
{
    private const string Bearer = ConsoleSessionSecurityTests.OperatorBearer;

    [Fact]
    public async Task BrowserConfirmedActionIsClaimedOnceAndReportsBoundedMcpInventory()
    {
        using var factory = CreateFactory();
        using var helper = factory.CreateClient();
        helper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        using var observation = await helper.PostAsJsonAsync(
            "/api/host/mcp-profiles/observations",
            new HostMcpProfileObservationRequest(
                "1.0.0",
                [
                    new HostMcpClientObservation(
                        "codex",
                        "conflict",
                        [
                            new("luthn", "stdio", true, "unsupported", null),
                            new("luthn-team", "http", true, "oauth", "example.test"),
                        ])
                ]));

        using var browser = factory.CreateClient();
        using var session = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(browser);
        var csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/operator/mcp-profiles/actions")
        {
            Content = JsonContent.Create(new
            {
                agentKind = "codex",
                operation = "activate-remote",
                displayName = "Example Team",
                remoteUrl = "https://example.test/mcp",
            }),
        };
        createRequest.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);
        using var created = await browser.SendAsync(createRequest);
        using var claimed = await helper.PostAsync("/api/host/mcp-profiles/actions/claim", null);
        using var emptyClaim = await helper.PostAsync("/api/host/mcp-profiles/actions/claim", null);
        using var claimedBody = await JsonDocument.ParseAsync(await claimed.Content.ReadAsStreamAsync());
        var actionId = claimedBody.RootElement.GetProperty("action").GetProperty("id").GetString();
        using var completed = await helper.PostAsJsonAsync(
            $"/api/host/mcp-profiles/actions/{actionId}/complete",
            new { outcome = "succeeded" });
        using var snapshot = await browser.GetAsync("/api/operator/mcp-profiles");
        using var snapshotBody = await JsonDocument.ParseAsync(await snapshot.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, observation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonDocument.ParseAsync(await emptyClaim.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("action").ValueKind);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        Assert.True(snapshotBody.RootElement.GetProperty("helperOnline").GetBoolean());
        Assert.Equal("conflict", snapshotBody.RootElement.GetProperty("clients")[0].GetProperty("mode").GetString());
        Assert.Equal("succeeded", snapshotBody.RootElement.GetProperty("action").GetProperty("state").GetString());
        Assert.DoesNotContain("command", snapshotBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", snapshotBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://example.test/mcp")]
    [InlineData("https://user@example.test/mcp")]
    [InlineData("https://example.test/mcp?code=secret")]
    [InlineData("https://example.test/mcp#secret")]
    public async Task RemoteProfileRejectsUnsafeUris(string remoteUrl)
    {
        using var factory = CreateFactory();
        using var browser = factory.CreateClient();
        using var session = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(browser);
        var csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/operator/mcp-profiles/actions")
        {
            Content = JsonContent.Create(new
            {
                agentKind = "codex",
                operation = "activate-remote",
                displayName = "Example Team",
                remoteUrl,
            }),
        };
        request.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);

        using var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LocalRestoreRejectsRemoteConnectionValues()
    {
        using var factory = CreateFactory();
        using var browser = factory.CreateClient();
        using var session = await ConsoleSessionSecurityTests.CreateArmedLocalSessionAsync(browser);
        var csrf = Assert.Single(session.Headers.GetValues(ConsoleAccessOptions.AntiforgeryHeaderName));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/operator/mcp-profiles/actions")
        {
            Content = JsonContent.Create(new
            {
                agentKind = "codex",
                operation = "restore-local",
                displayName = "Local Luthn",
                remoteUrl = "https://example.test/mcp",
            }),
        };
        request.Headers.Add(ConsoleAccessOptions.AntiforgeryHeaderName, csrf);

        using var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting(
                "Luthn:OperatorConfig:Directory",
                Path.Combine(Path.GetTempPath(), "luthn-host-helper-tests", Guid.NewGuid().ToString("N")));
            builder.UseSetting("Luthn:Identity:Mode", "SingleOwner");
            builder.UseSetting("Luthn:Console:LocalOnly", "true");
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Bearer)))
                .ToLowerInvariant();
            builder.UseSetting("Luthn:Auth:Tokens:0:Name", "test-host-helper");
            builder.UseSetting("Luthn:Auth:Tokens:0:Sha256Digest", $"sha256:{digest}");
            builder.UseSetting("Luthn:Auth:Tokens:0:WorkspaceId", "default");
            builder.UseSetting("Luthn:Auth:Tokens:0:ActorKind", "Service");
            builder.UseSetting("Luthn:Auth:Tokens:0:IsOperator", "true");
            builder.UseSetting("Luthn:Auth:Tokens:0:Scopes:0", ServiceScopes.ConfigWrite);
            builder.UseSetting("Luthn:Auth:Tokens:0:Scopes:1", ServiceScopes.AgentConnectionWrite);
        });
}
