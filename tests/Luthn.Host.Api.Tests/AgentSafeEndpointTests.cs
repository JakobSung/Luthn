using Luthn.Core.Classification;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Luthn.Host.Api.Tests;

public sealed class AgentSafeEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AgentSafeEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SafeContextEndpointReturnsOnlyAgentAllowedWikiProposals()
    {
        using var factory = CreateFactoryWithSeedData();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            coreTags = new[] { "runbook" },
            maxItems = 10
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.RootElement.TryGetProperty("coreTags", out var coreTags));
        Assert.Contains(coreTags.EnumerateArray(), tag => tag.GetString() == "runbook");
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("wiki-public", item.GetProperty("id").GetString());
        Assert.True(item.TryGetProperty("coreTags", out var itemCoreTags));
        Assert.Contains(itemCoreTags.EnumerateArray(), tag => tag.GetString() == "runbook");
        Assert.DoesNotContain("contract", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SafeContextEndpointRejectsReservedTagAlias()
    {
        using var factory = CreateFactoryWithSeedData();
        using var client = factory.CreateClient();
        var reservedAlias = string.Concat("ont", "ologyTags");

        using var response = await client.PostAsJsonAsync(
            "/api/agent/context-packs",
            new Dictionary<string, object?>
            {
                [reservedAlias] = new[] { "runbook" },
                ["maxItems"] = 10
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SafeContextEndpointReturnsDemoSeededPublicRunbook()
    {
        using var factory = CreateFactoryWithDemoSeedData();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            coreTags = new[] { "demo" },
            maxItems = 10
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(DemoDataSeeder.WikiProposalId, item.GetProperty("id").GetString());
        Assert.Contains(
            item.GetProperty("coreTags").EnumerateArray(),
            tag => tag.GetString() == "demo");
        Assert.DoesNotContain("raw", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vault", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentSearchEndpointReturnsRankedSafeResultsForQueryAndCoreTags()
    {
        using var factory = CreateFactoryWithSearchSeedData();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/search", new
        {
            query = "billing outage",
            coreTags = new[] { "runbook" },
            maxItems = 10
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("billing outage", body.RootElement.GetProperty("query").GetString());
        Assert.Contains(
            body.RootElement.GetProperty("coreTags").EnumerateArray(),
            tag => tag.GetString() == "runbook");
        Assert.Equal(
            ["wiki-title-match", "wiki-summary-match"],
            body.RootElement.GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!)
                .ToArray());
        Assert.All(
            body.RootElement.GetProperty("results").EnumerateArray(),
            item => Assert.True(item.GetProperty("score").GetInt32() > 0));
    }

    [Fact]
    public async Task ContextPackEndpointUsesQueryAndCoreTagsForRankedItems()
    {
        using var factory = CreateFactoryWithSearchSeedData();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            query = "billing outage",
            coreTags = new[] { "runbook" },
            maxItems = 10
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["wiki-title-match", "wiki-summary-match"],
            body.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!)
                .ToArray());
        Assert.DoesNotContain("private customer", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentSearchEndpointNeverReturnsConfidentialOrAgentBlockedRecords()
    {
        using var factory = CreateFactoryWithSearchSeedData();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/search", new
        {
            query = "contract terms",
            coreTags = new[] { "contract" },
            maxItems = 10
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("wiki-contract-public", result.GetProperty("id").GetString());
        Assert.DoesNotContain("private customer", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw vault", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentSearchEndpointRejectsReservedTagAlias()
    {
        using var factory = CreateFactoryWithSeedData();
        using var client = factory.CreateClient();
        var reservedAlias = string.Concat("ont", "ologyTags");

        using var response = await client.PostAsJsonAsync(
            "/api/agent/search",
            new Dictionary<string, object?>
            {
                ["query"] = "runbook",
                [reservedAlias] = new[] { "runbook" },
                ["maxItems"] = 10
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AgentSearchEndpointRejectsOversizedQuery()
    {
        using var factory = CreateFactoryWithSeedData();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/search", new
        {
            query = new string('q', 501),
            coreTags = new[] { "runbook" },
            maxItems = 10
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("query must be 500 characters or fewer.", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ContextPackEndpointRejectsOversizedCoreTags()
    {
        using var factory = CreateFactoryWithSeedData();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent/context-packs", new
        {
            query = "runbook",
            coreTags = new[] { new string('t', 65) },
            maxItems = 10
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("coreTags entries must be 64 characters or fewer.", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task WikiProposalEndpointDoesNotReturnAgentBlockedProposal()
    {
        using var factory = CreateFactoryWithSeedData();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/wiki/proposals/wiki-sensitive");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WikiProposalEndpointUsesSafeSourceReferenceLabel()
    {
        using var factory = CreateFactoryWithSeedData();
        using var client = factory.CreateClient();

        var markdown = await client.GetStringAsync("/api/wiki/proposals/wiki-public");

        Assert.Contains("source-public", markdown, StringComparison.Ordinal);
        Assert.Contains("source-event, redacted-summary, safe-projection-only", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("safe-source:wiki-public", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("raw vault", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RawVaultRoutesAndAgentToolsAreNotExposedByDefault()
    {
        using var factory = CreateFactoryWithSeedData();
        using var client = factory.CreateClient();

        using var vaultResponse = await client.GetAsync("/api/vault/raw/source-sensitive");
        using var agentToolResponse = await client.GetAsync("/api/agent/read_raw_vault");

        Assert.Equal(HttpStatusCode.NotFound, vaultResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, agentToolResponse.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactoryWithSeedData()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.ConfigureServices(services =>
            {
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                using var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
                db.Database.EnsureCreated();
                db.WikiProposals.AddRange(
                    new WikiProposalRecord
                    {
                        Id = "wiki-public",
                        SourceEventId = "source-public",
                        Title = "Public runbook",
                        SafeSummary = "Public-safe release steps.",
                        Sensitivity = SensitivityLevel.Public,
                        CoreTags = ["runbook"],
                        AllowsAgentContext = true,
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    new WikiProposalRecord
                    {
                        Id = "wiki-sensitive",
                        SourceEventId = "source-sensitive",
                        Title = "Contract note",
                        SafeSummary = "Redacted sensitive placeholder.",
                        Sensitivity = SensitivityLevel.Confidential,
                        CoreTags = ["contract"],
                        AllowsAgentContext = false,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                db.SaveChanges();
            });
        });
    }

    private WebApplicationFactory<Program> CreateFactoryWithSearchSeedData()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.ConfigureServices(services =>
            {
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                using var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
                db.Database.EnsureCreated();
                db.WikiProposals.AddRange(
                    new WikiProposalRecord
                    {
                        Id = "wiki-summary-match",
                        SourceEventId = "source-summary-match",
                        Title = "Operations fallback",
                        SafeSummary = "Billing outage mitigation runbook.",
                        Sensitivity = SensitivityLevel.Public,
                        CoreTags = ["runbook"],
                        AllowsAgentContext = true,
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    new WikiProposalRecord
                    {
                        Id = "wiki-title-match",
                        SourceEventId = "source-title-match",
                        Title = "Billing outage runbook",
                        SafeSummary = "Queue restart steps.",
                        Sensitivity = SensitivityLevel.Public,
                        CoreTags = ["runbook"],
                        AllowsAgentContext = true,
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    new WikiProposalRecord
                    {
                        Id = "wiki-wrong-tag",
                        SourceEventId = "source-wrong-tag",
                        Title = "Billing outage escalation",
                        SafeSummary = "Public-safe support summary.",
                        Sensitivity = SensitivityLevel.Public,
                        CoreTags = ["support"],
                        AllowsAgentContext = true,
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    new WikiProposalRecord
                    {
                        Id = "wiki-unmatched",
                        SourceEventId = "source-unmatched",
                        Title = "Release checklist",
                        SafeSummary = "Deployment smoke tests.",
                        Sensitivity = SensitivityLevel.Public,
                        CoreTags = ["runbook"],
                        AllowsAgentContext = true,
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    new WikiProposalRecord
                    {
                        Id = "wiki-contract-public",
                        SourceEventId = "source-contract-public",
                        Title = "Contract terms runbook",
                        SafeSummary = "Public-safe approval checklist.",
                        Sensitivity = SensitivityLevel.Public,
                        CoreTags = ["contract"],
                        AllowsAgentContext = true,
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    new WikiProposalRecord
                    {
                        Id = "wiki-contract-confidential",
                        SourceEventId = "source-contract-confidential",
                        Title = "Contract terms private customer note",
                        SafeSummary = "Private customer raw vault terms.",
                        Sensitivity = SensitivityLevel.Confidential,
                        CoreTags = ["contract"],
                        AllowsAgentContext = true,
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    new WikiProposalRecord
                    {
                        Id = "wiki-contract-blocked",
                        SourceEventId = "source-contract-blocked",
                        Title = "Contract terms raw vault record",
                        SafeSummary = "Raw vault source details.",
                        Sensitivity = SensitivityLevel.Public,
                        CoreTags = ["contract"],
                        AllowsAgentContext = false,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                db.SaveChanges();
            });
        });
    }

    private WebApplicationFactory<Program> CreateFactoryWithDemoSeedData()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Luthn:TestingDatabaseName", Guid.NewGuid().ToString("N"));
            builder.ConfigureServices(services =>
            {
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                using var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
                db.Database.EnsureCreated();
                DemoDataSeeder.SeedAsync(db).GetAwaiter().GetResult();
            });
        });
    }
}
