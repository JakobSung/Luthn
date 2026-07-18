using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Luthn.Host.Api.Tests;

public sealed class AgentConnectionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ReadBearer = "agent-connection-read-local";
    private const string WriteBearer = "agent-connection-write-local";

    private readonly WebApplicationFactory<Program> _factory;

    public AgentConnectionEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ObservationsReplaceChannelStateWithoutCreatingAnEventLog()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var connected = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(
                Channel("automatic-ingestion", configured: true, verification: "Verified"),
                Channel("mcp", configured: true, verification: "Verified")));
        using var connectedBody = await JsonDocument.ParseAsync(
            await connected.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        Assert.Equal("Verified", connectedBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, connectedBody.RootElement.GetProperty("channels").GetArrayLength());

        using var active = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(
                Channel(
                    "automatic-ingestion",
                    configured: true,
                    verification: "Verified",
                    activity: "Succeeded"),
                Channel("mcp", configured: true, verification: "Verified")));
        using var activeBody = await JsonDocument.ParseAsync(await active.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Equal("Active", activeBody.RootElement.GetProperty("state").GetString());
        Assert.NotEqual(
            JsonValueKind.Null,
            activeBody.RootElement.GetProperty("lastSuccessfulActivityAt").ValueKind);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal(2, await db.AgentConnectionChannels.CountAsync());
        Assert.All(
            await db.AgentConnectionChannels.ToArrayAsync(),
            record => Assert.Equal("luthn", record.ConfigurationOwner));
    }

    [Fact]
    public async Task UnknownConfigurationRefreshPreservesSuccessfulActivityEvidence()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var active = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(
                Channel(
                    "automatic-ingestion",
                    configured: true,
                    verification: "Verified",
                    activity: "Succeeded"),
                Channel(
                    "mcp",
                    configured: true,
                    verification: "Verified",
                    activity: "Succeeded")));
        using var activeBody = await JsonDocument.ParseAsync(await active.Content.ReadAsStreamAsync());
        var lastSuccess = activeBody.RootElement.GetProperty("lastSuccessfulActivityAt").GetDateTimeOffset();

        using var refreshed = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(
                Channel("automatic-ingestion", configured: true),
                Channel("mcp", configured: true, verification: "Verified")));
        using var refreshedBody = await JsonDocument.ParseAsync(
            await refreshed.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal("Active", refreshedBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(
            lastSuccess,
            refreshedBody.RootElement.GetProperty("lastSuccessfulActivityAt").GetDateTimeOffset());
        var channels = refreshedBody.RootElement.GetProperty("channels").EnumerateArray().ToArray();
        Assert.All(channels, channel => Assert.Equal("Active", channel.GetProperty("state").GetString()));
        Assert.All(
            channels,
            channel => Assert.Equal("Succeeded", channel.GetProperty("activityState").GetString()));
    }

    [Fact]
    public async Task FailedAndDisconnectedObservationsUseBoundedReplacementState()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var failed = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(
                Channel(
                    "automatic-ingestion",
                    configured: true,
                    verification: "Verified",
                    activity: "Failed",
                    failureCode: "delivery.timeout"),
                Channel("mcp", configured: true, verification: "Verified")));
        using var failedBody = await JsonDocument.ParseAsync(await failed.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal("Degraded", failedBody.RootElement.GetProperty("state").GetString());
        Assert.Contains("delivery.timeout", failedBody.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("token", failedBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", failedBody.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var disconnected = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(
                Channel("automatic-ingestion", configured: false),
                Channel("mcp", configured: false)));
        using var disconnectedBody = await JsonDocument.ParseAsync(
            await disconnected.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode);
        Assert.Equal("Disconnected", disconnectedBody.RootElement.GetProperty("state").GetString());

        using var list = await client.GetAsync("/api/agent-connections");
        using var listBody = await JsonDocument.ParseAsync(await list.Content.ReadAsStreamAsync());
        var connection = Assert.Single(listBody.RootElement.GetProperty("connections").EnumerateArray());
        Assert.Equal("Disconnected", connection.GetProperty("state").GetString());
        Assert.Equal(2, connection.GetProperty("channels").GetArrayLength());
    }

    [Theory]
    [InlineData("Verified", "Unknown")]
    [InlineData("Unknown", "Succeeded")]
    public async Task DisconnectedAndHealthyChannelsResolveToDegraded(
        string verification,
        string activity)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(
                Channel("automatic-ingestion", configured: false),
                Channel("mcp", configured: true, verification, activity)));
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", body.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ObservationRejectsUnboundedFailureDetailAndUnknownFields()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var invalidFailure = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(Channel(
                "automatic-ingestion",
                configured: true,
                verification: "Failed",
                failureCode: "raw error with local /Users/example/path")));
        using var unknownField = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            new
            {
                agentName = "Codex",
                integrationKind = "host-hook-mcp",
                connectorVersion = "1",
                serviceToken = "must-not-be-accepted",
                channels = new[] { Channel("mcp", configured: true) }
            });
        using var inconsistentDisconnected = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(Channel("mcp", configured: false, verification: "Verified")));

        Assert.Equal(HttpStatusCode.BadRequest, invalidFailure.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, inconsistentDisconnected.StatusCode);
        Assert.DoesNotContain(
            "must-not-be-accepted",
            await unknownField.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObservationRejectsNullAndUndefinedChannelStatesWithoutPersistence()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var nullChannel = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            new
            {
                agentName = "Codex",
                integrationKind = "host-hook-mcp",
                connectorVersion = "1",
                channels = new object?[] { null }
            });
        using var undefinedVerification = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(new
            {
                channel = "mcp",
                configured = true,
                verificationState = 99,
                activityState = "Unknown",
                failureCode = (string?)null
            }));
        using var undefinedActivity = await client.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(new
            {
                channel = "mcp",
                configured = true,
                verificationState = "Unknown",
                activityState = 99,
                failureCode = (string?)null
            }));

        Assert.Equal(HttpStatusCode.BadRequest, nullChannel.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, undefinedVerification.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, undefinedActivity.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.AgentConnectionChannels.ToArrayAsync());
    }

    [Theory]
    [InlineData("agentName", "Bearer abcdefghijklmnop")]
    [InlineData("agentId", "sk-abcdefghijklmnop")]
    [InlineData("integrationKind", "aaaaaaaa.bbbbbbbb.cccccccc")]
    [InlineData("connectorVersion", "API_KEY=abcdefghijklmnop")]
    [InlineData("failureCode", "sk-abcdefghijklmnop")]
    [InlineData("agentName", "https://operator:secret@example.test/path")]
    [InlineData("agentName", "-----BEGIN PRIVATE KEY-----secret-----END PRIVATE KEY-----")]
    public async Task ObservationRejectsCredentialPatternsWithoutPersistence(
        string field,
        string credential)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var agentId = field == "agentId" ? credential : "codex";
        var payload = new Dictionary<string, object?>
        {
            ["agentName"] = field == "agentName" ? credential : "Codex",
            ["integrationKind"] = field == "integrationKind" ? credential : "host-hook-mcp",
            ["connectorVersion"] = field == "connectorVersion" ? credential : "1",
            ["channels"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["channel"] = "mcp",
                    ["configured"] = true,
                    ["verificationState"] = field == "failureCode" ? "Failed" : "Verified",
                    ["activityState"] = "Unknown",
                    ["failureCode"] = field == "failureCode" ? credential : null
                }
            }
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/agent-connections/{Uri.EscapeDataString(agentId)}/observations",
            payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.AgentConnectionChannels.ToArrayAsync());
    }

    [Fact]
    public async Task ReadAndWriteScopesAreIndependent()
    {
        using var factory = CreateAuthFactory();
        using var anonymous = factory.CreateClient();
        using var readClient = factory.CreateClient();
        using var writeClient = factory.CreateClient();
        readClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReadBearer);
        writeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WriteBearer);

        using var anonymousRead = await anonymous.GetAsync("/api/agent-connections");
        using var forbiddenWrite = await readClient.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(Channel("mcp", configured: true)));
        using var acceptedWrite = await writeClient.PostAsJsonAsync(
            "/api/agent-connections/codex/observations",
            Observation(Channel("mcp", configured: true, verification: "Verified")));
        using var forbiddenRead = await writeClient.GetAsync("/api/agent-connections");
        using var acceptedRead = await readClient.GetAsync("/api/agent-connections");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenWrite.StatusCode);
        Assert.Equal(HttpStatusCode.OK, acceptedWrite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, acceptedRead.StatusCode);
    }

    private static object Observation(params object[] channels) => new
    {
        agentName = "Codex",
        integrationKind = "host-hook-mcp",
        connectorVersion = "1",
        channels
    };

    private static object Channel(
        string channel,
        bool configured,
        string verification = "Unknown",
        string activity = "Unknown",
        string? failureCode = null) => new
        {
            channel,
            configured,
            verificationState = verification,
            activityState = activity,
            failureCode
        };

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
        });

    private WebApplicationFactory<Program> CreateAuthFactory() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConfigureToken(builder, 0, "connection-reader", ReadBearer, "agent.connection.read");
            ConfigureToken(builder, 1, "connection-writer", WriteBearer, "agent.connection.write");
        });

    private static void ConfigureToken(
        IWebHostBuilder builder,
        int index,
        string name,
        string bearer,
        string scope)
    {
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Name", name);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Sha256Digest", Sha256Digest(bearer));
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Scopes:0", scope);
    }

    private static string Sha256Digest(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
