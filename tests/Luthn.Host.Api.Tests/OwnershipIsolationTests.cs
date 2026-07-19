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

public sealed class OwnershipIsolationTests
{
    private const string AliceBearer = "ownership-alice-token";
    private const string BobBearer = "ownership-bob-token";
    private const string OperatorBearer = "ownership-operator-token";
    private const string UnboundBearer = "ownership-unbound-token";

    [Fact]
    public async Task MultiUserModeDerivesOwnerAndIsolatesReadSearchAndIdempotency()
    {
        using var factory = CreateFactory();
        using var alice = Client(factory, AliceBearer);
        using var bob = Client(factory, BobBearer);

        using var aliceMemory = await alice.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Alice release memory",
            safeSummary = "Alice public release memory.",
            coreTags = new[] { "release" },
            visibility = "SharedAcrossAgents",
            provenance = new { userId = "caller-spoof" }
        });
        Assert.Equal(HttpStatusCode.Created, aliceMemory.StatusCode);
        using var aliceMemoryBody = await JsonDocument.ParseAsync(await aliceMemory.Content.ReadAsStreamAsync());
        var memoryId = aliceMemoryBody.RootElement.GetProperty("id").GetString();

        using var bobRead = await bob.GetAsync($"/api/memory/items/{memoryId}");
        using var bobSearch = await bob.PostAsJsonAsync("/api/agent/search", new
        {
            query = "Alice release",
            coreTags = new[] { "release" },
            maxItems = 10
        });
        using var bobSearchBody = await JsonDocument.ParseAsync(await bobSearch.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.NotFound, bobRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bobSearch.StatusCode);
        Assert.Empty(bobSearchBody.RootElement.GetProperty("results").EnumerateArray());

        var turn = new
        {
            sessionId = "shared-session",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Public per-user turn summary.",
            coreTags = new[] { "ownership" },
            idempotencyKey = "same-key"
        };
        using var aliceTurn = await alice.PostAsJsonAsync("/api/agent/turn-summaries", turn);
        using var bobTurn = await bob.PostAsJsonAsync("/api/agent/turn-summaries", turn);
        using var aliceTurnBody = await JsonDocument.ParseAsync(await aliceTurn.Content.ReadAsStreamAsync());
        using var bobTurnBody = await JsonDocument.ParseAsync(await bobTurn.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.Created, aliceTurn.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bobTurn.StatusCode);
        Assert.NotEqual(
            aliceTurnBody.RootElement.GetProperty("summaryId").GetString(),
            bobTurnBody.RootElement.GetProperty("summaryId").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var memory = await db.SharedMemoryItems.SingleAsync(record => record.Id == memoryId);
        var provenance = await db.CollectionProvenance.SingleAsync(record => record.MemoryItemId == memoryId);
        Assert.Equal("alice", memory.OwnerUserId);
        Assert.Equal("alice", provenance.AuthenticatedUserId);
        Assert.Equal("caller-spoof", provenance.ClaimedUserId);
    }

    [Fact]
    public async Task MultiUserModeRejectsUnboundTokenAndCallerOwnerOverride()
    {
        using var factory = CreateFactory(includeUnboundToken: true);
        using var unbound = Client(factory, UnboundBearer);
        using var alice = Client(factory, AliceBearer);

        using var unboundWrite = await unbound.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Unbound",
            safeSummary = "Unbound token must fail.",
            coreTags = new[] { "security" }
        });
        using var spoof = await alice.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Spoof",
            safeSummary = "Caller owner override must fail.",
            coreTags = new[] { "security" },
            ownerUserId = "bob"
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, unboundWrite.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, spoof.StatusCode);
    }

    [Fact]
    public async Task ReadinessReportsConfiguredMultiUserIdentityBoundary()
    {
        using var factory = CreateFactory();
        using var response = await factory.CreateClient().GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Contains(
            body.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "identity" &&
                check.GetProperty("status").GetString() == "ready" &&
                check.GetProperty("detail").GetString()!.Contains("MultiUser", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublicationAndProvenanceAreOwnerScopedWithOperatorOverride()
    {
        using var factory = CreateFactory();
        using var alice = Client(factory, AliceBearer);
        using var bob = Client(factory, BobBearer);
        using var operatorClient = Client(factory, OperatorBearer);

        using var created = await alice.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Publication ownership",
            safeSummary = "Public owner-scoped publication memory.",
            coreTags = new[] { "publication" },
            visibility = "PublicSafe"
        });
        using var body = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync());
        var memoryId = body.RootElement.GetProperty("id").GetString();

        using var bobStatus = await bob.GetAsync($"/api/external-publication/memory-items/{memoryId}");
        using var operatorStatus = await operatorClient.GetAsync($"/api/external-publication/memory-items/{memoryId}");
        using var bobProvenance = await bob.GetAsync($"/api/provenance/memory-items/{memoryId}");
        using var operatorProvenance = await operatorClient.GetAsync($"/api/provenance/memory-items/{memoryId}");
        using var bobAudit = await bob.GetAsync("/api/audit-events");
        using var operatorAudit = await operatorClient.GetAsync("/api/audit-events");

        Assert.Equal(HttpStatusCode.NotFound, bobStatus.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorStatus.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bobProvenance.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorProvenance.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, bobAudit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorAudit.StatusCode);
    }

    [Fact]
    public async Task SensitiveReferencesAndAccessRequestsStayWithinOwnerBoundary()
    {
        using var factory = CreateFactory();
        using var alice = Client(factory, AliceBearer);
        using var bob = Client(factory, BobBearer);
        using var operatorClient = Client(factory, OperatorBearer);

        using var source = await alice.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "Internal recovery note contains a private key.",
            title = "Sensitive recovery note",
            safeSummary = "Recovery procedure metadata.",
            coreTags = new[] { "recovery" }
        });
        Assert.Equal(HttpStatusCode.Created, source.StatusCode);
        using var sourceBody = await JsonDocument.ParseAsync(await source.Content.ReadAsStreamAsync());
        var sensitiveReferenceId = sourceBody.RootElement.GetProperty("sensitiveReferenceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sensitiveReferenceId));

        var requestBody = new
        {
            sensitiveReferenceId,
            reason = "Need a redacted recovery summary.",
            sessionId = "ownership-sensitive-session",
            expiresInSeconds = 600
        };
        using var bobRequest = await bob.PostAsJsonAsync("/api/access-requests", requestBody);
        using var aliceRequest = await alice.PostAsJsonAsync("/api/access-requests", requestBody);
        Assert.Equal(HttpStatusCode.NotFound, bobRequest.StatusCode);
        Assert.Equal(HttpStatusCode.Created, aliceRequest.StatusCode);

        using var aliceRequestBody = await JsonDocument.ParseAsync(await aliceRequest.Content.ReadAsStreamAsync());
        var accessRequestId = aliceRequestBody.RootElement.GetProperty("id").GetString();
        using var operatorList = await operatorClient.GetAsync("/api/access-requests?status=Pending");
        Assert.Equal(HttpStatusCode.OK, operatorList.StatusCode);
        using var operatorListBody = await JsonDocument.ParseAsync(await operatorList.Content.ReadAsStreamAsync());
        Assert.Contains(
            operatorListBody.RootElement.GetProperty("requests").EnumerateArray(),
            item => item.GetProperty("id").GetString() == accessRequestId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal("alice", (await db.SensitiveRecordReferences.SingleAsync()).OwnerUserId);
        Assert.Equal("alice", (await db.SensitiveAccessRequests.SingleAsync()).OwnerUserId);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool includeUnboundToken = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            builder.UseSetting("Luthn:Identity:Mode", "MultiUser");
            ConfigureToken(builder, 0, "alice-token", AliceBearer, "alice", false,
                "memory.write", "memory.read", "agent.read", "agent.write.summary", "source.write",
                "access.request", "external-publication.read", "external-publication.write", "audit.read");
            ConfigureToken(builder, 1, "bob-token", BobBearer, "bob", false,
                "memory.write", "memory.read", "agent.read", "agent.write.summary", "source.write",
                "access.request", "external-publication.read", "external-publication.write", "audit.read");
            ConfigureToken(builder, 2, "operator-token", OperatorBearer, null, true,
                "audit.read", "access.decide", "external-publication.read", "external-publication.write");
            if (includeUnboundToken)
            {
                ConfigureToken(builder, 3, "unbound-token", UnboundBearer, null, false, "memory.write");
            }
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory, string bearer)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private static void ConfigureToken(
        IWebHostBuilder builder,
        int index,
        string name,
        string bearer,
        string? userId,
        bool isOperator,
        params string[] scopes)
    {
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Name", name);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Sha256Digest", Sha256(bearer));
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:UserId", userId);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:IsOperator", isOperator.ToString());
        for (var scopeIndex = 0; scopeIndex < scopes.Length; scopeIndex++)
        {
            builder.UseSetting($"Luthn:Auth:Tokens:{index}:Scopes:{scopeIndex}", scopes[scopeIndex]);
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
