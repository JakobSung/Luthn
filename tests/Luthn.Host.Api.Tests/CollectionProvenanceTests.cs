using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luthn.Core.Classification;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Luthn.Host.Api.Tests;

public sealed class CollectionProvenanceTests
{
    private const string WriterBearer = "provenance-writer-token";
    private const string ReaderBearer = "provenance-reader-token";
    private const string AgentBearer = "provenance-agent-token";

    [Fact]
    public async Task AuthenticatedActorIsServerDerivedAndOnlyAuditReaderCanReadProvenance()
    {
        using var factory = CreateAuthFactory();
        using var writer = factory.CreateClient();
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WriterBearer);

        using var created = await writer.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Release provenance",
            safeSummary = "Public release provenance summary.",
            coreTags = new[] { "release" },
            visibility = "SharedAcrossAgents",
            provenance = new
            {
                userId = "Owner.One",
                agentId = "Codex",
                connectorId = "Luthn.Codex.Connector"
            }
        });
        using var createdBody = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync());
        var memoryId = createdBody.RootElement.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var reader = factory.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReaderBearer);
        using var read = await reader.GetAsync($"/api/provenance/memory-items/{memoryId}");
        using var readBody = await JsonDocument.ParseAsync(await read.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("provenance-writer", readBody.RootElement.GetProperty("authenticatedActor").GetString());
        Assert.Equal("local-owner", readBody.RootElement.GetProperty("authenticatedUserId").GetString());
        Assert.Equal("service-token", readBody.RootElement.GetProperty("actorTrust").GetString());
        Assert.Equal("caller-supplied", readBody.RootElement.GetProperty("claimsTrust").GetString());
        Assert.Equal("owner.one", readBody.RootElement.GetProperty("claimedUserId").GetString());
        Assert.Equal("codex", readBody.RootElement.GetProperty("agentId").GetString());

        using var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AgentBearer);
        using var forbidden = await agent.GetAsync($"/api/provenance/memory-items/{memoryId}");
        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync($"/api/provenance/memory-items/{memoryId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task ServiceTokenNameCannotDowngradeVerifiedTransportTrust()
    {
        using var factory = CreateAuthFactory("local-anonymous");
        using var writer = factory.CreateClient();
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WriterBearer);

        using var created = await writer.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Transport trust",
            safeSummary = "Verified transport trust remains server-derived.",
            coreTags = new[] { "security" }
        });
        using var createdBody = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync());
        var memoryId = createdBody.RootElement.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var reader = factory.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReaderBearer);
        using var read = await reader.GetAsync($"/api/provenance/memory-items/{memoryId}");
        using var readBody = await JsonDocument.ParseAsync(await read.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("local-anonymous", readBody.RootElement.GetProperty("authenticatedActor").GetString());
        Assert.Equal("service-token", readBody.RootElement.GetProperty("actorTrust").GetString());
    }

    [Fact]
    public async Task ActorSpoofAndFutureCollectionTimeAreRejectedBeforePersistence()
    {
        using var factory = CreateAuthFactory();
        using var writer = factory.CreateClient();
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WriterBearer);

        using var spoof = await writer.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Spoof attempt",
            safeSummary = "Public-safe summary.",
            coreTags = new[] { "test" },
            provenance = new
            {
                authenticatedActor = "attacker",
                userId = "owner.one"
            }
        });
        using var future = await writer.PostAsJsonAsync("/api/memory/items", new
        {
            title = "Future timestamp",
            safeSummary = "Public-safe summary.",
            coreTags = new[] { "test" },
            provenance = new
            {
                collectedAt = DateTimeOffset.UtcNow.AddHours(1)
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, spoof.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
        Assert.Empty(await db.SharedMemoryItems.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.CollectionProvenance.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task ProvenanceCannotBeUpdatedThroughThePersistenceContext()
    {
        var options = new DbContextOptionsBuilder<LuthnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new LuthnDbContext(options);
        db.SharedMemoryItems.Add(new SharedMemoryItemRecord
        {
            Id = "memory-immutable",
            Title = "Immutable",
            SafeSummary = "Immutable provenance test.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["test"],
            Visibility = Luthn.Core.Memory.MemoryVisibility.PrivateToOwner,
            RetentionKind = Luthn.Core.Memory.MemoryRetentionKind.Durable,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            CreatedBy = "test"
        });
        db.CollectionProvenance.Add(new CollectionProvenanceRecord
        {
            Id = "provenance-immutable",
            MemoryItemId = "memory-immutable",
            AuthenticatedActor = "test",
            ActorTrust = CollectionProvenance.ServiceTokenActorTrust,
            ClaimsTrust = CollectionProvenance.NoClaimsTrust,
            ReceivedAt = DateTimeOffset.UnixEpoch
        });
        await db.SaveChangesAsync();

        var provenance = await db.CollectionProvenance.SingleAsync();
        provenance.AgentId = "modified";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Collection provenance records are immutable.", error.Message);
    }

    private static WebApplicationFactory<Program> CreateAuthFactory(string writerName = "provenance-writer") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.UseSetting("Luthn:Auth:RequireServiceToken", "true");
            ConfigureToken(builder, 0, writerName, WriterBearer, "memory.write");
            ConfigureToken(builder, 1, "provenance-reader", ReaderBearer, "audit.read");
            ConfigureToken(builder, 2, "provenance-agent", AgentBearer, "agent.read");
        });

    private static void ConfigureToken(
        IWebHostBuilder builder,
        int index,
        string name,
        string bearer,
        string scope)
    {
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Name", name);
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Sha256Digest", Sha256(bearer));
        builder.UseSetting($"Luthn:Auth:Tokens:{index}:Scopes:0", scope);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
