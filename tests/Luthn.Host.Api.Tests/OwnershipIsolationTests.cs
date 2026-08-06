using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Core.Common;
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
    private const string BobDeciderBearer = "ownership-bob-decider-token";
    private const string OperatorBearer = "ownership-operator-token";
    private const string UnboundBearer = "ownership-unbound-token";

    [Fact]
    public void DerivedPersonalWorkspaceAcceptsEmailStyleUserIds()
    {
        var userId = ServiceTokenAuthorization.NormalizeUserId("Alice@Example.com");

        Assert.Equal("alice@example.com", userId);
        var workspaceId = WorkspaceIds.ForLegacyUser(userId!);
        Assert.Equal("personal:alice@example.com", workspaceId);
        Assert.Equal(workspaceId, ServiceTokenAuthorization.NormalizeWorkspaceId(workspaceId));
    }

    [Fact]
    public async Task MultiUserModeDerivesPersonalWorkspaceAndIsolatesReadSearchAndIdempotency()
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
        Assert.Equal("personal:alice", memory.WorkspaceId);
        Assert.Equal("alice", provenance.AuthenticatedUserId);
        Assert.Equal("personal:alice", provenance.WorkspaceId);
        Assert.Equal("caller-spoof", provenance.ClaimedUserId);
    }

    [Fact]
    public async Task TurnSummaryIdempotencyKeyCannotCollideAcrossWorkspaceBoundaries()
    {
        using var factory = CreateFactory(
            aliceWorkspaceId: "personal:alice",
            bobWorkspaceId: "personal");
        using var alice = Client(factory, AliceBearer);
        using var bob = Client(factory, BobBearer);

        var common = new
        {
            sessionId = "ambiguous-workspace-session",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Public workspace collision regression summary.",
            coreTags = new[] { "ownership" }
        };
        using var aliceResponse = await alice.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            common.sessionId,
            common.turnId,
            common.sourceAgent,
            common.summary,
            common.coreTags,
            idempotencyKey = "bob"
        });
        using var bobResponse = await bob.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            common.sessionId,
            common.turnId,
            common.sourceAgent,
            common.summary,
            common.coreTags,
            idempotencyKey = "alice:bob"
        });
        using var aliceBody = await JsonDocument.ParseAsync(await aliceResponse.Content.ReadAsStreamAsync());
        using var bobBody = await JsonDocument.ParseAsync(await bobResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, aliceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bobResponse.StatusCode);
        Assert.NotEqual(
            aliceBody.RootElement.GetProperty("summaryId").GetString(),
            bobBody.RootElement.GetProperty("summaryId").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal(2, await db.SourceEvents.CountAsync());
        Assert.Equal(2, await db.ClassificationResults.CountAsync());
        Assert.Equal(2, await db.SharedMemoryItems.CountAsync());
    }

    [Fact]
    public async Task TokensBoundToTheSameWorkspaceShareAgentSafeMemory()
    {
        using var factory = CreateFactory(sharedWorkspace: true);
        using var alice = Client(factory, AliceBearer);
        using var bob = Client(factory, BobBearer);
        using var operatorClient = Client(factory, OperatorBearer);

        using var created = await alice.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Release memory",
            safeSummary = "Public-safe deployment memory.",
            coreTags = new[] { "release" },
            visibility = "SharedAcrossAgents"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync());
        var memoryId = createdBody.RootElement.GetProperty("id").GetString();

        using (var createdScope = factory.Services.CreateScope())
        {
            var createdDb = createdScope.ServiceProvider.GetRequiredService<LuthnDbContext>();
            var createdRecord = await createdDb.SharedMemoryItems.SingleAsync();
            Assert.Equal("team-alpha", createdRecord.WorkspaceId);
            Assert.True(createdRecord.AllowsAgentContext);
            Assert.Equal(Luthn.Core.Memory.MemoryVisibility.SharedAcrossAgents, createdRecord.Visibility);
        }

        using var bobRead = await bob.GetAsync($"/api/memory/items/{memoryId}");
        using var bobProvenance = await bob.GetAsync($"/api/provenance/memory-items/{memoryId}");
        using var operatorProvenance = await operatorClient.GetAsync($"/api/provenance/memory-items/{memoryId}");
        using var bobSearch = await bob.PostAsJsonAsync("/api/agent/search", new
        {
            query = "public-safe deployment memory",
            coreTags = new[] { "release" },
            maxItems = 10
        });
        using var bobSearchBody = await JsonDocument.ParseAsync(await bobSearch.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, bobRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bobProvenance.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorProvenance.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bobSearch.StatusCode);
        Assert.Contains(
            bobSearchBody.RootElement.GetProperty("results").EnumerateArray(),
            result => result.GetProperty("id").GetString() == memoryId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal("team-alpha", (await db.SharedMemoryItems.SingleAsync()).WorkspaceId);
    }

    [Fact]
    public async Task SharedWorkspaceKeepsSensitiveAccessRequestsOwnerScoped()
    {
        using var factory = CreateFactory(sharedWorkspace: true);
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
            sessionId = "shared-workspace-sensitive-session",
            expiresInSeconds = 600
        };
        using var bobCreate = await bob.PostAsJsonAsync("/api/access-requests", requestBody);
        using var aliceCreate = await alice.PostAsJsonAsync("/api/access-requests", requestBody);

        Assert.Equal(HttpStatusCode.NotFound, bobCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Created, aliceCreate.StatusCode);
        using var aliceCreateBody = await JsonDocument.ParseAsync(await aliceCreate.Content.ReadAsStreamAsync());
        var accessRequestId = aliceCreateBody.RootElement.GetProperty("id").GetString();

        using var approved = await operatorClient.PostAsJsonAsync(
            $"/api/access-requests/{accessRequestId}/approve",
            new
            {
                reason = "Approved bounded output.",
                redactedSummary = "Recovery procedure review completed."
            });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var bobRead = await bob.GetAsync($"/api/access-requests/{accessRequestId}");
        using var bobResult = await bob.GetAsync($"/api/access-requests/{accessRequestId}/result");
        using var aliceRead = await alice.GetAsync($"/api/access-requests/{accessRequestId}");
        using var aliceResult = await alice.GetAsync($"/api/access-requests/{accessRequestId}/result");

        Assert.Equal(HttpStatusCode.NotFound, bobRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bobResult.StatusCode);
        Assert.Equal(HttpStatusCode.OK, aliceRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, aliceResult.StatusCode);
    }

    [Fact]
    public async Task MultiUserModeRejectsUnboundTokenAndCallerScopeOverrides()
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
        using var workspaceSpoof = await alice.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Workspace spoof",
            safeSummary = "Caller workspace override must fail.",
            coreTags = new[] { "security" },
            workspaceId = "personal:bob"
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, unboundWrite.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, spoof.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, workspaceSpoof.StatusCode);
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
    public async Task PublicationAndProvenanceAreWorkspaceScopedForOperatorBinding()
    {
        using var factory = CreateFactory();
        using var alice = Client(factory, AliceBearer);
        using var bob = Client(factory, BobBearer);
        using var operatorClient = Client(factory, OperatorBearer);

        using var created = await alice.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Publication workspace",
            safeSummary = "Public workspace-scoped publication memory.",
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
    public async Task SensitiveReferencesAndAccessRequestsStayWithinWorkspaceBoundary()
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

    [Fact]
    public async Task SensitiveOperatorDetailStaysWithinOwnerAndWorkspaceBoundaries()
    {
        using (var sharedFactory = CreateFactory(sharedWorkspace: true))
        {
            using var alice = Client(sharedFactory, AliceBearer);
            using var bobDecider = Client(sharedFactory, BobDeciderBearer);
            using var operatorClient = Client(sharedFactory, OperatorBearer);

            using var source = await alice.PostAsJsonAsync("/api/sources", new
            {
                sourceSystem = "local",
                sourceType = "note",
                content = "Internal recovery note contains a private key.",
                title = "Sensitive recovery note",
                safeSummary = "Recovery procedure metadata.",
                coreTags = new[] { "recovery" }
            });
            using var sourceBody = await JsonDocument.ParseAsync(await source.Content.ReadAsStreamAsync());
            var sensitiveReferenceId = sourceBody.RootElement.GetProperty("sensitiveReferenceId").GetString();
            using var request = await alice.PostAsJsonAsync("/api/access-requests", new
            {
                sensitiveReferenceId,
                reason = "Need a redacted recovery summary.",
                sessionId = "shared-owner-boundary-session",
                expiresInSeconds = 600
            });
            using var requestBody = await JsonDocument.ParseAsync(await request.Content.ReadAsStreamAsync());
            var requestId = requestBody.RootElement.GetProperty("id").GetString();

            using var otherOwnerDetail = await bobDecider.GetAsync($"/api/access-requests/{requestId}/operator-detail");
            using var operatorDetail = await operatorClient.GetAsync($"/api/access-requests/{requestId}/operator-detail");

            Assert.Equal(HttpStatusCode.Created, source.StatusCode);
            Assert.Equal(HttpStatusCode.Created, request.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, otherOwnerDetail.StatusCode);
            Assert.Equal(HttpStatusCode.OK, operatorDetail.StatusCode);
        }

        using (var isolatedFactory = CreateFactory())
        {
            using var bob = Client(isolatedFactory, BobBearer);
            using var operatorClient = Client(isolatedFactory, OperatorBearer);

            using var source = await bob.PostAsJsonAsync("/api/sources", new
            {
                sourceSystem = "local",
                sourceType = "note",
                content = "Internal finance note contains a private key.",
                title = "Sensitive finance note",
                safeSummary = "Finance procedure metadata.",
                coreTags = new[] { "finance" }
            });
            using var sourceBody = await JsonDocument.ParseAsync(await source.Content.ReadAsStreamAsync());
            var sensitiveReferenceId = sourceBody.RootElement.GetProperty("sensitiveReferenceId").GetString();
            using var request = await bob.PostAsJsonAsync("/api/access-requests", new
            {
                sensitiveReferenceId,
                reason = "Need a redacted finance summary.",
                sessionId = "workspace-boundary-session",
                expiresInSeconds = 600
            });
            using var requestBody = await JsonDocument.ParseAsync(await request.Content.ReadAsStreamAsync());
            var requestId = requestBody.RootElement.GetProperty("id").GetString();

            using var crossWorkspaceDetail = await operatorClient.GetAsync($"/api/access-requests/{requestId}/operator-detail");

            Assert.Equal(HttpStatusCode.Created, source.StatusCode);
            Assert.Equal(HttpStatusCode.Created, request.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, crossWorkspaceDetail.StatusCode);
        }
    }

    [Fact]
    public async Task AgentMutationRequestsAreRejectedWithoutChangingMemoryOrSensitiveState()
    {
        using var factory = CreateFactory();
        using var alice = Client(factory, AliceBearer);

        using var memoryResponse = await alice.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Agent mutation target",
            safeSummary = "This memory must remain unchanged.",
            coreTags = new[] { "mutation-boundary" },
            visibility = "SharedAcrossAgents"
        });
        Assert.Equal(HttpStatusCode.Created, memoryResponse.StatusCode);
        using var memoryBody = await JsonDocument.ParseAsync(await memoryResponse.Content.ReadAsStreamAsync());
        var memoryId = memoryBody.RootElement.GetProperty("id").GetString();

        using var sourceResponse = await alice.PostAsJsonAsync("/api/sources", new
        {
            sourceSystem = "local",
            sourceType = "note",
            content = "Internal recovery note contains a private key.",
            title = "Sensitive mutation target",
            safeSummary = "Only the redacted reference is agent-visible.",
            coreTags = new[] { "mutation-boundary" }
        });
        Assert.Equal(HttpStatusCode.Created, sourceResponse.StatusCode);
        using var sourceBody = await JsonDocument.ParseAsync(await sourceResponse.Content.ReadAsStreamAsync());
        var sensitiveReferenceId = sourceBody.RootElement.GetProperty("sensitiveReferenceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sensitiveReferenceId));

        using var accessResponse = await alice.PostAsJsonAsync("/api/access-requests", new
        {
            sensitiveReferenceId,
            reason = "Need the redacted summary for a safe task.",
            sessionId = "agent-mutation-boundary-session",
            expiresInSeconds = 600
        });
        Assert.Equal(HttpStatusCode.Created, accessResponse.StatusCode);
        using var accessBody = await JsonDocument.ParseAsync(await accessResponse.Content.ReadAsStreamAsync());
        var accessRequestId = accessBody.RootElement.GetProperty("id").GetString();

        using var beforeScope = factory.Services.CreateScope();
        var beforeDb = beforeScope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var beforeMemory = await beforeDb.SharedMemoryItems.SingleAsync(item => item.Id == memoryId);
        var beforeAccess = await beforeDb.SensitiveAccessRequests.SingleAsync(item => item.Id == accessRequestId);
        var before = new
        {
            SourceEvents = await beforeDb.SourceEvents.CountAsync(),
            MemoryItems = await beforeDb.SharedMemoryItems.CountAsync(),
            SensitiveReferences = await beforeDb.SensitiveRecordReferences.CountAsync(),
            AccessRequests = await beforeDb.SensitiveAccessRequests.CountAsync(),
            AccessDecisions = await beforeDb.SensitiveAccessDecisions.CountAsync(),
            AuditEvents = await beforeDb.AuditEvents.CountAsync(),
            MemoryTitle = beforeMemory.Title,
            MemorySummary = beforeMemory.SafeSummary,
            AccessStatus = beforeAccess.Status,
            AccessDecidedBy = beforeAccess.DecidedBy,
            AccessDecidedAt = beforeAccess.DecidedAt
        };

        foreach (var method in new[] { HttpMethod.Put, new HttpMethod("PATCH"), HttpMethod.Delete })
        {
            await AssertRejectedAsync(
                alice,
                method,
                $"/api/memory/items/{memoryId}",
                new { title = "Unauthorized mutation", safeSummary = "Must not be applied." });
            await AssertRejectedAsync(
                alice,
                method,
                "/api/sources",
                new { content = "Unauthorized source mutation." });
            await AssertRejectedAsync(
                alice,
                method,
                "/api/agent/turn-summaries",
                new { summary = "Unauthorized turn mutation." });
        }

        foreach (var decision in new[] { "approve", "deny" })
        {
            using var response = await alice.PostAsJsonAsync(
                $"/api/access-requests/{accessRequestId}/{decision}",
                new { reason = "Agent mutation probe." });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var afterScope = factory.Services.CreateScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var afterMemory = await afterDb.SharedMemoryItems.SingleAsync(item => item.Id == memoryId);
        var afterAccess = await afterDb.SensitiveAccessRequests.SingleAsync(item => item.Id == accessRequestId);
        Assert.Equal(before.SourceEvents, await afterDb.SourceEvents.CountAsync());
        Assert.Equal(before.MemoryItems, await afterDb.SharedMemoryItems.CountAsync());
        Assert.Equal(before.SensitiveReferences, await afterDb.SensitiveRecordReferences.CountAsync());
        Assert.Equal(before.AccessRequests, await afterDb.SensitiveAccessRequests.CountAsync());
        Assert.Equal(before.AccessDecisions, await afterDb.SensitiveAccessDecisions.CountAsync());
        Assert.Equal(before.AuditEvents, await afterDb.AuditEvents.CountAsync());
        Assert.Equal(before.MemoryTitle, afterMemory.Title);
        Assert.Equal(before.MemorySummary, afterMemory.SafeSummary);
        Assert.Equal(before.AccessStatus, afterAccess.Status);
        Assert.Equal(before.AccessDecidedBy, afterAccess.DecidedBy);
        Assert.Equal(before.AccessDecidedAt, afterAccess.DecidedAt);
    }

    private static async Task AssertRejectedAsync(
        HttpClient client,
        HttpMethod method,
        string uri,
        object payload)
    {
        using var request = new HttpRequestMessage(method, uri)
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await client.SendAsync(request);
        Assert.False(
            response.IsSuccessStatusCode,
            $"{method} {uri} unexpectedly succeeded with {(int)response.StatusCode}.");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        bool includeUnboundToken = false,
        bool sharedWorkspace = false,
        string? aliceWorkspaceId = null,
        string? bobWorkspaceId = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            builder.UseSetting("Luthn:Identity:Mode", "MultiUser");
            ConfigureToken(builder, 0, "alice-token", AliceBearer, "alice", false,
                sharedWorkspace ? "team-alpha" : aliceWorkspaceId,
                "memory.write", "memory.read", "agent.read", "agent.write.summary", "source.write",
                "access.request", "external-publication.read", "external-publication.write", "audit.read");
            ConfigureToken(builder, 1, "bob-token", BobBearer, "bob", false,
                sharedWorkspace ? "team-alpha" : bobWorkspaceId,
                "memory.write", "memory.read", "agent.read", "agent.write.summary", "source.write",
                "access.request", "external-publication.read", "external-publication.write", "audit.read");
            ConfigureToken(builder, 2, "operator-token", OperatorBearer, null, true,
                sharedWorkspace ? "team-alpha" : "personal:alice",
                "audit.read", "access.decide", "external-publication.read", "external-publication.write");
            ConfigureToken(builder, 3, "bob-decider-token", BobDeciderBearer, "bob", false,
                sharedWorkspace ? "team-alpha" : bobWorkspaceId,
                "access.decide");
            if (includeUnboundToken)
            {
                ConfigureToken(builder, 4, "unbound-token", UnboundBearer, null, false, null, "memory.write");
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
        string? workspaceId,
        params string[] scopes)
    {
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Name", name);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Sha256Digest", Sha256(bearer));
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:UserId", userId);
        if (workspaceId is not null)
        {
            builder.UseSetting($"Luthn:Auth:Tokens:{index}:WorkspaceId", workspaceId);
        }
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:IsOperator", isOperator.ToString());
        for (var scopeIndex = 0; scopeIndex < scopes.Length; scopeIndex++)
        {
            builder.UseSetting($"Luthn:Auth:Tokens:{index}:Scopes:{scopeIndex}", scopes[scopeIndex]);
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
