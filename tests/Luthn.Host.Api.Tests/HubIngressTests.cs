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

public sealed class HubIngressTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AliceBearer = "hub-alice-token";
    private const string BobBearer = "hub-bob-token";
    private readonly WebApplicationFactory<Program> _factory;

    public HubIngressTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task HubIngressIdentityUsesOnlyServerConfiguredScope()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory, AliceBearer);
        const string capsule = "Release decision for the team.";

        using var spoofed = await client.PostAsJsonAsync("/api/hub/ingress/capsules", new
        {
            idempotencyKey = "event-spoofed",
            contentDigest = Digest(capsule),
            capsule,
            workspaceId = "workspace-bob",
            memberId = "bob",
            agentId = "other-agent",
            sessionId = "other-session"
        });
        using var accepted = await PostAsync(client, "event-identity", capsule);

        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var scope = factory.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<LuthnDbContext>()
            .HubIngressQueue.SingleAsync();
        Assert.Equal("organization-1", record.OrganizationId);
        Assert.Equal("workspace-alice", record.WorkspaceId);
        Assert.Equal("alice", record.MemberUserId);
        Assert.Equal("connection-alice", record.AgentConnectionId);
        Assert.Equal("codex", record.AgentId);
        Assert.Equal("session-alice", record.SessionId);
        Assert.DoesNotContain("bob", record.TurnId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IngressIdempotencyReturnsExistingReceiptAndRejectsDigestConflict()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory, AliceBearer);
        using var first = await PostAsync(client, "event-idempotent", "same capsule");
        using var duplicate = await PostAsync(client, "event-idempotent", "same capsule");
        using var conflict = await PostAsync(client, "event-idempotent", "different capsule");
        using var firstBody = await JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync());
        using var duplicateBody = await JsonDocument.ParseAsync(await duplicate.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            firstBody.RootElement.GetProperty("receiptId").GetString(),
            duplicateBody.RootElement.GetProperty("receiptId").GetString());
        Assert.True(duplicateBody.RootElement.GetProperty("duplicate").GetBoolean());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<LuthnDbContext>()
            .HubIngressQueue.CountAsync());
    }

    [Fact]
    public async Task HubIngressBackpressureIsScopedAndReturnsBoundedRetryMetadata()
    {
        using var factory = CreateFactory(agentPendingLimit: 1);
        using var alice = CreateClient(factory, AliceBearer);
        using var bob = CreateClient(factory, BobBearer);

        using var first = await PostAsync(alice, "alice-1", "alice first");
        using var saturated = await PostAsync(alice, "alice-2", "alice second");
        using var isolated = await PostAsync(bob, "bob-1", "bob first");
        using var body = await JsonDocument.ParseAsync(await saturated.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, saturated.StatusCode);
        Assert.Equal("5", saturated.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0") ??
            saturated.Headers.GetValues("Retry-After").Single());
        Assert.Equal("hub.ingress.agent_capacity", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(5, body.RootElement.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(HttpStatusCode.Accepted, isolated.StatusCode);
    }

    [Fact]
    public async Task HubIngressSecurityPersistsNoPlaintextAndKeepsReceiptAndAuditContentFree()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory, AliceBearer);
        const string capsule = "credential=secret prompt transcript /Users/alice/private";

        using var response = await PostAsync(client, "event-sensitive", capsule);
        var responseText = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.DoesNotContain(capsule, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace-alice", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", responseText, StringComparison.OrdinalIgnoreCase);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var record = await db.HubIngressQueue.SingleAsync();
        var audit = await db.AuditEvents.SingleAsync(item => item.Action == "hub.ingress.accepted");
        Assert.DoesNotContain(capsule, record.ProtectedCapsule, StringComparison.Ordinal);
        Assert.Equal("metadata-only", audit.PayloadClass);
        Assert.Equal("protected-capsule-only", audit.RedactionState);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(audit), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HubIngressIsZeroOutboundAndDisabledByDefault()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
        });
        using var client = factory.CreateClient();

        using var response = await PostAsync(client, "disabled-1", "disabled capsule");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<LuthnDbContext>()
            .HubIngressQueue.ToArrayAsync());
    }

    private WebApplicationFactory<Program> CreateFactory(int agentPendingLimit = 250) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Identity:Mode", "MultiUser");
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            builder.UseSetting("Luthn:Hub:Ingress:Enabled", "true");
            builder.UseSetting("Luthn:Hub:Ingress:AgentPendingLimit", agentPendingLimit.ToString());
            ConfigureToken(builder, 0, AliceBearer, "alice", "workspace-alice", "connection-alice", "session-alice");
            ConfigureToken(builder, 1, BobBearer, "bob", "workspace-bob", "connection-bob", "session-bob");
        });

    private static void ConfigureToken(
        IWebHostBuilder builder,
        int index,
        string bearer,
        string userId,
        string workspaceId,
        string connectionId,
        string sessionId)
    {
        var prefix = $"Luthn:Auth:Tokens:{index}";
        builder.UseSetting($"{prefix}:Name", $"hub-{userId}");
        builder.UseSetting($"{prefix}:Sha256Digest", Digest(bearer));
        builder.UseSetting($"{prefix}:Scopes:0", ServiceScopes.HubIngressWrite);
        builder.UseSetting($"{prefix}:UserId", userId);
        builder.UseSetting($"{prefix}:WorkspaceId", workspaceId);
        builder.UseSetting($"{prefix}:ActorKind", "Agent");
        builder.UseSetting($"{prefix}:HubOrganizationId", "organization-1");
        builder.UseSetting($"{prefix}:HubAgentConnectionId", connectionId);
        builder.UseSetting($"{prefix}:HubAgentId", "codex");
        builder.UseSetting($"{prefix}:HubSessionId", sessionId);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, string bearer)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string idempotencyKey, string capsule) =>
        client.PostAsJsonAsync("/api/hub/ingress/capsules", new
        {
            idempotencyKey,
            contentDigest = Digest(capsule),
            capsule
        });

    private static string Digest(string value) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";
}
