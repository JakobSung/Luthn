using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Luthn.Core.Classification;
using Luthn.Core.Memory;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Luthn.Host.Api.Tests;

public sealed class TurnSummaryEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TurnSummaryEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SafeTurnSummaryCreatesAgentVisibleSharedMemory()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-safe-1",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Published release note for external contributors.",
            coreTags = new[] { "release", "codex" },
            title = "Codex release note",
            idempotencyKey = "summary-safe-1"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.False(body.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.True(body.RootElement.GetProperty("allowsAgentContext").GetBoolean());
        Assert.Equal("Public", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());

        using var contextResponse = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            query = "release note",
            coreTags = new[] { "release" },
            maxItems = 10
        });
        using var contextBody = await JsonDocument.ParseAsync(await contextResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        var item = Assert.Single(contextBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.StartsWith("memory-turn-summary-", item.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("Codex release note", item.GetProperty("title").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var source = await db.SourceEvents.SingleAsync();
        var classification = await db.ClassificationResults.SingleAsync();
        var memory = await db.SharedMemoryItems.SingleAsync();

        Assert.Equal("turn-summary", source.SourceType);
        Assert.False(source.ContainsSensitiveMaterial);
        Assert.Equal(StorageDecisionKind.WikiCandidate, classification.StorageDecision);
        Assert.Equal(SensitivityLevel.Public, memory.Sensitivity);
        Assert.Equal(MemoryVisibility.SharedAcrossAgents, memory.Visibility);
        Assert.True(memory.AllowsAgentContext);
        Assert.Equal("session-safe-1", memory.SourceSessionId);
    }

    [Fact]
    public async Task SensitiveTurnSummaryStaysBehindMemoryBoundary()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/turn-summaries", new
        {
            sessionId = "session-sensitive-1",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Customer credential and private key rotation detail.",
            coreTags = new[] { "security" },
            idempotencyKey = "summary-sensitive-1"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.False(body.RootElement.GetProperty("allowsAgentContext").GetBoolean());
        Assert.Equal("Restricted", body.RootElement.GetProperty("classification").GetProperty("sensitivity").GetString());

        using var contextResponse = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            query = "credential",
            coreTags = new[] { "security" },
            maxItems = 10
        });
        using var contextBody = await JsonDocument.ParseAsync(await contextResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        Assert.Empty(contextBody.RootElement.GetProperty("items").EnumerateArray());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        var memory = await db.SharedMemoryItems.SingleAsync();
        Assert.Equal(SensitivityLevel.Restricted, memory.Sensitivity);
        Assert.Equal(MemoryVisibility.PrivateToOwner, memory.Visibility);
        Assert.False(memory.AllowsAgentContext);
    }

    [Fact]
    public async Task TurnSummaryIntakeIsIdempotentByKey()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var request = new
        {
            sessionId = "session-duplicate-1",
            turnId = "turn-1",
            sourceAgent = "codex",
            summary = "Published setup note for contributors.",
            coreTags = new[] { "setup" },
            idempotencyKey = "summary-duplicate-1"
        };

        using var first = await client.PostAsJsonAsync("/api/agent/turn-summaries", request);
        using var second = await client.PostAsJsonAsync("/api/agent/turn-summaries", request);
        using var firstBody = await JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync());
        using var secondBody = await JsonDocument.ParseAsync(await second.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(firstBody.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.True(secondBody.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.Equal(
            firstBody.RootElement.GetProperty("summaryId").GetString(),
            secondBody.RootElement.GetProperty("summaryId").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Equal(1, await db.SourceEvents.CountAsync());
        Assert.Equal(1, await db.ClassificationResults.CountAsync());
        Assert.Equal(1, await db.SharedMemoryItems.CountAsync());
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
        });
    }
}
